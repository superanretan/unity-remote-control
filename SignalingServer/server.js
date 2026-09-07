// Minimal signaling server / device registry for Unity Remote Control (WebGL ↔ Vision Pro).
//
// Responsibilities (and nothing more):
//   • hosts register themselves and heartbeat            → device registry
//   • controllers receive the live device list           → discovery
//   • offer / answer / ice-candidate relay               → WebRTC session setup
//   • disconnect relay + socket-close notification       → cleanup / reconnect
//
// RemoteCommand JSON never passes through here — it travels peer-to-peer over the RTCDataChannel.
//
// Env:
//   PORT            listen port (default 8787)
//   HOST            bind address (default 0.0.0.0)
//   DEVICE_TIMEOUT  seconds without heartbeat before a host is dropped (default 6)
//   TLS_CERT / TLS_KEY  PEM paths → serve wss:// instead of ws://
//   LOG=verbose     log every relayed message

import http from "node:http";
import https from "node:https";
import fs from "node:fs";
import { randomUUID } from "node:crypto";
import { WebSocketServer } from "ws";

const PORT = Number(process.env.PORT || 8787);
const HOST = process.env.HOST || "0.0.0.0";
const DEVICE_TIMEOUT_MS = Number(process.env.DEVICE_TIMEOUT || 6) * 1000;
const VERBOSE = process.env.LOG === "verbose";
const MAX_MESSAGE_BYTES = 256 * 1024;

// ───────── state ─────────
/** @type {Map<string, {socket, deviceId, deviceName, platform, status, lastSeen, peerId}>} deviceId → host */
const hosts = new Map();
/** @type {Map<string, {socket, clientId, peerId}>} clientId → controller */
const controllers = new Map();

// ───────── http(s) + ws ─────────
const server = process.env.TLS_CERT && process.env.TLS_KEY
  ? https.createServer({ cert: fs.readFileSync(process.env.TLS_CERT), key: fs.readFileSync(process.env.TLS_KEY) }, healthHandler)
  : http.createServer(healthHandler);

function healthHandler(req, res) {
  if (req.url === "/devices") {
    res.writeHead(200, { "content-type": "application/json", "access-control-allow-origin": "*" });
    res.end(JSON.stringify(deviceList()));
    return;
  }
  res.writeHead(200, { "content-type": "text/plain" });
  res.end(`remote-control-signaling ok — hosts=${hosts.size} controllers=${controllers.size}\n`);
}

const wss = new WebSocketServer({ server, maxPayload: MAX_MESSAGE_BYTES });

wss.on("connection", (socket, req) => {
  socket.role = null;      // "host" | "controller"
  socket.id = null;        // deviceId or clientId
  socket.isAlive = true;
  socket.on("pong", () => { socket.isAlive = true; });

  socket.on("message", (data) => {
    let msg;
    try { msg = JSON.parse(data.toString()); } catch { return sendError(socket, "invalid-json"); }
    if (!msg || typeof msg.type !== "string") return sendError(socket, "missing-type");
    handle(socket, msg);
  });

  socket.on("close", () => onSocketClosed(socket));
  socket.on("error", () => { /* close follows */ });

  log(`[ws] connection from ${req.socket.remoteAddress}`);
});

// ───────── message handling ─────────
function handle(socket, msg) {
  if (VERBOSE) log(`[msg] ${socket.role ?? "?"}:${socket.id ?? "?"} → ${msg.type}`);

  switch (msg.type) {
    // ----- host -----
    case "register-device": {
      const deviceId = typeof msg.deviceId === "string" && msg.deviceId ? msg.deviceId : randomUUID();
      const existing = hosts.get(deviceId);
      if (existing && existing.socket !== socket) {
        // Same device re-registering on a new socket (Wi-Fi hiccup): drop the stale socket quietly.
        existing.socket.role = null;
        try { existing.socket.close(4000, "replaced"); } catch {}
      }
      socket.role = "host";
      socket.id = deviceId;
      hosts.set(deviceId, {
        socket, deviceId,
        deviceName: String(msg.deviceName || "Vision Pro"),
        platform: String(msg.platform || "unknown"),
        status: msg.status === "busy" ? "busy" : "available",
        lastSeen: Date.now(),
        peerId: existing?.socket === socket ? existing.peerId : null,
      });
      send(socket, { type: "registered", deviceId });
      log(`[registry] host registered: "${hosts.get(deviceId).deviceName}" (${deviceId})`);
      broadcastDeviceList();
      return;
    }

    case "heartbeat": {
      const host = socket.role === "host" ? hosts.get(socket.id) : null;
      if (!host) return sendError(socket, "not-registered");
      host.lastSeen = Date.now();
      return;
    }

    case "set-status": {
      const host = socket.role === "host" ? hosts.get(socket.id) : null;
      if (!host) return sendError(socket, "not-registered");
      const status = msg.status === "busy" ? "busy" : "available";
      if (host.status !== status) {
        host.status = status;
        broadcastDeviceList();
      }
      return;
    }

    // ----- controller -----
    case "register-controller": {
      const clientId = randomUUID();
      socket.role = "controller";
      socket.id = clientId;
      controllers.set(clientId, { socket, clientId, peerId: null });
      send(socket, { type: "registered", clientId });
      send(socket, { type: "device-list", devices: deviceList() });
      return;
    }

    case "list-devices": {
      send(socket, { type: "device-list", devices: deviceList() });
      return;
    }

    // ----- relay -----
    case "offer":
    case "answer":
    case "ice-candidate":
    case "disconnect":
      return relay(socket, msg);

    default:
      return sendError(socket, `unknown-type:${msg.type}`);
  }
}

function relay(socket, msg) {
  if (!socket.role) return sendError(socket, "not-registered");
  const targetId = typeof msg.targetId === "string" ? msg.targetId : null;
  if (!targetId) return sendError(socket, "missing-targetId");

  const target = socket.role === "controller" ? hosts.get(targetId) : controllers.get(targetId);
  if (!target || target.socket.readyState !== 1) {
    if (msg.type !== "disconnect") sendError(socket, "device-not-found");
    return;
  }

  // Session bookkeeping: the offer pairs the two sides; disconnect unpairs them.
  if (msg.type === "offer" && socket.role === "controller") {
    const host = target;
    if (host.peerId && host.peerId !== socket.id && controllers.get(host.peerId)?.socket.readyState === 1) {
      return send(socket, { type: "error", message: "device-busy" });
    }
    host.peerId = socket.id;
    controllers.get(socket.id).peerId = host.deviceId;
    if (host.status !== "busy") { host.status = "busy"; broadcastDeviceList(); }
  }

  const forwarded = { ...msg, fromId: socket.id };
  delete forwarded.targetId;
  send(target.socket, forwarded);

  if (msg.type === "disconnect") unpair(socket, /*notifyPeer*/ false);
}

// ───────── lifecycle ─────────
function onSocketClosed(socket) {
  if (socket.role === "host") {
    const host = hosts.get(socket.id);
    if (host && host.socket === socket) {
      hosts.delete(socket.id);
      log(`[registry] host left: "${host.deviceName}" (${host.deviceId})`);
      notifyPeer(host.peerId, controllers, socket.id, "host-disconnected");
      broadcastDeviceList();
    }
  } else if (socket.role === "controller") {
    const ctl = controllers.get(socket.id);
    if (ctl) {
      controllers.delete(socket.id);
      // Tell the paired host the browser tab is gone so it can stop capture immediately.
      notifyPeer(ctl.peerId, hosts, socket.id, "controller-disconnected");
      const host = ctl.peerId ? hosts.get(ctl.peerId) : null;
      if (host && host.peerId === socket.id) {
        host.peerId = null;
        if (host.status !== "available") { host.status = "available"; broadcastDeviceList(); }
      }
    }
  }
}

function unpair(socket, notify) {
  if (socket.role === "controller") {
    const ctl = controllers.get(socket.id);
    const host = ctl?.peerId ? hosts.get(ctl.peerId) : null;
    if (ctl) ctl.peerId = null;
    if (host && host.peerId === socket.id) {
      host.peerId = null;
      if (host.status !== "available") { host.status = "available"; broadcastDeviceList(); }
    }
  } else if (socket.role === "host") {
    const host = hosts.get(socket.id);
    const ctl = host?.peerId ? controllers.get(host.peerId) : null;
    if (ctl) ctl.peerId = null;
    if (host) {
      host.peerId = null;
      if (host.status !== "available") { host.status = "available"; broadcastDeviceList(); }
    }
  }
}

function notifyPeer(peerId, table, fromId, reason) {
  if (!peerId) return;
  const peer = table.get(peerId);
  if (!peer || peer.socket.readyState !== 1) return;
  send(peer.socket, { type: "disconnect", fromId, reason });
  peer.peerId = null;
}

// Heartbeat sweep: drop hosts that stopped heart-beating, ping every socket to detect dead TCP.
setInterval(() => {
  const now = Date.now();
  let changed = false;
  for (const [id, host] of hosts) {
    if (now - host.lastSeen > DEVICE_TIMEOUT_MS) {
      hosts.delete(id);
      changed = true;
      log(`[registry] host timed out: "${host.deviceName}" (${id})`);
      notifyPeer(host.peerId, controllers, id, "host-timeout");
      try { host.socket.close(4001, "heartbeat-timeout"); } catch {}
    }
  }
  if (changed) broadcastDeviceList();

  for (const socket of wss.clients) {
    if (socket.isAlive === false) { socket.terminate(); continue; }
    socket.isAlive = false;
    try { socket.ping(); } catch {}
  }
}, Math.max(1000, Math.min(DEVICE_TIMEOUT_MS / 2, 5000)));

// ───────── helpers ─────────
function deviceList() {
  return [...hosts.values()].map(h => ({
    deviceId: h.deviceId, deviceName: h.deviceName, platform: h.platform, status: h.status,
  }));
}

function broadcastDeviceList() {
  const payload = JSON.stringify({ type: "device-list", devices: deviceList() });
  for (const ctl of controllers.values()) {
    if (ctl.socket.readyState === 1) ctl.socket.send(payload);
  }
}

function send(socket, obj) {
  if (socket.readyState === 1) socket.send(JSON.stringify(obj));
}

function sendError(socket, message) {
  send(socket, { type: "error", message });
}

function log(line) {
  console.log(`${new Date().toISOString()} ${line}`);
}

server.listen(PORT, HOST, () => {
  const scheme = process.env.TLS_CERT ? "wss" : "ws";
  log(`[signaling] listening on ${scheme}://${HOST}:${PORT}  (device timeout ${DEVICE_TIMEOUT_MS / 1000}s)`);
});
