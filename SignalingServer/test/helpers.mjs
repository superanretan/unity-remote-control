// Test harness: N server instances sharing ONE store (MemoryStore, or RedisStore when REDIS_URL is set)
// to reproduce the Vercel topology where host and controller land on different instances.

import http from "node:http";
import { once } from "node:events";
import { WebSocketServer, WebSocket } from "ws";
import { loadConfig } from "../src/config.js";
import { MemoryStore } from "../src/store/memory.js";
import { RedisStore } from "../src/store/redis.js";
import { createSignaling } from "../src/signaling.js";

export const quiet = () => {};

export async function makeStore(config) {
  if (process.env.REDIS_URL) {
    const s = new RedisStore(process.env.REDIS_URL, { keyPrefix: `${config.keyPrefix}-test-${Date.now().toString(36)}`, log: quiet });
    await s.ready();
    return s;
  }
  return new MemoryStore();
}

export async function startCluster({ instances = 2, env = {} } = {}) {
  const config = loadConfig({ DEVICE_TIMEOUT: "3", PAIR_LEASE_SECONDS: "5", TICK_MS: "500", MAILBOX_TTL_SECONDS: "10", ...env });
  const store = await makeStore(config);
  const nodes = [];
  for (let i = 0; i < instances; i++) {
    const signaling = createSignaling({ store, config, log: process.env.TEST_LOG ? console.log : quiet });
    const server = http.createServer((req, res) => signaling.handleHttp(req, res));
    const wss = new WebSocketServer({ server, maxPayload: config.maxMessageBytes });
    signaling.attach(wss);
    server.listen(0, "127.0.0.1");
    await once(server, "listening");
    const port = server.address().port;
    nodes.push({ signaling, server, wss, port, url: `ws://127.0.0.1:${port}/api/signaling`, httpUrl: `http://127.0.0.1:${port}` });
  }
  return {
    config, store, nodes,
    async stop() {
      for (const n of nodes) { await n.signaling.close(); n.wss.close(); n.server.close(); }
      await store.close();
    },
  };
}

/** Minimal promise-based WS client with a message inbox and typed waits. */
export class Client {
  constructor(url, { origin } = {}) {
    this.url = url;
    this.inbox = [];
    this.waiters = [];
    this.closed = new Promise((resolve) => { this._resolveClosed = resolve; });
    this.ws = new WebSocket(url, origin ? { headers: { origin } } : {});
    this.ws.on("message", (data) => {
      const msg = JSON.parse(data.toString());
      const i = this.waiters.findIndex(w => w.pred(msg));
      if (i >= 0) { const [w] = this.waiters.splice(i, 1); w.resolve(msg); }
      else this.inbox.push(msg);
    });
    this.ws.on("close", (code, reason) => this._resolveClosed({ code, reason: reason.toString() }));
    this.ws.on("error", () => {});
  }

  static async open(url, opts) {
    const c = new Client(url, opts);
    await once(c.ws, "open");
    return c;
  }

  send(obj) { this.ws.send(JSON.stringify(obj)); }

  /** Resolve with the first message (already queued or future) matching `pred`; reject after timeout. */
  next(pred, timeoutMs = 3000) {
    if (typeof pred === "string") { const t = pred; pred = (m) => m.type === t; }
    const i = this.inbox.findIndex(pred);
    if (i >= 0) return Promise.resolve(this.inbox.splice(i, 1)[0]);
    return new Promise((resolve, reject) => {
      const w = { pred, resolve };
      this.waiters.push(w);
      setTimeout(() => {
        const j = this.waiters.indexOf(w);
        if (j >= 0) { this.waiters.splice(j, 1); reject(new Error(`timeout waiting for ${pred.name || "message"}; inbox=${JSON.stringify(this.inbox.map(m => m.type))}`)); }
      }, timeoutMs).unref();
    });
  }

  /** Resolves true if a matching message arrives within `ms`, false otherwise (for "must NOT happen" checks). */
  async receives(pred, ms) {
    try { await this.next(pred, ms); return true; } catch { return false; }
  }

  close(code, reason) { this.ws.close(code, reason); return this.closed; }
  terminate() { this.ws.terminate(); return this.closed; }
}

export const sleep = (ms) => new Promise(r => setTimeout(r, ms));

export async function registerHost(url, deviceId, name = "Vision Pro Test", extra = {}) {
  const c = await Client.open(url);
  c.send({ type: "register-device", deviceId, deviceName: name, platform: "visionOS", status: "available", ...extra });
  const reg = await c.next("registered");
  c.heartbeat = setInterval(() => { if (c.ws.readyState === 1) c.send({ type: "heartbeat" }); }, 500);
  c.heartbeat.unref();
  c.stopHeartbeat = () => clearInterval(c.heartbeat);
  return { client: c, deviceId: reg.deviceId };
}

export async function registerController(url, clientId, opts) {
  const c = await Client.open(url, opts);
  c.send({ type: "register-controller", clientId });
  const reg = await c.next("registered");
  await c.next("device-list");
  return { client: c, clientId: reg.clientId };
}

export function id(prefix) {
  return `${prefix}${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`.slice(0, 32).padEnd(12, "x");
}
