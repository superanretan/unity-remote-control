// In-process implementation of the store contract (see redis.js for the Redis one).
//
// Used when REDIS_URL is not set (local LAN dev, single instance) and by the test-suite, where one
// MemoryStore is shared by several server instances in the same process to simulate the multi-instance
// Vercel topology. Semantics (atomicity, TTLs, FIFO mailboxes, compare-and-set leases, fire-and-forget
// pub/sub) mirror the Redis implementation exactly so behaviour does not diverge between environments.

import { EventEmitter } from "node:events";

export class MemoryStore {
  constructor() {
    this.kind = "memory";
    this._registry = new Map();   // room → Map<deviceId, entryJson>
    this._seen = new Map();       // room → Map<deviceId, lastSeenMs>
    this._mailbox = new Map();    // key → { items: string[], expiresAt }
    this._seq = new Map();        // key → number
    this._kv = new Map();         // key → { value, expiresAt }
    this._bus = new EventEmitter();
    this._bus.setMaxListeners(0);
  }

  async ready() {}
  async close() { this._bus.removeAllListeners(); }
  async ping() { return true; }

  async selfTest() { return (this._lastSelfTest = { kind: this.kind, ping: 0, pubsub: "ok", lua: "n/a", error: null }); }
  lastSelfTest() { return this._lastSelfTest ?? null; }
  async keepalive() {}

  // ───────── registry ─────────

  async registryUpsert(room, deviceId, entryJson, nowMs) {
    this._map(this._registry, room).set(deviceId, entryJson);
    this._map(this._seen, room).set(deviceId, String(nowMs));
  }

  async registryTouch(room, deviceId, nowMs) {
    if (!this._map(this._registry, room).has(deviceId)) return false;
    this._map(this._seen, room).set(deviceId, String(nowMs));
    return true;
  }

  async registryGet(room, deviceId) {
    const entry = this._map(this._registry, room).get(deviceId);
    if (entry === undefined) return null;
    const seen = Number(this._map(this._seen, room).get(deviceId) || 0);
    return { entryJson: entry, seenMs: seen, pairOwner: this._get(this._pairKey(room, deviceId)) };
  }

  async registryReadAll(room) {
    const entries = Object.fromEntries(this._map(this._registry, room));
    const seen = {};
    for (const [id, v] of this._map(this._seen, room)) seen[id] = Number(v);
    const pairs = {};
    for (const id of Object.keys(entries)) pairs[id] = this._get(this._pairKey(room, id));
    return { entries, seen, pairs };
  }

  async registryRemove(room, deviceIds) {
    for (const id of deviceIds) {
      this._map(this._registry, room).delete(id);
      this._map(this._seen, room).delete(id);
    }
  }

  /** Compare-and-delete: removes each device only if its lastSeen is still the observed (stale) value. */
  async registryRemoveIfStale(room, observed) {
    let removed = 0;
    for (const { id, seenMs } of observed) {
      const current = this._map(this._seen, room).get(id);
      if (current !== undefined && Number(current) !== seenMs) continue;     // refreshed meanwhile - keep
      this._map(this._registry, room).delete(id);
      this._map(this._seen, room).delete(id);
      removed++;
    }
    return removed;
  }

  // --------- socket ownership (which live connection currently speaks for an id) ---------

  async claimOwner(room, role, id, token, ttlSec) {
    this._set(this._ownKey(room, role, id), token, ttlSec);
  }

  async ownerIs(room, role, id, token) {
    return this._get(this._ownKey(room, role, id)) === token;
  }

  async refreshOwner(room, role, id, token, ttlSec) {
    const key = this._ownKey(room, role, id);
    if (this._get(key) !== token) return false;
    this._set(key, token, ttlSec);
    return true;
  }

  // ───────── mailbox ─────────

  async mailboxPush(room, id, json, ttlSec, max) {
    const key = this._mboxKey(room, id);
    const seq = (this._seq.get(key) || 0) + 1;
    this._seq.set(key, seq);
    const box = this._box(key);
    box.items.push(`${seq}:${json}`);
    if (box.items.length > max) box.items.splice(0, box.items.length - max);
    box.expiresAt = Date.now() + ttlSec * 1000;
    return seq;
  }

  async mailboxDrain(room, id, count) {
    const box = this._box(this._mboxKey(room, id));
    return box.items.splice(0, count);
  }

  /** Puts undelivered items back at the front, preserving order. */
  async mailboxUnshift(room, id, items) {
    if (!items.length) return;
    const box = this._box(this._mboxKey(room, id));
    box.items.unshift(...items);
    if (!box.expiresAt) box.expiresAt = Date.now() + 45000;
  }

  // ───────── pairing lease ─────────

  async pairAcquire(room, deviceId, clientId, ttlSec) {
    const key = this._pairKey(room, deviceId);
    const owner = this._get(key);
    if (owner !== null && owner !== clientId) return { ok: false, owner };
    this._set(key, clientId, ttlSec);
    this._set(this._ctlKey(room, clientId), deviceId, ttlSec);
    return { ok: true, owner: clientId };
  }

  async pairRefresh(room, deviceId, clientId, ttlSec) {
    const key = this._pairKey(room, deviceId);
    if (this._get(key) !== clientId) return false;
    this._set(key, clientId, ttlSec);
    this._set(this._ctlKey(room, clientId), deviceId, ttlSec);
    return true;
  }

  async pairRelease(room, deviceId, clientId) {
    const key = this._pairKey(room, deviceId);
    if (this._get(key) !== clientId) return false;
    this._kv.delete(key);
    if (this._get(this._ctlKey(room, clientId)) === deviceId) this._kv.delete(this._ctlKey(room, clientId));
    return true;
  }

  async pairOwner(room, deviceId) { return this._get(this._pairKey(room, deviceId)); }
  async pairOfController(room, clientId) { return this._get(this._ctlKey(room, clientId)); }

  // ───────── events (fire-and-forget, like Redis pub/sub) ─────────

  async publish(event) {
    const payload = JSON.stringify(event);
    // Deliver asynchronously so publisher and subscriber never share a call stack (matches Redis).
    setImmediate(() => this._bus.emit("event", payload));
  }

  subscribe(handler) {
    const wrapped = (payload) => { try { handler(JSON.parse(payload)); } catch { /* ignore */ } };
    this._bus.on("event", wrapped);
    return () => this._bus.off("event", wrapped);
  }

  // ───────── internals ─────────

  _map(outer, room) {
    let m = outer.get(room);
    if (!m) { m = new Map(); outer.set(room, m); }
    return m;
  }

  _box(key) {
    let box = this._mailbox.get(key);
    if (box && box.expiresAt && box.expiresAt <= Date.now()) { this._mailbox.delete(key); box = null; }
    if (!box) { box = { items: [], expiresAt: 0 }; this._mailbox.set(key, box); }
    return box;
  }

  _get(key) {
    const rec = this._kv.get(key);
    if (!rec) return null;
    if (rec.expiresAt && rec.expiresAt <= Date.now()) { this._kv.delete(key); return null; }
    return rec.value;
  }

  _set(key, value, ttlSec) {
    this._kv.set(key, { value, expiresAt: ttlSec ? Date.now() + ttlSec * 1000 : 0 });
  }

  _ownKey(room, role, id) { return `${room}|own|${role}|${id}`; }
  _pairKey(room, deviceId) { return `${room}|pair|${deviceId}`; }
  _ctlKey(room, clientId) { return `${room}|ctl|${clientId}`; }
  _mboxKey(room, id) { return `${room}|mbox|${id}`; }
}
