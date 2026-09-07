# Setup hosta i klienta — przyciski, komendy, sceny

Ten dokument opisuje **jak dziś działa wysyłanie komend z przycisków** i **jak od zera skonfigurować hosta i klienta** (natywnego i WebGL).
Architektura sieciowa WebGL ↔ Vision Pro jest opisana w [WEBGL_VISIONOS_REMOTE.md](WEBGL_VISIONOS_REMOTE.md),
zmiany 2.0 (kanał powrotny host → kontroler, token signalingu, TURN, Vercel) — w [REMOTE_CONTROLLER.md](REMOTE_CONTROLLER.md).
Używasz paczki w osobnym projekcie i piszesz własne UI kontrolera? Zacznij od [INTEGRATION.md](INTEGRATION.md) —
tam jest wdrożenie serwera na Vercelu, instalacja paczki, generowanie assetów SO i build WebGL.

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
{"commandType":"set_color","targetId":"demo_cube","value":"#FF0000","payload":"","requestId":""}
```

| Pole | Znaczenie |
|---|---|
| `commandType` | klucz handlera po stronie hosta (`ICommandHandler.CommandType`) |
| `targetId` | `CommandTarget.TargetId` obiektu w scenie hosta |
| `value` | wartość główna (interpretuje ją handler) |
| `payload` | opcjonalny dodatkowy JSON |
| `requestId` | opcjonalne (2.0): gdy niepuste, host WebRTC odpowiada `HostMessage{messageType:"ack", requestId}` |

W drugą stronę (tylko WebRTC, 2.0) host wysyła `HostMessage` — `{"messageType":"state","schemaVersion":1,"topic":"navigation","value":"compartment-a","payload":"","requestId":""}` — patrz [REMOTE_CONTROLLER.md §1](REMOTE_CONTROLLER.md).

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
| `NetworkConfig` | `NetworkConfig` | port UDP, **Signaling Server Url**, **Signaling Token**, **Device Name**, **Device Timeout** (15 s), **Ice Server Entries** (STUN/TURN + credentiale), parametry video |
| `CommandSendChannel` | `CommandEventChannel` | UI → transport (kontroler) |
| `CommandReceivedChannel` | `CommandEventChannel` | transport → `CommandProcessor` (host) |
| `ConnectRequestChannel` | `StringEventChannel` | UI → transport: IP (natywny) lub deviceId (WebGL) |
| `DisconnectRequestChannel` | `VoidEventChannel` | UI → transport |
| `OnConnectedChannel` / `OnDisconnectedChannel` | `VoidEventChannel` | transport → UI (kontroler) |
| `OnClientConnectedChannel` / `OnClientDisconnectedChannel` | `VoidEventChannel` | transport → logika (host) |
| `HostMessageSendChannel` | `StringEventChannel` | logika hosta → `VisionProWebRtcHost` → DataChannel (JSON `HostMessage`, 2.0) |
| `HostMessageReceivedChannel` | `StringEventChannel` | `WebGLRemoteTransport` → UI kontrolera (JSON `HostMessage`, 2.0) |
| `HandlerRegistry` / `TargetRegistry` | registry | rejestry runtime |
| `LogChannel` | `StringEventChannel` | logi → `DebugLogUI` |

Nowe assety: **Create ▸ Remote Control ▸ …** (Config / Events / Registries).

---

## 5. Setup HOSTA

### 5a. Vision Pro (WebRTC)

1. Otwórz swoją scenę (lub `Assets/Scenes/VisionProHostScene.unity` jako przykład).
2. Przeciągnij prefab **`RemoteControl_VisionProHost`** (`Runtime/DefaultSetup/Prefabs/`). Zawiera:
   `VisionProSignalingClient` (rejestracja + heartbeat + reconnect), `VisionProWebRtcHost` (WebRTC, DataChannel → `CommandReceivedChannel`, start/stop capture, kanał powrotny `HostMessageSendChannel`), `CommandProcessor`.
3. W `NetworkConfig.asset` ustaw:
   - `Signaling Server Url` — **Vercel:** `wss://<projekt>.vercel.app/api/signaling`; lokalnie `ws://<ip-pc>:8787`. Pole zserializowane to nadal `_signalingServerUrl` (override z `remotecontrol.json` działa bez zmian).
   - `Signaling Token` — wartość `ROOM_TOKEN` z serwera (doklejana jako `?token=…`). Puste = serwer w trybie otwartym.
   - `Device Name` (np. `Vision Pro Office`; można nadpisać na instancji w `Device Name Override` na `VisionProSignalingClient`).
   - `Device Timeout` = **15** (host wysyła tę wartość do serwera w `register-device`; musi być większa niż przerwa na reconnect po wymuszonym zamknięciu socketa na Vercelu, 1–10 s).
   - `Ice Server Entries` — domyślnie jeden STUN. TURN: dodaj wpis z `urls` + `username` + `credential` (uwaga: trafiają do buildu; lepiej krótkożyciowe credentiale z serwera — REMOTE_CONTROLLER.md §3).
4. Dodaj `CommandTarget` + handlery jak w §3.
5. (2.0) **Stan zwrotny do kontrolera:** w swojej logice podnoś `HostMessage.State(topic, value).ToJson()` na `HostMessageSendChannel.asset` (albo wołaj `VisionProWebRtcHost.SetState`). Host cache'uje ostatnią wartość per temat i po otwarciu DataChannelu wysyła pełny snapshot, potem zmiany. Stan capture (`starting/streaming/stopped/error`) i `ack` na komendy z `requestId` idą automatycznie. Nie wołaj `VisionProNativeBridge` bezpośrednio.
6. (Opcjonalnie) Canvas z `TextMeshProUGUI` + `DebugLogUI` (`Log Channel` → `LogChannel.asset`) — podgląd logów `[Signaling] [WebRTC] [DataChannel] [ScreenCapture]` na urządzeniu. Odrzucenie przez serwer (`unauthorized`) jest tam widoczne.
7. Build visionOS na Macu — post‑procesor sam dodaje pakiet WebRTC, ReplayKit i wpisy Info.plist (szczegóły: WEBGL_VISIONOS_REMOTE.md §4).

Przepływ: start → rejestracja w signalingu (`?token=…`) → widoczny na liście → controller klika Connect → DataChannel open → `OnClientConnectedChannel` + snapshot `HostMessage` + start capture (`capture: starting → streaming`) → komendy trafiają do `CommandProcessor` (+ `ack`) → disconnect: stop capture, host nadal widoczny na liście.
Zerwanie socketa signalingu (co ≤300 s na Vercelu) jest przejrzyste: klient łączy się ponownie z tym samym `deviceId`, sesja WebRTC trwa.

### 5b. Host natywny (Unity Transport, bez zmian)

1. Prefab **`RemoteControl_HostCore`** (`TransportHost` + `CommandProcessor`), autostart na porcie z `NetworkConfig.Port` (7777).
2. `CommandTarget` + handlery jak w §3. Przykład: `Assets/Scenes/HostScene.unity`.

---

## 6. Setup KLIENTA (kontrolera)

### 6a. WebGL (przeglądarka → Vision Pro)

1. Scena: `Assets/Scenes/ControllerScene.unity` (można odtworzyć: **Tools ▸ Remote Control ▸ WebRTC ▸ Build WebGL Controller Scene**). Zawiera:
   - **`RemoteControl_WebGLClientCore`** — `WebGLDiscoveryClient` (lista urządzeń z signalingu) + `WebGLRemoteTransport` (WebRTC DataChannel; nasłuchuje `CommandSendChannel`, `ConnectRequestChannel`, `DisconnectRequestChannel`, podnosi `OnConnected/OnDisconnected` oraz **`HostMessageReceivedChannel`** z każdą wiadomością zwrotną hosta).
   - **`NetworkDiscoveryPanel`** (pod Canvasem) — dropdown, Refresh, Connect, Disconnect, status, `ConnectedGroup` (RawImage + `RemoteVideoView`, przyciski R/G/B + `DemoControllerUI`), log (`DebugLogUI`). `DiscoveryBinder` sam znajduje `WebGLDiscoveryClient` w scenie.
2. `NetworkConfig.Signaling Server Url` = ten sam co host (**Vercel:** `wss://<projekt>.vercel.app/api/signaling`; strona po HTTPS ⇒ `wss://`), `Signaling Token` = ten sam `ROOM_TOKEN`. Domena, na której hostujesz build, musi być w `ALLOWED_ORIGINS` serwera.
3. Własne przyciski: §2 (najprościej `CommandButton` pod `ConnectedGroup/ControlButtons`; przy większej liczbie dodaj `HorizontalLayoutGroup`).
4. (2.0) **Reakcja na stan hosta:** zasubskrybuj `HostMessageReceivedChannel.asset` (`StringEventChannel`), parsuj `HostMessage.FromJson(json)`: `IsSnapshot` → `SnapshotEntries()`, `IsState` → `topic/value` (np. podświetl aktywny compartment), `IsCapture` → `value` = `starting/streaming/stopped/error` (czy obraz faktycznie leci), `IsAck` → `requestId`. Chcesz `ack` na każdą komendę — włącz `Auto Request Id` na `WebGLRemoteTransport` albo ustaw `command.requestId` sam.
5. Build: Build Profiles ▸ Web, tylko `ControllerScene`; hostuj po HTTPS (Vercel). Pliki `.br` wymagają nagłówka `Content-Encoding: br` (albo wyłącz kompresję w Player Settings).

Przeglądarka trzyma stabilny `clientId` w `sessionStorage` (F5 zachowuje sesję parowania; zduplikowana karta dostaje nowy id przez Web Locks). Zerwanie socketa signalingu co ≤300 s (Vercel) nie rusza sesji WebRTC ani listy urządzeń. Zamknięcie karty wysyła `disconnect` (`beforeunload` + `pagehide`), a Vision Pro przestaje nagrywać w ciągu kilku sekund.

Po stronie WebGL **nie dodawaj** `RemoteControl_ClientCore` (Unity Transport UDP nie działa w przeglądarce).

### 6b. Kontroler natywny (Windows/Android → host natywny, bez zmian)

1. Scena `Assets/Scenes/NativeControllerScene.unity` (zachowana stara `ControllerScene`): prefab **`RemoteControl_ClientCore`** (`TransportClient`) + Canvas z `DemoControllerUI` (pole IP, Connect/Disconnect, R/G/B).
2. Wpisz IP hosta → Connect → przyciski wysyłają `set_color` na `demo_cube`.
3. Własne przyciski: §2 — te same kanały SO, `CommandButton` działa tu identycznie.

> Panel `NetworkDiscoveryPanel` można też użyć natywnie: wystarczy implementacja `RemoteDiscoveryBase` (np. UDP broadcast), która w `DiscoveredDevice.address` poda IP hosta — panel wyśle je na `ConnectRequestChannel`, a `TransportClient` się połączy. Takiej implementacji dziś w repo nie ma.

---

## 7. Szybki test

1. `cd SignalingServer && npm install && npm start` (lokalnie bez Redisa; produkcja = Vercel, patrz `SignalingServer/README.md` §2). `NetworkConfig.Signaling Token` puste, gdy serwer nie ma `ROOM_TOKEN`.
2. Editor: otwórz `VisionProHostScene` → Play (host rejestruje się; w Editorze **nie odpowie na offer** — brak natywnego WebRTC, to normalne). `curl http://localhost:8787/devices` pokazuje urządzenie; znika ~15 s po zatrzymaniu Play.
3. Build WebGL → otwórz stronę → dropdown pokazuje hosta → Connect.
4. Na prawdziwym Vision Pro: po Connect pojawia się UI sterowania i video; log kontrolera pokazuje `[DataChannel] Snapshot received (…)` i `[DataChannel] Received: [capture] topic=capture value=streaming`; Red → log hosta `[DataChannel] Received: [set_color] target=demo_cube value=#FF0000` → `[OK] Executed 'set_color' on 'demo_cube'` → kostka czerwona.

Debug: prefiksy logów `[Discovery] [Signaling] [WebRTC] [Video] [DataChannel] [ScreenCapture]`; tabela typowych błędów w WEBGL_VISIONOS_REMOTE.md §10.
