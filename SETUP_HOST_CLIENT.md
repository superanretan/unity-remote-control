# Host and client setup — buttons, commands, scenes

This document describes **how sending commands from buttons works today** and **how to set up a host and a
client from scratch** (native and WebGL).
The WebGL ↔ Vision Pro network architecture is described in [WEBGL_VISIONOS_REMOTE.md](WEBGL_VISIONOS_REMOTE.md);
the 2.0 changes (host → controller return channel, signaling token, TURN, Vercel) in
[REMOTE_CONTROLLER.md](REMOTE_CONTROLLER.md).
Using the package in a separate project and writing your own controller UI? Start with
[INTEGRATION.md](INTEGRATION.md) — it covers deploying the server on Vercel, installing the package,
generating the SO assets and building for WebGL.

---

## 1. How a button becomes a command

A button never knows about the transport. It raises a `RemoteCommand` on the **`CommandSendChannel`** SO, and
whichever transport is in the scene picks it up and sends it on.

```
[Button.onClick]
      │
      ▼
DemoControllerUI / CommandButton        new RemoteCommand("set_color", "demo_cube", "#FF0000")
      │
      ▼
CommandSendChannel.Raise(cmd)           (ScriptableObject event channel)
      │
      ├─ native: TransportClient.SendCommand      → Unity Transport (UDP) → TransportHost
      └─ WebGL:  WebGLRemoteTransport.SendCommand → RTCDataChannel        → VisionProWebRtcHost
                                                                             │
                                                    RemoteCommand.FromJson(json)
                                                                             │
                                                    CommandReceivedChannel.Raise(cmd)
                                                                             │
                                                    CommandProcessor
                                                      ├─ HandlerRegistry.TryGetHandler(cmd.commandType)  → SetColorHandler
                                                      └─ TargetRegistry.TryGetTarget(cmd.targetId)       → CommandTarget "demo_cube"
                                                                             │
                                                    handler.Handle(cmd, target.gameObject)
```

The same JSON (`RemoteCommand.ToJson()`) goes over the wire regardless of transport:

```json
{"commandType":"set_color","targetId":"demo_cube","value":"#FF0000","payload":"","requestId":""}
```

| Field | Meaning |
|---|---|
| `commandType` | handler key on the host (`ICommandHandler.CommandType`) |
| `targetId` | `CommandTarget.TargetId` of an object in the host scene |
| `value` | primary value (interpreted by the handler) |
| `payload` | optional extra JSON |
| `requestId` | optional (2.0): when non-empty, a WebRTC host answers with `HostMessage{messageType:"ack", requestId}` |

In the other direction (WebRTC only, 2.0) the host sends a `HostMessage` —
`{"messageType":"state","schemaVersion":1,"topic":"navigation","value":"compartment-a","payload":"","requestId":""}` —
see [REMOTE_CONTROLLER.md §1](REMOTE_CONTROLLER.md).

### What is in the controller scene today

`NetworkDiscoveryPanel.prefab` (ControllerScene) has a **`DemoControllerUI`** component on its root
([Assets/Script/DemoControllerUI.cs](Assets/Script/DemoControllerUI.cs)) with three buttons wired up:
`ConnectedGroup/ControlButtons/RedBtn|GreenBtn|BlueBtn`.

```csharp
_redButton?.onClick.AddListener(() => SendColor("#FF0000"));
...
private void SendColor(string hexColor)
{
    if (!_isConnected) { _logChannel?.Raise("[UI] Not connected."); return; }
    var cmd = new RemoteCommand("set_color", _targetId, hexColor);   // _targetId = "demo_cube"
    _commandSendChannel?.Raise(cmd);
}
```

`DemoControllerUI` listens on `OnConnectedChannel` / `OnDisconnectedChannel` and disables the buttons while
there is no connection. The `_ipInputField`, `_connectButton` and `_disconnectButton` fields are left empty on
WebGL — Connect/Disconnect is handled by `NetworkDiscoveryPanel`.
The `ConnectedGroup` group (buttons + video preview) is only shown by the panel after `OnConnectedChannel`.

---

## 2. Adding your own button (controller)

### Option A — no code: `CommandButton`

1. Add a `Button` (TMP) to the Canvas. On WebGL, put it under
   `NetworkDiscoveryPanel/ConnectedGroup/ControlButtons` so it hides on disconnect.
2. Add the **`CommandButton`** component (`Assets/RemoteControlCore/Runtime/UI/CommandButton.cs`).
3. Set it up in the Inspector:
   - `Command Type` — e.g. `toggle_object`
   - `Target Id` — e.g. `demo_cube`
   - `Value` / `Payload` — whatever the handler needs
   - `Command Send Channel` → `Assets/RemoteControlCore/Runtime/DefaultSetup/SO/CommandSendChannel.asset`
   - (optional) `On Connected Channel` / `On Disconnected Channel` → button interactable only while connected
   - (optional) `Log Channel` → `LogChannel.asset`

A click is `CommandSendChannel.Raise(new RemoteCommand(type, target, value, payload))`. The value can be
changed at runtime through `CommandButton.SetValue(string)`, e.g. from a slider.

### Option B — your own UI script

```csharp
using SuperAnretan.RemoteControl;
using UnityEngine;
using UnityEngine.UI;

public class MyControls : MonoBehaviour
{
    [SerializeField] CommandEventChannel _commandSendChannel;   // SO: CommandSendChannel.asset
    [SerializeField] Button _jumpButton;

    void OnEnable()  => _jumpButton.onClick.AddListener(Jump);
    void OnDisable() => _jumpButton.onClick.RemoveListener(Jump);

    void Jump() => _commandSendChannel.Raise(new RemoteCommand("jump", "player", "1.5"));
}
```

That is all — whichever transport is in the scene (native or WebGL) sends the command. Do not reference
`TransportClient` / `WebGLRemoteTransport` directly.

---

## 3. Adding your own command (host)

1. **Handler** — a class deriving from `CommandHandlerBase`:

```csharp
using SuperAnretan.RemoteControl;
using UnityEngine;

public class JumpHandler : CommandHandlerBase
{
    public override string CommandType => "jump";          // must match the controller's commandType

    public override void Handle(RemoteCommand command, GameObject target)
    {
        float force = float.TryParse(command.value, out var f) ? f : 1f;
        target.GetComponent<Rigidbody>()?.AddForce(Vector3.up * force, ForceMode.Impulse);
    }
}
```

2. Put the handler on any GameObject in the host scene and assign **`Handler Registry`** →
   `HandlerRegistry.asset`. The handler registers itself in `OnEnable`.
3. **Target** — on the object you want to control, add **`CommandTarget`**, set `Target Id` (e.g. `player`)
   and `Registry` → `TargetRegistry.asset`.
4. `CommandProcessor` (part of the host prefab) does the rest: finds the handler by `commandType`, the target
   by `targetId`, and calls `Handle`.

Rules: one `CommandType` = one handler (a duplicate overwrites with a warning), one `TargetId` = one object.
The handler receives the target's `GameObject` and decides for itself which component it looks for.

---

## 4. Shared assets (ScriptableObjects)

Everything lives in `Assets/RemoteControlCore/Runtime/DefaultSetup/SO/` and is already wired into the prefabs:

| Asset | Type | Role |
|---|---|---|
| `NetworkConfig` | `NetworkConfig` | UDP port, **Signaling Server Url**, **Signaling Token**, **Device Name**, **Device Timeout** (15 s), **Ice Server Entries** (STUN/TURN + credentials), video parameters |
| `CommandSendChannel` | `CommandEventChannel` | UI → transport (controller) |
| `CommandReceivedChannel` | `CommandEventChannel` | transport → `CommandProcessor` (host) |
| `ConnectRequestChannel` | `StringEventChannel` | UI → transport: IP (native) or deviceId (WebGL) |
| `DisconnectRequestChannel` | `VoidEventChannel` | UI → transport |
| `OnConnectedChannel` / `OnDisconnectedChannel` | `VoidEventChannel` | transport → UI (controller) |
| `OnClientConnectedChannel` / `OnClientDisconnectedChannel` | `VoidEventChannel` | transport → app logic (host) |
| `HostMessageSendChannel` | `StringEventChannel` | host logic → `VisionProWebRtcHost` → DataChannel (`HostMessage` JSON, 2.0) |
| `HostMessageReceivedChannel` | `StringEventChannel` | `WebGLRemoteTransport` → controller UI (`HostMessage` JSON, 2.0) |
| `HandlerRegistry` / `TargetRegistry` | registry | runtime registries |
| `LogChannel` | `StringEventChannel` | logs → `DebugLogUI` |

New assets: **Create ▸ Remote Control ▸ …** (Config / Events / Registries).

---

## 5. HOST setup

### 5a. Vision Pro (WebRTC)

1. Open your scene (or `Assets/Scenes/VisionProHostScene.unity` as an example).
2. Drag in the **`RemoteControl_VisionProHost`** prefab (`Runtime/DefaultSetup/Prefabs/`). It contains
   `VisionProSignalingClient` (registration + heartbeat + reconnect), `VisionProWebRtcHost` (WebRTC,
   DataChannel → `CommandReceivedChannel`, capture start/stop, `HostMessageSendChannel` return channel) and
   `CommandProcessor`.
3. In `NetworkConfig.asset` set:
   - `Signaling Server Url` — **Vercel:** `wss://<project>.vercel.app/api/signaling`; locally
     `ws://<pc-ip>:8787`. The serialized field is still `_signalingServerUrl` (the `remotecontrol.json`
     override works unchanged).
   - `Signaling Token` — the server's `ROOM_TOKEN` value (appended as `?token=…`). Empty = server runs in
     open mode.
   - `Device Name` (e.g. `Vision Pro Office`; can be overridden per instance via `Device Name Override` on
     `VisionProSignalingClient`).
   - `Device Timeout` = **15** (the host sends this value to the server in `register-device`; it must exceed
     the reconnect gap after Vercel's forced socket close, 1–10 s).
   - `Ice Server Entries` — one STUN entry by default. For TURN, add an entry with `urls` + `username` +
     `credential` (note: these ship inside the build; prefer short-lived credentials issued by the server —
     REMOTE_CONTROLLER.md §3).
4. Add a `CommandTarget` and handlers as in §3.
5. (2.0) **State back to the controller:** in your own logic, raise `HostMessage.State(topic, value).ToJson()`
   on `HostMessageSendChannel.asset` (or call `VisionProWebRtcHost.SetState`). The host caches the last value
   per topic and sends a full snapshot once the DataChannel opens, then the changes. Capture state
   (`starting/streaming/stopped/error`) and `ack`s for commands carrying a `requestId` are sent
   automatically. Do not call `VisionProNativeBridge` directly.
6. (Optional) A Canvas with `TextMeshProUGUI` + `DebugLogUI` (`Log Channel` → `LogChannel.asset`) shows the
   `[Signaling] [WebRTC] [DataChannel] [ScreenCapture]` logs on the device. A server rejection
   (`unauthorized`) is visible there.
7. Build for visionOS on a Mac — the post-processor adds the WebRTC package, ReplayKit and the Info.plist
   entries by itself (details: WEBGL_VISIONOS_REMOTE.md §4).

Flow: start → register with signaling (`?token=…`) → visible in the list → controller clicks Connect →
DataChannel open → `OnClientConnectedChannel` + `HostMessage` snapshot + capture start
(`capture: starting → streaming`) → commands reach `CommandProcessor` (+ `ack`) → disconnect: capture stops,
the host stays visible in the list.
A dropped signaling socket (every ≤300 s on Vercel) is transparent: the client reconnects with the same
`deviceId` and the WebRTC session continues.

### 5b. Native host (Unity Transport, unchanged)

1. The **`RemoteControl_HostCore`** prefab (`TransportHost` + `CommandProcessor`), autostarting on
   `NetworkConfig.Port` (7777).
2. `CommandTarget` + handlers as in §3. Example: `Assets/Scenes/HostScene.unity`.

---

## 6. CLIENT (controller) setup

### 6a. WebGL (browser → Vision Pro)

1. Scene: `Assets/Scenes/ControllerScene.unity` (regenerate with
   **Tools ▸ Remote Control ▸ WebRTC ▸ Build WebGL Controller Scene**). It contains:
   - **`RemoteControl_WebGLClientCore`** — `WebGLDiscoveryClient` (device list from signaling) +
     `WebGLRemoteTransport` (WebRTC DataChannel; listens on `CommandSendChannel`, `ConnectRequestChannel`
     and `DisconnectRequestChannel`, raises `OnConnected`/`OnDisconnected` and
     **`HostMessageReceivedChannel`** for every message the host sends back).
   - **`NetworkDiscoveryPanel`** (under the Canvas) — dropdown, Refresh, Connect, Disconnect, status,
     `ConnectedGroup` (RawImage + `RemoteVideoView`, R/G/B buttons + `DemoControllerUI`) and a log
     (`DebugLogUI`). `DiscoveryBinder` finds the `WebGLDiscoveryClient` in the scene by itself.
2. `NetworkConfig.Signaling Server Url` = the same as the host (**Vercel:**
   `wss://<project>.vercel.app/api/signaling`; a page served over HTTPS requires `wss://`),
   `Signaling Token` = the same `ROOM_TOKEN`. The domain hosting the build must be in the server's
   `ALLOWED_ORIGINS`.
3. Your own buttons: §2 (easiest is `CommandButton` under `ConnectedGroup/ControlButtons`; add a
   `HorizontalLayoutGroup` once there are several).
4. (2.0) **Reacting to host state:** subscribe to `HostMessageReceivedChannel.asset` (`StringEventChannel`)
   and parse with `HostMessage.FromJson(json)`: `IsSnapshot` → `SnapshotEntries()`, `IsState` →
   `topic`/`value` (e.g. highlight the active compartment), `IsCapture` → `value` =
   `starting/streaming/stopped/error` (whether the picture is actually flowing), `IsAck` → `requestId`. For an
   `ack` on every command, enable `Auto Request Id` on `WebGLRemoteTransport` or set `command.requestId`
   yourself.
5. Build: Build Profiles ▸ Web with `ControllerScene` only; host it over HTTPS (Vercel). `.br` files need a
   `Content-Encoding: br` header (or turn compression off in Player Settings).

The browser keeps a stable `clientId` in `sessionStorage` (F5 preserves the pairing session; a duplicated tab
gets a new id through Web Locks). The signaling socket dropping every ≤300 s (Vercel) affects neither the
WebRTC session nor the device list. Closing the tab sends `disconnect` (`beforeunload` + `pagehide`), and the
Vision Pro stops recording within a few seconds.

On WebGL, do **not** add `RemoteControl_ClientCore` (Unity Transport UDP does not work in a browser).

### 6b. Native controller (Windows/Android → native host, unchanged)

1. Scene `Assets/Scenes/NativeControllerScene.unity` (the preserved old `ControllerScene`): the
   **`RemoteControl_ClientCore`** prefab (`TransportClient`) + a Canvas with `DemoControllerUI` (IP field,
   Connect/Disconnect, R/G/B).
2. Enter the host IP → Connect → the buttons send `set_color` to `demo_cube`.
3. Your own buttons: §2 — the same SO channels, `CommandButton` behaves identically here.

> `NetworkDiscoveryPanel` can be used natively too: all it needs is a `RemoteDiscoveryBase` implementation
> (e.g. UDP broadcast) that puts the host IP in `DiscoveredDevice.address`. The panel then raises it on
> `ConnectRequestChannel` and `TransportClient` connects. No such implementation exists in this repo today.

---

## 7. Quick test

1. `cd SignalingServer && npm install && npm start` (locally, without Redis; production = Vercel, see
   `SignalingServer/README.md` §2). Leave `NetworkConfig.Signaling Token` empty when the server has no
   `ROOM_TOKEN`.
2. Editor: open `VisionProHostScene` → Play (the host registers; in the Editor it will **not answer an
   offer** — there is no native WebRTC there, which is expected). `curl http://localhost:8787/devices` shows
   the device; it disappears ~15 s after leaving Play.
3. Build for WebGL → open the page → the dropdown shows the host → Connect.
4. On a real Vision Pro: after Connect the control UI and the video appear; the controller log shows
   `[DataChannel] Snapshot received (…)` and
   `[DataChannel] Received: [capture] topic=capture value=streaming`; Red → the host log shows
   `[DataChannel] Received: [set_color] target=demo_cube value=#FF0000` →
   `[OK] Executed 'set_color' on 'demo_cube'` → the cube turns red.

Debugging: the log prefixes are `[Discovery] [Signaling] [WebRTC] [Video] [DataChannel] [ScreenCapture]`; a
table of common failures is in WEBGL_VISIONOS_REMOTE.md §10.
