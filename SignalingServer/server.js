// Local / self-hosted entry point: the same signaling logic as api/signaling.js, listening on :8787.
//
//   npm start                     → ws://0.0.0.0:8787  (in-memory store — LAN development)
//   REDIS_URL=redis://… npm start → same behaviour as the Vercel deployment, any number of instances
//
// Any path upgrades (/, /api/signaling, …) so the URL shape used against Vercel also works here.
//
// Env (see src/config.js for the full list):
//   PORT, HOST, TLS_CERT / TLS_KEY (PEM paths → wss://), REDIS_URL, DEVICE_TIMEOUT, PAIR_LEASE_SECONDS,
//   ALLOWED_ORIGINS, ROOM_TOKEN / ROOM_TOKENS, TURN_URLS / TURN_SECRET, LOG=verbose

import http from "node:http";
import https from "node:https";
import fs from "node:fs";
import { WebSocketServer } from "ws";
import { bootstrap, log } from "./src/bootstrap.js";

const PORT = Number(process.env.PORT || 8787);
const HOST = process.env.HOST || "0.0.0.0";

const { config, store, signaling } = bootstrap();

const requestHandler = (req, res) => {
  signaling.handleHttp(req, res).catch((e) => {
    res.writeHead(500, { "content-type": "text/plain" });
    res.end(`error: ${e.message}\n`);
  });
};

const useTls = !!(process.env.TLS_CERT && process.env.TLS_KEY);
const server = useTls
  ? https.createServer({ cert: fs.readFileSync(process.env.TLS_CERT), key: fs.readFileSync(process.env.TLS_KEY) }, requestHandler)
  : http.createServer(requestHandler);

const wss = new WebSocketServer({ server, maxPayload: config.maxMessageBytes });
signaling.attach(wss);

await signaling.verifyStore();

server.listen(PORT, HOST, () => {
  log(`[signaling] listening on ${useTls ? "wss" : "ws"}://${HOST}:${PORT}  (paths: /, /api/signaling; GET /devices, /api/devices)`);
});

for (const sig of ["SIGINT", "SIGTERM"]) {
  process.on(sig, async () => {
    log(`[signaling] ${sig} — shutting down`);
    await signaling.close();
    await store.close();
    server.close(() => process.exit(0));
    setTimeout(() => process.exit(0), 1000).unref();
  });
}
