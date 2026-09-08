# WebGL Controller ↔ Vision Pro Host (WebRTC)

A second transport path that lives **next to** the original native one (IP input → Unity Transport UDP).
Nothing in the command core changed: `RemoteCommand`, `CommandProcessor`, `CommandHandlerBase`,
`CommandHandlerRegistry`, `CommandTarget`, `CommandTargetRegistry` and all ScriptableObject event
channels are used as-is.

> **Step-by-step integration** (deploy the signaling server, install the package in your own project, build a
> custom controller UI, upload the WebGL build): [INTEGRATION.md](INTEGRATION.md).
>
> **2.0:** host → controller return channel (`HostMessage`), signaling token / origin allowlist, TURN credentials,
> and the signaling server as a **Vercel Function + Redis** — see [REMOTE_CONTROLLER.md](REMOTE_CONTROLLER.md) and
> [SignalingServer/README.md](SignalingServer/README.md). Breaking changes are listed there (§0).

```
NATIVE (unchanged)                          WEBGL ↔ VISION PRO (new)
Controller (Win/Android) ─ UTP UDP ─ Host    Browser (Unity WebGL) ─ WebRTC ─ Vision Pro (visionOS)
TransportClient / TransportHost              WebGLRemoteTransport  / VisionProWebRtcHost
IP typed by hand                             Signaling server = device registry + SDP/ICE relay
```

---

## 1. Architecture

### Discovery
| Piece | Where | Role |
|---|---|---|
| `RemoteDiscoveryBase` | Runtime/Discovery | Abstract backend: `Devices`, `DevicesChanged`, `StatusChanged`, `Refresh()` |
| `DiscoveredDevice` | Runtime/Discovery | `{ deviceId, deviceName, platform, status, address }` |
| `WebGLDiscoveryClient` | Runtime/WebGL | Mirrors the server's `device-list` pushes (no UDP in a browser) |
| `NetworkDiscoveryPanel` | Runtime/Discovery | Dropdown + Refresh + Connect + Disconnect + status; knows only `RemoteDiscoveryBase` and the SO channels |
| `DiscoveryBinder` | Runtime/Discovery | Finds the scene's `RemoteDiscoveryBase` at startup and hands it to the panel (prefab stays backend-agnostic) |

A native UDP backend can later derive from `RemoteDiscoveryBase` and put an IP in `DiscoveredDevice.address`; the panel will
raise that IP on `ConnectRequestChannel` and the existing `TransportClient` will connect — zero UI changes.

### Signaling (`SignalingServer/`)
Node.js `ws` server, deployed as a **Vercel Function** (Fluid compute) with all state in **Redis**, or run locally with `npm start`.
Hosts `register-device` + `heartbeat`; controllers `register-controller` with a **stable browser-generated `clientId`** and get
`device-list` pushes; `offer/answer/ice-candidate/disconnect` are relayed through per-recipient **mailboxes** in Redis (pub/sub is only a
doorbell), so host and controller may sit on different function instances and a message sent during a reconnect gap is not lost.
It **never** carries `RemoteCommand`, `HostMessage` or media.

Vercel closes every WebSocket after **300 s** (Hobby max duration). Both clients reconnect (~1 s) and re-register with the same ids;
a socket closing changes nothing — not the registry, not the pairing, not the WebRTC session. A host leaves the list only when its
`lastSeen` is older than the `deviceTimeout` it announced (`NetworkConfig.DeviceTimeout`, 15 s); a pairing ends only on an explicit
`disconnect` from a live socket or when its 30 s lease is not refreshed by any instance (`disconnect{controller-gone}` to the host).
`disconnect{host-timeout}` reaches the controller only for a really stale host. One controller per host: pairing is a `SET NX`
lease, so of two simultaneous offers exactly one wins and the other gets `error: device-busy`.

Auth: `?token=<ROOM_TOKEN>` (`NetworkConfig.SignalingToken`, appended automatically), `ALLOWED_ORIGINS`, input validation.
URL on Vercel: `wss://<project>.vercel.app/api/signaling`.

### WebRTC
* **Browser creates the offer** (`RTCDataChannel "commands"` + `addTransceiver("video", {direction:"recvonly"})`).
* **Host answers** and binds its (still dormant) native video track to the offered transceiver → the answer is `sendonly`.
  No renegotiation is needed when capture starts later.
* **DataChannel open == connected.** The host then starts screen capture; the controller shows the control UI.
* Every session has a `sessionId`; stale answers/candidates from a previous attempt are ignored.

```
WEBGL CONTROLLER                                   VISION PRO HOST
NetworkDiscoveryPanel ─ConnectRequestChannel(deviceId)─▶ WebGLRemoteTransport
      │                                                    │ .jslib: WS offer ──▶ signaling ──▶ VisionProSignalingClient
      │                                                    │                                        │ OnOffer
      │                                                    │◀── answer / ice ◀── signaling ◀── VisionProWebRtcHost ── VPR_HandleRemoteOffer (native)
      │  OnConnectedChannel ◀──────── DataChannel open ────┼────────────────────────────────────────▶ OnClientConnectedChannel
DemoControllerUI ─CommandSendChannel─▶ RemoteCommand.ToJson ─ DataChannel ─▶ RemoteCommand.FromJson ─▶ CommandReceivedChannel ─▶ CommandProcessor
RemoteVideoView ◀─ Video track ◀──── native encoder ◀── RTCVideoSource ◀── spectator camera / ReplayKit
```

### DataChannel payload
Controller → host: exactly the JSON `RemoteCommand.ToJson()` already produces for Unity Transport (`requestId` optional, 2.0):
```json
{"commandType":"set_color","targetId":"demo_cube","value":"#FF0000","payload":"","requestId":""}
```
Host → controller (2.0): `HostMessage` — `state` / `snapshot` / `capture` / `ack`, raised on `HostMessageReceivedChannel`:
```json
{"messageType":"state","schemaVersion":1,"topic":"navigation","value":"compartment-a","payload":"","requestId":""}
```
The host sends a full `snapshot` right after the DataChannel opens, then incremental `state`; `capture` reports
`starting / streaming / stopped / error:<code>` independently of the DataChannel. Details: [REMOTE_CONTROLLER.md §1](REMOTE_CONTROLLER.md).

### Screen capture on visionOS (public APIs only, no passthrough, no enterprise entitlement)
| Backend | OS | Notes |
|---|---|---|
| **ReplayKit** `RPScreenRecorder.startCapture` | visionOS 1.0+ (deprecated in 27) | Default today. Captures the app's rendered content (windows/volumes/immersive frame buffer), passthrough is not included. First start shows Apple's consent alert. |
| **UnityCamera** — `VisionCameraStreamer` → `VPR_PushFrameBGRA` | any | Not a system capture: the app renders a spectator camera and pushes the pixels itself. **The only backend that works for a fully immersive (Metal / Compositor Services) host** — see the warning below. No consent alert. |

> **Measured, not assumed:** a fully immersive Unity app renders through Compositor Services, and that
> composition is not exposed to any system capture API. ReplayKit captures the app's *window*, which such an
> app never draws into, so the stream is a steady 20+ fps of uniformly dark frames — the browser reports
> `framesDecoded` climbing, `packetsLost 0`, and a centre pixel of `rgba(37,37,37,255)`. Nothing downstream can
> fix that. An immersive host must use the `UnityCamera` backend. ReplayKit stays correct for a *windowed*
> visionOS app. The ReplayKit path now logs the pixel format, size and mean luma of its first frame, so this
> is one line in the host log rather than an afternoon.

A ScreenCaptureKit backend used to sit here and was removed in 2.2.0: it needed the Xcode 27 SDK, was never
compiled into a shipped build, and its source selection (screens / applications / windows) had nothing to
offer an immersive app either. Nothing links `ScreenCaptureKit.framework` any more.

`VisionProWebRtcHost → Capture Backend`: `Auto`, `ReplayKit`, `UnityCamera`. **`Auto` resolves to `UnityCamera`
whenever a `VisionCameraStreamer` is in the scene**, and to ReplayKit otherwise — so an immersive host works
without anyone remembering to flip a switch, and a windowed host keeps the system capture.
### What the stream costs the headset

The `UnityCamera` path renders an extra pass and reads it back, so it is paid for in headset frame time. What
keeps it small:

| Decision | Why |
|---|---|
| Camera `enabled = false`, rendered on demand | Only the streamed frames are rendered. At 15 fps that is a quarter of a 60 fps camera's work. |
| Renders at the stream resolution | 960x540 is a quarter of 1080p and under half of 720p, and the encoder never has to rescale. |
| No HDR, no MSAA, no post-processing, no depth texture, shadows off | Post-processing on a second camera is pure waste for a preview. URP's knobs are set through reflection so the package keeps no render-pipeline dependency. |
| `RenderPipeline.SubmitRenderRequest` under an SRP | `Camera.Render()` is a legacy-pipeline call and does not belong in URP. |
| One readback in flight | A late frame is dropped, never queued — the stream stays current and memory stays flat. |
| Vertical flip inside the native row copy | The copy happens anyway, so flipping it costs nothing; a full-screen blit would not. |
| Culling mask | The cheapest saving available: do not render what the operator does not need. |

`NetworkConfig` defaults to **960x540 @ 15 fps, 1200 kbit/s** for exactly this reason, and the fields are
range-capped at 1280x720 @ 30. Both the size and the rate reach the encoder (`adaptOutputFormat`,
`maxBitrateBps`, `maxFramerate`), and one CPU copy per frame remains: readback buffer to pooled
`CVPixelBuffer` (1.6 MB per frame at 540p, dwarfed by the render pass). A zero-copy variant — readback
straight into the IOSurface — is the next step if measurement ever calls for it.

Preferring H.264 on the controller side matters here too: without it the negotiation lands on VP8 and the
headset encodes in software through `libvpx`, which costs far more than the render pass this section is about.

### Video pipeline
```
visionOS (system capture, windowed app):
           CMSampleBuffer ─▶ RTCCVPixelBuffer ─▶ RTCVideoFrame ─▶ RTCVideoSource (adaptOutputFormat WxH@fps) ─▶ encoder ─▶ RTP
visionOS (UnityCamera, immersive app):
           spectator Camera ─▶ RenderTexture ─▶ AsyncGPUReadback ─▶ VPR_PushFrameBGRA ─▶ CVPixelBuffer (pooled, IOSurface) ─▶ same path
browser:   MediaStreamTrack ─▶ hidden <video muted playsinline> ─▶ gl.texSubImage2D(GL.textures[id]) ─▶ Texture2D ─▶ RawImage
```
No frame ever crosses into C# on the host. On the controller `RemoteVideoView` creates an RGBA `Texture2D` of the incoming size and the
`.jslib` copies the newest decoded frame into it (only when `requestVideoFrameCallback` reported a new frame). `_useHtmlOverlay` shows the raw
`<video>` element on top of the canvas as a debugging fallback.

Unity's WebGL2 backend allocates `Texture2D` storage with `glTexStorage2D`, which makes the texture **immutable**, so the upload must be
`texSubImage2D` (a `texImage2D` fails with `GL_INVALID_OPERATION: Texture is immutable` and no frame ever lands). `texSubImage2D` also demands
that the `<video>` matches the texture exactly, so `RemoteVideoView` passes the allocated size down and the `.jslib` skips any frame whose
resolution has already moved on — the encoder ramps 320x180 → 1280x720 over the first seconds of a session, so this happens on every connect.
The `.jslib` verifies `texSubImage2D` on the first frame of every texture it uploads into and falls back to `texImage2D` for that
texture if the storage is mutable (WebGL1).

---

## 2. Files

### New
```
Assets/RemoteControlCore/Runtime/Discovery/DiscoveredDevice.cs
Assets/RemoteControlCore/Runtime/Discovery/RemoteDiscoveryBase.cs
Assets/RemoteControlCore/Runtime/Discovery/NetworkDiscoveryPanel.cs
Assets/RemoteControlCore/Runtime/Discovery/DiscoveryBinder.cs
Assets/RemoteControlCore/Runtime/WebGL/WebGLRemoteBridge.cs          # DllImport façade + event queue
Assets/RemoteControlCore/Runtime/WebGL/WebGLDiscoveryClient.cs
Assets/RemoteControlCore/Runtime/WebGL/WebGLRemoteTransport.cs
Assets/RemoteControlCore/Runtime/WebGL/RemoteVideoView.cs
Assets/RemoteControlCore/Runtime/Core/HostMessage.cs                  # 2.0 host → controller envelope
Assets/RemoteControlCore/Runtime/DefaultSetup/SO/HostMessageSendChannel.asset, HostMessageReceivedChannel.asset   # 2.0
Assets/RemoteControlCore/Runtime/Plugins/WebGL/WebGLRemoteBridge.jslib    # WebSocket + RTCPeerConnection + video texture (WebGL only)
Assets/RemoteControlCore/Runtime/VisionOS/SuperAnretan.RemoteControl.VisionOS.asmdef   # Editor + VisionOS only
Assets/RemoteControlCore/Runtime/VisionOS/SignalingMessage.cs
Assets/RemoteControlCore/Runtime/VisionOS/VisionProNativeBridge.cs   # DllImport("__Internal") + thread-safe event queue
Assets/RemoteControlCore/Runtime/VisionOS/VisionScreenCapture.cs      # VisionScreenCapture.StartCapture()/StopCapture()
Assets/RemoteControlCore/Runtime/VisionOS/VisionProSignalingClient.cs # ClientWebSocket, register/heartbeat/reconnect
Assets/RemoteControlCore/Runtime/VisionOS/VisionProWebRtcHost.cs      # session coordinator, DataChannel → CommandReceivedChannel
Assets/RemoteControlCore/Runtime/Plugins/visionOS/VisionProRemoteBridge.h   (VisionOS only)
Assets/RemoteControlCore/Runtime/Plugins/visionOS/WebRtcHostBridge.mm       (VisionOS only)
Assets/RemoteControlCore/Runtime/Plugins/visionOS/ScreenCaptureBridge.mm    (VisionOS only)
Assets/RemoteControlCore/Editor/RemoteControlSetupBuilder.cs          # Tools ▸ Remote Control ▸ WebRTC ▸ …
Assets/RemoteControlCore/Editor/RemoteControlVisionOSPostProcessor.cs # Xcode: SPM WebRTC, ReplayKit, Info.plist
Assets/RemoteControlCore/Runtime/DefaultSetup/Prefabs/RemoteControl_WebGLClientCore.prefab
Assets/RemoteControlCore/Runtime/DefaultSetup/Prefabs/RemoteControl_VisionProHost.prefab
Assets/RemoteControlCore/Runtime/DefaultSetup/Prefabs/NetworkDiscoveryPanel.prefab
Assets/Scenes/VisionProHostScene.unity
Assets/Scenes/NativeControllerScene.unity    # copy of the previous ControllerScene (IP input, Unity Transport)
SignalingServer/server.js, package.json, README.md, src/*, api/*, vercel.json, test/*   # 2.0: Vercel Function + Redis
WEBGL_VISIONOS_REMOTE.md, REMOTE_CONTROLLER.md
```

### Modified
```
Assets/RemoteControlCore/Runtime/Network/NetworkConfig.cs      # + signalingServerUrl, signalingToken, deviceName, heartbeatInterval, deviceTimeout, iceServerEntries (STUN/TURN), video*
Assets/RemoteControlCore/Runtime/Network/NetworkUtility.cs     # System.Net.Sockets guarded out of WebGL
Assets/RemoteControlCore/Runtime/SuperAnretan.RemoteControl.Runtime.asmdef   # + UnityEngine.UI
Assets/RemoteControlCore/Editor/SuperAnretan.RemoteControl.Editor.asmdef     # + VisionOS asmdef, TMP, UI
Assets/RemoteControlCore/Runtime/DefaultSetup/SO/NetworkConfig.asset         # new fields serialized
Assets/Scenes/ControllerScene.unity                            # rebuilt for WebGL (old one preserved as NativeControllerScene)
.gitignore                                                     # SignalingServer/node_modules
```
Untouched: `RemoteControl_ClientCore.prefab`, `RemoteControl_HostCore.prefab`, `HostScene.unity`, `TransportClient`, `TransportHost`, the command core.

---

## 3. Controller setup (WebGL build)

1. `NetworkConfig` (Assets/RemoteControlCore/Runtime/DefaultSetup/SO) → **Signaling Server Url** = `wss://<project>.vercel.app/api/signaling` (or `ws://<lan-ip>:8787` for a plain-http dev page), **Signaling Token** = the server's `ROOM_TOKEN`. The page's origin must be in the server's `ALLOWED_ORIGINS`.
2. Scene: `Assets/Scenes/ControllerScene.unity` (regenerate any time with *Tools ▸ Remote Control ▸ WebRTC ▸ Build WebGL Controller Scene*).
   It contains `RemoteControl_WebGLClientCore` (WebGLDiscoveryClient + WebGLRemoteTransport) and `NetworkDiscoveryPanel`
   (dropdown / Refresh / Connect / Disconnect / status / RemoteVideoView / Red-Green-Blue demo buttons / log).
3. File ▸ Build Profiles ▸ **Web** ▸ Scene list: only `ControllerScene`. Player Settings: Compression = Disabled or Gzip+fallback for simple hosts,
   `Color Space` as in the project. No special template is required.
4. Build, then host the folder over **HTTPS** (Vercel/Netlify/nginx). For LAN dev: `python -m http.server 8080` and open `http://<pc-ip>:8080`.
5. `TransportClient`/Unity Transport are *not* in this scene. UTP 2.4 compiles for WebGL, so the assembly builds, but never add the native client prefab to the WebGL scene.

## 4. Vision Pro setup (visionOS build — on a Mac)

Prerequisites: Unity 6000.3 **visionOS Build Support** module, Xcode 16+,
Apple Developer account. Windowed apps need nothing else; for a fully-immersive/MR app add `com.unity.xr.visionos` / PolySpatial as usual.

1. `NetworkConfig` → **Device Name** = `Vision Pro Office` (or override per instance on `VisionProSignalingClient`), same **Signaling Server Url** and **Signaling Token**, **Device Timeout** 15 s.
2. Scene: `Assets/Scenes/VisionProHostScene.unity` (or drop `RemoteControl_VisionProHost.prefab` into your own scene). The prefab holds
   `VisionProSignalingClient`, `VisionProWebRtcHost`, `CommandProcessor` wired to the shared registries/channels. Attach your `CommandTarget`s
   and handlers exactly as before.
3. Switch platform to visionOS, build → Xcode project. `RemoteControlVisionOSPostProcessor` automatically:
   * adds the Swift package **`https://github.com/livekit/webrtc-xcframework` @ `150.7871.01`** (product `LiveKitWebRTC`, visionOS 2.2+ slices) to `UnityFramework`
     **and to the app target** — `LiveKitWebRTC` is a dynamic framework, so the app has to link it for Xcode to embed it in the `.app`
   * links `ReplayKit`, `CoreMedia`, `CoreVideo`
   * adds `NSLocalNetworkUsageDescription` and `NSScreenCaptureUsageDescription` to Info.plist
4. In Xcode: set your Team/signing, let SPM resolve the package (first time needs network), run on device.

The native plugin compiles against either `LiveKitWebRTC` (`LKRTC…` classes) or a plain visionOS `WebRTC.xcframework` (`RTC…`) — see the `RC_RTC()` macro.
LiveKit runs enum types and enum constants through `RTC_OBJC_TYPE` as well, so every WebRTC identifier in `WebRtcHostBridge.mm`
goes through `RC_RTC()` — `RC_RTC(PeerConnectionState)`, `RC_RTC(SdpSemanticsUnifiedPlan)`, `RC_RTC(VideoRotation_0)`, … A bare
`RTC…` name compiles only against a plain WebRTC build and breaks the LiveKit one.
If you prefer a manual xcframework, drop it into Xcode and remove the SPM lines from the post-processor.

## 5. Signaling server
See [SignalingServer/README.md](SignalingServer/README.md). Local quick start:
```bash
cd SignalingServer && npm install && npm start      # ws://0.0.0.0:8787, in-memory store
```
Production: deploy `SignalingServer/` as a Vercel project (Root Directory = `SignalingServer`, Fluid compute on) with a
**single-region** Redis over TCP (`REDIS_URL`, or whatever the Marketplace integration injected — `KV_URL`,
`REDIS_TLS_URL`, `UPSTASH_REDIS_URL` are read too; a REST URL is refused), `ROOM_TOKEN`, `ALLOWED_ORIGINS`; add a
Firewall rate limit on `/api/signaling`. Verify the deployment with `curl /api/health?token=…` → `"state":"ok"`.
Without a usable Redis the server refuses to pretend it works: clients get `server-misconfigured:<slug>`.
Do not deploy while a presentation is running. Hobby: 300 s max duration per socket (handled), non-commercial use per Vercel ToS.
Self-hosting alternative: TLS via reverse proxy (Caddy/nginx/Cloudflare Tunnel) or `TLS_CERT`/`TLS_KEY`.

## 6. Network flow
```
Vision Pro app start → WS connect (?token=) → register-device "Vision Pro Office" {deviceTimeout:15} → heartbeat every 2 s
WebGL page start     → WS connect (?token=) → register-controller {clientId (sessionStorage + Web Lock)} → device-list → dropdown
                       every ≤300 s (Vercel): socket closed → reconnect ~1 s → re-register with the same id → nothing else changes
User: select + Connect
   browser: RTCPeerConnection(iceServers), createDataChannel("commands"), addTransceiver(video recvonly), offer → server → host
   host:    setRemoteDescription, attach dormant video track, createAnswer → server → browser
   both:    trickle ICE via server, DTLS, SCTP
DataChannel open  → controller: OnConnectedChannel (control UI + video view visible)
                  → host:       OnClientConnectedChannel, HostMessage snapshot + capture{starting}, VisionScreenCapture.StartCapture() → frames → video track → capture{streaming}
Button → RemoteCommand{requestId?} → CommandSendChannel → DataChannel → RemoteCommand.FromJson → CommandReceivedChannel → CommandProcessor → handler
                                                                                                 └─ ack{requestId} → DataChannel → HostMessageReceivedChannel
App state change → HostMessage.State(topic,value) → HostMessageSendChannel → VisionProWebRtcHost.TrySend → DataChannel → HostMessageReceivedChannel → UI
Disconnect (either side / tab closed (beforeunload + pagehide) / Wi-Fi lost / ICE failed / pairing lease expired)
   host: stop capture, close peer, set-status available, stays registered
   controller: video cleared, UI back to device list, discovery keeps running, optional auto-reconnect (3 attempts)
```

## 7. Required permissions / capabilities
* **visionOS**: no entitlement. First `startCapture` shows the system **screen recording consent**; the user must accept it on the headset.
  Local-network access prompt (`NSLocalNetworkUsageDescription`) appears when WebRTC opens LAN sockets. No microphone/camera.
  Fully-immersive apps: capture is of the rendered frame buffer; passthrough is never included (this is the intended behaviour).
* **Browser**: no permissions (receive-only). Video is `muted`, so autoplay is allowed; the page must be HTTPS when signaling is `wss://`.
* **Signaling**: shared token (`ROOM_TOKEN` ↔ `NetworkConfig.SignalingToken`, sent as `?token=`), `Origin` allowlist, Vercel Firewall rate limit. The token ships in the public WebGL build — it is obfuscation, not authentication.

## 8. Known limitations
* One controller per host.
* No system capture API can capture a fully immersive (Compositor Services) app — use the `UnityCamera` backend there. Passthrough is never included in any backend.
* ReplayKit is deprecated in visionOS 27. When it goes, a windowed host loses its capture path; an immersive host is unaffected because it never used one.
* `UnityCamera` costs one extra scene render plus one GPU readback per streamed frame (at the configured size and rate, not at display rate). The readback crosses into a pinned `NativeArray`; no managed copy is made. A zero-copy Metal blit into the `CVPixelBuffer` is the natural follow-up if thermals demand it.
* Capture cannot start silently on the very first run — Apple's consent UI must be accepted on the Vision Pro.
* `com.unity.webrtc` does not support visionOS — hence the native bridge and the external xcframework (SPM, network needed at first build).
* The `LiveKitWebRTC` binary is not committed; the post-processor pins its version.
* No TURN configured by default (STUN only): both peers need a routable path (same LAN or reachable NAT). 2.0 can express TURN with credentials (`NetworkConfig.IceServerEntries`) and the server can mint short-lived ones for the browser (`TURN_URLS`/`TURN_SECRET`); the Vision Pro still uses only its `NetworkConfig` entries (follow-up in REMOTE_CONTROLLER.md §3.2). A TURN server itself is not part of the repo.
* Vercel Hobby closes every signaling socket after 300 s; handled transparently, but the Vision Pro drops anything it tries to send during its ~1 s reconnect gap (`VisionProSignalingClient.Send` has no outbound queue). ICE candidates generated exactly then are lost; negotiation retries cover it in practice.
* Do not redeploy the signaling server during a presentation (old sockets stay on the old deployment until they close).
* Native UDP discovery for the old path was never in the repository; the old scene still uses an IP field (`NativeControllerScene`).
* WebGL `WebGLRemoteBridge` uses `makeDynCall` (Unity 6 documented pattern); requires the default (non-threaded) WebGL build.

## 9. Testing

**Discovery** — run the server, play `VisionProHostScene` in the Editor (signaling client is pure C#): log shows `[Signaling] Registered as "Vision Pro"`,
`curl http://localhost:8787/devices` lists it. Open the WebGL build: dropdown shows the name; stop the Editor → entry disappears after ~15 s (`NetworkConfig.DeviceTimeout`).

**Commands** — on a real Vision Pro (or any visionOS build): Connect, press Red → host log `[DataChannel] Received: [set_color] target=demo_cube value=#FF0000`
and `[OK] Executed 'set_color' on 'demo_cube'`, cube turns red. `CommandProcessor` is the stock one.

**Video** — after Connect the host log shows `[ScreenCapture] ReplayKit startCapture…`, `[Video] Screen capture started — streaming.`;
controller log shows `[DataChannel] Received: [capture] topic=capture value=streaming`, `[Video] Started (1280,720)` and `[Video] Texture 1280x720 created.` Tick `Use Html Overlay` on `RemoteVideoView` to see the raw
`<video>` if the texture path misbehaves. `chrome://webrtc-internals` shows inbound-rtp frames.

## 10. Debugging / common errors
| Symptom | Check |
|---|---|
| Device not in dropdown | Host log `[Signaling] Connected`? `curl /api/devices?token=…`. Same server URL **and token** on both sides? Same room (`ROOM_TOKENS`)? Heartbeat interval < `Device Timeout`? |
| `[Signaling] ERROR — server rejected the connection: unauthorized` / `origin-not-allowed` | `NetworkConfig.SignalingToken` ≠ server `ROOM_TOKEN`, or the page origin is missing from `ALLOWED_ORIGINS`. |
| `Signaling reconnecting...` every 5 min | Normal on Vercel Hobby (300 s max duration). Session and list are unaffected; if they are, the server is not the 2.0 one. |
| `Signaling offline — reconnecting...` | URL/port, TLS cert validity, mixed content (https page + `ws://` is blocked), firewall. |
| `error: device-busy` | Another controller holds the pairing lease; it is released by its explicit `disconnect` or expires 30 s after its last socket vanished. |
| Host log `Peer disconnect … (controller-gone)` | The controller's pairing lease expired: tab killed without `disconnect`, or no instance refreshed it for 30 s. Expected; host is available again. |
| ICE `failed` | No route between peers (different networks) → add TURN. Vision Pro denied Local Network permission → Settings ▸ Privacy ▸ Local Network. |
| DataChannel never opens | Answer never arrived (host native bridge missing → `Native bridge unavailable` log; only device builds have WebRTC). |
| Video track missing | Host log `capture-error`: `-5801` = consent declined, `-5803` = recording failed to start (retry after leaving/entering immersive space), `replaykit-unavailable` = another app records. |
| `play() blocked` in controller log | Browser autoplay policy — Connect is a click so it normally passes; otherwise click the page. |
| Black texture but overlay works | GL texture id mismatch — make sure `RemoteVideoView` is on the RawImage GameObject and WebGL 2 is enabled. |
| Stream connects and decodes but the picture is uniformly dark, overlay included | The host is a fully immersive app being captured by ReplayKit, which sees only its empty window. Put a `VisionCameraStreamer` in the host scene — `Auto` then resolves to `UnityCamera` by itself. Confirm with the host log line `[ScreenCapture] First frame: … mean sample N`: a mean near 0 is the captured surface itself being black. |
| Host frame rate drops while streaming | Lower `NetworkConfig` video width/height/fps (960x540 @ 15 is the recommended ceiling), narrow the streamer's `Culling Mask`, and look for `[Video] NOTE — … is a heavy preview` in the host log. |
| `capture` reports `streaming` but no frame ever arrives | `Capture Backend` is `UnityCamera` and no `VisionCameraStreamer` is in the scene — the host logs a warning saying exactly this. |
| Picture arrives upside down | Tick `VisionCameraStreamer → Flip Vertically` (GPU readback row order is platform-dependent). |
| `webrtc-internals` shows VP8 / `libvpx` instead of H.264 | The controller now asks for H.264 first via `setCodecPreferences`, so this means the browser offered no H.264 or the host has no H.264 encoder. VP8 works but the headset then encodes in software, which is a lot of heat. |
| Console floods `GL_INVALID_OPERATION: glTexImage2DRobustANGLE: Texture is immutable` and the video stays black | A build older than this fix uploaded frames with `texImage2D` into Unity's immutable (`texStorage2D`) texture. Rebuild with the current Core — the upload path is `texSubImage2D` now. |
| `DllNotFoundException`/`EntryPointNotFoundException` | `.jslib` platform must be WebGL only; `.mm/.h` must be VisionOS only (both are set in the metas). |
| Xcode: `LiveKitWebRTC/LiveKitWebRTC.h not found` | SPM package didn't resolve (offline) — File ▸ Packages ▸ Resolve Package Versions. |
| Xcode: `#error "No WebRTC framework found."` + a cascade of `Unknown type name 'RC_RTC'` | The post-processor never ran. Check the Editor log for `[RemoteControl] No .xcodeproj found` / `UnityFramework target not found`; a build that predates 2.0.1 hit this on every visionOS build because the project was looked up as `Unity-iPhone.xcodeproj`. Rebuild from Unity — do not hand-add the package, the whole wiring is missing. |
| Runtime: `dyld: Library not loaded: @rpath/LiveKitWebRTC.framework/LiveKitWebRTC` | The framework was linked only into `UnityFramework` and never embedded. Fixed in 2.0.1 (app target links it too); on an older Xcode project add `LiveKitWebRTC` to the app target's *Frameworks, Libraries, and Embedded Content*. |
| Host in the **Editor** registers but never answers / logs stall | The Editor stops ticking Play Mode when it loses focus; signaling threads keep heart-beating but `Update()` doesn't pump events. Focus the Editor, or `unity command set_autotick --enable true`. Editor hosts always reject offers with `peer-create-failed` (no native WebRTC) — that's expected. |
| Controller in a **background tab** reacts seconds late | Browsers throttle `requestAnimationFrame` for hidden tabs, so Unity's main loop (and the C# event pump) pauses; JS sockets/timers still run. Keep the controller tab visible. |

## 11. Log prefixes
`[Discovery]` `[Signaling]` `[WebRTC]` `[Video]` `[DataChannel]` `[ScreenCapture]` — all go through `LogChannel` → `DebugLogUI` and `Debug.Log`.
Per-frame paths (texture upload, frame push) never log.
