// End-to-end tests against two server instances sharing one store.
// Run: `npm test`   (in-memory store)   or   `REDIS_URL=redis://… npm test` (real Redis, real cross-instance).

import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { startCluster, Client, registerHost, registerController, sleep, id } from "./helpers.mjs";

let cluster, A, B;

before(async () => {
  cluster = await startCluster({ instances: 2 });
  [A, B] = cluster.nodes;
});
after(async () => { await cluster.stop(); });

test("host on A is listed by a controller on B; HTTP /devices and /api/devices agree", async () => {
  const deviceId = id("dev");
  const host = await registerHost(A.url, deviceId, "Vision Pro Office");
  const ctl = await registerController(B.url, id("ctl"));
  const list = await ctl.client.next(m => m.type === "device-list" && m.devices.some(d => d.deviceId === deviceId));
  const dev = list.devices.find(d => d.deviceId === deviceId);
  assert.equal(dev.deviceName, "Vision Pro Office");
  assert.equal(dev.status, "available");

  for (const path of ["/devices", "/api/devices"]) {
    const res = await fetch(B.httpUrl + path);
    assert.equal(res.status, 200);
    const json = await res.json();
    assert.ok(json.some(d => d.deviceId === deviceId), path);
  }
  host.client.stopHeartbeat(); await host.client.close(); await ctl.client.close();
});

test("full negotiation relays across instances: offer → answer → trickle ICE → disconnect", async () => {
  const deviceId = id("dev");
  const host = await registerHost(A.url, deviceId);
  const ctl = await registerController(B.url, id("ctl"));

  ctl.client.send({ type: "offer", targetId: deviceId, sessionId: "s1", sdp: "v=0 offer" });
  const offer = await host.client.next("offer");
  assert.equal(offer.fromId, ctl.clientId);
  assert.equal(offer.sessionId, "s1");
  assert.equal(offer.targetId, undefined, "targetId is stripped");

  host.client.send({ type: "answer", targetId: ctl.clientId, sessionId: "s1", sdp: "v=0 answer" });
  const answer = await ctl.client.next("answer");
  assert.equal(answer.fromId, deviceId);
  assert.equal(answer.sdp, "v=0 answer");

  host.client.send({ type: "ice-candidate", targetId: ctl.clientId, sessionId: "s1", candidate: "candidate:1", sdpMid: "0", sdpMLineIndex: 0 });
  ctl.client.send({ type: "ice-candidate", targetId: deviceId, sessionId: "s1", candidate: "candidate:2", sdpMid: "0", sdpMLineIndex: 0 });
  assert.equal((await ctl.client.next("ice-candidate")).candidate, "candidate:1");
  assert.equal((await host.client.next("ice-candidate")).candidate, "candidate:2");

  // busy while paired (lease-derived), available after explicit disconnect
  const busy = await ctl.client.next(m => m.type === "device-list" && m.devices.find(d => d.deviceId === deviceId)?.status === "busy");
  assert.ok(busy);
  ctl.client.send({ type: "disconnect", targetId: deviceId, reason: "user" });
  const disc = await host.client.next("disconnect");
  assert.equal(disc.reason, "user");
  assert.equal(disc.fromId, ctl.clientId);
  assert.equal(await cluster.store.pairOwner("default", deviceId), null);

  host.client.stopHeartbeat(); await host.client.close(); await ctl.client.close();
});

test("fromId is stamped from the socket, never trusted from the payload", async () => {
  const deviceId = id("dev");
  const host = await registerHost(A.url, deviceId);
  const ctl = await registerController(B.url, id("ctl"));
  ctl.client.send({ type: "offer", targetId: deviceId, sessionId: "s1", sdp: "x", fromId: "attacker" });
  const offer = await host.client.next("offer");
  assert.equal(offer.fromId, ctl.clientId);
  host.client.stopHeartbeat(); await host.client.close(); await ctl.client.close();
});

test("host socket close (forced reconnect) does NOT notify the controller nor drop the host from the list", async () => {
  const deviceId = id("dev");
  const host = await registerHost(A.url, deviceId);
  const ctl = await registerController(B.url, id("ctl"));
  ctl.client.send({ type: "offer", targetId: deviceId, sessionId: "s1", sdp: "x" });
  await host.client.next("offer");

  host.client.stopHeartbeat();
  await host.client.close(1001, "simulated-vercel-max-duration");
  await sleep(600);   // > one tick
  assert.equal(await ctl.client.receives(m => m.type === "disconnect", 800), false, "no host-disconnected");
  ctl.client.send({ type: "list-devices" });
  const list = await ctl.client.next("device-list");
  assert.ok(list.devices.some(d => d.deviceId === deviceId && d.status === "busy"), "still listed and still busy");

  // Host comes back on the OTHER instance with the same deviceId; pairing is intact.
  const host2 = await registerHost(B.url, deviceId);
  assert.equal(await cluster.store.pairOwner("default", deviceId), ctl.clientId);
  ctl.client.send({ type: "ice-candidate", targetId: deviceId, sessionId: "s1", candidate: "c" });
  assert.equal((await host2.client.next("ice-candidate")).candidate, "c");

  host2.client.stopHeartbeat(); await host2.client.close(); await ctl.client.close();
});

test("controller socket close does NOT release the lease; same clientId re-registers on another instance and keeps the session", async () => {
  const deviceId = id("dev");
  const clientId = id("ctl");
  const host = await registerHost(A.url, deviceId);
  const ctl = await registerController(A.url, clientId);
  ctl.client.send({ type: "offer", targetId: deviceId, sessionId: "s1", sdp: "x" });
  await host.client.next("offer");

  await ctl.client.close(1001, "simulated-vercel-max-duration");
  await sleep(600);
  assert.equal(await host.client.receives(m => m.type === "disconnect", 800), false, "no controller-disconnected");
  assert.equal(await cluster.store.pairOwner("default", deviceId), clientId, "lease still held");

  const ctl2 = await registerController(B.url, clientId);
  assert.equal(ctl2.clientId, clientId);
  // A second controller still sees busy and is rejected.
  const other = await registerController(B.url, id("ctl"));
  other.client.send({ type: "offer", targetId: deviceId, sessionId: "s2", sdp: "x" });
  const err = await other.client.next("error");
  assert.equal(err.message, "device-busy");
  assert.equal(await host.client.receives(m => m.type === "offer", 500), false, "loser's offer never reaches the host");

  // Lease is refreshed by the new instance beyond its original TTL (5 s in tests).
  await sleep(5500);
  assert.equal(await cluster.store.pairOwner("default", deviceId), clientId, "lease refreshed across reconnect");

  host.client.stopHeartbeat(); await host.client.close(); await ctl2.client.close(); await other.client.close();
});

test("two controllers offering at the same instant: exactly one wins, lease points to the winner", async () => {
  const deviceId = id("dev");
  const host = await registerHost(A.url, deviceId);
  const c1 = await registerController(A.url, id("ctl"));
  const c2 = await registerController(B.url, id("ctl"));
  c1.client.send({ type: "offer", targetId: deviceId, sessionId: "a", sdp: "x" });
  c2.client.send({ type: "offer", targetId: deviceId, sessionId: "b", sdp: "x" });

  const offer = await host.client.next("offer");
  const owner = await cluster.store.pairOwner("default", deviceId);
  assert.equal(offer.fromId, owner, "host received the offer of the lease owner");
  const loser = owner === c1.clientId ? c2 : c1;
  const err = await loser.client.next("error");
  assert.equal(err.message, "device-busy");
  assert.equal(await host.client.receives(m => m.type === "offer", 500), false, "only one offer delivered");

  host.client.stopHeartbeat(); await host.client.close(); await c1.client.close(); await c2.client.close();
});

test("message published while the recipient is between sockets is delivered after re-registration", async () => {
  const deviceId = id("dev");
  const clientId = id("ctl");
  const host = await registerHost(A.url, deviceId);
  const ctl = await registerController(B.url, clientId);
  ctl.client.send({ type: "offer", targetId: deviceId, sessionId: "s1", sdp: "x" });
  await host.client.next("offer");

  // Controller loses its socket exactly while the host answers.
  await ctl.client.close(1001, "gap");
  host.client.send({ type: "answer", targetId: clientId, sessionId: "s1", sdp: "late-answer" });
  host.client.send({ type: "ice-candidate", targetId: clientId, sessionId: "s1", candidate: "late-c1" });
  await sleep(300);

  const ctl2 = await registerController(A.url, clientId);      // comes back on the other instance
  assert.equal((await ctl2.client.next("answer")).sdp, "late-answer");
  assert.equal((await ctl2.client.next("ice-candidate")).candidate, "late-c1");

  host.client.stopHeartbeat(); await host.client.close(); await ctl2.client.close();
});

test("host that really dies: gone from the list after DEVICE_TIMEOUT, paired controller gets host-timeout once, new offer gets device-not-found", async () => {
  const deviceId = id("dev");
  const host = await registerHost(A.url, deviceId);
  const ctl = await registerController(B.url, id("ctl"));
  ctl.client.send({ type: "offer", targetId: deviceId, sessionId: "s1", sdp: "x" });
  await host.client.next("offer");

  host.client.stopHeartbeat();
  await host.client.terminate();          // app killed: no disconnect, no more heartbeats
  const disc = await ctl.client.next(m => m.type === "disconnect", 6000);
  assert.equal(disc.reason, "host-timeout");
  assert.equal(disc.fromId, deviceId);
  ctl.client.inbox = [];                  // drop device-list pushes queued before the host went stale
  ctl.client.send({ type: "list-devices" });
  const list = await ctl.client.next("device-list");
  assert.equal(list.devices.some(d => d.deviceId === deviceId), false);

  ctl.client.send({ type: "offer", targetId: deviceId, sessionId: "s2", sdp: "x" });
  assert.equal((await ctl.client.next("error")).message, "device-not-found");
  await ctl.client.close();
});

test("controller that vanishes without disconnect: host gets controller-gone after lease expiry; host stays available for others", async () => {
  const deviceId = id("dev");
  const host = await registerHost(A.url, deviceId);
  const ctl = await registerController(B.url, id("ctl"));
  ctl.client.send({ type: "offer", targetId: deviceId, sessionId: "s1", sdp: "x" });
  await host.client.next("offer");
  await sleep(700);                                     // let the host instance observe the lease

  await ctl.client.terminate();                         // tab killed, no beforeunload
  assert.equal(await host.client.receives(m => m.type === "disconnect", 2000), false, "not before the lease expires");
  const disc = await host.client.next(m => m.type === "disconnect", 6000);
  assert.equal(disc.reason, "controller-gone");
  assert.equal(disc.fromId, ctl.clientId);

  const other = await registerController(A.url, id("ctl"));
  other.client.send({ type: "offer", targetId: deviceId, sessionId: "s2", sdp: "x" });
  assert.equal((await host.client.next("offer")).fromId, other.clientId);
  host.client.stopHeartbeat(); await host.client.close(); await other.client.close();
});

test("reload: same clientId re-pairs; a late disconnect from the OLD socket cannot kill the new session", async () => {
  const deviceId = id("dev");
  const clientId = id("ctl");
  const host = await registerHost(A.url, deviceId);
  const oldTab = await registerController(B.url, clientId);
  oldTab.client.send({ type: "offer", targetId: deviceId, sessionId: "s1", sdp: "x" });
  await host.client.next("offer");

  // New page registers with the same id BEFORE the old socket manages to send its page-unload disconnect.
  const newTab = await registerController(A.url, clientId);
  const evicted = await oldTab.client.closed;
  assert.equal(evicted.code, 4000);
  assert.equal(await cluster.store.pairOwner("default", deviceId), clientId, "eviction did not release the lease");

  newTab.client.send({ type: "offer", targetId: deviceId, sessionId: "s2", sdp: "x" });
  assert.equal((await host.client.next("offer")).sessionId, "s2");
  // Whatever the old socket tries now is ignored (it is evicted) — no disconnect reaches the host.
  try { oldTab.client.send({ type: "disconnect", targetId: deviceId, reason: "page-unload" }); } catch { /* already closed */ }
  assert.equal(await host.client.receives(m => m.type === "disconnect", 800), false);

  host.client.stopHeartbeat(); await host.client.close(); await newTab.client.close();
});

test("ownership fence: with the eviction event delayed, the replaced socket can neither release the lease nor steal mailbox items", async () => {
  const deviceId = id("dev");
  const clientId = id("ctl");
  const host = await registerHost(A.url, deviceId);
  const oldTab = await registerController(B.url, clientId);
  oldTab.client.send({ type: "offer", targetId: deviceId, sessionId: "s1", sdp: "x" });
  await host.client.next("offer");

  // Simulate the pub/sub eviction not reaching instance B yet: swallow "evict" events.
  const realPublish = cluster.store.publish.bind(cluster.store);
  cluster.store.publish = (ev) => ev.t === "evict" ? Promise.resolve() : realPublish(ev);
  try {
    const newTab = await registerController(A.url, clientId);          // claims ownership on A
    assert.equal(oldTab.client.ws.readyState, 1, "old socket still open — eviction event was swallowed");

    // Old socket tries to release the pairing → must be ignored, and it gets closed as 'replaced'.
    oldTab.client.send({ type: "disconnect", targetId: deviceId, reason: "page-unload" });
    assert.equal((await oldTab.client.closed).code, 4000);
    assert.equal(await cluster.store.pairOwner("default", deviceId), clientId, "lease intact");
    assert.equal(await host.client.receives(m => m.type === "disconnect", 500), false, "host saw no disconnect");

    // Messages for the id go to the new owner only.
    host.client.send({ type: "answer", targetId: clientId, sessionId: "s1", sdp: "for-new-tab" });
    assert.equal((await newTab.client.next("answer")).sdp, "for-new-tab");
    await newTab.client.close();
  } finally { cluster.store.publish = realPublish; }
  host.client.stopHeartbeat(); await host.client.close();
});

test("stale-host cleanup is compare-and-delete: a host that comes back between read and delete survives", async () => {
  const deviceId = id("dev");
  const host = await registerHost(A.url, deviceId);
  host.client.stopHeartbeat();
  await sleep(3300);                                                    // stale (cluster DEVICE_TIMEOUT = 3 s)
  // Interleave: refresh lastSeen right after deviceList observed it as stale but before the delete lands.
  const realRemove = cluster.store.registryRemoveIfStale.bind(cluster.store);
  cluster.store.registryRemoveIfStale = async (room, observed) => {
    await cluster.store.registryTouch(room, deviceId, Date.now());     // heartbeat arrives "in between"
    return realRemove(room, observed);
  };
  try {
    const list = await (await fetch(A.httpUrl + "/devices")).json();
    assert.equal(list.some(d => d.deviceId === deviceId), false, "reported stale in this read");
    const again = await (await fetch(A.httpUrl + "/devices")).json();
    assert.ok(again.some(d => d.deviceId === deviceId), "but NOT deleted — the refreshed entry survived");
  } finally { cluster.store.registryRemoveIfStale = realRemove; }
  await host.client.close();
});

test("heartbeat self-heals a lost registry entry", async () => {
  const deviceId = id("dev");
  const host = await registerHost(A.url, deviceId, "Healer");
  await cluster.store.registryRemove("default", [deviceId]);           // simulate Redis flush
  await sleep(700);                                                     // next heartbeat (500 ms in tests)
  const list = await (await fetch(A.httpUrl + "/devices")).json();
  assert.ok(list.some(d => d.deviceId === deviceId && d.deviceName === "Healer"));
  host.client.stopHeartbeat(); await host.client.close();
});

test("a controller switching to another device releases its previous lease", async () => {
  const devA = id("dev"), devB = id("dev");
  const hostA = await registerHost(A.url, devA); const hostB = await registerHost(B.url, devB);
  const ctl = await registerController(A.url, id("ctl"));
  ctl.client.send({ type: "offer", targetId: devA, sessionId: "a", sdp: "x" });
  await hostA.client.next("offer");
  ctl.client.send({ type: "offer", targetId: devB, sessionId: "b", sdp: "x" });
  await hostB.client.next("offer");
  assert.equal(await cluster.store.pairOwner("default", devA), null, "old lease released");
  assert.equal(await cluster.store.pairOwner("default", devB), ctl.clientId);
  hostA.client.stopHeartbeat(); hostB.client.stopHeartbeat();
  await hostA.client.close(); await hostB.client.close(); await ctl.client.close();
});

test("upgrade without a valid ROOM_TOKEN is rejected with an explicit error; token in URL works; rooms are isolated", async () => {
  const secured = await startCluster({ instances: 1, env: { ROOM_TOKENS: "show=tokA,test=tokB" } });
  const [S] = secured.nodes;
  try {
    const bad = new Client(S.url);
    const err = await bad.next("error");
    assert.equal(err.message, "unauthorized");
    assert.equal((await bad.closed).code, 4401);

    const bad2 = new Client(S.url + "?token=wrong");
    assert.equal((await bad2.closed).code, 4401);

    const host = await registerHost(S.url + "?token=tokA", id("dev"), "Show Host");
    const showCtl = await registerController(S.url + "?token=tokA", id("ctl"));
    const testCtl = await registerController(S.url + "?token=tokB", id("ctl"));
    showCtl.client.send({ type: "list-devices" });
    testCtl.client.send({ type: "list-devices" });
    assert.equal((await showCtl.client.next("device-list")).devices.length, 1);
    assert.equal((await testCtl.client.next("device-list")).devices.length, 0, "other room sees nothing");

    assert.equal((await fetch(S.httpUrl + "/api/devices")).status, 401);
    assert.equal((await (await fetch(S.httpUrl + "/api/devices?token=tokA")).json()).length, 1);

    host.client.stopHeartbeat(); await host.client.close(); await showCtl.client.close(); await testCtl.client.close();
  } finally { await secured.stop(); }
});

test("Origin allowlist: disallowed browser origin rejected, allowed origin and header-less (Vision Pro) accepted", async () => {
  const secured = await startCluster({ instances: 1, env: { ALLOWED_ORIGINS: "https://controller.vercel.app" } });
  const [S] = secured.nodes;
  try {
    const bad = new Client(S.url, { origin: "https://evil.example" });
    assert.equal((await bad.next("error")).message, "origin-not-allowed");
    assert.equal((await bad.closed).code, 4403);

    const good = await registerController(S.url, id("ctl"), { origin: "https://controller.vercel.app" });
    const noOrigin = await registerHost(S.url, id("dev"));
    noOrigin.client.stopHeartbeat(); await noOrigin.client.close(); await good.client.close();

    const cors = await fetch(S.httpUrl + "/devices", { headers: { origin: "https://evil.example" } });
    assert.equal(cors.headers.get("access-control-allow-origin"), null);
    const cors2 = await fetch(S.httpUrl + "/devices", { headers: { origin: "https://controller.vercel.app" } });
    assert.equal(cors2.headers.get("access-control-allow-origin"), "https://controller.vercel.app");
  } finally { await secured.stop(); }
});

test("input validation: non-string / oversized deviceName sanitized, bad ids replaced, bad targetId rejected", async () => {
  const host = await Client.open(A.url);
  host.send({ type: "register-device", deviceId: "../x", deviceName: { evil: true }, platform: 42 });
  const reg = await host.next("registered");
  assert.notEqual(reg.deviceId, "../x");
  assert.match(reg.deviceId, /^[a-f0-9]{32}$/);

  const ctl = await registerController(B.url, id("ctl"));
  ctl.client.send({ type: "list-devices" });
  const list = await ctl.client.next("device-list");
  const dev = list.devices.find(d => d.deviceId === reg.deviceId);
  assert.equal(dev.deviceName, "Vision Pro");
  assert.equal(dev.platform, "unknown");

  host.send({ type: "register-device", deviceId: reg.deviceId, deviceName: "A".repeat(500) + "" });
  await host.next("registered");
  ctl.client.send({ type: "list-devices" });
  const list2 = await ctl.client.next("device-list");
  assert.equal(list2.devices.find(d => d.deviceId === reg.deviceId).deviceName.length, 64);

  ctl.client.send({ type: "offer", targetId: 12345, sdp: "x" });
  assert.equal((await ctl.client.next("error")).message, "missing-targetId");
  ctl.client.send({ type: "offer", targetId: reg.deviceId, sessionId: "bad session id!", sdp: "x" });
  assert.equal((await ctl.client.next("error")).message, "invalid-sessionId");
  ctl.client.ws.send("not json");
  assert.equal((await ctl.client.next("error")).message, "invalid-json");

  await host.close(); await ctl.client.close();
});

test("per-device timeout announced by the host is honoured", async () => {
  const deviceId = id("dev");
  const host = await registerHost(A.url, deviceId, "Slow", { deviceTimeout: 6 });   // cluster default is 3 s
  host.client.stopHeartbeat();
  await sleep(3600);
  const res = await (await fetch(A.httpUrl + "/devices")).json();
  assert.ok(res.some(d => d.deviceId === deviceId), "still listed after 3.6 s thanks to deviceTimeout=6");
  await host.client.close();
});

test("GET /api/health reports the store verdict and is token-gated", async () => {
  const secured = await startCluster({ instances: 1, env: { ROOM_TOKEN: "healthtoken" } });
  const [S] = secured.nodes;
  try {
    assert.equal((await fetch(S.httpUrl + "/api/health")).status, 401);

    const res = await fetch(S.httpUrl + "/api/health?token=healthtoken");
    assert.equal(res.status, 200);
    const h = await res.json();
    assert.equal(h.ok, true);
    assert.equal(h.state, "ok");
    assert.equal(h.store, "memory");
    assert.equal(h.pubsub, "ok");
    assert.equal(h.misconfigured, null);
    assert.equal(typeof h.instance, "string");
    assert.equal(h.pairLeaseSec, 5);                      // from the test cluster env

    assert.match(await (await fetch(S.httpUrl + "/")).text(), /remote-control-signaling ok/);
  } finally { await secured.stop(); }
});

test("misconfigured deployment (on Vercel without Redis) tells every client why instead of showing an empty list", async () => {
  const broken = await startCluster({ instances: 1, env: { VERCEL: "1" } });
  const [S] = broken.nodes;
  try {
    const ctl = await Client.open(S.url);
    ctl.send({ type: "register-controller", clientId: id("ctl") });
    assert.equal((await ctl.next("error")).message, "server-misconfigured:no-redis-url");

    const host = await Client.open(S.url);
    host.send({ type: "register-device", deviceId: id("dev"), deviceName: "X" });
    assert.equal((await host.next("error")).message, "server-misconfigured:no-redis-url");

    const res = await fetch(S.httpUrl + "/api/health");   // open mode → no token needed
    assert.equal(res.status, 503);
    const h = await res.json();
    assert.equal(h.ok, false);
    assert.equal(h.state, "misconfigured");
    assert.equal(h.misconfigured, "no-redis-url");
    assert.match(h.misconfiguredDetail, /Storage → Marketplace/);
    assert.match(await (await fetch(S.httpUrl + "/")).text(), /MISCONFIGURED:no-redis-url/);

    await ctl.close(); await host.close();
  } finally { await broken.stop(); }
});

test("ephemeral TURN credentials are issued on register when TURN_URLS/TURN_SECRET are set", async () => {
  const turn = await startCluster({ instances: 1, env: { TURN_URLS: "turn:turn.example:3478?transport=udp,turns:turn.example:5349", TURN_SECRET: "s3cret", TURN_TTL_SECONDS: "600" } });
  try {
    const c = await Client.open(turn.nodes[0].url);
    c.send({ type: "register-controller", clientId: id("ctl") });
    const reg = await c.next("registered");
    assert.equal(reg.iceServers.length, 1);
    assert.deepEqual(reg.iceServers[0].urls, ["turn:turn.example:3478?transport=udp", "turns:turn.example:5349"]);
    assert.match(reg.iceServers[0].username, /^\d+:[A-Za-z0-9_-]+$/);
    assert.ok(reg.iceServers[0].credential.length > 10);
    await c.close();
  } finally { await turn.stop(); }
});
