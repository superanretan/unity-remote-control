// Host-independent signaling logic: device registry, controller discovery, offer/answer/ICE relay.
// Shared by the Vercel Function adapter (api/signaling.js) and the local dev server (server.js).
//
// Design invariants (see README "How it survives Vercel's 300 s socket limit"):
//   - A closing WebSocket means nothing for registry, pairing or the WebRTC session. Sockets are
//     replaced every <=300 s on Vercel; only a stale lastSeen removes a host, only lease expiry or
//     an explicit disconnect ends a pairing.
//   - Relay goes through a per-recipient mailbox in the store (RPUSH / LPOP); pub/sub is only a
//     doorbell. The mailbox is drained right after a socket registers, so a message that arrived
//     during a reconnect gap is delivered as soon as the recipient is back, on any instance.
//   - fromId is always stamped from the socket, never trusted from the payload.
//   - Every id has exactly one live owner in the store (own:<role>:<id> = "<instance>:<epoch>").
//     Registration claims it; every later message and mailbox drain is fenced on it, so a replaced
//     socket can neither steal mailbox items nor release a lease.
//   - One socket's messages are processed strictly in order (per-socket promise chain), so
//     register-controller followed by list-devices / offer cannot race.

import { randomUUID, randomBytes } from "node:crypto";
import { isValidId, isValidSessionId, sanitizeName, sanitizeShort, normalizeStatus, normalizeDeviceTimeout } from "./validate.js";
import { issueIceServers } from "./turn.js";

const OPEN = 1;
const CLOSE_REPLACED = 4000;
const CLOSE_UNAUTHORIZED = 4401;
const CLOSE_ORIGIN = 4403;
const DRAIN_BATCH = 32;
const OWNER_TTL_SEC = 3600;         // refreshed every tick; only there to garbage-collect dead instances' keys

export function createSignaling({ store, config, log = console.log }) {
  const instanceId = randomBytes(4).toString("hex");
  const rooms = new Map();          // room → { hosts: Map<id, socket>, controllers: Map<id, socket>, listTimer }
  const sockets = new Set();
  let epochCounter = 0;
  let tickTimer = null;
  let unsubscribe = null;
  let closed = false;
  // pending | ok | degraded (pub/sub down) | error | misconfigured, reported by GET /api/health.
  let storeStatus = { state: "pending", detail: null };

  const verbose = (line) => { if (config.verbose) log(line); };

  // ───────── attach / detach ─────────

  function attach(wss) {
    unsubscribe = store.subscribe(onStoreEvent);
    wss.on("connection", (socket, req) => onConnection(socket, req));
    tickTimer = setInterval(() => { tick().catch(e => log(`[tick] ${e.message}`)); }, config.tickMs);
    if (typeof tickTimer.unref === "function") tickTimer.unref();
    log(`[signaling] instance ${instanceId} attached (store=${store.kind}, deviceTimeout=${config.deviceTimeoutMs / 1000}s, pairLease=${config.pairLeaseSec}s, rooms=${config.rooms.open ? "open" : [...new Set(config.rooms.byToken.values())].join("|")})`);
  }

  // Confirms the store is usable and records the verdict for GET /api/health. Never throws and never
  // blocks the cold start: an unreachable Redis must be reported, not turned into a hung upgrade.
  async function verifyStore() {
    if (config.misconfigured) {
      storeStatus = { state: "misconfigured", detail: config.misconfigured.slug };
      return storeStatus;
    }
    try {
      await withTimeout(store.ready(), 15000, "store-ready-timeout");
      const t = await withTimeout(store.selfTest(), 20000, "store-selftest-timeout");
      if (t.error) {
        storeStatus = { state: "error", detail: t.error };
        log(`[FATAL] store unreachable: ${t.error}`);
      } else if (typeof t.lua === "string" && t.lua.startsWith("error")) {
        storeStatus = { state: "error", detail: t.lua };
        log(`[FATAL] Redis rejected a Lua script: ${t.lua}`);
      } else if (t.pubsub !== "ok") {
        storeStatus = { state: "degraded", detail: "pubsub-down" };
        log("[WARN] pub/sub doorbell not working — relay still arrives (mailbox is drained on registration) but " +
            "negotiation can be slow. Check that the provider supports pub/sub over TCP.");
      } else {
        storeStatus = { state: "ok", detail: null };
        log(`[store] self-test ok (ping ${t.ping} ms, pubsub ok, lua ${t.lua})`);
      }
    } catch (e) {
      storeStatus = { state: "error", detail: e.message };
      log(`[FATAL] store verification failed: ${e.message}`);
    }
    return storeStatus;
  }

  function withTimeout(promise, ms, label) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error(label)), ms);
      if (typeof timer.unref === "function") timer.unref();
      Promise.resolve(promise).then(
        (v) => { clearTimeout(timer); resolve(v); },
        (e) => { clearTimeout(timer); reject(e); },
      );
    });
  }

  async function close() {
    closed = true;
    if (tickTimer) clearInterval(tickTimer);
    if (unsubscribe) unsubscribe();
    for (const s of sockets) { try { s.terminate(); } catch { /* ignore */ } }
    sockets.clear();
  }

  // ───────── connection lifecycle ─────────

  function onConnection(socket, req) {
    const { room, error, closeCode } = authorize(req);
    const epoch = ++epochCounter;
    socket.rc = {
      room, role: null, id: null, epoch, token: `${instanceId}:${epoch}`, evicted: false, isAlive: true,
      entryJson: null,         // host: last registry entry we wrote (to self-heal after a registry loss)
      chain: Promise.resolve(), lastSeq: 0, draining: false, drainAgain: false,
      peerDeviceId: null,      // controller: deviceId it holds a lease for
      pairedWith: null,        // host: controller id last observed on its lease
    };
    sockets.add(socket);
    socket.on("close", () => onSocketClosed(socket));
    socket.on("error", () => { /* close follows */ });

    if (error) {
      // Accept the upgrade just long enough to tell the client why, then close: a raw HTTP 401
      // surfaces in the browser as an opaque close code 1006.
      log(`[ws] rejected ${req.socket?.remoteAddress ?? "?"}: ${error}`);
      send(socket, { type: "error", message: error });
      try { socket.close(closeCode, error); } catch { /* ignore */ }
      return;
    }

    socket.on("pong", () => { socket.rc.isAlive = true; });
    socket.on("message", (data) => {
      let msg;
      try { msg = JSON.parse(data.toString()); } catch { return sendError(socket, "invalid-json"); }
      if (!msg || typeof msg !== "object" || typeof msg.type !== "string") return sendError(socket, "missing-type");
      // Serialize per socket so registration always completes before the next message is handled.
      socket.rc.chain = socket.rc.chain
        .then(() => handle(socket, msg))
        .catch((e) => {
          log(`[handle] ${msg.type} failed: ${e.message}`);
          sendError(socket, "server-unavailable");
        });
    });

    if (config.debugForceCloseMs > 0) {
      const t = setTimeout(() => { try { socket.close(1001, "debug-force-close"); } catch { /* ignore */ } }, config.debugForceCloseMs);
      if (typeof t.unref === "function") t.unref();
    }

    verbose(`[ws] connection from ${req.socket?.remoteAddress ?? "?"} room=${room}`);
  }

  function authorize(req) {
    let url;
    try { url = new URL(req.url || "/", "http://localhost"); } catch { url = new URL("http://localhost/"); }

    // Origin allowlist: browsers always send Origin; ClientWebSocket (Vision Pro) does not — the token
    // is the gate for non-browser clients.
    const origin = req.headers?.origin;
    if (config.allowedOrigins && typeof origin === "string" && origin) {
      if (!config.allowedOrigins.has(origin.toLowerCase().replace(/\/+$/, "")))
        return { room: null, error: "origin-not-allowed", closeCode: CLOSE_ORIGIN };
    }

    if (config.rooms.open) return { room: "default", error: null };
    const token = url.searchParams.get("token") || headerToken(req);
    const room = token ? config.rooms.byToken.get(token) : undefined;
    if (!room) return { room: null, error: "unauthorized", closeCode: CLOSE_UNAUTHORIZED };
    return { room, error: null };
  }

  function headerToken(req) {
    const h = req.headers?.["x-signaling-token"];
    if (typeof h === "string" && h) return h;
    const auth = req.headers?.authorization;
    if (typeof auth === "string" && auth.toLowerCase().startsWith("bearer ")) return auth.slice(7).trim();
    return null;
  }

  function onSocketClosed(socket) {
    sockets.delete(socket);
    const rc = socket.rc;
    if (!rc || !rc.role || !rc.id) return;
    const r = roomState(rc.room);
    const table = rc.role === "host" ? r.hosts : r.controllers;
    // Only forget the socket locally, and only if it is still the current one for that id.
    if (table.get(rc.id) === socket) table.delete(rc.id);
    // Deliberately no registry/pair/status change and no peer notification: on Vercel this happens
    // every <=300 s for healthy clients. Staleness is decided by lastSeen / lease expiry.
    verbose(`[ws] closed ${rc.role}:${rc.id}${rc.evicted ? " (evicted)" : ""}`);
  }

  // ───────── message handling ─────────

  async function handle(socket, msg) {
    const rc = socket.rc;
    if (rc.evicted) return;                              // stale socket replaced elsewhere — ignore everything
    verbose(`[msg] ${rc.role ?? "?"}:${rc.id ?? "?"} → ${msg.type}`);

    // Never pretend to work: without a shared store on Vercel the peers would never find each other.
    // Say so on the wire, so the reason reaches Unity's LogChannel instead of an empty device list.
    if (config.misconfigured) return sendError(socket, `server-misconfigured:${config.misconfigured.slug}`);

    // Ownership fence: a registered socket whose id has since been claimed by a newer one (possibly
    // on another instance whose eviction event is still in flight) must not act on anything.
    if (rc.role && !(msg.type === "register-device" || msg.type === "register-controller")) {
      if (!await store.ownerIs(rc.room, rc.role, rc.id, rc.token)) { evict(socket, "lost ownership"); return; }
    }

    switch (msg.type) {
      case "register-device":     return registerDevice(socket, msg);
      case "heartbeat":           return heartbeat(socket);
      case "set-status":          return setStatus(socket, msg);
      case "register-controller": return registerController(socket, msg);
      case "list-devices":        return send(socket, { type: "device-list", devices: await deviceList(rc.room) });
      case "offer":
      case "answer":
      case "ice-candidate":
      case "disconnect":          return relay(socket, msg);
      default:                    return sendError(socket, `unknown-type:${sanitizeShort(msg.type, 32)}`);
    }
  }

  async function registerDevice(socket, msg) {
    const rc = socket.rc;
    const deviceId = isValidId(msg.deviceId) ? msg.deviceId : randomUUID().replace(/-/g, "");
    const entry = {
      deviceId,
      deviceName: sanitizeName(msg.deviceName, config.maxDeviceNameLength),
      platform: sanitizeShort(msg.platform, 32, "unknown"),
      status: normalizeStatus(msg.status),
      timeoutMs: normalizeDeviceTimeout(msg.deviceTimeout),
    };

    rc.entryJson = JSON.stringify(entry);
    await store.registryUpsert(rc.room, deviceId, rc.entryJson, Date.now());
    await adopt(socket, "host", deviceId);
    send(socket, { type: "registered", deviceId, iceServers: issueIceServers(config.turn, deviceId) });
    log(`[registry] host registered: "${entry.deviceName}" (${deviceId}) room=${rc.room}`);

    // Observe an existing lease so "controller-gone" can be detected after a host reconnect.
    rc.pairedWith = await store.pairOwner(rc.room, deviceId);

    await store.publish({ t: "devices", room: rc.room });
    await drainMailbox(socket);
  }

  async function heartbeat(socket) {
    const rc = socket.rc;
    if (rc.role !== "host") return sendError(socket, "not-registered");
    const ok = await store.registryTouch(rc.room, rc.id, Date.now());
    if (!ok && rc.entryJson) {
      // Registry entry vanished (Redis flush / eviction) while the socket is alive: put it back
      // instead of leaving the host invisible until its next reconnect.
      await store.registryUpsert(rc.room, rc.id, rc.entryJson, Date.now());
      await store.publish({ t: "devices", room: rc.room });
      log(`[registry] re-inserted host ${rc.id} after registry loss`);
    }
  }

  async function setStatus(socket, msg) {
    const rc = socket.rc;
    if (rc.role !== "host") return sendError(socket, "not-registered");
    const current = await store.registryGet(rc.room, rc.id);
    if (!current) return sendError(socket, "not-registered");
    let entry;
    try { entry = JSON.parse(current.entryJson); } catch { entry = { deviceId: rc.id }; }
    const status = normalizeStatus(msg.status);
    if (entry.status === status) return;
    entry.status = status;
    rc.entryJson = JSON.stringify(entry);
    await store.registryUpsert(rc.room, rc.id, rc.entryJson, Date.now());
    await store.publish({ t: "devices", room: rc.room });
  }

  async function registerController(socket, msg) {
    const rc = socket.rc;
    // The browser owns its identity (sessionStorage + Web Locks), so a reconnect keeps fromId stable
    // and the host's guards keep matching. Unknown or invalid ids are server-generated.
    const clientId = isValidId(msg.clientId) ? msg.clientId : randomUUID().replace(/-/g, "");
    await adopt(socket, "controller", clientId);
    send(socket, { type: "registered", clientId, iceServers: issueIceServers(config.turn, clientId) });
    send(socket, { type: "device-list", devices: await deviceList(rc.room) });

    // Re-registration of a paired controller (forced reconnect): refresh the lease right away so the
    // gap never depends on the tick cadence.
    const deviceId = await store.pairOfController(rc.room, clientId);
    if (deviceId && await store.pairRefresh(rc.room, deviceId, clientId, config.pairLeaseSec)) rc.peerDeviceId = deviceId;

    await drainMailbox(socket);
  }

  // Binds (role, id) to the socket and claims the id's single ownership in the store. Any previous
  // socket with the same id, here or on another instance, is fenced out immediately and closed as
  // soon as the eviction event reaches it.
  async function adopt(socket, role, id) {
    const rc = socket.rc;
    rc.role = role;
    rc.id = id;
    const r = roomState(rc.room);
    const table = role === "host" ? r.hosts : r.controllers;
    const previous = table.get(id);
    if (previous && previous !== socket) evict(previous, "replaced (local)");
    table.set(id, socket);
    await store.claimOwner(rc.room, role, id, rc.token, OWNER_TTL_SEC);
    // Fire-and-forget: other instances holding a stale socket for this id close it (see onStoreEvent).
    store.publish({ t: "evict", room: rc.room, role, id, epoch: rc.epoch, instance: instanceId }).catch(() => {});
  }

  function evict(socket, why) {
    const rc = socket.rc;
    if (!rc || rc.evicted) return;
    rc.evicted = true;
    verbose(`[ws] evicting ${rc.role}:${rc.id} — ${why}`);
    try { socket.close(CLOSE_REPLACED, "replaced"); } catch { /* ignore */ }
  }

  async function relay(socket, msg) {
    const rc = socket.rc;
    if (!rc.role) return sendError(socket, "not-registered");
    const targetId = isValidId(msg.targetId) ? msg.targetId : null;
    if (!targetId) return sendError(socket, "missing-targetId");
    if (msg.sessionId !== undefined && msg.sessionId !== null && msg.sessionId !== "" && !isValidSessionId(msg.sessionId))
      return sendError(socket, "invalid-sessionId");

    if (msg.type === "offer") {
      if (rc.role !== "controller") return sendError(socket, "offer-from-host");
      const target = await store.registryGet(rc.room, targetId);
      if (!target || isStale(target, Date.now())) return sendError(socket, "device-not-found");

      // One target per controller: switching devices without an explicit disconnect releases the old
      // lease instead of leaving the previous host "busy" until expiry.
      if (rc.peerDeviceId && rc.peerDeviceId !== targetId) {
        if (await store.pairRelease(rc.room, rc.peerDeviceId, rc.id)) await store.publish({ t: "devices", room: rc.room });
        rc.peerDeviceId = null;
      }

      // Pairing arbitration: one atomic SET NX-style lease. Two controllers offering in the same
      // instant get exactly one winner; the loser is told device-busy and never reaches the host.
      const lease = await store.pairAcquire(rc.room, targetId, rc.id, config.pairLeaseSec);
      if (!lease.ok) return send(socket, { type: "error", message: "device-busy" });
      rc.peerDeviceId = targetId;
      await store.publish({ t: "devices", room: rc.room });
    }

    const forwarded = { ...msg, fromId: rc.id };
    delete forwarded.targetId;
    if (typeof forwarded.reason === "string") forwarded.reason = sanitizeShort(forwarded.reason, 64, "disconnect");

    if (msg.type === "disconnect") {
      // Explicit disconnect from a live socket is the ONLY client action that releases the lease.
      let released = false;
      if (rc.role === "controller") {
        released = await store.pairRelease(rc.room, targetId, rc.id);
        if (rc.peerDeviceId === targetId) rc.peerDeviceId = null;
      } else {
        released = await store.pairRelease(rc.room, rc.id, targetId);
        if (rc.pairedWith === targetId) rc.pairedWith = null;
      }
      if (released) await store.publish({ t: "devices", room: rc.room });
      if (rc.role === "controller") {
        // Do not queue a disconnect for a host that is already gone — nothing would read it.
        const target = await store.registryGet(rc.room, targetId);
        if (!target) return;
      }
    }

    await store.mailboxPush(rc.room, targetId, JSON.stringify(forwarded), config.mailboxTtlSec, config.mailboxMax);
    await store.publish({ t: "wake", room: rc.room, id: targetId });
  }

  // ───────── mailbox delivery ─────────

  async function drainMailbox(socket) {
    const rc = socket.rc;
    if (rc.draining) { rc.drainAgain = true; return; }
    rc.draining = true;
    try {
      do {
        rc.drainAgain = false;
        let items;
        do {
          if (socket.readyState !== OPEN || rc.evicted) return;
          // Fenced before and after the pop: a socket replaced elsewhere must not take messages
          // meant for its successor. LPOP is atomic, so whatever was popped is ours to restore.
          if (!await store.ownerIs(rc.room, rc.role, rc.id, rc.token)) { evict(socket, "lost ownership"); return; }
          items = await store.mailboxDrain(rc.room, rc.id, DRAIN_BATCH);
          if (items.length === 0) break;
          if (socket.readyState !== OPEN || rc.evicted || !await store.ownerIs(rc.room, rc.role, rc.id, rc.token)) {
            // Popped but must not deliver — put them back for whoever owns this id now.
            await store.mailboxUnshift(rc.room, rc.id, items).catch(() => {});
            if (!rc.evicted) evict(socket, "lost ownership");
            return;
          }
          for (const raw of items) deliver(socket, raw);
        } while (items.length === DRAIN_BATCH);
      } while (rc.drainAgain);
    } finally {
      rc.draining = false;
    }
  }

  function deliver(socket, raw) {
    const rc = socket.rc;
    const colon = raw.indexOf(":");
    const seq = colon > 0 ? Number(raw.slice(0, colon)) : NaN;
    const json = colon > 0 ? raw.slice(colon + 1) : raw;
    if (Number.isFinite(seq)) {
      if (seq <= rc.lastSeq) return;                        // duplicate drain — drop
      rc.lastSeq = seq;
    }
    if (rc.role === "host" && json.includes('"type":"disconnect"')) rc.pairedWith = null;   // no extra controller-gone later
    if (socket.readyState === OPEN) socket.send(json);
  }

  // ───────── store events (doorbell / eviction / device-list invalidation) ─────────

  function onStoreEvent(ev) {
    if (closed || !ev || typeof ev.room !== "string") return;
    const r = rooms.get(ev.room);
    switch (ev.t) {
      case "wake": {
        if (!r) return;
        const s = r.hosts.get(ev.id) || r.controllers.get(ev.id);
        if (s) drainMailbox(s).catch(e => log(`[drain] ${e.message}`));
        return;
      }
      case "evict": {
        if (!r) return;
        const s = (ev.role === "host" ? r.hosts : r.controllers).get(ev.id);
        if (s && !(ev.instance === instanceId && ev.epoch === s.rc.epoch)) {
          (ev.role === "host" ? r.hosts : r.controllers).delete(ev.id);
          evict(s, `replaced by instance ${ev.instance}`);
        }
        return;
      }
      case "devices":
        scheduleDeviceList(ev.room);
        return;
      default:
        return;
    }
  }

  // Coalesces bursts (register + set-status + lease) into one list read per room.
  function scheduleDeviceList(room) {
    const r = roomState(room);
    if (r.listTimer) return;
    r.listTimer = setTimeout(async () => {
      r.listTimer = null;
      if (r.controllers.size === 0) return;
      try {
        const payload = JSON.stringify({ type: "device-list", devices: await deviceList(room) });
        for (const s of r.controllers.values()) if (s.readyState === OPEN) s.send(payload);
      } catch (e) { log(`[devices] ${e.message}`); }
    }, 100);
  }

  // ───────── registry read ─────────

  function isStale(rec, now) {
    let timeoutMs = config.deviceTimeoutMs;
    try { const e = JSON.parse(rec.entryJson); if (e.timeoutMs) timeoutMs = e.timeoutMs; } catch { /* default */ }
    return now - rec.seenMs > timeoutMs;
  }

  async function deviceList(room) {
    const { entries, seen, pairs } = await store.registryReadAll(room);
    const now = Date.now();
    const devices = [];
    const stale = [];
    for (const [id, json] of Object.entries(entries)) {
      let e;
      try { e = JSON.parse(json); } catch { stale.push({ id, seenMs: seen[id] || 0 }); continue; }
      const timeoutMs = e.timeoutMs || config.deviceTimeoutMs;
      if (now - (seen[id] || 0) > timeoutMs) { stale.push({ id, seenMs: seen[id] || 0 }); continue; }
      devices.push({
        deviceId: id,
        deviceName: e.deviceName,
        platform: e.platform,
        status: (e.status === "busy" || pairs[id]) ? "busy" : "available",
      });
    }
    if (stale.length) {
      // Compare-and-delete: a host that heart-beats between our read and this delete survives.
      store.registryRemoveIfStale(room, stale).then((n) => {
        if (n > 0) for (const { id } of stale) log(`[registry] host timed out: ${id} room=${room}`);
      }).catch(() => {});
    }
    devices.sort((a, b) => a.deviceName.localeCompare(b.deviceName) || a.deviceId.localeCompare(b.deviceId));
    return devices;
  }

  // ───────── per-instance maintenance tick ─────────

  async function tick() {
    if (closed) return;
    const now = Date.now();

    // The subscriber connection has no outbound traffic of its own, and providers reap idle ones.
    await store.keepalive();

    // Dead-TCP detection. terminate() is not "device left": the client reconnects and re-registers.
    for (const s of sockets) {
      if (s.rc?.isAlive === false) { try { s.terminate(); } catch { /* ignore */ } continue; }
      if (s.rc) s.rc.isAlive = false;
      try { s.ping(); } catch { /* ignore */ }
    }

    // Refresh ownership of live registered sockets; drop those that lost ownership elsewhere.
    for (const s of sockets) {
      const rc = s.rc;
      if (!rc || !rc.role || rc.evicted || s.readyState !== OPEN) continue;
      if (!await store.refreshOwner(rc.room, rc.role, rc.id, rc.token, OWNER_TTL_SEC)) evict(s, "lost ownership (tick)");
    }

    for (const [room, r] of rooms) {
      // Controllers: keep the lease alive while their socket lives here, and notice a dead host.
      for (const s of r.controllers.values()) {
        const rc = s.rc;
        if (!rc.peerDeviceId || s.readyState !== OPEN) continue;
        const deviceId = rc.peerDeviceId;
        const host = await store.registryGet(room, deviceId);
        if (!host || isStale(host, now)) {
          // Only here — with a stale lastSeen — is the host declared gone. Never on socket close.
          send(s, { type: "disconnect", fromId: deviceId, reason: "host-timeout" });
          await store.pairRelease(room, deviceId, rc.id);
          rc.peerDeviceId = null;
          await store.publish({ t: "devices", room });
          continue;
        }
        if (!await store.pairRefresh(room, deviceId, rc.id, config.pairLeaseSec)) rc.peerDeviceId = null;   // lost to expiry/other
      }

      // Hosts: a vanished lease means no instance anywhere still holds that controller's socket.
      for (const s of r.hosts.values()) {
        const rc = s.rc;
        if (s.readyState !== OPEN) continue;
        const owner = await store.pairOwner(room, rc.id);
        if (rc.pairedWith && !owner) {
          send(s, { type: "disconnect", fromId: rc.pairedWith, reason: "controller-gone" });
          log(`[pair] lease expired: controller ${rc.pairedWith} gone from host ${rc.id}`);
          rc.pairedWith = null;
          await store.publish({ t: "devices", room });
        } else if (owner) {
          rc.pairedWith = owner;
        }
      }
    }
  }

  // ───────── HTTP (health + debug registry) ─────────

  async function handleHttp(req, res) {
    let url;
    try { url = new URL(req.url || "/", "http://localhost"); } catch { url = new URL("http://localhost/"); }
    const path = url.pathname.replace(/\/+$/, "") || "/";
    const origin = req.headers.origin;
    const cors = {};
    if (typeof origin === "string" && origin && (!config.allowedOrigins || config.allowedOrigins.has(origin.toLowerCase().replace(/\/+$/, ""))))
      cors["access-control-allow-origin"] = origin;

    if (req.method === "OPTIONS") {
      res.writeHead(204, { ...cors, "access-control-allow-methods": "GET", "access-control-allow-headers": "x-signaling-token" });
      return res.end();
    }

    if (path === "/devices" || path === "/api/devices") {
      const room = roomFromRequest(req, url);
      if (!room) { res.writeHead(401, { ...cors, "content-type": "application/json" }); return res.end('{"error":"unauthorized"}'); }
      try {
        const devices = await deviceList(room);
        res.writeHead(200, { ...cors, "content-type": "application/json", "cache-control": "no-store" });
        return res.end(JSON.stringify(devices));
      } catch (e) {
        res.writeHead(503, { ...cors, "content-type": "application/json" });
        return res.end(JSON.stringify({ error: "store-unavailable", detail: e.message }));
      }
    }

    // One curl that says whether the deployment is actually wired up (see README §2.3).
    if (path === "/health" || path === "/api/health") {
      if (!roomFromRequest(req, url)) { res.writeHead(401, { ...cors, "content-type": "application/json" }); return res.end('{"error":"unauthorized"}'); }
      if (storeStatus.state === "pending") await verifyStore();
      const last = store.lastSelfTest ? store.lastSelfTest() : null;
      let ping = null;
      try { const t0 = Date.now(); await store.ping(); ping = Date.now() - t0; } catch { /* reported via state */ }

      const body = {
        ok: storeStatus.state === "ok" || storeStatus.state === "degraded",
        state: storeStatus.state,
        detail: storeStatus.detail,
        store: store.kind,
        redisUrlSource: config.redisUrlSource,
        ping,
        pubsub: last?.pubsub ?? "unknown",
        lua: last?.lua ?? "unknown",
        misconfigured: config.misconfigured?.slug ?? null,
        misconfiguredDetail: config.misconfigured?.detail ?? null,
        instance: instanceId,
        vercel: config.onVercel,
        region: config.region || null,
        deviceTimeoutSec: config.deviceTimeoutMs / 1000,
        pairLeaseSec: config.pairLeaseSec,
        tickMs: config.tickMs,
        rooms: rooms.size,
        ...localCounts(),
      };
      res.writeHead(body.ok ? 200 : 503, { ...cors, "content-type": "application/json", "cache-control": "no-store" });
      return res.end(JSON.stringify(body, null, 2) + "\n");
    }

    const { localHosts, localControllers } = localCounts();
    const state = config.misconfigured ? `MISCONFIGURED:${config.misconfigured.slug}` : storeStatus.state;
    res.writeHead(200, { ...cors, "content-type": "text/plain; charset=utf-8", "cache-control": "no-store" });
    res.end(`remote-control-signaling ${state} — instance=${instanceId} store=${store.kind} localHosts=${localHosts} localControllers=${localControllers}\n`);
  }

  // Room the request is authorised for, or null. Open mode (no token configured) means "default".
  function roomFromRequest(req, url) {
    if (config.rooms.open) return "default";
    const token = url.searchParams.get("token") || headerToken(req);
    return (token ? config.rooms.byToken.get(token) : undefined) ?? null;
  }

  function localCounts() {
    let localHosts = 0, localControllers = 0;
    for (const r of rooms.values()) { localHosts += r.hosts.size; localControllers += r.controllers.size; }
    return { localHosts, localControllers };
  }

  // ───────── helpers ─────────

  function roomState(room) {
    let r = rooms.get(room);
    if (!r) { r = { hosts: new Map(), controllers: new Map(), listTimer: null }; rooms.set(room, r); }
    return r;
  }

  function send(socket, obj) {
    if (socket.readyState === OPEN) socket.send(JSON.stringify(obj));
  }

  function sendError(socket, message) {
    send(socket, { type: "error", message });
  }

  return { attach, close, handleHttp, verifyStore, instanceId, deviceList, _tick: tick };
}
