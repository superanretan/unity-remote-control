# Remote Controller 2.0 — return channel, signaling auth, TURN, Vercel

This document collects **every 2.0 change** to the `com.superanretan.remotecontrol` package
(`Assets/RemoteControlCore`) for the WebGL controller ↔ Apple Vision Pro host path. The base architecture is
described in [WEBGL_VISIONOS_REMOTE.md](WEBGL_VISIONOS_REMOTE.md), the step-by-step setup in
[SETUP_HOST_CLIENT.md](SETUP_HOST_CLIENT.md) and the server in
[SignalingServer/README.md](SignalingServer/README.md).

> **Looking for step-by-step instructions** (deploying to Vercel, installing the package in your own project,
> your own controller UI, the Vision Pro host, building and uploading WebGL)? → [INTEGRATION.md](INTEGRATION.md).
> This document describes *what* 2.0 brings and what is breaking, not *how* to deploy it.

The native path (`TransportClient` / `TransportHost` / Unity Transport) and the command core
(`RemoteCommand`, `CommandProcessor`, `CommandHandlerBase`, the registries, the SO channels) work unchanged.
`RemoteCommand` gained one **optional** field (`requestId`) — older hosts and controllers ignore it.

---

## 0. What is breaking, what has to be rewired

| Change | Breaking? | What a consumer must do |
|---|---|---|
| Signaling server rewritten (Vercel + Redis, new protocol `register-controller{clientId}`, `register-device{deviceTimeout}`) | **yes** — the old 1.x `server.js` does not fully work with 2.0 clients (no stable server-side `clientId`, so the session dies on reconnect) | deploy the new `SignalingServer/` (Vercel or `npm start`) and update the URL |
| URL shape on Vercel: `wss://<project>.vercel.app/api/signaling` | yes (value only) | `NetworkConfig.SignalingServerUrl` — the `_signalingServerUrl` field **was not renamed**, so the `remotecontrol.json` override still works |
| `NetworkConfig.IceServersJson()` returns an array of `RTCIceServer` objects instead of an array of strings | **yes** for external callers of that method; the package's `.jslib` and `.mm` accept both formats | nothing, if you only use the package's own components |
| `NetworkConfig._iceServers` (string[]) → `_iceServerEntries` (list of `IceServerEntry`) | no — migrated automatically when the asset loads, old values preserved | save the asset (Ctrl+S) after opening the project so the new shape reaches disk |
| `NetworkConfig.DeviceTimeout` default 6 → **15 s**, and it is now actually sent to the server | no | if your asset had 6, raise it to 15 in the Inspector |
| New `NetworkConfig.SignalingToken` field | no (empty = server in open mode) | enter the server's `ROOM_TOKEN` |
| New SO channels `HostMessageSendChannel`, `HostMessageReceivedChannel` | no — the `RemoteControl_VisionProHost` and `RemoteControl_WebGLClientCore` prefabs have them wired | **only** if you have your own GameObjects carrying `VisionProWebRtcHost` / `WebGLRemoteTransport` outside the prefabs: drag the assets in from `Runtime/DefaultSetup/SO/` |
| `RemoteCommand.requestId` | no (optional, defaults to `""`) | — |

1.x scenes and prefabs open without manual rewiring. The **Tools ▸ Remote Control ▸ WebRTC ▸ Create Prefabs**
menu creates the missing SO channels and rewires the prefabs, should a consumer want to regenerate them.

---

## 1. Host → controller return channel

### 1.1 The `HostMessage` envelope (`Runtime/Core/HostMessage.cs`)

```json
{"messageType":"state","schemaVersion":1,"topic":"navigation","value":"compartment-a","payload":"","requestId":""}
```

| Field | Meaning |
|---|---|
| `messageType` | `state` \| `snapshot` \| `capture` \| `ack` — a receiver **ignores** unknown types (extensibility) |
| `schemaVersion` | `1` today (`HostMessage.CurrentSchemaVersion`) |
| `topic` | what changed (`navigation`, `capture`, or the `commandType` for an `ack`) |
| `value` | primary value |
| `payload` | optional JSON (snapshot entries, error details) |
| `requestId` | for `ack`: a copy of `RemoteCommand.requestId` |

| `messageType` | `topic` | `value` | `payload` |
|---|---|---|---|
| `state` | any | any | optional JSON |
| `snapshot` | `""` | `""` | `{"entries":[{"topic","value","payload"}]}` — full state right after the DataChannel opens |
| `capture` | `capture` | `starting` \| `streaming` \| `stopped` \| `error` | for `error`: the code (`-5801` = consent declined, `-5803` = start failed, `replaykit-unavailable`, `native-unavailable`) |
| `ack` | the command's `commandType` | `dispatched` \| `rejected` | for `rejected`: the reason (`invalid-command`) |

`ToJson()` / `FromJson()` as in `RemoteCommand`. Helpers: `HostMessage.State(topic, value)`,
`HostMessage.Capture(state, detail)`, `HostMessage.Ack(command, result)`, `HostMessage.Snapshot(entries)`,
`msg.SnapshotEntries()`.

**`ack` means "parsed and raised on `CommandReceivedChannel`".** Whether a handler existed and whether it
succeeded is `CommandProcessor`'s business (the core is unchanged). The application reports a handler's
outcome as a `state` — e.g. after a compartment change the handler raises
`HostMessage.State("navigation","compartment-a")`.

### 1.2 Host side (`VisionProWebRtcHost`)

Public API:

```csharp
bool TrySend(HostMessage message);   // false when no DataChannel is open
bool TrySend(string hostMessageJson);
bool SetState(string topic, string value, string payload = "");   // cache + send
void ClearState(string topic);
string CaptureState { get; }         // starting | streaming | stopped | error
```

Input goes through an SO channel — **`HostMessageSendChannel`** (`StringEventChannel`, field
`_hostMessageSendChannel`). The host application raises ready-made JSON:

```csharp
[SerializeField] StringEventChannel _hostMessageSendChannel;   // SO: HostMessageSendChannel.asset

void OnCompartmentChanged(string id) =>
    _hostMessageSendChannel.Raise(HostMessage.State("navigation", id).ToJson());
```

Rules:
- Application code **does not call** `VisionProNativeBridge.SendData`. `TrySend` checks `IsConnected`, while
  sending through the static bridge would bypass that check and tie the domain layer to visionOS.
- Every `state` is **cached per `topic`**, even when nobody is connected. After `datachannel-open` the host
  sends one `snapshot` with every topic plus the current `capture` state, then incremental changes. The
  controller always starts from the full state, with no extra work in the application.
- Capture state is reported **independently** of the DataChannel: `starting` on `StartCapture`, `streaming`
  once frames actually flow (ReplayKit or `VisionCameraStreamer`), `stopped`, and `error` with a code.
  "DataChannel open" is not "video is flowing".
- A command with a non-empty `requestId` gets an `ack` (`_ackCommands`, enabled by default).

New Inspector fields: `Host Message Send Channel`, `Send Snapshot On Connect` (true), `Ack Commands` (true).

### 1.3 Controller side (`WebGLRemoteTransport`)

Every text message from the DataChannel is raised **verbatim** on **`HostMessageReceivedChannel`**
(`StringEventChannel`, field `_hostMessageReceivedChannel`), plus there is a typed
`event Action<HostMessage> OnHostMessage`.

```csharp
[SerializeField] StringEventChannel _hostMessageReceivedChannel;   // SO: HostMessageReceivedChannel.asset

void OnEnable()  => _hostMessageReceivedChannel.OnRaised += OnHostMessage;
void OnDisable() => _hostMessageReceivedChannel.OnRaised -= OnHostMessage;

void OnHostMessage(string json)
{
    var msg = HostMessage.FromJson(json);
    if (msg == null) return;
    if (msg.IsSnapshot) foreach (var e in msg.SnapshotEntries()) Apply(e.topic, e.value);
    else if (msg.IsState)  Apply(msg.topic, msg.value);            // e.g. highlight the active compartment
    else if (msg.IsCapture) _videoBadge.text = msg.value;          // starting / streaming / stopped / error
    else if (msg.IsAck)    _pending.Remove(msg.requestId);
}
```

Request correlation: set `command.requestId` yourself, or enable `Auto Request Id` on
`WebGLRemoteTransport` (**off** by default) — every command without a `requestId` then gets a fresh one
(`NextRequestId()`) and the host answers with an `ack`.

---

## 2. Signaling server authentication

| Element | Where | Value |
|---|---|---|
| Shared token | server: env `ROOM_TOKEN` (or `ROOM_TOKENS=name=token,…` for several rooms) | any random string |
| | Unity: `NetworkConfig.SignalingToken` (field `_signalingToken`) | the same string |
| Origin allowlist | server: env `ALLOWED_ORIGINS=https://<controller>.vercel.app` | a missing `Origin` header (Vision Pro `ClientWebSocket`) is let through — the token is the gate |
| Upgrade rate limit | Vercel Firewall, path `/api/signaling` | e.g. 30 req/min/IP |

How it works:
- Both clients connect to `NetworkConfig.SignalingConnectUrl` = `SignalingServerUrl` + `?token=…`
  (`NetworkConfig.BuildConnectUrl` does not duplicate the token if the URL already carries one).
  `SignalingServerUrl` stays raw, so the `remotecontrol.json` override by the `_signalingServerUrl` field
  name works as before.
- Without a valid token, or with a disallowed Origin, the server accepts the upgrade only long enough to send
  `{"type":"error","message":"unauthorized" | "origin-not-allowed"}` and closes the socket with code
  **4401 / 4403**. Both sides then log on `LogChannel`:
  `[Signaling] ERROR — server rejected the connection: unauthorized. Check NetworkConfig.SignalingToken against the server's ROOM_TOKEN.`
  (the controller additionally shows "Signaling rejected: …" in the panel). A raw HTTP 401 would surface in
  the browser as a silent `code=1006`.
- `fromId` is always stamped from the socket, so impersonating another `deviceId`/`clientId` is impossible.
- Rooms (optional): `ROOM_TOKENS=show=tokA,test=tokB` gives two host-controller pairs on one server, mutually
  invisible in each other's device list. Redis keys are prefixed with the room name.
- Input validation: `deviceName` ≤ 64 characters with control characters stripped and non-strings replaced;
  `deviceId`/`clientId` must match `^[A-Za-z0-9_-]{8,64}$`; `sessionId` is validated; 256 KB per message.

**Being honest about the token:** it ends up in the public WebGL bundle and in the URL. It is obfuscation
against accidental visitors and crawlers, not authentication. Rotate it per event.

The server's required environment variables and deployment behind TLS:
[SignalingServer/README.md](SignalingServer/README.md) §2 (Vercel: `REDIS_URL`, `ROOM_TOKEN`,
`ALLOWED_ORIGINS`, `DEVICE_TIMEOUT`, `PAIR_LEASE_SECONDS`, …) and §8 (self-hosting: `PORT`, `HOST`,
`TLS_CERT`, `TLS_KEY`, or a Caddy/nginx reverse proxy).

---

## 3. TURN with authentication

### 3.1 New format in `NetworkConfig`

```csharp
[Serializable] public class IceServerEntry { public string[] urls; public string username; public string credential; }
IReadOnlyList<IceServerEntry> IceServerEntries   // new
string[] IceServers                              // kept (flat URL list, no credentials)
string IceServersJson()                          // NEW FORMAT, see below
```

`IceServersJson()`:

```json
[{"urls":["stun:stun.l.google.com:19302"]},
 {"urls":["turn:turn.example.com:3478?transport=udp","turns:turn.example.com:5349"],"username":"u","credential":"c"}]
```

- The `.jslib` (`parseIceServers`) passes `username`/`credential` on to `RTCPeerConnection` and still accepts
  the old format (`["stun:…"]`).
- The `.mm` (`RCParseIceServers`) builds **one** `RTCIceServer` per entry —
  `initWithURLStrings:username:credential:` when credentials are present, `initWithURLStrings:` otherwise.
  Previously every URL ended up in a single IceServer.
- Migration: the old `_iceServers` (string[]) is read through `ISerializationCallbackReceiver` and converted
  into entries without credentials; the legacy field is hidden (`HideInInspector`) and cleared on the next
  save. Values in an existing `NetworkConfig.asset` are not lost.

### 3.2 Secrets — short-lived credentials from the signaling server

Static credentials in the asset would end up in the public WebGL build. The server mints them instead
(the coturn `use-auth-secret` / TURN REST API scheme):

```
TURN_URLS=turn:turn.example.com:3478?transport=udp,turns:turn.example.com:5349
TURN_SECRET=<coturn static-auth-secret>
TURN_TTL_SECONDS=3600
```

The `registered` reply carries
`iceServers:[{urls, username:"<expiry>:<id>", credential:base64(HMAC-SHA1(secret, username))}]`.
The browser **merges** them with the `NetworkConfig` entries when creating the `RTCPeerConnection`
(implemented). The secret never leaves the server, and a leaked credential expires after
`TURN_TTL_SECONDS`.

The **Vision Pro** currently uses only the `NetworkConfig` entries: a host on the same network as the TURN
server rarely needs TURN, and the host path is deliberately left untouched. Follow-up (about 15 lines): an
`iceServers` field in `SignalingMessage` → `VisionProSignalingClient` remembers it from `registered` →
`VisionProWebRtcHost.OnOffer` merges the JSON before `CreatePeer`.

On a LAN today TURN is not a blocker — STUN is enough. Going beyond the LAN means TURN on both sides plus the
above.

---

## 4. Signaling on Vercel (WebSocket + Redis)

Details and rationale: [SignalingServer/README.md](SignalingServer/README.md). In short:

- **URL:** `wss://<project>.vercel.app/api/signaling` (the token is appended automatically). Locally it is
  still `ws://<pc-ip>:8787` (`npm start`, with both `/` and `/api/signaling` accepted).
- **Hobby:** every socket is closed after 300 s. The clients reconnect (~1 s) with the same ids; the server
  keeps the registry, the pairing and the message mailboxes in Redis, so **a dropped socket means nothing** —
  neither for the device list nor for the WebRTC session. `disconnect{host-timeout}` is only sent on a stale
  `lastSeen`, and `disconnect{controller-gone}` only after the pairing lease expires (30 s without a refresh
  from any instance).
- **Stable controller `clientId`:** generated in the browser (`crypto.getRandomValues`, 128 bit), kept in
  `sessionStorage` (surviving F5) and protected by a **Web Lock**, so a duplicated tab gets a new id. That
  keeps `fromId` after a reconnect matching the host's `_controllerId`, so the `ice-candidate` / `disconnect`
  guards in `VisionProWebRtcHost` keep working.
- **`DEVICE_TIMEOUT`:** the host sends `NetworkConfig.DeviceTimeout` (15 s) in `register-device`; the
  server's env var is only a fallback. The two values can no longer drift apart.
- Closing the tab: `beforeunload` + `pagehide` (skipping `persisted`) → `disconnect{page-unload}`; the
  fallback is lease expiry; last of all, the host's own detection (ICE / DataChannel).
- Tests: `npm test` (25 scenarios, including a socket lost mid-negotiation, arbitration between two offers,
  duplicate eviction, token/Origin, and Redis variable resolution), plus `npm run test:soak`.

### 4a. Deployment

Step by step, with commands: **[INTEGRATION.md](INTEGRATION.md) §A**. In short: `npx vercel --prod` in the
`SignalingServer` directory (no repo import), a Redis database from the Storage → Marketplace panel, three
environment variables, a redeploy, and verification with `curl /api/health?token=…`.

Without a working Redis the server does not pretend to work: the log gets a `[FATAL]`, `/api/health` returns
503, and every host and controller registration gets `{"type":"error","message":"server-misconfigured:<slug>"}`,
so the reason shows up on Unity's `LogChannel` instead of as an empty device list. The startup self-test pings
Redis, checks the pub/sub doorbell and dry-runs every Lua script. The connection string is read from
`REDIS_URL`, `KV_URL`, `REDIS_TLS_URL` or `UPSTASH_REDIS_URL`, and a REST URL is rejected with instructions on
what to copy instead.

---

## 5. Files

```
New
  Assets/RemoteControlCore/Runtime/Core/HostMessage.cs
  Assets/RemoteControlCore/Runtime/DefaultSetup/SO/HostMessageSendChannel.asset
  Assets/RemoteControlCore/Runtime/DefaultSetup/SO/HostMessageReceivedChannel.asset
  SignalingServer/src/{config,validate,turn,bootstrap,signaling}.js, src/store/{redis,memory}.js
  SignalingServer/api/{signaling,devices}.js, vercel.json, test/*
  REMOTE_CONTROLLER.md
Changed
  Runtime/Network/NetworkConfig.cs            token, IceServerEntry + migration, IceServersJson(), DeviceTimeout 15
  Runtime/Core/RemoteCommand.cs               + requestId
  Runtime/VisionOS/VisionProWebRtcHost.cs     TrySend, HostMessageSendChannel, snapshot, capture state, ack
  Runtime/VisionOS/VisionProSignalingClient.cs SignalingConnectUrl, deviceTimeout in register-device, close-reason logging
  Runtime/VisionOS/SignalingMessage.cs        + deviceTimeout
  Runtime/WebGL/WebGLRemoteTransport.cs       HostMessageReceivedChannel, OnHostMessage, Auto Request Id
  Runtime/WebGL/WebGLDiscoveryClient.cs       SignalingConnectUrl, no dropdown flicker, signaling-rejected
  Runtime/Plugins/WebGL/WebGLRemoteBridge.jslib  clientId, Web Locks, pagehide, object-shaped ICE + TURN from the server
  Runtime/Plugins/visionOS/WebRtcHostBridge.mm   RCParseIceServers (one IceServer per entry)
  Runtime/DefaultSetup/Prefabs/RemoteControl_VisionProHost.prefab, RemoteControl_WebGLClientCore.prefab
  Runtime/DefaultSetup/SO/NetworkConfig.asset
  Editor/RemoteControlSetupBuilder.cs
  SignalingServer/server.js, package.json, README.md
  WEBGL_VISIONOS_REMOTE.md, SETUP_HOST_CLIENT.md, Assets/RemoteControlCore/CHANGELOG.md, package.json (2.0.0)
```

## 6. Release

Package version: `2.2.0` (`Assets/RemoteControlCore/package.json`), released as tag **`vpwebgl2.2`**.
A consumer points at it from `Packages/manifest.json`:

```json
"com.superanretan.remotecontrol": "https://github.com/superanretan/unity-remote-control.git?path=Assets/RemoteControlCore#vpwebgl2.2"
```

Tag history of the 2.x line, all on the `Webgl_VisionPro` branch:

| Tag | What it added |
|---|---|
| `vpwebgl2.0` | host → controller return channel, signaling auth, TURN credentials, Vercel + Redis signaling; WebGL video upload fixed with `texSubImage2D` |
| `vpwebgl2.1` | `VisionCameraStreamer` — streams what an immersive app renders instead of its empty ReplayKit window |
| `vpwebgl2.2` | ScreenCaptureKit backend removed, spectator stream made cheap (960x540 @ 15 fps defaults) |

`vpwebgl2.1.1` and `vpwebgl2.2` point at the same commit; use `vpwebgl2.2`.

Cutting the next release:

```bash
git add -A && git commit -m "<what changed>" && git tag vpwebgl2.3 && git push && git push --tags
```

Bump `"version"` in `Assets/RemoteControlCore/package.json` and add a `CHANGELOG.md` entry in the same
commit, so a consumer resolving the tag gets a matching version number.
