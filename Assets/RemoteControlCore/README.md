# Remote Control

Universal local Wi-Fi remote control plugin for Unity.  
One build = **Host** (3D scene), another = **Controller** (UI).  
Commands travel over **Unity Transport**, are serialized as **JSON**, and are dispatched through **ScriptableObject event channels** — zero singletons, zero static state.

## Requirements

| Dependency | Version |
|---|---|
| Unity | 6000.0+ |
| Unity Transport | 2.4+ |
| TextMeshPro | (included with Unity 6) |

## Installation

### Via Git URL (recommended)

In Unity → **Window → Package Manager → + → Add package from git URL**:

```
https://github.com/YOUR_ORG/unity-remote-control.git?path=Packages/com.superanretan.remotecontrol
```

### Local / embedded

Clone the repo and the package is already embedded under `Packages/com.superanretan.remotecontrol`.

## Quick Start

### 1. Create ScriptableObject assets

**Tools → Remote Control → Create All SO Assets**

This generates all required event channels, registries, and config assets under  
`Assets/RemoteControl/SO/`.

### 2. Host Scene setup

1. Create a new scene.
2. Add an empty GameObject → attach **TransportHost**.  
   Assign: `NetworkConfig`, `CommandReceivedChannel`, `LogChannel`.
3. Add an empty GameObject → attach **CommandProcessor**.  
   Assign: `CommandReceivedChannel`, `TargetRegistry`, `HandlerRegistry`, `LogChannel`.
4. Add a Cube → attach **CommandTarget** (targetId = `demo_cube`).  
   Assign: `TargetRegistry`.
5. On the same Cube (or new GO) → attach your handler (e.g. **SetColorHandler**).  
   Assign: `HandlerRegistry`.
6. Add a Canvas → attach **DebugLogUI**. Assign: `LogChannel`.

### 3. Controller Scene setup

1. Create a new scene.
2. Add an empty GameObject → attach **TransportClient**.  
   Assign: `NetworkConfig`, `CommandSendChannel`, `ConnectRequestChannel`,  
   `DisconnectRequestChannel`, `LogChannel`, `OnConnectedChannel`, `OnDisconnectedChannel`.
3. Add a Canvas with:
   - TMP InputField (IP address)
   - Connect / Disconnect buttons
   - Color buttons (Red, Green, Blue)
4. Attach **DemoControllerUI** to the Canvas.  
   Assign: `CommandSendChannel`, `ConnectRequestChannel`, `DisconnectRequestChannel`,  
   `OnConnectedChannel`, `OnDisconnectedChannel`, `LogChannel`, and UI references.
5. Add **DebugLogUI** to the Canvas. Assign: `LogChannel`.

### 4. Build & Test

1. **Build** the Host scene as a standalone build → run it.
2. **Play** the Controller scene in the Editor (or build separately).
3. Enter the Host's local IP → Connect → tap a color button.
4. The Cube changes color. Debug log shows the full command lifecycle.

## Architecture

```
Controller UI  ──►  CommandSendChannel (SO)  ──►  TransportClient
                                                       │
                                               Unity Transport
                                                       │
TransportHost  ──►  CommandReceivedChannel (SO) ──►  CommandProcessor
                                                       │
                                              ┌────────┴────────┐
                                         HandlerRegistry    TargetRegistry
                                              │                  │
                                         ICommandHandler    CommandTarget
```

- **No singletons / no static Instance / no service locator**
- All wiring via **ScriptableObject** drag-and-drop in Inspector
- Handlers register/unregister automatically via `OnEnable`/`OnDisable`

## Command Format (JSON)

```json
{
  "commandType": "set_color",
  "targetId": "demo_cube",
  "value": "#FF0000",
  "payload": ""
}
```

| Field | Type | Description |
|---|---|---|
| `commandType` | `string` | Handler lookup key |
| `targetId` | `string` | Runtime target lookup key |
| `value` | `string` | Primary value |
| `payload` | `string` | Optional extra JSON blob |

## Creating Custom Handlers

```csharp
using SuperAnretan.RemoteControl;
using UnityEngine;

public class ToggleObjectHandler : CommandHandlerBase
{
    public override string CommandType => "toggle_object";

    public override void Handle(RemoteCommand command, GameObject target)
    {
        target.SetActive(!target.activeSelf);
    }
}
```

Attach to any GameObject, assign the `HandlerRegistry` SO, done.

## License

MIT — see [LICENSE](LICENSE).
