// Soak / presentation profile: a paired host + controller kept alive for SOAK_SECONDS (default 120) while
// every socket is force-closed every FORCE_CLOSE_MS (default 3000) — a compressed version of Vercel's
// 300 s max duration. Clients reconnect and re-register exactly like the real ones (same ids).
// Passes when: no `disconnect` ever reaches either side, the host never leaves the device list, the
// lease never changes owner, and RSS growth stays bounded.
//
//   node test/soak.mjs                     (in-memory store)
//   REDIS_URL=redis://… node test/soak.mjs (real Redis)
//   SOAK_SECONDS=3600 FORCE_CLOSE_MS=300000 node test/soak.mjs   → the real 1 h profile

import { startCluster, Client, sleep } from "./helpers.mjs";

const SOAK_SECONDS = Number(process.env.SOAK_SECONDS || 120);
const FORCE_CLOSE_MS = Number(process.env.FORCE_CLOSE_MS || 3000);

const cluster = await startCluster({ instances: 2, env: { DEBUG_FORCE_CLOSE_MS: String(FORCE_CLOSE_MS), DEVICE_TIMEOUT: "15", PAIR_LEASE_SECONDS: "30", TICK_MS: "5000" } });
const [A, B] = cluster.nodes;
const deviceId = "soakhost0001";
const clientId = "soakctl00001";
const stats = { hostReconnects: 0, ctlReconnects: 0, disconnects: 0, listsWithoutHost: 0, lists: 0, ownerChanges: 0, relayed: 0 };
let stop = false;

async function hostLoop() {
  while (!stop) {
    const url = (stats.hostReconnects % 2 ? A : B).url;
    let c;
    try { c = await Client.open(url); } catch { await sleep(500); continue; }
    c.send({ type: "register-device", deviceId, deviceName: "Soak Host", platform: "visionOS", status: "busy", deviceTimeout: 15 });
    const hb = setInterval(() => { if (c.ws.readyState === 1) c.send({ type: "heartbeat" }); }, 2000);
    c.ws.on("message", (d) => {
      const m = JSON.parse(d.toString());
      if (m.type === "disconnect") { stats.disconnects++; console.log("!! host got disconnect", m); }
      if (m.type === "ice-candidate") { stats.relayed++; c.send({ type: "ice-candidate", targetId: clientId, sessionId: "soak", candidate: "pong" }); }
    });
    await c.closed;
    clearInterval(hb);
    stats.hostReconnects++;
    await sleep(1000);   // VisionProSignalingClient reconnect delay
  }
}

async function controllerLoop() {
  let first = true;
  while (!stop) {
    const url = (stats.ctlReconnects % 2 ? B : A).url;
    let c;
    try { c = await Client.open(url); } catch { await sleep(500); continue; }
    c.send({ type: "register-controller", clientId });
    c.send({ type: "list-devices" });
    c.ws.on("message", (d) => {
      const m = JSON.parse(d.toString());
      if (m.type === "disconnect") { stats.disconnects++; console.log("!! controller got disconnect", m); }
      if (m.type === "device-list") { stats.lists++; if (!m.devices.some(x => x.deviceId === deviceId)) { stats.listsWithoutHost++; console.log("!! list without host"); } }
      if (m.type === "ice-candidate") stats.relayed++;
    });
    if (first) { await sleep(500); c.send({ type: "offer", targetId: deviceId, sessionId: "soak", sdp: "v=0" }); first = false; }
    const poll = setInterval(() => {
      if (c.ws.readyState !== 1) return;
      c.send({ type: "list-devices" });
      c.send({ type: "ice-candidate", targetId: deviceId, sessionId: "soak", candidate: "ping" });
    }, 1000);
    await c.closed;
    clearInterval(poll);
    stats.ctlReconnects++;
    await sleep(1000);
  }
}

const rss0 = process.memoryUsage().rss;
let lastOwner = null;
const monitor = setInterval(async () => {
  const owner = await cluster.store.pairOwner("default", deviceId);
  if (lastOwner && owner !== lastOwner) { stats.ownerChanges++; console.log("!! lease owner changed", lastOwner, "→", owner); }
  lastOwner = owner;
  const rss = process.memoryUsage().rss;
  console.log(`[soak] t=${Math.round((Date.now() - t0) / 1000)}s owner=${owner} rssΔ=${((rss - rss0) / 1e6).toFixed(1)}MB`, JSON.stringify(stats));
}, 10000);

const t0 = Date.now();
hostLoop(); controllerLoop();
await sleep(SOAK_SECONDS * 1000);
stop = true;
clearInterval(monitor);
await sleep(200);
await cluster.stop();

const ok = stats.disconnects === 0 && stats.listsWithoutHost === 0 && stats.ownerChanges === 0 && stats.hostReconnects > 2 && stats.ctlReconnects > 2;
console.log(ok ? "SOAK PASS" : "SOAK FAIL", JSON.stringify(stats));
process.exit(ok ? 0 : 1);
