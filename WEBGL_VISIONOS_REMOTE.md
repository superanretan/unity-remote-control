# WebGL Controller ↔ Vision Pro Host (WebRTC)

A second transport path that lives **next to** the original native one (IP input → Unity Transport UDP).
Nothing in the command core changed: `RemoteCommand`, `CommandProcessor`, `CommandHandlerBase`,
`CommandHandlerRegistry`, `CommandTarget`, `CommandTargetRegistry` and all ScriptableObject event
channels are used as-is.

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
Tiny Node.js `ws` server. Hosts `register-device` + `heartbeat`; controllers get `device-list` pushes; `offer/answer/ice-candidate/disconnect`
are relayed. It **never** carries `RemoteCommand` or media. Hosts vanish from the list `DEVICE_TIMEOUT` seconds after their last heartbeat
(`NetworkConfig.DeviceTimeout` / `HeartbeatInterval` on the Unity side). One controller per host; a second offer gets `error: device-busy`.

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
RemoteVideoView ◀─ Video track ◀──── native encoder ◀── RTCVideoSource ◀── ReplayKit / ScreenCaptureKit
```

### DataChannel payload
Exactly the JSON `RemoteCommand.ToJson()` already produces for Unity Transport:
```json
{"commandType":"set_color","targetId":"demo_cube","value":"#FF0000","payload":""}
```

### Screen capture on visionOS (public APIs only, no passthrough, no enterprise entitlement)
| Backend | OS | Notes |
|---|---|---|
| **ReplayKit** `RPScreenRecorder.startCapture` | visionOS 1.0+ (deprecated in 27) | Default today. Captures the app's rendered content (windows/volumes/immersive frame buffer), passthrough is not included. First start shows Apple's consent alert. |
| **ScreenCaptureKit** `SCContentSharingPicker.presentPickerForCurrentApplication` + `SCStream` | visionOS 27+ (beta, Xcode 27) | Apple's replacement. System picker restricted to *this app*. Enable with `RemoteControlVisionOSPostProcessor.EnableScreenCaptureKit = true` (needs the Xcode 27 SDK). |

`VisionProWebRtcHost → Capture Backend`: `Auto` (ScreenCaptureKit when the OS has it, else ReplayKit), `ReplayKit`, `ScreenCaptureKit`.
ScreenCaptureKit does **not** exist on visionOS 1–26 — the name in the original brief maps to ReplayKit there.

### Video pipeline
```
visionOS:  CMSampleBuffer ─▶ RTCCVPixelBuffer ─▶ RTCVideoFrame ─▶ RTCVideoSource (adaptOutputFormat WxH@fps) ─▶ H.264 HW encoder ─▶ RTP
browser:   MediaStreamTrack ─▶ hidden <video muted playsinline> ─▶ gl.texImage2D(GL.textures[id]) ─▶ Texture2D ─▶ RawImage
```
No frame ever crosses into C# on the host. On the controller `RemoteVideoView` creates an RGBA `Texture2D` of the incoming size and the
`.jslib` copies the newest decoded frame into it (only when `requestVideoFrameCallback` reported a new frame). `_useHtmlOverlay` shows the raw
`<video>` element on top of the canvas as a debugging fallback.

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
SignalingServer/server.js, package.json, README.md
WEBGL_VISIONOS_REMOTE.md
```

### Modified
```
Assets/RemoteControlCore/Runtime/Network/NetworkConfig.cs      # + signalingServerUrl, deviceName, heartbeatInterval, deviceTimeout, iceServers, video*
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

1. `NetworkConfig` (Assets/RemoteControlCore/Runtime/DefaultSetup/SO) → **Signaling Server Url** = `wss://your-server` (or `ws://<lan-ip>:8787` for a plain-http dev page).
2. Scene: `Assets/Scenes/ControllerScene.unity` (regenerate any time with *Tools ▸ Remote Control ▸ WebRTC ▸ Build WebGL Controller Scene*).
   It contains `RemoteControl_WebGLClientCore` (WebGLDiscoveryClient + WebGLRemoteTransport) and `NetworkDiscoveryPanel`
   (dropdown / Refresh / Connect / Disconnect / status / RemoteVideoView / Red-Green-Blue demo buttons / log).
3. File ▸ Build Profiles ▸ **Web** ▸ Scene list: only `ControllerScene`. Player Settings: Compression = Disabled or Gzip+fallback for simple hosts,
   `Color Space` as in the project. No special template is required.
4. Build, then host the folder over **HTTPS** (Vercel/Netlify/nginx). For LAN dev: `python -m http.server 8080` and open `http://<pc-ip>:8080`.
5. `TransportClient`/Unity Transport are *not* in this scene. UTP 2.4 compiles for WebGL, so the assembly builds, but never add the native client prefab to the WebGL scene.

## 4. Vision Pro setup (visionOS build — on a Mac)

Prerequisites: Unity 6000.3 **visionOS Build Support** module, Xcode 16+ (Xcode 27 beta for the ScreenCaptureKit backend),
Apple Developer account. Windowed apps need nothing else; for a fully-immersive/MR app add `com.unity.xr.visionos` / PolySpatial as usual.

1. `NetworkConfig` → **Device Name** = `Vision Pro Office` (or override per instance on `VisionProSignalingClient`), same **Signaling Server Url**.
2. Scene: `Assets/Scenes/VisionProHostScene.unity` (or drop `RemoteControl_VisionProHost.prefab` into your own scene). The prefab holds
   `VisionProSignalingClient`, `VisionProWebRtcHost`, `CommandProcessor` wired to the shared registries/channels. Attach your `CommandTarget`s
   and handlers exactly as before.
3. Switch platform to visionOS, build → Xcode project. `RemoteControlVisionOSPostProcessor` automatically:
   * adds the Swift package **`https://github.com/livekit/webrtc-xcframework` @ `150.7871.01`** (product `LiveKitWebRTC`, visionOS 2.2+ slices) to `UnityFramework`
   * links `ReplayKit`, `CoreMedia`, `CoreVideo` (+ `ScreenCaptureKit` and `VPR_ENABLE_SCREENCAPTUREKIT=1` when `EnableScreenCaptureKit` is on)
   * adds `NSLocalNetworkUsageDescription` and `NSScreenCaptureUsageDescription` to Info.plist
4. In Xcode: set your Team/signing, let SPM resolve the package (first time needs network), run on device.

The native plugin compiles against either `LiveKitWebRTC` (`LKRTC…` classes) or a plain visionOS `WebRTC.xcframework` (`RTC…`) — see the `RC_RTC()` macro.
If you prefer a manual xcframework, drop it into Xcode and remove the SPM lines from the post-processor.

## 5. Signaling server
See [SignalingServer/README.md](SignalingServer/README.md). Quick start:
```bash
cd SignalingServer && npm install && npm start      # ws://0.0.0.0:8787
```
Put it behind TLS (Caddy/nginx/Cloudflare Tunnel) for `wss://`.

## 6. Network flow
```
Vision Pro app start → WS connect → register-device "Vision Pro Office" → heartbeat every 2 s
WebGL page start     → WS connect → register-controller → device-list → dropdown
User: select + Connect
   browser: RTCPeerConnection(iceServers), createDataChannel("commands"), addTransceiver(video recvonly), offer → server → host
   host:    setRemoteDescription, attach dormant video track, createAnswer → server → browser
   both:    trickle ICE via server, DTLS, SCTP
DataChannel open  → controller: OnConnectedChannel (control UI + video view visible)
                  → host:       OnClientConnectedChannel, VisionScreenCapture.StartCapture() → frames → video track
Button → RemoteCommand → CommandSendChannel → DataChannel → RemoteCommand.FromJson → CommandReceivedChannel → CommandProcessor → handler
Disconnect (either side / tab closed / Wi-Fi lost / ICE failed)
   host: stop capture, close peer, set-status available, stays registered
   controller: video cleared, UI back to device list, discovery keeps running, optional auto-reconnect (3 attempts)
```

## 7. Required permissions / capabilities
* **visionOS**: no entitlement. First `startCapture` shows the system **screen recording consent**; the user must accept it on the headset.
  Local-network access prompt (`NSLocalNetworkUsageDescription`) appears when WebRTC opens LAN sockets. No microphone/camera.
  Fully-immersive apps: capture is of the rendered frame buffer; passthrough is never included (this is the intended behaviour).
* **Browser**: no permissions (receive-only). Video is `muted`, so autoplay is allowed; the page must be HTTPS when signaling is `wss://`.
* **Signaling**: none. Add auth/origin checks before exposing it on the internet.

## 8. Known limitations
* One controller per host.
* Capture cannot start silently on the very first run — Apple's consent UI must be accepted on the Vision Pro.
* ScreenCaptureKit backend requires visionOS 27 + Xcode 27 (beta at the time of writing); ReplayKit is deprecated in 27 but works.
* `com.unity.webrtc` does not support visionOS — hence the native bridge and the external xcframework (SPM, network needed at first build).
* The `LiveKitWebRTC` binary is not committed; the post-processor pins its version.
* No TURN configured by default (STUN only): both peers need a routable path (same LAN or reachable NAT). Add `turn:` URLs to `NetworkConfig.IceServers` if needed.
* Host → controller DataChannel messages are logged only (protocol is one-way for commands).
* Native UDP discovery for the old path was never in the repository; the old scene still uses an IP field (`NativeControllerScene`).
* WebGL `WebGLRemoteBridge` uses `makeDynCall` (Unity 6 documented pattern); requires the default (non-threaded) WebGL build.

## 9. Testing

**Discovery** — run the server, play `VisionProHostScene` in the Editor (signaling client is pure C#): log shows `[Signaling] Registered as "Vision Pro"`,
`curl http://localhost:8787/devices` lists it. Open the WebGL build: dropdown shows the name; stop the Editor → entry disappears after ~6 s.

**Commands** — on a real Vision Pro (or any visionOS build): Connect, press Red → host log `[DataChannel] Received: [set_color] target=demo_cube value=#FF0000`
and `[OK] Executed 'set_color' on 'demo_cube'`, cube turns red. `CommandProcessor` is the stock one.

**Video** — after Connect the host log shows `[ScreenCapture] ReplayKit startCapture…`, `[Video] Screen capture started — streaming.`;
controller log shows `[Video] Started (1280,720)` and `[Video] Texture 1280x720 created.` Tick `Use Html Overlay` on `RemoteVideoView` to see the raw
`<video>` if the texture path misbehaves. `chrome://webrtc-internals` shows inbound-rtp frames.

## 10. Debugging / common errors
| Symptom | Check |
|---|---|
| Device not in dropdown | Host log `[Signaling] Connected`? `curl /devices`. Same server URL on both sides? Heartbeat interval < `DEVICE_TIMEOUT`? |
| `Signaling offline — reconnecting...` | URL/port, TLS cert validity, mixed content (https page + `ws://` is blocked), firewall. |
| `error: device-busy` | Another controller is paired; host `set-status available` happens after its disconnect. |
| ICE `failed` | No route between peers (different networks) → add TURN. Vision Pro denied Local Network permission → Settings ▸ Privacy ▸ Local Network. |
| DataChannel never opens | Answer never arrived (host native bridge missing → `Native bridge unavailable` log; only device builds have WebRTC). |
| Video track missing | Host log `capture-error`: `-5801` = consent declined, `-5803` = recording failed to start (retry after leaving/entering immersive space), `replaykit-unavailable` = another app records. |
| `play() blocked` in controller log | Browser autoplay policy — Connect is a click so it normally passes; otherwise click the page. |
| Black texture but overlay works | GL texture id mismatch — make sure `RemoteVideoView` is on the RawImage GameObject and WebGL 2 is enabled. |
| `DllNotFoundException`/`EntryPointNotFoundException` | `.jslib` platform must be WebGL only; `.mm/.h` must be VisionOS only (both are set in the metas). |
| Xcode: `LiveKitWebRTC/LiveKitWebRTC.h not found` | SPM package didn't resolve (offline) — File ▸ Packages ▸ Resolve Package Versions. |
| Xcode: `presentPickerForCurrentApplication` unknown | Xcode < 27 with `EnableScreenCaptureKit = true` → set it back to `false`. |
| Host in the **Editor** registers but never answers / logs stall | The Editor stops ticking Play Mode when it loses focus; signaling threads keep heart-beating but `Update()` doesn't pump events. Focus the Editor, or `unity command set_autotick --enable true`. Editor hosts always reject offers with `peer-create-failed` (no native WebRTC) — that's expected. |
| Controller in a **background tab** reacts seconds late | Browsers throttle `requestAnimationFrame` for hidden tabs, so Unity's main loop (and the C# event pump) pauses; JS sockets/timers still run. Keep the controller tab visible. |

## 11. Log prefixes
`[Discovery]` `[Signaling]` `[WebRTC]` `[Video]` `[DataChannel]` `[ScreenCapture]` — all go through `LogChannel` → `DebugLogUI` and `Debug.Log`.
Per-frame paths (texture upload, frame push) never log.
