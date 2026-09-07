# Remote Control — Signaling Server

Tiny Node.js WebSocket server that acts as a **device registry + WebRTC signaling relay** between the
Unity **WebGL controller** and the **Vision Pro host**. It never sees `RemoteCommand` JSON — that goes
peer-to-peer over the RTCDataChannel.

## Run locally

```bash
cd SignalingServer
npm install
npm start            # ws://0.0.0.0:8787
```

Options via environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `PORT` | `8787` | Listen port |
| `HOST` | `0.0.0.0` | Bind address |
| `DEVICE_TIMEOUT` | `6` | Seconds without heartbeat before a host disappears from the list |
| `TLS_CERT`, `TLS_KEY` | – | PEM cert/key → serves `wss://` (required when the WebGL page is on HTTPS) |
| `LOG=verbose` | – | Log every relayed message |

Health / debug: `GET /` → status line, `GET /devices` → JSON device list.

## Run behind HTTPS (production / VPS)

A browser page served over **https://** may only open **wss://** sockets. Two easy options:

1. **Reverse proxy** (nginx / Caddy / Cloudflare Tunnel) terminating TLS and forwarding to `ws://127.0.0.1:8787`.
   Caddy example:
   ```
   signal.example.com {
       reverse_proxy 127.0.0.1:8787
   }
   ```
2. **Direct TLS**: `TLS_CERT=/etc/letsencrypt/live/x/fullchain.pem TLS_KEY=/etc/letsencrypt/live/x/privkey.pem PORT=443 npm start`

Then set `NetworkConfig.SignalingServerUrl = wss://signal.example.com` in Unity.

For LAN development use a plain `http://` WebGL page (e.g. `python -m http.server`) and `ws://<pc-ip>:8787`.

## Protocol

All messages are JSON with a `type` field. The server adds `fromId` to relayed messages and strips `targetId`.

| Direction | Message | Notes |
|---|---|---|
| host → server | `{ "type":"register-device", "deviceId", "deviceName", "platform", "status" }` | reply: `{ "type":"registered", "deviceId" }` |
| host → server | `{ "type":"heartbeat" }` | every `NetworkConfig.HeartbeatInterval` s |
| host → server | `{ "type":"set-status", "status":"available"\|"busy" }` | |
| controller → server | `{ "type":"register-controller" }` | reply: `registered` + `device-list` |
| controller → server | `{ "type":"list-devices" }` | reply: `device-list` |
| server → controllers | `{ "type":"device-list", "devices":[{ "deviceId","deviceName","platform","status" }] }` | pushed on every change |
| controller → host | `{ "type":"offer", "targetId", "sessionId", "sdp" }` | pairs the two sockets, host → `busy` |
| host → controller | `{ "type":"answer", "targetId", "sessionId", "sdp" }` | |
| both | `{ "type":"ice-candidate", "targetId", "sessionId", "candidate", "sdpMid", "sdpMLineIndex" }` | |
| both | `{ "type":"disconnect", "targetId", "reason" }` | unpairs, host → `available` |
| server → peer | `{ "type":"disconnect", "fromId", "reason":"host-disconnected"\|"controller-disconnected"\|"host-timeout" }` | sent when the other socket dies |
| server → client | `{ "type":"error", "message" }` | e.g. `device-not-found`, `device-busy` |

One controller per host at a time (MVP). A second `offer` to a busy host gets `error: device-busy`.
