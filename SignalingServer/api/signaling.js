// Vercel Function: WebSocket signaling endpoint.
//   wss://<project>.vercel.app/api/signaling?token=<ROOM_TOKEN>
//
// Pattern documented by Vercel (docs/functions/websockets): http.createServer + ws WebSocketServer,
// `export default server`. Requires Fluid compute; the connection is force-closed at maxDuration
// (300 s on Hobby) — clients reconnect and re-register, state lives in Redis (src/signaling.js).

import http from "node:http";
import { WebSocketServer } from "ws";
import { bootstrap } from "../src/bootstrap.js";

const { config, signaling } = bootstrap();

const server = http.createServer((req, res) => {
  signaling.handleHttp(req, res).catch((e) => {
    res.writeHead(500, { "content-type": "text/plain" });
    res.end(`error: ${e.message}\n`);
  });
});

const wss = new WebSocketServer({ server, maxPayload: config.maxMessageBytes });

// Attach first so no upgrade is missed, then verify the store in the background: an unreachable Redis
// must not stall the cold start, it must be *reported* (GET /api/health, and an explicit error to every
// client that tries to register).
signaling.attach(wss);
signaling.verifyStore();

export default server;
