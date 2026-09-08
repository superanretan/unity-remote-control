# Remote Controller 2.0 — kanał powrotny, autoryzacja signalingu, TURN, Vercel

Ten dokument zbiera **wszystkie zmiany wersji 2.0** paczki `com.superanretan.remotecontrol`
(`Assets/RemoteControlCore`) dla ścieżki WebGL controller ↔ Apple Vision Pro host. Architekturę bazową
opisuje [WEBGL_VISIONOS_REMOTE.md](WEBGL_VISIONOS_REMOTE.md), setup krok po kroku —
[SETUP_HOST_CLIENT.md](SETUP_HOST_CLIENT.md), serwer — [SignalingServer/README.md](SignalingServer/README.md).

> **Szukasz instrukcji krok po kroku** (wdrożenie na Vercelu, instalacja paczki w swoim projekcie, własne UI
> kontrolera, host na Vision Pro, build i wrzucenie WebGL)? → [INTEGRATION.md](INTEGRATION.md).
> Ten dokument opisuje *co* przynosi wersja 2.0 i co jest breaking, a nie *jak* to wdrożyć.

Natywna ścieżka (`TransportClient` / `TransportHost` / Unity Transport) i rdzeń komend
(`RemoteCommand`, `CommandProcessor`, `CommandHandlerBase`, rejestry, kanały SO) działają bez zmian.
`RemoteCommand` dostał jedno **opcjonalne** pole (`requestId`) — stare hosty i kontrolery ignorują je.

---

## 0. Co jest breaking, co trzeba przewiązać

| Zmiana | Breaking? | Co zrobić u konsumenta |
|---|---|---|
| Serwer signalingowy przepisany (Vercel + Redis, nowy protokół `register-controller{clientId}`, `register-device{deviceTimeout}`) | **tak** — stary `server.js` 1.x nie współpracuje z klientami 2.0 w pełni (brak stabilnego `clientId` po stronie serwera → sesja pada po reconnectcie) | wdrożyć nowy `SignalingServer/` (Vercel lub `npm start`), zaktualizować URL |
| Kształt URL-a na Vercelu: `wss://<projekt>.vercel.app/api/signaling` | tak (tylko wartość) | `NetworkConfig.SignalingServerUrl` — pole `_signalingServerUrl` **nie zmieniło nazwy**, override z `remotecontrol.json` dalej działa |
| `NetworkConfig.IceServersJson()` zwraca tablicę obiektów `RTCIceServer` zamiast tablicy stringów | **tak** dla zewnętrznych konsumentów tej metody; `.jslib` i `.mm` w paczce akceptują oba formaty | nic, jeśli używasz tylko komponentów paczki |
| `NetworkConfig._iceServers` (string[]) → `_iceServerEntries` (lista `IceServerEntry`) | nie — migracja automatyczna przy wczytaniu assetu, stare wartości zachowane | zapisz asset (Ctrl+S) po otwarciu projektu, żeby nowy kształt trafił na dysk |
| `NetworkConfig.DeviceTimeout` domyślnie 6 → **15 s** i jest teraz faktycznie wysyłany do serwera | nie | jeśli asset miał 6 — podnieś do 15 (Inspector) |
| Nowe pole `NetworkConfig.SignalingToken` | nie (puste = serwer w trybie otwartym) | wpisz `ROOM_TOKEN` z serwera |
| Nowe kanały SO `HostMessageSendChannel`, `HostMessageReceivedChannel` | nie — prefaby `RemoteControl_VisionProHost` i `RemoteControl_WebGLClientCore` mają je podpięte | **tylko** jeśli masz własne GameObjecty z `VisionProWebRtcHost` / `WebGLRemoteTransport` poza prefabem: przeciągnij assety z `Runtime/DefaultSetup/SO/` w Inspectorze |
| `RemoteCommand.requestId` | nie (opcjonalne, domyślnie `""`) | — |

Sceny i prefaby z 1.x otwierają się bez ręcznego przewiązywania. Menu **Tools ▸ Remote Control ▸ WebRTC ▸
Create Prefabs** tworzy brakujące kanały SO i przewiązuje prefaby, gdyby konsument chciał je zregenerować.

---

## 1. Kanał powrotny host → kontroler

### 1.1 Koperta `HostMessage` (`Runtime/Core/HostMessage.cs`)

```json
{"messageType":"state","schemaVersion":1,"topic":"navigation","value":"compartment-a","payload":"","requestId":""}
```

| Pole | Znaczenie |
|---|---|
| `messageType` | `state` \| `snapshot` \| `capture` \| `ack` — odbiorca **ignoruje** nieznane typy (rozszerzalność) |
| `schemaVersion` | dziś `1` (`HostMessage.CurrentSchemaVersion`) |
| `topic` | co się zmieniło (`navigation`, `capture`, dla `ack` — `commandType`) |
| `value` | wartość główna |
| `payload` | opcjonalny JSON (wpisy snapshotu, szczegóły błędu) |
| `requestId` | dla `ack` — kopia `RemoteCommand.requestId` |

| `messageType` | `topic` | `value` | `payload` |
|---|---|---|---|
| `state` | dowolny | dowolny | opcjonalny JSON |
| `snapshot` | `""` | `""` | `{"entries":[{"topic","value","payload"}]}` — pełny stan tuż po otwarciu DataChannelu |
| `capture` | `capture` | `starting` \| `streaming` \| `stopped` \| `error` | dla `error`: kod (`-5801` = odmowa zgody, `-5803` = start nie powiódł się, `replaykit-unavailable`, `native-unavailable`) |
| `ack` | `commandType` komendy | `dispatched` \| `rejected` | dla `rejected`: powód (`invalid-command`) |

`ToJson()` / `FromJson()` jak w `RemoteCommand`. Pomocniki: `HostMessage.State(topic, value)`,
`HostMessage.Capture(state, detail)`, `HostMessage.Ack(command, result)`, `HostMessage.Snapshot(entries)`,
`msg.SnapshotEntries()`.

**`ack` = „sparsowane i podniesione na `CommandReceivedChannel`".** Czy handler istniał i czy się udał, to
sprawa `CommandProcessor` (rdzeń bez zmian). Wynik działania handlera aplikacja raportuje jako `state`
(np. po zmianie compartmentu handler podnosi `HostMessage.State("navigation","compartment-a")`).

### 1.2 Strona hosta (`VisionProWebRtcHost`)

Publiczne API:

```csharp
bool TrySend(HostMessage message);   // false gdy brak otwartego DataChannelu
bool TrySend(string hostMessageJson);
bool SetState(string topic, string value, string payload = "");   // cache + wysyłka
void ClearState(string topic);
string CaptureState { get; }         // starting | streaming | stopped | error
```

Wejście przez kanał SO — **`HostMessageSendChannel`** (`StringEventChannel`, pole
`_hostMessageSendChannel`). Aplikacja hosta podnosi gotowy JSON:

```csharp
[SerializeField] StringEventChannel _hostMessageSendChannel;   // SO: HostMessageSendChannel.asset

void OnCompartmentChanged(string id) =>
    _hostMessageSendChannel.Raise(HostMessage.State("navigation", id).ToJson());
```

Zasady:
- Kod aplikacji **nie woła** `VisionProNativeBridge.SendData` — `TrySend` sprawdza `IsConnected`, a wysyłka
  przez statyczny bridge omijałaby kontrolę połączenia i wiązała domenę z visionOS.
- Każdy `state` jest **cache'owany per `topic`** (także gdy nikt nie jest połączony). Po `datachannel-open`
  host wysyła jeden `snapshot` ze wszystkimi tematami i aktualny `capture`, potem zmiany przyrostowo.
  Kontroler zawsze startuje z pełnym stanem — zero pracy w aplikacji.
- Stan capture jest raportowany **niezależnie** od DataChannelu: `starting` przy `StartCapture`,
  `streaming` gdy ReplayKit/ScreenCaptureKit faktycznie dostarcza klatki, `stopped`, `error` z kodem.
  „DataChannel open" ≠ „obraz idzie".
- Komenda z niepustym `requestId` dostaje `ack` (`_ackCommands`, domyślnie włączone).

Nowe pola Inspectora: `Host Message Send Channel`, `Send Snapshot On Connect` (true), `Ack Commands` (true).

### 1.3 Strona kontrolera (`WebGLRemoteTransport`)

Każda wiadomość tekstowa z DataChannelu jest podnoszona **bez zmian** na
**`HostMessageReceivedChannel`** (`StringEventChannel`, pole `_hostMessageReceivedChannel`).
Dodatkowo typowane zdarzenie `event Action<HostMessage> OnHostMessage`.

```csharp
[SerializeField] StringEventChannel _hostMessageReceivedChannel;   // SO: HostMessageReceivedChannel.asset

void OnEnable()  => _hostMessageReceivedChannel.OnRaised += OnHostMessage;
void OnDisable() => _hostMessageReceivedChannel.OnRaised -= OnHostMessage;

void OnHostMessage(string json)
{
    var msg = HostMessage.FromJson(json);
    if (msg == null) return;
    if (msg.IsSnapshot) foreach (var e in msg.SnapshotEntries()) Apply(e.topic, e.value);
    else if (msg.IsState)  Apply(msg.topic, msg.value);           // np. podświetl aktywny compartment
    else if (msg.IsCapture) _videoBadge.text = msg.value;          // starting / streaming / stopped / error
    else if (msg.IsAck)    _pending.Remove(msg.requestId);
}
```

Korelacja żądań: ustaw `command.requestId` samodzielnie albo włącz `Auto Request Id` na
`WebGLRemoteTransport` (domyślnie **wyłączone**) — każda komenda bez `requestId` dostanie świeży
(`NextRequestId()`), a host odpowie `ack`.

---

## 2. Uwierzytelnianie serwera signalingowego

| Element | Gdzie | Wartość |
|---|---|---|
| Token współdzielony | serwer: env `ROOM_TOKEN` (lub `ROOM_TOKENS=nazwa=token,…` dla kilku pokoi) | dowolny losowy string |
| | Unity: `NetworkConfig.SignalingToken` (pole `_signalingToken`) | ten sam string |
| Allowlista Origin | serwer: env `ALLOWED_ORIGINS=https://<kontroler>.vercel.app` | brak nagłówka `Origin` (Vision Pro `ClientWebSocket`) przepuszczany — bramką jest token |
| Rate limit upgrade'u | Vercel Firewall, ścieżka `/api/signaling` | np. 30 req/min/IP |

Jak to działa:
- Oba klienty łączą się na `NetworkConfig.SignalingConnectUrl` = `SignalingServerUrl` + `?token=…`
  (`NetworkConfig.BuildConnectUrl` nie dubluje tokenu, jeśli URL już go ma). `SignalingServerUrl`
  zostaje surowe — override z `remotecontrol.json` po nazwie pola `_signalingServerUrl` działa jak dotąd.
- Serwer bez poprawnego tokenu / z niedozwolonym Origin przyjmuje upgrade tylko po to, żeby odesłać
  `{"type":"error","message":"unauthorized" | "origin-not-allowed"}` i zamyka socket kodem **4401 / 4403**.
  Na `LogChannel` po obu stronach ląduje
  `[Signaling] ERROR — server rejected the connection: unauthorized. Check NetworkConfig.SignalingToken against the server's ROOM_TOKEN.`
  (kontroler dodatkowo dostaje status „Signaling rejected: …" w panelu). Surowe HTTP 401 dałoby w
  przeglądarce nieme `code=1006`.
- `fromId` jest zawsze stemplowany z socketa — podszycie się pod inny `deviceId`/`clientId` jest niemożliwe.
- Pokoje (opcjonalnie): `ROOM_TOKENS=show=tokA,test=tokB` → dwie pary host-kontroler na jednym serwerze,
  wzajemnie niewidoczne na liście urządzeń. Klucze Redis są prefiksowane nazwą pokoju.
- Walidacja wejścia: `deviceName` ≤ 64 znaki, znaki sterujące usuwane, nie-stringi zastępowane;
  `deviceId`/`clientId` `^[A-Za-z0-9_-]{8,64}$`; `sessionId` walidowany; 256 KB na wiadomość.

**Uczciwie o tokenie:** trafia do publicznego bundla WebGL i do URL-a. To zaciemnienie przeciw
przypadkowym gościom i crawlerom, nie uwierzytelnianie. Rotuj per wydarzenie.

Wymagane zmienne środowiskowe serwera i wdrożenie za TLS: [SignalingServer/README.md](SignalingServer/README.md)
§2 (Vercel: `REDIS_URL`, `ROOM_TOKEN`, `ALLOWED_ORIGINS`, `DEVICE_TIMEOUT`, `PAIR_LEASE_SECONDS`, …) i §8
(self-hosting: `PORT`, `HOST`, `TLS_CERT`, `TLS_KEY` lub reverse proxy Caddy/nginx).

---

## 3. TURN z uwierzytelnianiem

### 3.1 Nowy format w `NetworkConfig`

```csharp
[Serializable] public class IceServerEntry { public string[] urls; public string username; public string credential; }
IReadOnlyList<IceServerEntry> IceServerEntries   // nowe
string[] IceServers                              // zachowane (płaska lista URL-i, bez credentiali)
string IceServersJson()                          // NOWY FORMAT, patrz niżej
```

`IceServersJson()`:

```json
[{"urls":["stun:stun.l.google.com:19302"]},
 {"urls":["turn:turn.example.com:3478?transport=udp","turns:turn.example.com:5349"],"username":"u","credential":"c"}]
```

- `.jslib` (`parseIceServers`) przekazuje `username`/`credential` do `RTCPeerConnection`; akceptuje też stary
  format (`["stun:…"]`).
- `.mm` (`RCParseIceServers`) buduje **po jednym** `RTCIceServer` na wpis —
  `initWithURLStrings:username:credential:` gdy są credentiale, `initWithURLStrings:` gdy nie. Wcześniej
  wszystkie URL-e trafiały do jednego IceServera.
- Migracja: stare `_iceServers` (string[]) jest wczytywane przez `ISerializationCallbackReceiver` i
  zamieniane na wpisy bez credentiali; pole legacy jest ukryte (`HideInInspector`) i czyszczone przy
  następnym zapisie. Wartości z istniejących `NetworkConfig.asset` nie giną.

### 3.2 Sekrety — krótkożyciowe credentiale z serwera signalingowego

Statyczne credentiale w assecie wylądowałyby w publicznym buildzie WebGL. Zamiast tego serwer mintuje je
(schemat coturn `use-auth-secret` / TURN REST API):

```
TURN_URLS=turn:turn.example.com:3478?transport=udp,turns:turn.example.com:5349
TURN_SECRET=<static-auth-secret coturna>
TURN_TTL_SECONDS=3600
```

Odpowiedź `registered` niesie `iceServers:[{urls, username:"<expiry>:<id>", credential:base64(HMAC-SHA1(secret, username))}]`.
Przeglądarka **scala** je z wpisami z `NetworkConfig` przy tworzeniu `RTCPeerConnection` (zaimplementowane).
Sekret nie opuszcza serwera; wyciek credentiala wygasa po `TURN_TTL_SECONDS`.

**Vision Pro** korzysta dziś tylko z wpisów `NetworkConfig` — host w tej samej sieci co TURN zwykle nie
potrzebuje TURN-a, a ścieżka hosta jest celowo nietknięta. Follow-up (ok. 15 linii): pole `iceServers`
w `SignalingMessage` → `VisionProSignalingClient` zapamiętuje je z `registered` →
`VisionProWebRtcHost.OnOffer` scala JSON przed `CreatePeer`.

Dziś (LAN) TURN nie jest blokerem — STUN wystarczy. Wyjście poza LAN = TURN po obu stronach + powyższe.

---

## 4. Signaling na Vercelu (WebSocket + Redis)

Szczegóły i uzasadnienia: [SignalingServer/README.md](SignalingServer/README.md). W skrócie:

- **URL:** `wss://<projekt>.vercel.app/api/signaling` (token doklejany automatycznie). Lokalnie dalej
  `ws://<pc-ip>:8787` (`npm start`, `/` i `/api/signaling` akceptowane).
- **Hobby:** każdy socket jest zamykany po 300 s. Klienty łączą się ponownie (~1 s) z tymi samymi id;
  serwer trzyma rejestr, parowanie i skrzynki wiadomości w Redisie, więc **zerwanie socketa nic nie znaczy** —
  ani dla listy urządzeń, ani dla sesji WebRTC. `disconnect{host-timeout}` idzie tylko przy nieświeżym
  `lastSeen`, `disconnect{controller-gone}` tylko po wygaśnięciu lease'u parowania (30 s bez odświeżenia
  przez jakąkolwiek instancję).
- **Stabilny `clientId` kontrolera:** generowany w przeglądarce (`crypto.getRandomValues`, 128 bit),
  trzymany w `sessionStorage` (przeżywa F5), chroniony **Web Lockiem** — zduplikowana karta dostaje nowy id.
  Dzięki temu `fromId` po reconnectcie zgadza się z `_controllerId` hosta i guardy `ice-candidate` /
  `disconnect` w `VisionProWebRtcHost` dalej działają.
- **`DEVICE_TIMEOUT`:** host wysyła `NetworkConfig.DeviceTimeout` (15 s) w `register-device`; env serwera to
  tylko fallback. Wartości nie mogą się już rozjechać.
- Zamknięcie karty: `beforeunload` + `pagehide` (pomijając `persisted`) → `disconnect{page-unload}`;
  fallback: wygaśnięcie lease'u; ostatecznie własna detekcja hosta (ICE / DataChannel).
- Testy: `npm test` (25 scenariuszy, w tym utrata socketa w trakcie negocjacji, arbitraż dwóch ofert,
  eksmisja duplikatu, token/Origin, rozpoznanie zmiennej z Redisem), `npm run test:soak`.

### 4a. Wdrożenie

Krok po kroku, z komendami: **[INTEGRATION.md](INTEGRATION.md) §A**. W skrócie: `npx vercel --prod`
w katalogu `SignalingServer` (bez importu repo), baza Redis z panelu Storage → Marketplace, trzy zmienne
środowiskowe, redeploy, weryfikacja przez `curl /api/health?token=…`.

Bez działającego Redisa serwer nie udaje, że działa: log dostaje `[FATAL]`, `/api/health` zwraca 503, a każda
rejestracja hosta i kontrolera dostaje `{"type":"error","message":"server-misconfigured:<slug>"}`, więc powód
widać na `LogChannel` w Unity, a nie jako pustą listę urządzeń. Autotest przy starcie pinguje Redisa,
sprawdza dzwonek pub/sub i wywołuje na próbę każdy skrypt Lua. Połączenie jest czytane z `REDIS_URL`,
`KV_URL`, `REDIS_TLS_URL` lub `UPSTASH_REDIS_URL`, a URL REST-owy jest odrzucany z instrukcją, co skopiować.

---

## 5. Pliki

```
Nowe
  Assets/RemoteControlCore/Runtime/Core/HostMessage.cs
  Assets/RemoteControlCore/Runtime/DefaultSetup/SO/HostMessageSendChannel.asset
  Assets/RemoteControlCore/Runtime/DefaultSetup/SO/HostMessageReceivedChannel.asset
  SignalingServer/src/{config,validate,turn,bootstrap,signaling}.js, src/store/{redis,memory}.js
  SignalingServer/api/{signaling,devices}.js, vercel.json, test/*
  REMOTE_CONTROLLER.md
Zmienione
  Runtime/Network/NetworkConfig.cs            token, IceServerEntry + migracja, IceServersJson(), DeviceTimeout 15
  Runtime/Core/RemoteCommand.cs               + requestId
  Runtime/VisionOS/VisionProWebRtcHost.cs     TrySend, HostMessageSendChannel, snapshot, capture state, ack
  Runtime/VisionOS/VisionProSignalingClient.cs SignalingConnectUrl, deviceTimeout w register-device, log powodu zamknięcia
  Runtime/VisionOS/SignalingMessage.cs        + deviceTimeout
  Runtime/WebGL/WebGLRemoteTransport.cs       HostMessageReceivedChannel, OnHostMessage, Auto Request Id
  Runtime/WebGL/WebGLDiscoveryClient.cs       SignalingConnectUrl, brak mrugania listy, signaling-rejected
  Runtime/Plugins/WebGL/WebGLRemoteBridge.jslib  clientId, Web Locks, pagehide, ICE obiektowe + TURN z serwera
  Runtime/Plugins/visionOS/WebRtcHostBridge.mm   RCParseIceServers (po jednym IceServerze na wpis)
  Runtime/DefaultSetup/Prefabs/RemoteControl_VisionProHost.prefab, RemoteControl_WebGLClientCore.prefab
  Runtime/DefaultSetup/SO/NetworkConfig.asset
  Editor/RemoteControlSetupBuilder.cs
  SignalingServer/server.js, package.json, README.md
  WEBGL_VISIONOS_REMOTE.md, SETUP_HOST_CLIENT.md, Assets/RemoteControlCore/CHANGELOG.md, package.json (2.0.0)
```

## 6. Release

Wersja paczki: `2.0.2` (`Assets/RemoteControlCore/package.json`). Proponowany tag: `vpwebgl2.0`.
Konsument wskazuje go w `Packages/manifest.json`:

```json
"com.superanretan.remotecontrol": "https://github.com/superanretan/unity-remote-control.git?path=Assets/RemoteControlCore#vpwebgl2.0"
```

Zmiany są w working tree (bez commitu — do przeglądu). Po akceptacji:

```bash
git add -A && git commit -m "2.0.0: host→controller return channel, signaling auth, TURN credentials, Vercel+Redis signaling" && git tag vpwebgl2.0 && git push && git push --tags
```
