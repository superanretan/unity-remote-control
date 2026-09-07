# Remote Control — Signaling Server

WebSocket **device registry + WebRTC signaling relay** between the Unity **WebGL controller** and the
**Vision Pro host**. It never sees `RemoteCommand` / `HostMessage` JSON nor media — those go peer-to-peer
over the RTCDataChannel / video track.

Two deployment shapes share the same logic (`src/`):

| | Entry point | State | Use |
|---|---|---|---|
| **Vercel Function** | `api/signaling.js` (WebSocket), `api/devices.js` (GET) | **Redis** (`REDIS_URL`) | production / presentations |
| **Local / self-hosted** | `server.js` on `:8787` | in-memory (or Redis when `REDIS_URL` is set) | LAN development |

```
Vision Pro ──wss──▶ instance A ─┐
                                ├── Redis: registry + pairing lease + mailboxes + doorbell (pub/sub)
browser    ──wss──▶ instance B ─┘
```

---

## 1. Run locally

```bash
cd SignalingServer
npm install
npm start            # ws://0.0.0.0:8787  — in-memory store, single instance
```

Any path upgrades (`ws://<pc-ip>:8787`, `ws://<pc-ip>:8787/api/signaling`), so the URL shape used against
Vercel also works locally. `GET /` → status line, `GET /devices` and `GET /api/devices` → JSON registry.

Point `NetworkConfig.SignalingServerUrl` at `ws://<pc-ip>:8787` and serve the WebGL page over plain
`http://` (a `https://` page may only open `wss://`).

To test the exact production behaviour (several instances, Redis, forced socket closes) locally:

```bash
REDIS_URL=redis://127.0.0.1:6379 PORT=8787 npm start &
REDIS_URL=redis://127.0.0.1:6379 PORT=8788 npm start &
```

Tests: `npm test` (25 scenarios — 4 configuration, 21 end-to-end on two in-process instances;
`REDIS_URL=… npm test` runs the whole suite against a real Redis), `npm run test:soak` (paired session kept
alive while every socket is force-closed every 3 s — a compressed Vercel max-duration cycle).

---

## 2. Deploy on Vercel (Hobby)

### 2.1 Platform facts this design depends on (verified 2026-09 against vercel.com/docs)

- WebSockets in Vercel Functions require **Fluid compute** (default for projects created after 2025-04-23; toggle: Project ▸ Settings ▸ Functions ▸ *Fluid Compute*). `vercel.json` also sets `"fluid": true`.
- A WebSocket is pinned to one function instance, but **new connections are not guaranteed to reach the same instance**. Host and controller usually sit on different instances → all state lives in Redis.
- **Max duration on Hobby: 300 s, default and maximum.** Every WebSocket is closed after 300 s. Clients reconnect (host ~1 s, browser ~1 s) and re-register with the **same** ids; nothing is lost (see §4).
- After a deployment, existing connections stay on the old deployment until they close; new ones go to the new one. Redis is shared, so this is survivable.
- Functions run in one region by default (`iad1`). Hobby may pick any **single** region (`"regions": ["fra1"]` in `vercel.json`).

### 2.2 Redis — provisioning, click by click

Redis is **not another server to run**: it is a managed database you create from the Vercel dashboard, on the
free plan, billed through Vercel. It is required because Vercel Functions are stateless with no sticky
routing — the headset's socket lands on instance A, the browser's on instance B, and those two processes
share nothing. Vercel documents this exact pattern for WebSockets.

1. Project → **Storage** → **Marketplace** → **Upstash for Redis** (or **Redis Cloud**) → Install.
2. Plan **Free**. Region: **the one closest to the function region** — `vercel.json` pins functions to
   `fra1`, so pick `eu-central-1` / Frankfurt. Create.
3. **Connect to Project** → this project → environments **Production** and **Preview**.
4. Settings → **Environment Variables**: check that a value starting with `rediss://` exists.

**Step 4 is the one that bites.** The Upstash integration injects *REST* credentials
(`KV_REST_API_URL`, `KV_REST_API_TOKEN`, and historically `KV_URL`), and the REST API **cannot `SUBSCRIBE`**,
which the cross-instance doorbell needs. The server therefore looks for a TCP connection string under any of
these names, in order:

| Variable | Who sets it |
|---|---|
| `REDIS_URL` | Redis Cloud integration, or you, by hand |
| `KV_URL` | legacy Vercel KV / Upstash integration — usually already a `rediss://` URL |
| `REDIS_TLS_URL` | some providers |
| `UPSTASH_REDIS_URL` | Upstash, depending on integration version |

If none of them holds a `rediss://` URL, open the provider console from the Storage tab, copy the
**TCP / RESP connection string** (`rediss://default:…@….upstash.io:6379`) and add it by hand as `REDIS_URL`.
A REST URL in any of those variables is rejected at boot with an explicit message rather than silently
half-working, and `GET /api/health` tells you which variable was used.

Two more rules that matter:

- **Single-region, not Global.** Global databases serve reads from eventually-consistent replicas, which breaks the two read-your-writes paths we rely on: *offer written by instance A, read immediately by instance B* and *`SET NX` pairing arbitration* (a lagging replica would admit two controllers).
- The server uses **`ioredis` over TCP**, two connections per instance: one subscriber, one for commands.

**Free-tier budget.** An active paired session costs roughly 5–7 K Redis commands per hour (heartbeat every
2 s, device-list poll every 5 s, maintenance tick every 10 s). A one-hour presentation plus a few short test
sessions daily lands near 300 K commands per month — inside Upstash's free 500 K. Redis Cloud's free plan has
no monthly quota but caps throughput at 100 ops/s, which is also far above what this server does.

Keys (prefix `KEY_PREFIX`, default `rc`; one namespace per room):

| Key | Type | Role |
|---|---|---|
| `rc:<room>:registry` | hash `deviceId → JSON{deviceName,platform,status,timeoutMs}` | whole registry in one key |
| `rc:<room>:seen` | hash `deviceId → lastSeen ms` | touched by every heartbeat; staleness decided on read, stale fields `HDEL`ed lazily |
| `rc:<room>:mbox:<id>` | list `"<seq>:<json>"`, TTL `MAILBOX_TTL_SECONDS` | messages waiting for `<id>`; `RPUSH` on relay, `LPOP n` on drain, `LTRIM` to `MAILBOX_MAX` |
| `rc:<room>:seq:<id>` | counter | `INCR` per queued message → dedup stamp |
| `rc:<room>:pair:<deviceId>` | string `clientId`, TTL `PAIR_LEASE_SECONDS` | pairing lease + "busy" arbitration (`SET NX EX`) |
| `rc:<room>:ctl:<clientId>` | string `deviceId`, same TTL | reverse map so a re-registering controller refreshes its lease immediately |
| `rc:<room>:own:<role>:<id>` | string `<instance>:<epoch>`, TTL 1 h (refreshed per tick) | the single live socket allowed to speak for `<id>`; every message and every mailbox drain is fenced on it, so a replaced socket on another instance can neither steal messages nor release the lease before its eviction event arrives |
| `rc:events` | pub/sub | `{t:"wake"\|"devices"\|"evict", room, id, …}` — a doorbell, never a transport |

All compare-and-set paths (lease acquire / refresh-if-owner / release-if-owner, mailbox push+trim+expire) are
Lua scripts, so two instances can never interleave a `GET` and a `SET`.

### 2.3 Project settings

1. Deploy this folder. **No repo import and no Git are required** — the CLI uploads the directory from disk:

```bash
cd SignalingServer && npx vercel --prod
```

   The first run asks you to log in and name the project, and is always a production deployment. `npm run deploy`
   is the same thing. `.vercelignore` keeps `server.js` out of the upload on purpose: Vercel's zero-config Node
   detection would treat a root-level `server.js` as the server entrypoint and route every request to it, bypassing
   `api/signaling.js` and its `maxDuration`.

   If you prefer Git: Add New → Project → import the repo → **Root Directory** = `SignalingServer`, framework
   preset *Other*, no build command.
2. Environment variables (Production + Preview):

| Variable | Required | Default | Meaning |
|---|---|---|---|
| `REDIS_URL` | **yes** | – | `redis://` / `rediss://` TCP URL of the single-region database. Also read from `KV_URL`, `REDIS_TLS_URL`, `UPSTASH_REDIS_URL` (first match wins). A REST URL (`https://…`) is refused — see §2.2. Missing on Vercel = fatal: every client is answered `server-misconfigured:no-redis-url` |
| `ROOM_TOKEN` | recommended | – | shared secret; clients connect with `?token=…` (`NetworkConfig.SignalingToken`). Unset = open mode (anyone with the URL can connect) |
| `ROOM_TOKENS` | optional | – | multi-tenant instead of `ROOM_TOKEN`: `presentation=tokA,test=tokB`. Each token is its own room; rooms never see each other's devices |
| `ALLOWED_ORIGINS` | recommended | any | comma list of browser origins, e.g. `https://controller.vercel.app`. Requests **without** an `Origin` header (Vision Pro `ClientWebSocket`) are allowed — the token is their gate |
| `DEVICE_TIMEOUT` | optional | `15` | seconds without heartbeat before a host leaves the list (fallback when the host does not announce its own `deviceTimeout` — package ≥ 2.0 does) |
| `PAIR_LEASE_SECONDS` | optional | `30` | pairing lease TTL; refreshed every `TICK_MS` by the instance holding the controller socket |
| `TICK_MS` | optional | `10000` | maintenance tick: ping/pong, lease refresh, stale checks, subscriber keepalive |
| `MAILBOX_TTL_SECONDS` / `MAILBOX_MAX` | optional | `45` / `200` | undelivered relay messages: max age / max count per recipient |
| `KEY_PREFIX` | optional | `rc` | Redis key namespace (share one database between deployments) |
| `TURN_URLS`, `TURN_SECRET`, `TURN_TTL_SECONDS` | optional | – | issue ephemeral TURN credentials on registration (see §6) |
| `LOG=verbose` | optional | – | log every message |

3. `vercel.json` already sets `maxDuration: 300` for `api/signaling.js`, `"fluid": true`,
   `"regions": ["fra1"]`, and rewrites `/devices → /api/devices`.
4. **Firewall rate limit** (optional): Project ▸ Firewall ▸ add a rate-limit rule on path `/api/signaling`
   (e.g. 30 requests / minute / IP). Rate limits apply to each WebSocket upgrade request.
5. Redeploy — environment variables only take effect on a new deployment — then verify with one command:

```bash
curl "https://<project>.vercel.app/api/health?token=<ROOM_TOKEN>"
```

Expected: `"state":"ok"`, `"store":"redis"`, `"pubsub":"ok"`, `"lua":"ok"`, `"misconfigured":null`, plus the
`redisUrlSource` telling you which variable was used and `ping` in milliseconds. Anything else is explained
in `detail` / `misconfiguredDetail`. `state` can also be `degraded` (pub/sub down: relay still arrives via the
mailbox on registration, but negotiation is slow) or `error` (Redis unreachable, or a Lua script rejected —
the boot self-test dry-runs every script, so a broken one shows up immediately, not mid-session).

Then `curl "https://<project>.vercel.app/api/devices?token=<ROOM_TOKEN>"` → `[]` before the host starts, one
entry after.

Unity: `NetworkConfig.SignalingServerUrl = wss://<project>.vercel.app/api/signaling`,
`NetworkConfig.SignalingToken = <ROOM_TOKEN>`. Both clients append the token themselves.

---

## 3. Authorization — what it is and what it is not

- `fromId` is always stamped from the socket that registered; a client cannot impersonate another.
- The **token travels in the URL of a public web page** (the WebGL build). It keeps strangers and
  crawlers out; it is *obfuscation*, not authentication. Rotate it per event if it leaks.
- Connections without a valid token / from a disallowed origin are accepted just long enough to send
  `{type:"error",message:"unauthorized" | "origin-not-allowed"}` and are closed with code **4401 / 4403**.
  A raw HTTP 401 would reach the browser as an opaque close code 1006 and the Unity log would stay silent —
  this way `[Signaling] ERROR — server rejected the connection: unauthorized …` shows up on `LogChannel`
  on both the controller and the Vision Pro.
- `deviceName` is limited to 64 chars, control characters stripped, non-strings replaced; `deviceId` /
  `clientId` must match `^[A-Za-z0-9_-]{8,64}$` (otherwise the server assigns one); `sessionId` is validated.
- `GET /api/devices` needs the token too (`?token=` or `x-signaling-token` header) when a token is configured.

---

## 4. How it survives Vercel's 300 s socket limit

The one rule: **a WebSocket closing means nothing.** Not for the registry, not for the pairing, not for the
WebRTC session. Concretely:

| Event | Old server (single process) | This server |
|---|---|---|
| host socket closes | host removed, controller gets `disconnect{host-disconnected}`, status → available | nothing. Host reconnects in ~1 s and re-registers with the same `deviceId`; only a stale `lastSeen` (> `deviceTimeout`) removes it |
| controller socket closes | pairing released, host gets `controller-disconnected`, status → available | nothing. Controller reconnects with the same `clientId` (sessionStorage) and its lease is refreshed immediately on `register-controller` |
| `disconnect{host-disconnected \| host-timeout}` | on socket close / heartbeat sweep | **only** when the host's `lastSeen` is really stale — checked by the instance holding the controller socket. `WebGLRemoteTransport.ShouldRetry` treats these as final, so they must never be spurious |
| pairing ends | socket close, explicit disconnect | explicit `disconnect` from a **live** socket, or lease expiry (`PAIR_LEASE_SECONDS`, no instance anywhere refreshes it). The instance holding the **host** socket notices the vanished lease and sends `disconnect{controller-gone}` |
| relay | direct `socket.send` to the peer | `RPUSH mbox:<target>` + `PUBLISH wake`. The recipient's instance drains on the doorbell **and right after registration** — a message published during a reconnect gap waits in the mailbox instead of being lost |
| `device-list` | broadcast to local sockets | `PUBLISH devices`; every instance reads the registry once and pushes to its local controllers. The browser also polls every 5 s |
| same id registers on a new socket | close old socket (`4000 replaced`) | same, across instances: registration claims the `own:` record, the old socket fails its ownership fence on its next message/drain and is closed (`evict` event or fence); the pairing is **not** released |

Timings: host heartbeat 2 s, `deviceTimeout` 15 s (announced by the host from `NetworkConfig.DeviceTimeout`),
reconnect back-off 1 s → ×2 → 10 s, lease 30 s refreshed every 5 s and on re-registration. In the worst
case (server unreachable for several attempts) the host may blink out of the list; a healthy Vercel
reconnect (~1 s) never gets close to any of these limits.

Closing the tab: `beforeunload` **and** `pagehide` (not when `persisted`, i.e. bfcache) send
`disconnect{page-unload}` so the Vision Pro stops capturing at once; if that frame never leaves the
browser, the lease expires within `PAIR_LEASE_SECONDS` and the host gets `controller-gone`; and the host's
own ICE / DataChannel detection remains the final safety net.

Duplicate tab (Duplicate Tab / Ctrl+click inherits a *copy* of `sessionStorage`): the browser takes a
**Web Lock** named after its `clientId`; if the lock is already held, the duplicate generates a new id.
Should two connections still collide, the evicted one receives close code 4000 and rotates its id.

---

## 5. Protocol

All messages are JSON with a `type`. The server stamps `fromId` on relayed messages and strips `targetId`.
Unchanged from 1.x except where marked **new**.

| Direction | Message | Notes |
|---|---|---|
| host → server | `{ "type":"register-device", "deviceId", "deviceName", "platform", "status", "deviceTimeout" }` | `deviceTimeout` **new** (seconds; 0/absent → `DEVICE_TIMEOUT`). Reply `{ "type":"registered", "deviceId", "iceServers":[…] }` (`iceServers` **new**, may be empty) |
| host → server | `{ "type":"heartbeat" }` | every `NetworkConfig.HeartbeatInterval` s |
| host → server | `{ "type":"set-status", "status":"available"\|"busy" }` | effective status = host says busy **or** a pairing lease exists |
| controller → server | `{ "type":"register-controller", "clientId" }` | `clientId` **new**: browser-generated, stable per tab. Reply `registered{clientId, iceServers}` + `device-list` |
| controller → server | `{ "type":"list-devices" }` | reply `device-list` |
| server → controllers | `{ "type":"device-list", "devices":[{ "deviceId","deviceName","platform","status" }] }` | pushed on change + on request |
| controller → host | `{ "type":"offer", "targetId", "sessionId", "sdp" }` | takes the pairing lease; loser gets `error: device-busy`; unknown/stale host → `error: device-not-found` |
| host → controller | `{ "type":"answer", "targetId", "sessionId", "sdp" }` | |
| both | `{ "type":"ice-candidate", "targetId", "sessionId", "candidate", "sdpMid", "sdpMLineIndex" }` | |
| both | `{ "type":"disconnect", "targetId", "reason" }` | releases the lease **only** when sent by the lease owner (controller) or to the lease owner (host) |
| server → controller | `{ "type":"disconnect", "fromId", "reason":"host-timeout" }` | host's `lastSeen` stale — the only server-originated disconnect towards a controller |
| server → host | `{ "type":"disconnect", "fromId", "reason":"controller-gone" }` | pairing lease expired |
| server → client | `{ "type":"error", "message" }` | `unauthorized`, `origin-not-allowed`, `device-busy`, `device-not-found`, `not-registered`, `missing-targetId`, `invalid-sessionId`, `invalid-json`, `server-unavailable`, `server-misconfigured:<slug>` (deployment has no usable Redis — see §2.2) |

Close codes: `4000 replaced` (another socket registered your id), `4401 unauthorized`, `4403 origin-not-allowed`.

---

## 6. TURN with short-lived credentials (optional)

STUN alone connects peers on the same Wi-Fi. A phone on LTE and a Vision Pro on Wi-Fi need TURN, and
TURN needs `username` / `credential`. Putting static TURN credentials into `NetworkConfig` ships them in
the public WebGL build. Instead, let the signaling server mint them:

```
TURN_URLS=turn:turn.example.com:3478?transport=udp,turns:turn.example.com:5349
TURN_SECRET=<coturn static-auth-secret>
TURN_TTL_SECONDS=3600
```

On every `register-*` the reply carries `iceServers: [{ urls:[…], username:"<expiry>:<id>", credential:base64(HMAC-SHA1(secret, username)) }]`
(coturn `use-auth-secret` / TURN REST API scheme). The browser merges these with `NetworkConfig.IceServerEntries`
when it creates the `RTCPeerConnection`. The Vision Pro currently uses only its `NetworkConfig` entries;
extending `VisionProWebRtcHost` to merge `registered.iceServers` is the documented follow-up
(see `REMOTE_CONTROLLER.md`). The secret never leaves the server; a leaked credential dies after `TURN_TTL_SECONDS`.

---

## 7. Operations

- **Do not deploy during a presentation.** Old connections stay on the old deployment until they close;
  new ones go to the new deployment. Redis is shared so it works, but don't do it in front of an audience.
- Keep the **function region and the Redis region together**; Redis **single-region, never Global**.
- Vercel **Hobby is for non-commercial use** per Vercel's terms. A commercial presentation is a licensing
  question to settle, not a technical one.
- **STUN-only does not connect across networks** (LTE phone ↔ Wi-Fi headset). Package 2.0 can express TURN
  credentials (`NetworkConfig.IceServerEntries`, §6); a TURN server is still needed.
- Watch the function logs for `[registry] host timed out` (host really gone) and `[pair] lease expired`
  (controller vanished without `disconnect`).
- Health: `GET /api/health?token=…` → the full JSON verdict (§2.3 step 5). `GET /` or a non-upgrade
  `GET /api/signaling` → one line: `remote-control-signaling <state> — instance=… store=redis …`.
- Vision Pro sends nothing while its own socket is between reconnects (~1 s every 300 s). ICE candidates
  the native stack generates exactly then are dropped by `VisionProSignalingClient.Send` — negotiation
  normally has many candidates and a 20 s connect timeout with retry, so this has not been observed to
  matter, but it is the one gap the server cannot close.

---

## 8. Self-hosting behind TLS (alternative to Vercel)

A browser page served over **https://** may only open **wss://**. Options:

1. **Reverse proxy** (Caddy / nginx / Cloudflare Tunnel) terminating TLS and forwarding to `ws://127.0.0.1:8787` — recommended.
   ```
   signal.example.com {
       reverse_proxy 127.0.0.1:8787
   }
   ```
2. **Direct TLS**: `TLS_CERT=/etc/letsencrypt/live/x/fullchain.pem TLS_KEY=/etc/letsencrypt/live/x/privkey.pem PORT=443 npm start`.

Run at least with `ROOM_TOKEN` and `ALLOWED_ORIGINS`; add `REDIS_URL` if you run more than one process.
