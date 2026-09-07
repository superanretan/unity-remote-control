# Setup hosta i klienta — przyciski, komendy, sceny

Ten dokument opisuje **jak dziś działa wysyłanie komend z przycisków** i **jak od zera skonfigurować hosta i klienta** (natywnego i WebGL).
Architektura sieciowa WebGL ↔ Vision Pro jest opisana w [WEBGL_VISIONOS_REMOTE.md](WEBGL_VISIONOS_REMOTE.md).

---

## 1. Jak działa przycisk → komenda

Przycisk nigdy nie zna transportu. Wysyła `RemoteCommand` na kanał SO **`CommandSendChannel`**, a transport, który jest w scenie, zabiera go i wysyła dalej.

```
[Button.onClick]
      │
      ▼
DemoControllerUI / CommandButton        new RemoteCommand("set_color", "demo_cube", "#FF0000")
      │
      ▼
CommandSendChannel.Raise(cmd)           (ScriptableObject event channel)
      │
      ├─ natywny:  TransportClient.SendCommand   → Unity Transport (UDP) → TransportHost
      └─ WebGL:    WebGLRemoteTransport.SendCommand → RTCDataChannel     → VisionProWebRtcHost
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

Na drucie leci zawsze ten sam JSON (`RemoteCommand.ToJson()`), niezależnie od transportu:

```json
{"commandType":"set_color","targetId":"demo_cube","value":"#FF0000","payload":""}
```

| Pole | Znaczenie |
|---|---|
| `commandType` | klucz handlera po stronie hosta (`ICommandHandler.CommandType`) |
| `targetId` | `CommandTarget.TargetId` obiektu w scenie hosta |
| `value` | wartość główna (interpretuje ją handler) |
| `payload` | opcjonalny dodatkowy JSON |

### Co jest w scenie kontrolera dziś

`NetworkDiscoveryPanel.prefab` (ControllerScene) ma na korzeniu komponent **`DemoControllerUI`** ([Assets/Script/DemoControllerUI.cs](Assets/Script/DemoControllerUI.cs)) z podpiętymi trzema przyciskami `ConnectedGroup/ControlButtons/RedBtn|GreenBtn|BlueBtn`:

```csharp
_redButton?.onClick.AddListener(() => SendColor("#FF0000"));
...
private void SendColor(string hexColor)
{
    if (!_isConnected) { _logChannel?.Raise("[UI] Nie połączono."); return; }
    var cmd = new RemoteCommand("set_color", _targetId, hexColor);   // _targetId = "demo_cube"
    _commandSendChannel?.Raise(cmd);
}
```

`DemoControllerUI` nasłuchuje `OnConnectedChannel` / `OnDisconnectedChannel` i blokuje przyciski, gdy nie ma połączenia. Pola `_ipInputField`, `_connectButton`, `_disconnectButton` są w WebGL puste — Connect/Disconnect obsługuje `NetworkDiscoveryPanel`.
Grupa `ConnectedGroup` (przyciski + podgląd video) jest pokazywana przez panel dopiero po `OnConnectedChannel`.

---

## 2. Dodanie własnego przycisku (kontroler)

### Wariant A — bez kodu: `CommandButton`

1. Dodaj `Button` (TMP) do Canvasu (w WebGL najlepiej pod `NetworkDiscoveryPanel/ConnectedGroup/ControlButtons`, żeby chował się po disconnect).
2. Dodaj komponent **`CommandButton`** (`Assets/RemoteControlCore/Runtime/UI/CommandButton.cs`).
3. Ustaw w Inspectorze:
   - `Command Type` — np. `toggle_object`
   - `Target Id` — np. `demo_cube`
   - `Value` / `Payload` — wg potrzeb handlera
   - `Command Send Channel` → `Assets/RemoteControlCore/Runtime/DefaultSetup/SO/CommandSendChannel.asset`
   - (opcjonalnie) `On Connected Channel` / `On Disconnected Channel` → przycisk aktywny tylko po połączeniu
   - (opcjonalnie) `Log Channel` → `LogChannel.asset`

Kliknięcie = `CommandSendChannel.Raise(new RemoteCommand(type, target, value, payload))`. Wartość można zmienić w locie przez `CommandButton.SetValue(string)` (np. ze slidera).

### Wariant B — własny skrypt UI

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

Nic więcej — transport w scenie (natywny lub WebGL) sam wyśle komendę. Nie odwołuj się do `TransportClient` / `WebGLRemoteTransport` bezpośrednio.

---

## 3. Dodanie własnej komendy (host)

1. **Handler** — klasa dziedzicząca z `CommandHandlerBase`:

```csharp
using SuperAnretan.RemoteControl;
using UnityEngine;

public class JumpHandler : CommandHandlerBase
{
    public override string CommandType => "jump";          // musi być równe commandType z kontrolera

    public override void Handle(RemoteCommand command, GameObject target)
    {
        float force = float.TryParse(command.value, out var f) ? f : 1f;
        target.GetComponent<Rigidbody>()?.AddForce(Vector3.up * force, ForceMode.Impulse);
    }
}
```

2. Dodaj handler na dowolny GameObject w scenie hosta i przypisz **`Handler Registry`** → `HandlerRegistry.asset`. Handler rejestruje się sam w `OnEnable`.
3. **Target** — na obiekcie, którym chcesz sterować, dodaj **`CommandTarget`**, ustaw `Target Id` (np. `player`) i `Registry` → `TargetRegistry.asset`.
4. `CommandProcessor` (jest w prefabie hosta) zrobi resztę: znajdzie handler po `commandType`, target po `targetId`, wywoła `Handle`.

Zasady: jeden `CommandType` = jeden handler (duplikat nadpisuje z warningiem), jeden `TargetId` = jeden obiekt. Handler dostaje `GameObject` targetu — sam decyduje, jakiego komponentu szuka.

---

## 4. Wspólne assety (ScriptableObjects)

Wszystko leży w `Assets/RemoteControlCore/Runtime/DefaultSetup/SO/` i jest już podpięte do prefabów:

| Asset | Typ | Rola |
|---|---|---|
| `NetworkConfig` | `NetworkConfig` | port UDP, **Signaling Server Url**, **Device Name**, parametry video |
| `CommandSendChannel` | `CommandEventChannel` | UI → transport (kontroler) |
| `CommandReceivedChannel` | `CommandEventChannel` | transport → `CommandProcessor` (host) |
| `ConnectRequestChannel` | `StringEventChannel` | UI → transport: IP (natywny) lub deviceId (WebGL) |
| `DisconnectRequestChannel` | `VoidEventChannel` | UI → transport |
| `OnConnectedChannel` / `OnDisconnectedChannel` | `VoidEventChannel` | transport → UI (kontroler) |
| `OnClientConnectedChannel` / `OnClientDisconnectedChannel` | `VoidEventChannel` | transport → logika (host) |
| `HandlerRegistry` / `TargetRegistry` | registry | rejestry runtime |
| `LogChannel` | `StringEventChannel` | logi → `DebugLogUI` |

Nowe assety: **Create ▸ Remote Control ▸ …** (Config / Events / Registries).

---

## 5. Setup HOSTA

### 5a. Vision Pro (WebRTC)

1. Otwórz swoją scenę (lub `Assets/Scenes/VisionProHostScene.unity` jako przykład).
2. Przeciągnij prefab **`RemoteControl_VisionProHost`** (`Runtime/DefaultSetup/Prefabs/`). Zawiera:
   `VisionProSignalingClient` (rejestracja + heartbeat), `VisionProWebRtcHost` (WebRTC, DataChannel → `CommandReceivedChannel`, start/stop capture), `CommandProcessor`.
3. W `NetworkConfig.asset` ustaw `Signaling Server Url` (`wss://…`, lokalnie `ws://<ip-pc>:8787`) i `Device Name` (np. `Vision Pro Office`). Nazwę można też nadpisać na instancji w polu `Device Name Override` na `VisionProSignalingClient`.
4. Dodaj `CommandTarget` + handlery jak w §3.
5. (Opcjonalnie) Canvas z `TextMeshProUGUI` + `DebugLogUI` (`Log Channel` → `LogChannel.asset`) — podgląd logów `[Signaling] [WebRTC] [DataChannel] [ScreenCapture]` na urządzeniu.
6. Build visionOS na Macu — post‑procesor sam dodaje pakiet WebRTC, ReplayKit i wpisy Info.plist (szczegóły: WEBGL_VISIONOS_REMOTE.md §4).

Przepływ: start → rejestracja w signalingu → widoczny na liście → controller klika Connect → DataChannel open → `OnClientConnectedChannel` + start capture → komendy trafiają do `CommandProcessor` → disconnect: stop capture, host nadal widoczny na liście.

### 5b. Host natywny (Unity Transport, bez zmian)

1. Prefab **`RemoteControl_HostCore`** (`TransportHost` + `CommandProcessor`), autostart na porcie z `NetworkConfig.Port` (7777).
2. `CommandTarget` + handlery jak w §3. Przykład: `Assets/Scenes/HostScene.unity`.

---

## 6. Setup KLIENTA (kontrolera)

### 6a. WebGL (przeglądarka → Vision Pro)

1. Scena: `Assets/Scenes/ControllerScene.unity` (można odtworzyć: **Tools ▸ Remote Control ▸ WebRTC ▸ Build WebGL Controller Scene**). Zawiera:
   - **`RemoteControl_WebGLClientCore`** — `WebGLDiscoveryClient` (lista urządzeń z signalingu) + `WebGLRemoteTransport` (WebRTC DataChannel; nasłuchuje `CommandSendChannel`, `ConnectRequestChannel`, `DisconnectRequestChannel`, podnosi `OnConnected/OnDisconnected`).
   - **`NetworkDiscoveryPanel`** (pod Canvasem) — dropdown, Refresh, Connect, Disconnect, status, `ConnectedGroup` (RawImage + `RemoteVideoView`, przyciski R/G/B + `DemoControllerUI`), log (`DebugLogUI`). `DiscoveryBinder` sam znajduje `WebGLDiscoveryClient` w scenie.
2. `NetworkConfig.Signaling Server Url` = ten sam co host (strona po HTTPS ⇒ `wss://`).
3. Własne przyciski: §2 (najprościej `CommandButton` pod `ConnectedGroup/ControlButtons`; przy większej liczbie dodaj `HorizontalLayoutGroup`).
4. Build: Build Profiles ▸ Web, tylko `ControllerScene`; hostuj po HTTPS. Pliki `.br` wymagają nagłówka `Content-Encoding: br` (albo wyłącz kompresję w Player Settings).

Po stronie WebGL **nie dodawaj** `RemoteControl_ClientCore` (Unity Transport UDP nie działa w przeglądarce).

### 6b. Kontroler natywny (Windows/Android → host natywny, bez zmian)

1. Scena `Assets/Scenes/NativeControllerScene.unity` (zachowana stara `ControllerScene`): prefab **`RemoteControl_ClientCore`** (`TransportClient`) + Canvas z `DemoControllerUI` (pole IP, Connect/Disconnect, R/G/B).
2. Wpisz IP hosta → Connect → przyciski wysyłają `set_color` na `demo_cube`.
3. Własne przyciski: §2 — te same kanały SO, `CommandButton` działa tu identycznie.

> Panel `NetworkDiscoveryPanel` można też użyć natywnie: wystarczy implementacja `RemoteDiscoveryBase` (np. UDP broadcast), która w `DiscoveredDevice.address` poda IP hosta — panel wyśle je na `ConnectRequestChannel`, a `TransportClient` się połączy. Takiej implementacji dziś w repo nie ma.

---

## 7. Szybki test

1. `cd SignalingServer && npm install && npm start`
2. Editor: otwórz `VisionProHostScene` → Play (host rejestruje się; w Editorze **nie odpowie na offer** — brak natywnego WebRTC, to normalne). `curl http://localhost:8787/devices` pokazuje urządzenie.
3. Build WebGL → otwórz stronę → dropdown pokazuje hosta → Connect.
4. Na prawdziwym Vision Pro: po Connect pojawia się UI sterowania i video; Red → log hosta `[DataChannel] Received: [set_color] target=demo_cube value=#FF0000` → `[OK] Executed 'set_color' on 'demo_cube'` → kostka czerwona.

Debug: prefiksy logów `[Discovery] [Signaling] [WebRTC] [Video] [DataChannel] [ScreenCapture]`; tabela typowych błędów w WEBGL_VISIONOS_REMOTE.md §10.
