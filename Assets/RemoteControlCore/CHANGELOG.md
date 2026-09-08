# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.1.0] - 2026-09-08

### Added
- **`UnityCamera` capture backend + `VisionCameraStreamer`.** ReplayKit and ScreenCaptureKit capture the
  app's *window*. A fully immersive Unity app renders through Compositor Services and never draws into that
  window, so the browser received a steady 21 fps of uniformly dark frames — measured end to end: the GL
  upload was proven correct (a pixel read back out of the Unity texture before the first upload was the
  magenta the app wrote, after it `rgba(37,37,37,255)`), the raw `<video>` overlay was equally black, and
  `webrtc-internals` reported 2446 frames decoded with zero loss. No amount of work downstream fixes a black
  source. The new backend skips the system capture: `VisionCameraStreamer` renders a spectator camera into a
  `RenderTexture`, reads it back with `AsyncGPUReadback` and pushes the pixels through the new
  `VPR_PushFrameBGRA` into a pooled IOSurface-backed `CVPixelBuffer`. By default the camera follows
  `Camera.main`, so the operator sees roughly what the wearer sees; assign `Source Camera` for a fixed view.
  It never touches the XR camera — a `targetTexture` there would break stereo rendering.
- **The ReplayKit path describes its first frame** (pixel format, size, plane count, mean luma). A mean near
  zero says the captured surface itself is black, which is one log line instead of an afternoon.

### Changed
- **The controller asks for H.264 first.** The offer comes from the browser, so the host's
  `preferredCodec` could never win: negotiation landed on VP8 and the Vision Pro encoded 720p in software
  (`libvpx`). `WebGLRemoteBridge` now reorders the video transceiver's codecs with `setCodecPreferences`.

## [2.0.3] - 2026-09-08

### Added
- **Video pipeline diagnostics.** With `texSubImage2D` accepted by the driver the WebGL controller still
  showed a flat grey quad, which is what an untouched `Texture2D` looks like — so the question moved from
  "why is the upload rejected" to "where does the frame actually go". `RemoteVideoView._diagnostics`
  (on by default) fills every freshly created texture with magenta, shows the raw `<video>` overlay, and
  reports once every two seconds which gate in `updateTexture` is dropping frames, including how many
  frames the browser has actually presented through `requestVideoFrameCallback`. The `.jslib` reads one
  pixel back out of `GL.textures[texId]` twice: before the first upload, where it must be the magenta
  Unity wrote (anything else proves `GetNativeTexturePtr` did not hand us that texture), and after the
  first upload, where it must be the frame. A build stamp is logged on init so a stale deployment is
  obvious. Set `_diagnostics` to false to silence all of it.

## [2.0.2] - 2026-09-08

### Fixed
- **The WebGL controller never showed a single video frame.** Unity's WebGL2 backend allocates `Texture2D`
  storage with `glTexStorage2D`, which makes the texture **immutable**, so the per-frame
  `gl.texImage2D(..., videoElement)` in `WebGLRemoteBridge.jslib` was rejected with
  `GL_INVALID_OPERATION: glTexImage2DRobustANGLE: Texture is immutable` — every upload, on every frame.
  The `RawImage` stayed black while the DataChannel worked perfectly, because commands never touch the
  texture path. Frames now go in through `texSubImage2D`, the only legal upload into immutable storage;
  the first frame of each texture is verified with `glGetError` and falls back to `texImage2D` if the
  storage turns out to be mutable (WebGL1).
- **Frames were uploaded against a stale resolution.** `texSubImage2D` requires the source video to match
  the texture exactly, and the visionOS encoder ramps 320x180 → 480x270 → 640x360 → 960x540 → 1280x720 over
  the first seconds of every session. `RemoteVideoView` now passes the allocated size down and the `.jslib`
  skips any frame whose resolution has already moved on, re-emitting `video-size` so the texture is
  reallocated first — the likely source of the one-off
  `GL_INVALID_VALUE: glCopySubTextureCHROMIUM: Offset overflows texture dimensions` as well.
- `RemoteVideoView` clears its cached native texture pointer before `Destroy()`ing the `Texture2D`, so an
  upload can never land in a recycled GL texture id, and the upload path restores the previous texture
  binding and `UNPACK_FLIP_Y_WEBGL` state even when it throws.

## [2.0.1] - 2026-09-08

### Fixed
- **visionOS Xcode wiring never ran.** `RemoteControlVisionOSPostProcessor` resolved the project through
  `PBXProject.GetPBXProjectPath()`, which hardcodes `Unity-iPhone.xcodeproj`; a visionOS build emits
  `Unity-VisionOS.xcodeproj`, so the post-processor logged a warning and returned — no LiveKitWebRTC package,
  no ReplayKit, no Info.plist keys. `WebRtcHostBridge.mm` then failed with `#error "No WebRTC framework found."`
  and a cascade of `Unknown type name 'RC_RTC'`. The project and the app target are now resolved by name with
  a `*.xcodeproj` fallback.
- **LiveKitWebRTC is a dynamic framework** — it is now linked from the app target as well, so Xcode embeds it
  into the `.app` instead of only into `UnityFramework`.
- **`WebRtcHostBridge.mm` did not compile against LiveKitWebRTC.** LiveKit prefixes enums and enum constants
  too (`RTC_OBJC_TYPE` → `LKRTC…`), so the bare `RTCPeerConnectionState`, `RTCSdpSemanticsUnifiedPlan`,
  `RTCDataChannelStateOpen`, `RTCVideoRotation_0`, … now all go through the `RC_RTC()` macro.
- **Signaling server could not be deployed to Vercel.** `package.json` had a `main` field pointing at `server.js`,
  so Vercel's Node detection treated the project as a server app and failed with
  `No entrypoint found in "/vercel/path0"` — `.vercelignore` deliberately withholds `server.js` so the functions in
  `api/` stay in charge. The field is gone and `vercel.json` now declares `"framework": null`.
- **`GET /api/health` returned 404 on Vercel.** The endpoint was implemented inside the shared `handleHttp`, which
  the local `server.js` serves for every path, but a Function only receives the path its file maps to. Added
  `api/health.js` and a `/health` rewrite, plus a test asserting that every path the server answers has a matching
  file under `api/` — behavioural tests cannot see this class of bug.
  Verified against the live deployment: `state: ok`, `store: redis`, `pubsub: ok`, `lua: ok`, which also confirms the
  Lua scripts against a real Redis engine for the first time.

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
