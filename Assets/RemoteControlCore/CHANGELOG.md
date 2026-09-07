# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.0.0] - 2026-09-07

Full write-up: [REMOTE_CONTROLLER.md](../../REMOTE_CONTROLLER.md).

### Added
- **Host → controller return channel.** `HostMessage` versioned envelope (`state` / `snapshot` / `capture` / `ack`,
  `schemaVersion` 1) with `ToJson` / `FromJson`. `VisionProWebRtcHost.TrySend(...)`, `SetState`, `CaptureState`;
  new SO channel `HostMessageSendChannel` (host input) and `HostMessageReceivedChannel` (controller output, raised
  by `WebGLRemoteTransport`, plus `OnHostMessage` event). Full state snapshot right after the DataChannel opens,
  incremental `state` afterwards; capture state (`starting` / `streaming` / `stopped` / `error:<code>`) reported
  independently of the DataChannel.
- `RemoteCommand.requestId` (optional) + `ack` reply; `WebGLRemoteTransport.Auto Request Id` option.
- `NetworkConfig.SignalingToken` + `SignalingConnectUrl` (`?token=…` appended for both clients).
- `NetworkConfig.IceServerEntry` (`urls`, `username`, `credential`) list and `IceServerEntries`; TURN credentials
  reach `RTCPeerConnection` in the browser and one `RTCIceServer` per entry on visionOS.
- Signaling server issues short-lived TURN credentials (`TURN_URLS` / `TURN_SECRET`, coturn REST scheme); the
  browser merges them into its ICE configuration.
- Signaling server: token / rooms (`ROOM_TOKEN`, `ROOM_TOKENS`), `Origin` allowlist (`ALLOWED_ORIGINS`), input
  validation, explicit `unauthorized` / `origin-not-allowed` errors surfaced on `LogChannel` (close codes 4401 / 4403).
- Signaling server runs as a **Vercel Function** (WebSocket, Fluid compute, `maxDuration: 300`) with all state in
  **Redis** (registry, pairing lease, per-recipient mailboxes, pub/sub doorbell); `api/signaling.js`, `api/devices.js`,
  `vercel.json`; local `server.js` kept (in-memory store when `REDIS_URL` is unset). 25 tests + soak script.
- Signaling server provisioning safety net: the Redis connection string is read from `REDIS_URL`, `KV_URL`,
  `REDIS_TLS_URL` or `UPSTASH_REDIS_URL` (whatever the Vercel Marketplace integration injected); a REST URL is
  refused with an actionable message; a missing store on Vercel is fatal instead of silently starting an
  in-memory instance, and every client is answered `server-misconfigured:<slug>` so the reason reaches
  `LogChannel`. Boot self-test pings Redis, verifies the pub/sub doorbell and dry-runs every Lua script.
  New `GET /api/health?token=…` returns the whole verdict as JSON; the `/` status line no longer claims `ok`
  without checking. Subscriber keepalive added (providers reap idle connections).
- Browser controller: stable 128-bit `clientId` (sessionStorage + Web Locks; rotates on close code 4000),
  `pagehide` disconnect (ignoring bfcache), `signaling-rejected` event, identical device lists no longer rebuild the dropdown.
- Host announces `deviceTimeout` in `register-device` (from `NetworkConfig.DeviceTimeout`), so server and asset can no longer drift.
- Editor: **Create Prefabs** creates the two new channels and wires them.
- **Usable as a real package.** New menu item **Tools ▸ Remote Control ▸ WebRTC ▸ Create SO Assets** generates the
  full set of ScriptableObjects (config, channels, registries) in the *consuming* project under
  `Assets/RemoteControl/SO/`. A package installed from a Git URL lives in `Library/PackageCache` and is read-only,
  so the Editor tooling now resolves its asset folders at runtime instead of writing into the package.
- Deployment without a repo import: `SignalingServer/.vercelignore` (keeps `server.js` out of the upload so
  Vercel does not capture it as the server entrypoint) and `npm run deploy` → `npx vercel --prod` from the folder.
- `Deploy~/webgl-vercel.json` — ready-made `Content-Encoding` / `Content-Type` headers for a compressed Unity
  WebGL build hosted on Vercel, verified against a Brotli build with decompression fallback off.
- **[INTEGRATION.md](../../INTEGRATION.md)** — end-to-end guide: deploy the signaling server, install the package,
  generate the assets, build a custom controller UI, set up the Vision Pro host, build and upload WebGL.

### Changed
- **Breaking:** `NetworkConfig.IceServersJson()` returns an array of `RTCIceServer` objects instead of URL strings
  (both bundled consumers accept either format).
- **Breaking:** signaling protocol — `register-controller` carries a client-generated `clientId`; a WebSocket close no
  longer removes a host, releases a pairing or notifies the peer; `disconnect{host-timeout}` only on stale
  heartbeat, new `disconnect{controller-gone}` on pairing-lease expiry. Requires the 2.0 server.
- Signaling URL on Vercel: `wss://<project>.vercel.app/api/signaling` (serialized field `_signalingServerUrl` unchanged).
- `NetworkConfig.DeviceTimeout` default 6 → 15 s; server `DEVICE_TIMEOUT` default 6 → 15 s; server `TICK_MS`
  default 5000 → 10000 (a 30 s pairing lease is still refreshed three times per period, at half the Redis
  command rate); functions pinned to `fra1` in `vercel.json`.
- `NetworkConfig._iceServers` (string[]) migrated automatically to `_iceServerEntries` via `ISerializationCallbackReceiver`.
- `VisionProSignalingClient` connects to `SignalingConnectUrl`, logs the server's close reason.
- Prefabs `RemoteControl_VisionProHost` / `RemoteControl_WebGLClientCore` wired to the new channels (no consumer action).

### Fixed
- Controller `clientId` changed on every signaling reconnect, so the host rejected `ice-candidate` / `disconnect`
  after a Wi-Fi hiccup or a forced socket close.
- A duplicated browser tab could steal the original tab's `answer` and kill its session.

## [1.0.0] - 2026-04-19

### Added
- Core command system with `RemoteCommand` JSON serialization
- Handler-based command dispatch via `ICommandHandler` / `CommandHandlerBase`
- Runtime registries: `CommandTargetRegistry`, `CommandHandlerRegistry`
- `CommandProcessor` for event-driven command routing
- ScriptableObject event channels: `CommandEventChannel`, `StringEventChannel`, `VoidEventChannel`
- `TransportHost` — Unity Transport server (listens for incoming commands)
- `TransportClient` — Unity Transport client (sends commands to host)
- `NetworkConfig` ScriptableObject for port / connection settings
- `NetworkUtility` for local IP discovery
- `CommandTarget` MonoBehaviour for registering scene objects
- `DebugLogUI` for on-screen TMP debug logging
- Editor menu to auto-create all required ScriptableObject assets
- Basic Demo sample with Host scene (cube) and Controller scene (color buttons)
