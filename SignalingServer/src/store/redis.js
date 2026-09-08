// Redis implementation of the store contract (ioredis over TCP — the REST client cannot SUBSCRIBE).
//
// Two connections: `cmd` for commands, `sub` in subscriber mode (a subscriber connection cannot issue
// other commands). Both live as long as the function instance, which on Fluid compute is as long as
// its WebSockets — exactly what we want.
//
// Keys (all under KEY_PREFIX, default "rc"):
//   rc:<room>:registry           hash  deviceId → JSON {deviceId, deviceName, platform, status, timeoutMs}
//   rc:<room>:seen               hash  deviceId → lastSeen ms   (touched by every heartbeat)
//   rc:<room>:mbox:<id>          list  "<seq>:<json>" — messages waiting for <id>, TTL MAILBOX_TTL_SECONDS
//   rc:<room>:seq:<id>           int   INCR per queued message → dedup stamp
//   rc:<room>:pair:<deviceId>    str   clientId holding the pairing lease, TTL PAIR_LEASE_SECONDS
//   rc:<room>:ctl:<clientId>     str   reverse map → deviceId, same TTL (lets a re-registering controller
//                                      refresh its lease immediately instead of waiting for the tick)
//   rc:<room>:own:<role>:<id>    str   "<instance>:<epoch>" of the socket that currently speaks for <id>.
//                                      Every message/drain is fenced on it, so a replaced socket on another
//                                      instance can neither steal mailbox items nor release the lease.
//   rc:events                    pub/sub channel — JSON {t:"wake"|"devices"|"evict", room, id, epoch}
//
// Every compare-and-set operation is a Lua script so two instances can never interleave a GET and a SET.

import Redis from "ioredis";
import { randomBytes } from "node:crypto";

const LUA = {
  // KEYS[1]=mbox KEYS[2]=seq  ARGV[1]=json ARGV[2]=ttlSec ARGV[3]=max
  mailboxPush: `
    local seq = redis.call('INCR', KEYS[2])
    redis.call('EXPIRE', KEYS[2], ARGV[2] * 20)
    local len = redis.call('RPUSH', KEYS[1], seq .. ':' .. ARGV[1])
    if len > tonumber(ARGV[3]) then redis.call('LTRIM', KEYS[1], len - tonumber(ARGV[3]), -1) end
    redis.call('EXPIRE', KEYS[1], ARGV[2])
    return seq`,

  // KEYS[1]=pair KEYS[2]=ctl  ARGV[1]=clientId ARGV[2]=deviceId ARGV[3]=ttlSec  → {1, owner} | {0, owner}
  pairAcquire: `
    local owner = redis.call('GET', KEYS[1])
    if owner and owner ~= ARGV[1] then return {0, owner} end
    redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[3])
    redis.call('SET', KEYS[2], ARGV[2], 'EX', ARGV[3])
    return {1, ARGV[1]}`,

  // KEYS[1]=pair KEYS[2]=ctl  ARGV[1]=clientId ARGV[2]=deviceId ARGV[3]=ttlSec → 1 if refreshed
  pairRefresh: `
    if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
    redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[3])
    redis.call('SET', KEYS[2], ARGV[2], 'EX', ARGV[3])
    return 1`,

  // KEYS[1]=pair KEYS[2]=ctl  ARGV[1]=clientId ARGV[2]=deviceId → 1 if released
  pairRelease: `
    if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
    redis.call('DEL', KEYS[1])
    if redis.call('GET', KEYS[2]) == ARGV[2] then redis.call('DEL', KEYS[2]) end
    return 1`,

  // KEYS[1]=registry KEYS[2]=seen  ARGV = id1, seen1, id2, seen2, ... → number removed
  // Deletes a device only if its lastSeen is still the value the caller observed as stale.
  registryRemoveIfStale: `
    local removed = 0
    for i = 1, #ARGV, 2 do
      local current = redis.call('HGET', KEYS[2], ARGV[i])
      if current == false or current == ARGV[i + 1] then
        redis.call('HDEL', KEYS[1], ARGV[i])
        redis.call('HDEL', KEYS[2], ARGV[i])
        removed = removed + 1
      end
    end
    return removed`,

  // KEYS[1]=own  ARGV[1]=token ARGV[2]=ttlSec → 1 if still owner (and refreshed)
  refreshOwner: `
    if redis.call('GET', KEYS[1]) ~= ARGV[1] then return 0 end
    redis.call('SET', KEYS[1], ARGV[1], 'EX', ARGV[2])
    return 1`,
};

const LUA_KEYS = { mailboxPush: 2, pairAcquire: 2, pairRefresh: 2, pairRelease: 2, registryRemoveIfStale: 2, refreshOwner: 1 };

export class RedisStore {
  constructor(url, { keyPrefix = "rc", log = console.log } = {}) {
    this.kind = "redis";
    this._p = keyPrefix;
    this._log = log;
    const opts = {
      lazyConnect: false,
      maxRetriesPerRequest: 2,
      enableOfflineQueue: true,
      connectTimeout: 10000,
      retryStrategy: (times) => Math.min(1000 * 2 ** Math.min(times, 5), 15000),
    };
    this.cmd = new Redis(url, opts);
    this.sub = new Redis(url, opts);
    for (const [name, lua] of Object.entries(LUA)) this.cmd.defineCommand(name, { numberOfKeys: LUA_KEYS[name], lua });

    for (const [label, c] of [["cmd", this.cmd], ["sub", this.sub]]) {
      c.on("error", (e) => this._log(`[redis:${label}] ${e.message}`));
      c.on("reconnecting", () => this._log(`[redis:${label}] reconnecting…`));
    }

    this._handlers = new Set();
    this._eventsChannel = `${this._p}:events`;
    this._ready = this.sub.subscribe(this._eventsChannel).then(() => {
      this.sub.on("message", (_channel, payload) => {
        let event;
        try { event = JSON.parse(payload); } catch { return; }
        for (const h of this._handlers) { try { h(event); } catch (e) { this._log(`[redis] handler threw: ${e.message}`); } }
      });
    });
    // A dropped subscriber connection re-subscribes automatically (ioredis keeps the subscription set).
  }

  async ready() { await this._ready; }
  async ping() { return (await this.cmd.ping()) === "PONG"; }

  // Boot check reported by GET /api/health: is Redis reachable, does the pub/sub doorbell ring, and
  // does every Lua script load and run? The Lua dry-run catches a typo before it breaks a live
  // session; it uses a throwaway key prefix that is deleted again.
  async selfTest() {
    const out = { kind: this.kind, ping: null, pubsub: "down", lua: "ok", error: null };

    const t0 = Date.now();
    try {
      await this.cmd.ping();
      out.ping = Date.now() - t0;
    } catch (e) {
      out.error = `ping: ${e.message}`;
      return (this._lastSelfTest = out);
    }

    const token = randomBytes(8).toString("hex");
    out.pubsub = await new Promise((resolve) => {
      const handler = (ev) => {
        if (!ev || ev.t !== "selftest" || ev.token !== token) return;
        clearTimeout(timer);
        this._handlers.delete(handler);
        resolve("ok");
      };
      const timer = setTimeout(() => { this._handlers.delete(handler); resolve("down"); }, 5000);
      if (typeof timer.unref === "function") timer.unref();
      this._handlers.add(handler);
      this.publish({ t: "selftest", room: "__selftest", token }).catch(() => {});
    });

    const p = this._k("__selftest", token);
    try {
      await this.mailboxPush("__selftest", token, "{}", 10, 5);
      await this.cmd.pairAcquire(`${p}:pair`, `${p}:ctl`, "selftestclient", "selftestdevice", 10);
      await this.cmd.pairRefresh(`${p}:pair`, `${p}:ctl`, "selftestclient", "selftestdevice", 10);
      await this.cmd.pairRelease(`${p}:pair`, `${p}:ctl`, "selftestclient", "selftestdevice");
      await this.cmd.registryRemoveIfStale(`${p}:registry`, `${p}:seen`, "selftestdevice", "0");
      await this.cmd.refreshOwner(`${p}:own`, "selftest-token", 10);
    } catch (e) {
      out.lua = `error: ${e.message}`;
    } finally {
      try {
        await this.cmd.del(
          this._k("__selftest", `mbox:${token}`), this._k("__selftest", `seq:${token}`),
          `${p}:pair`, `${p}:ctl`, `${p}:registry`, `${p}:seen`, `${p}:own`,
        );
      } catch { /* ignore */ }
    }

    return (this._lastSelfTest = out);
  }

  // Last selfTest result, or null when it has not run yet.
  lastSelfTest() { return this._lastSelfTest ?? null; }

  // Keeps the subscriber connection from being reaped as idle: it has no outbound traffic of its
  // own, and PING is one of the few commands allowed while subscribed.
  async keepalive() {
    try { await this.sub.ping(); } catch { /* ioredis reconnects and re-subscribes on its own */ }
  }

  async close() {
    try { await this.sub.quit(); } catch { /* ignore */ }
    try { await this.cmd.quit(); } catch { /* ignore */ }
  }

  // ───────── registry ─────────

  async registryUpsert(room, deviceId, entryJson, nowMs) {
    await this.cmd.multi()
      .hset(this._k(room, "registry"), deviceId, entryJson)
      .hset(this._k(room, "seen"), deviceId, String(nowMs))
      .exec();
  }

  async registryTouch(room, deviceId, nowMs) {
    const exists = await this.cmd.hexists(this._k(room, "registry"), deviceId);
    if (!exists) return false;
    await this.cmd.hset(this._k(room, "seen"), deviceId, String(nowMs));
    return true;
  }

  async registryGet(room, deviceId) {
    const [[, entryJson], [, seen], [, owner]] = await this.cmd.multi()
      .hget(this._k(room, "registry"), deviceId)
      .hget(this._k(room, "seen"), deviceId)
      .get(this._k(room, `pair:${deviceId}`))
      .exec();
    if (entryJson === null || entryJson === undefined) return null;
    return { entryJson, seenMs: Number(seen || 0), pairOwner: owner ?? null };
  }

  async registryReadAll(room) {
    const [[, entries], [, seenRaw]] = await this.cmd.multi()
      .hgetall(this._k(room, "registry"))
      .hgetall(this._k(room, "seen"))
      .exec();
    const seen = {};
    for (const [id, v] of Object.entries(seenRaw || {})) seen[id] = Number(v);
    const ids = Object.keys(entries || {});
    const pairs = {};
    if (ids.length) {
      const owners = await this.cmd.mget(ids.map(id => this._k(room, `pair:${id}`)));
      ids.forEach((id, i) => { pairs[id] = owners[i] ?? null; });
    }
    return { entries: entries || {}, seen, pairs };
  }

  async registryRemove(room, deviceIds) {
    if (!deviceIds.length) return;
    await this.cmd.multi()
      .hdel(this._k(room, "registry"), ...deviceIds)
      .hdel(this._k(room, "seen"), ...deviceIds)
      .exec();
  }

  async registryRemoveIfStale(room, observed) {
    if (!observed.length) return 0;
    const args = [];
    for (const { id, seenMs } of observed) args.push(id, String(seenMs));
    return Number(await this.cmd.registryRemoveIfStale(this._k(room, "registry"), this._k(room, "seen"), ...args));
  }

  // ───────── socket ownership ─────────

  async claimOwner(room, role, id, token, ttlSec) {
    await this.cmd.set(this._k(room, `own:${role}:${id}`), token, "EX", ttlSec);
  }

  async ownerIs(room, role, id, token) {
    return (await this.cmd.get(this._k(room, `own:${role}:${id}`))) === token;
  }

  async refreshOwner(room, role, id, token, ttlSec) {
    return Number(await this.cmd.refreshOwner(this._k(room, `own:${role}:${id}`), token, ttlSec)) === 1;
  }

  // ───────── mailbox ─────────

  async mailboxPush(room, id, json, ttlSec, max) {
    return Number(await this.cmd.mailboxPush(this._k(room, `mbox:${id}`), this._k(room, `seq:${id}`), json, ttlSec, max));
  }

  // Atomic LPOP <count> (Redis 6.2+). Returns [] when empty.
  async mailboxDrain(room, id, count) {
    const items = await this.cmd.lpop(this._k(room, `mbox:${id}`), count);
    return items || [];
  }

  async mailboxUnshift(room, id, items) {
    if (!items.length) return;
    // LPUSH pushes in argument order to the head, so reverse to keep the original FIFO order.
    await this.cmd.lpush(this._k(room, `mbox:${id}`), ...[...items].reverse());
  }

  // ───────── pairing lease ─────────

  async pairAcquire(room, deviceId, clientId, ttlSec) {
    const [ok, owner] = await this.cmd.pairAcquire(this._k(room, `pair:${deviceId}`), this._k(room, `ctl:${clientId}`), clientId, deviceId, ttlSec);
    return { ok: Number(ok) === 1, owner: owner ?? null };
  }

  async pairRefresh(room, deviceId, clientId, ttlSec) {
    return Number(await this.cmd.pairRefresh(this._k(room, `pair:${deviceId}`), this._k(room, `ctl:${clientId}`), clientId, deviceId, ttlSec)) === 1;
  }

  async pairRelease(room, deviceId, clientId) {
    return Number(await this.cmd.pairRelease(this._k(room, `pair:${deviceId}`), this._k(room, `ctl:${clientId}`), clientId, deviceId)) === 1;
  }

  async pairOwner(room, deviceId) { return this.cmd.get(this._k(room, `pair:${deviceId}`)); }
  async pairOfController(room, clientId) { return this.cmd.get(this._k(room, `ctl:${clientId}`)); }

  // ───────── events ─────────

  async publish(event) {
    await this.cmd.publish(this._eventsChannel, JSON.stringify(event));
  }

  subscribe(handler) {
    this._handlers.add(handler);
    return () => this._handlers.delete(handler);
  }

  _k(room, suffix) { return `${this._p}:${room}:${suffix}`; }
}
