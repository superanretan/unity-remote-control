# Integracja i wdrożenie — od zera do działającego kontrolera

Ten dokument opisuje pełną drogę: postawienie serwera signalingowego na Vercelu, instalację paczki
`com.superanretan.remotecontrol` w **docelowym projekcie Unity**, napisanie **własnego UI kontrolera**,
setup hosta na Vision Pro i wrzucenie builda WebGL.

Paczka jest biblioteką, nie aplikacją. Nie narzuca UI: wszystko przechodzi przez kanały ScriptableObject,
więc własny interfejs kontrolera podłączasz bez dotykania kodu paczki i bez dziedziczenia po niczym.

Dokumenty towarzyszące: [WEBGL_VISIONOS_REMOTE.md](WEBGL_VISIONOS_REMOTE.md) (architektura),
[REMOTE_CONTROLLER.md](REMOTE_CONTROLLER.md) (co przynosi wersja 2.0 i co jest breaking),
[SETUP_HOST_CLIENT.md](SETUP_HOST_CLIENT.md) (przyciski → komendy),
[SignalingServer/README.md](SignalingServer/README.md) (serwer w szczegółach).

Kolejność jest istotna: najpierw A, bo adres serwera jest potrzebny w B, a build WebGL wypala go w sobie.

---

## A. Serwer signalingowy na Vercelu

Robisz to **raz**. Potem nie ruszasz, dopóki nie zmienisz kodu serwera.

Po co on jest: przeglądarka i Vision Pro muszą wymienić SDP oraz kandydatów ICE, zanim zestawią połączenie
peer-to-peer. Statyczny hosting tego nie zrobi, bo nie utrzyma WebSocketa. Sam build WebGL nigdy nie
wystarczy. Po zestawieniu połączenia serwer nie widzi już ani komend, ani obrazu.

### A1. Deploy bez importu repo

Nie musisz nic importować do Vercela ani łączyć z Gitem. CLI wysyła zawartość folderu z dysku.

```bash
cd D:\SUPERANRETAN\unity-remote-control\SignalingServer && npx vercel --prod
```

CLI poprosi o zalogowanie, potem o nazwę projektu i katalog. Bierz domyślne. Pierwszy deploy zawsze idzie na
produkcję. Zapisz adres, który wypisze na końcu, np. `https://remote-signaling.vercel.app`.

Plik `.vercelignore` pilnuje, żeby `server.js` nie poszedł na Vercela. To nie kosmetyka: Vercel traktuje
`server.js` w katalogu głównym jako wejście serwera Node i skierowałby do niego cały ruch, omijając funkcję
`api/signaling.js` i jej `maxDuration`. Lokalnie `npm start` dalej działa.

Alternatywa, jeśli wolisz Git: Add New → Project → import repo → **Root Directory `SignalingServer`** →
Framework *Other*, Build Command i Output puste.

### A2. Redis

Redis **nie jest osobnym serwerem do utrzymania**. To baza tworzona z panelu Vercela, plan darmowy,
rozliczana przez Vercela, bez maszyn i bez administracji.

Jest konieczna, bo funkcje Vercela są bezstanowe i nie ma przypinania klienta do instancji. Socket
z headsetu ląduje na instancji A, socket z przeglądarki na B, a to dwa osobne procesy, które nie dzielą
żadnej zmiennej. Bez wspólnego magazynu `offer` z przeglądarki nigdy nie dotarłby do hosta.

Panel Vercela → projekt signalingu → **Storage** → **Marketplace** → **Upstash for Redis** →
plan **Free** → region **eu-central-1 (Frankfurt)**, bo funkcje są przypięte do `fra1` w `vercel.json` →
**Create** → **Connect to Project**, środowiska **Production** i **Preview**.

Druga opcja z Marketplace to **Redis Cloud**: 30 MB, 100 operacji na sekundę, bez limitu miesięcznego.
Nasze zużycie to kilka operacji na sekundę, więc też wystarcza. Kod obsługuje oba bez zmian.

### A3. Zmienne środowiskowe

Settings → **Environment Variables**, środowiska Production i Preview.

**Najpierw sprawdź, czy jest połączenie po TCP.** Szukasz wartości zaczynającej się od `rediss://` pod
dowolną z nazw: `REDIS_URL`, `KV_URL`, `REDIS_TLS_URL`, `UPSTASH_REDIS_URL`. Serwer sprawdza je w tej
kolejności, więc jeśli któraś jest, nie robisz nic.

Jeśli widzisz tylko `KV_REST_API_URL` i `KV_REST_API_TOKEN`, to są credentiale **REST**, a REST nie
obsługuje `SUBSCRIBE`, którego serwer używa jako dzwonka między instancjami. Wtedy: karta Storage → otwórz
konsolę providera → skopiuj **TCP / RESP connection string** w formacie
`rediss://default:HASŁO@nazwa.upstash.io:6379` → dodaj ręcznie jako **`REDIS_URL`**. URL REST-owy wpisany
w te zmienne jest odrzucany przy starcie z komunikatem, co skopiować, więc nie da się tego przeoczyć.

Dodaj jeszcze dwie:

| Zmienna | Wartość |
|---|---|
| `ROOM_TOKEN` | losowy string, minimum 20 znaków, np. z `openssl rand -hex 16`. Ten sam wpiszesz w Unity |
| `ALLOWED_ORIGINS` | origin strony kontrolera, np. `https://moj-kontroler.vercel.app`. Bez ścieżki, bez ukośnika na końcu. Kilka domen po przecinku |

Pełna lista zmiennych, w tym opcjonalne `ROOM_TOKENS` (kilka niezależnych pokoi na jednym serwerze),
`DEVICE_TIMEOUT`, `PAIR_LEASE_SECONDS` i TURN, jest w [SignalingServer/README.md](SignalingServer/README.md) §2.3.

Token jedzie w publicznej stronie WebGL, więc jest zaciemnieniem, nie uwierzytelnieniem. Trzyma z dala
przypadkowych gości i crawlery. Rotuj go per wydarzenie.

### A4. Redeploy i weryfikacja

Zmienne działają dopiero od nowego deployu:

```bash
cd D:\SUPERANRETAN\unity-remote-control\SignalingServer && npx vercel --prod
```

Jedna komenda sprawdza całość:

```bash
curl "https://TWOJ-SIGNALING.vercel.app/api/health?token=TWOJ_ROOM_TOKEN"
```

Oczekujesz:

| Pole | Wartość |
|---|---|
| `state` | `ok` |
| `store` | `redis` |
| `pubsub` | `ok` |
| `lua` | `ok` |
| `misconfigured` | `null` |

`redisUrlSource` mówi, z której zmiennej wzięte jest połączenie, `ping` to opóźnienie do bazy w
milisekundach. Każdy inny wynik ma powód wpisany w `detail` albo `misconfiguredDetail`. Stan `degraded`
znaczy, że pub/sub nie działa i negocjacja będzie leniwa. Stan `error` to nieosiągalny Redis albo odrzucony
skrypt Lua; autotest przy starcie wywołuje na próbę każdy skrypt, więc literówka wychodzi natychmiast.

Rejestr urządzeń podejrzysz przez `curl "https://TWOJ-SIGNALING.vercel.app/api/devices?token=TWOJ_ROOM_TOKEN"`.
Przed uruchomieniem hosta zwróci `[]`.

### A5. Zasady eksploatacji

- **Nie deployuj serwera w trakcie prezentacji.** Stare połączenia zostają na starym deploymencie do
  zamknięcia, nowe idą na nowy. Dane w Redisie są wspólne, więc jest to przeżywalne, ale nie przy widowni.
- Region funkcji i region bazy trzymaj razem. Redis jednoregionowy, nigdy Global.
- Vercel Hobby jest wg regulaminu do użytku niekomercyjnego. Jeśli prezentacja jest komercyjna, to kwestia
  licencyjna, nie techniczna.
- Opcjonalnie: Project → Firewall → rate limit na ścieżce `/api/signaling`, np. 30 żądań na minutę na IP.

---

## B. Instalacja paczki w projekcie kontrolera

### B1. Dodanie paczki

Window → Package Manager → **+** → **Add package from git URL**:

```
https://github.com/superanretan/unity-remote-control.git?path=Assets/RemoteControlCore#vpwebgl2.0
```

Zamiast tagu możesz wskazać gałąź, ale na produkcję trzymaj się tagu. Wymagania paczki: Unity 6000.0+,
`com.unity.transport` 2.4+, `com.unity.ugui`, TextMeshPro. Zależności ciągną się same z `package.json`.

### B2. Wygeneruj własne assety ScriptableObject

**To jest krok, który łatwo przeoczyć.** Paczka zainstalowana z Git URL leży w `Library/PackageCache`
i jest **tylko do czytania**. Assetów SO z paczki nie da się edytować, a `NetworkConfig` musi dostać Twój
adres serwera i token.

Menu: **Tools ▸ Remote Control ▸ WebRTC ▸ Create SO Assets**

Tworzy komplet w `Assets/RemoteControl/SO/` w Twoim projekcie: `NetworkConfig`, `CommandSendChannel`,
`CommandReceivedChannel`, `ConnectRequestChannel`, `DisconnectRequestChannel`, `OnConnectedChannel`,
`OnDisconnectedChannel`, `OnClientConnectedChannel`, `OnClientDisconnectedChannel`, `LogChannel`,
`HostMessageSendChannel`, `HostMessageReceivedChannel`, `HandlerRegistry`, `TargetRegistry`.
Istniejących assetów nie nadpisuje, więc możesz uruchamiać wielokrotnie.

Pojedyncze assety zrobisz też przez **Create ▸ Remote Control ▸ …**.

Jeśli chcesz gotowe prefaby i sceny demonstracyjne w swoim projekcie:
**Tools ▸ Remote Control ▸ WebRTC ▸ Create Prefabs** oraz **Build WebGL Controller Scene**. Do własnego UI
nie są potrzebne, ale bywają wygodne jako referencja.

### B3. Konfiguracja `NetworkConfig`

Zaznacz `Assets/RemoteControl/SO/NetworkConfig.asset` i ustaw:

| Pole | Wartość |
|---|---|
| **Signaling Server Url** | `wss://TWOJ-SIGNALING.vercel.app/api/signaling` |
| **Signaling Token** | to samo co `ROOM_TOKEN` na serwerze |
| **Device Name** | nazwa hosta na liście, np. `Vision Pro Office` |
| **Heartbeat Interval** | 2 |
| **Device Timeout** | 15 |
| **Ice Server Entries** | jeden wpis STUN wystarcza w tej samej sieci Wi-Fi |

Ten sam asset trafia do projektu hosta na Vision Pro. Adres jest **wypalany w buildzie**, więc ustaw go
przed buildem. Token doklejany jest automatycznie jako `?token=…`, nie dopisuj go do URL-a ręcznie.

Pole zserializowane `_signalingServerUrl` celowo nie zmieniło nazwy między 1.x i 2.0, więc jeśli w swoim
projekcie nadpisujesz je w runtime po nazwie pola, ten mechanizm działa dalej.

TURN: potrzebny tylko wtedy, gdy przeglądarka i headset są w różnych sieciach. Wpis z `username` i
`credential` dodaj w `Ice Server Entries`, ale pamiętaj, że trafia do publicznego builda. Lepszy wariant to
krótkożyciowe credentiale wydawane przez serwer, opisane w [REMOTE_CONTROLLER.md](REMOTE_CONTROLLER.md) §3.2.

---

## C. Kontroler WebGL z własnym UI

### C1. Co musi być w scenie

Dwa komponenty, mogą wisieć na jednym pustym GameObjekcie:

| Komponent | Rola | Pola do przypięcia |
|---|---|---|
| `WebGLDiscoveryClient` | trzyma WebSocket do signalingu i lustrzy listę urządzeń | `Network Config`, `Log Channel`, `Poll Interval` (5) |
| `WebGLRemoteTransport` | WebRTC, DataChannel, komendy w obie strony | `Network Config`, `Command Send Channel`, `Connect Request Channel`, `Disconnect Request Channel`, `On Connected Channel`, `On Disconnected Channel`, `Host Message Received Channel`, `Log Channel` |

`WebGLRemoteTransport` ma też `Auto Request Id`, `Auto Reconnect`, `Max Reconnect Attempts` i
`Reconnect Delay`. Domyślne wartości są dobre.

**Nie dodawaj** `RemoteControl_ClientCore` ani `TransportClient` do scen WebGL. Unity Transport po UDP nie
działa w przeglądarce.

Poza WebGL oba komponenty są bezpiecznym no-opem, więc scena uruchomi się w Edytorze bez błędów, tylko bez
połączenia.

### C2. Lista urządzeń w swoim UI

`WebGLDiscoveryClient` dziedziczy po `RemoteDiscoveryBase`, więc UI nie musi wiedzieć nic o WebRTC:

```csharp
using System.Collections.Generic;
using SuperAnretan.RemoteControl;
using UnityEngine;

public class MyDevicePicker : MonoBehaviour
{
    [SerializeField] private RemoteDiscoveryBase _discovery;          // WebGLDiscoveryClient ze scen
    [SerializeField] private StringEventChannel _connectRequestChannel;   // SO: ConnectRequestChannel
    [SerializeField] private VoidEventChannel _disconnectRequestChannel;  // SO: DisconnectRequestChannel

    private readonly List<DiscoveredDevice> _devices = new();

    private void OnEnable()
    {
        _discovery.DevicesChanged += OnDevices;
        _discovery.StatusChanged += OnStatus;
        OnDevices(_discovery.Devices);
    }

    private void OnDisable()
    {
        _discovery.DevicesChanged -= OnDevices;
        _discovery.StatusChanged -= OnStatus;
    }

    private void OnDevices(IReadOnlyList<DiscoveredDevice> devices)
    {
        _devices.Clear();
        _devices.AddRange(devices);
        // Tu przebuduj swoją listę. d.deviceName na etykietę, d.IsAvailable na blokadę "busy".
    }

    private void OnStatus(string status) { /* "Searching for devices...", "Signaling reconnecting..." */ }

    public void Connect(DiscoveredDevice device) => _connectRequestChannel.Raise(device.ConnectKey);
    public void Refresh() => _discovery.Refresh();
    public void Disconnect() => _disconnectRequestChannel.Raise();
}
```

`DiscoveredDevice` ma `deviceId`, `deviceName`, `platform`, `status`, `IsAvailable` i `ConnectKey`.
`ConnectKey` to wartość, którą podnosisz na `ConnectRequestChannel`.

Nie chcesz szukać komponentu w Inspectorze: dodaj do obiektu z UI komponent `DiscoveryBinder`, który
znajduje backend w scenie przy starcie i wstrzykuje go do `NetworkDiscoveryPanel`. Do własnego UI prościej
jest przypiąć referencję ręcznie albo raz wywołać `FindFirstObjectByType<RemoteDiscoveryBase>()`.

Stan połączenia: `OnConnectedChannel` i `OnDisconnectedChannel` (`VoidEventChannel`). Na nich pokazujesz
i chowasz swój panel sterowania.

### C3. Wysyłanie komend

Zero zależności od transportu. Podnosisz `RemoteCommand` na `CommandSendChannel`:

```csharp
_commandSendChannel.Raise(new RemoteCommand("set_color", "demo_cube", "#FF0000"));
```

Pola: `commandType` to klucz handlera na hoście, `targetId` to `CommandTarget.TargetId` obiektu w scenie
hosta, `value` i `payload` interpretuje handler.

Bez kodu: dodaj komponent **`CommandButton`** na przycisk i wypełnij `Command Type`, `Target Id`, `Value`,
`Payload`, `Command Send Channel`. Opcjonalne `On Connected Channel` i `On Disconnected Channel` sprawiają,
że przycisk jest klikalny tylko po połączeniu. Wartość zmienisz w locie przez `CommandButton.SetValue`.

Potwierdzenia: ustaw `RemoteCommand.requestId` albo włącz `Auto Request Id` na `WebGLRemoteTransport`.
Host odpowie kopertą `ack` z tym samym `requestId`.

### C4. Odbiór stanu z hosta

Host wysyła `HostMessage` na DataChannelu. Każda wiadomość ląduje bez zmian na
`HostMessageReceivedChannel` (`StringEventChannel`):

```csharp
using SuperAnretan.RemoteControl;
using UnityEngine;

public class MyControllerState : MonoBehaviour
{
    [SerializeField] private StringEventChannel _hostMessageReceivedChannel;   // SO: HostMessageReceivedChannel

    private void OnEnable()  => _hostMessageReceivedChannel.OnRaised += OnHostMessage;
    private void OnDisable() => _hostMessageReceivedChannel.OnRaised -= OnHostMessage;

    private void OnHostMessage(string json)
    {
        var msg = HostMessage.FromJson(json);
        if (msg == null) return;

        if (msg.IsSnapshot)                                  // pełny stan zaraz po połączeniu
            foreach (var e in msg.SnapshotEntries()) Apply(e.topic, e.value);
        else if (msg.IsState)                                // zmiana przyrostowa
            Apply(msg.topic, msg.value);
        else if (msg.IsCapture)                              // starting | streaming | stopped | error
            ShowCaptureBadge(msg.value, msg.payload);
        else if (msg.IsAck)                                  // odpowiedź na komendę z requestId
            ClearPending(msg.requestId);
    }

    private void Apply(string topic, string value) { /* np. podświetl aktywny compartment */ }
    private void ShowCaptureBadge(string state, string detail) { }
    private void ClearPending(string requestId) { }
}
```

Dwie rzeczy warte podkreślenia. **Snapshot** przychodzi automatycznie po otwarciu DataChannelu i zawiera
ostatnią wartość każdego tematu, więc UI nigdy nie startuje z pustym stanem i nie musisz o nic pytać.
**Stan capture jest niezależny od DataChannelu**: „połączono" nie znaczy „obraz idzie", dopiero
`capture` = `streaming` to znaczy.

Wolisz zdarzenie C# od kanału SO: `WebGLRemoteTransport` wystawia `event Action<HostMessage> OnHostMessage`.

### C5. Podgląd obrazu z headsetu

Dodaj `RawImage` do Canvasu, a na tym samym obiekcie komponent **`RemoteVideoView`**. Przypnij `Target`
(ten `RawImage`), opcjonalnie `Aspect Fitter` (`AspectRatioFitter`), `On Disconnected Channel` i
`Log Channel`. Tekstura tworzy się sama w rozdzielczości strumienia. Pole `Use Html Overlay` pokazuje surowy
element `<video>` nad canvasem i służy tylko do diagnostyki.

### C6. Build WebGL i wrzucenie na Vercel

1. Player Settings → Publishing Settings. Sprawdź **Compression Format** i **Decompression Fallback**.
   Domyślnie w tym repo jest Brotli z wyłączonym fallbackiem, co wymaga nagłówków po stronie hostingu.
   Najprostsza alternatywa: włącz **Decompression Fallback**, wtedy nagłówki nie są potrzebne, kosztem
   trochę większego pobrania.
2. Build Profiles → **Web**, w liście scen tylko scena kontrolera. Build.
3. Do folderu builda, obok `index.html`, skopiuj gotowy plik nagłówków z paczki:
   `Deploy~/webgl-vercel.json` → zmień nazwę na `vercel.json`. Ustawia `Content-Encoding` dla `.br`, `.gz`
   i `.unityweb`, poprawny `Content-Type` dla `.wasm` i `.data`, oraz cache dla katalogu `Build/`.
   W repo plik leży w `Assets/RemoteControlCore/Deploy~/webgl-vercel.json`, po instalacji paczki w
   `Packages/com.superanretan.remotecontrol/Deploy~/` albo w `Library/PackageCache/…/Deploy~/`.
   Jeśli włączyłeś Decompression Fallback albo Twój projekt kontrolera już ma własny `vercel.json`, pomiń.
4. Wrzuć folder:

```bash
cd <folder-builda> && npx vercel --prod
```

Też bez importu repo. Pierwszy raz CLI zapyta o nazwę projektu; to będzie osobny projekt niż signaling.
Alternatywnie wrzuć build tak, jak robisz to dzisiaj, byle pod HTTPS.

5. Domenę, którą dostaniesz, wpisz na serwerze signalingowym w `ALLOWED_ORIGINS` i zrób redeploy serwera.

Strona po HTTPS może otwierać tylko `wss://`. Dlatego lokalny dev po LAN-ie robi się na stronie po zwykłym
`http://` z `ws://<ip-pc>:8787`, a nie na wersji z Vercela.

---

## D. Host na Vision Pro

### D1. Komponenty w scenie

Najprościej wrzucić prefab `RemoteControl_VisionProHost`. Ręcznie to trzy komponenty na jednym obiekcie:

| Komponent | Pola |
|---|---|
| `VisionProSignalingClient` | `Network Config`, `Log Channel`, `Auto Connect`, opcjonalnie `Device Name Override` |
| `VisionProWebRtcHost` | `Network Config`, `Signaling` (powyższy komponent), `Command Received Channel`, `On Client Connected Channel`, `On Client Disconnected Channel`, `Host Message Send Channel`, `Log Channel`, `Capture Backend` = Auto, `Auto Start Capture` = on, `Send Snapshot On Connect` = on, `Ack Commands` = on |
| `CommandProcessor` | `Command Received Channel`, `Handler Registry`, `Target Registry`, `Log Channel` |

`NetworkConfig` musi być **tym samym** assetem co w kontrolerze, z tym samym adresem i tokenem. Jeśli host
i kontroler to dwa różne projekty Unity, po prostu ustaw w obu identyczne wartości.

`Device Id` hosta jest trwały: generuje się raz i siedzi w `PlayerPrefs`, więc host wraca na listę pod tym
samym wpisem po restarcie i po wymuszonym reconnectcie.

### D2. Komendy: handler i target

```csharp
using SuperAnretan.RemoteControl;
using UnityEngine;

public class SetCompartmentHandler : CommandHandlerBase
{
    public override string CommandType => "set_compartment";      // musi zgadzać się z commandType z kontrolera

    public override void Handle(RemoteCommand command, GameObject target)
    {
        target.GetComponent<MyCompartmentView>()?.Show(command.value);
    }
}
```

Handler wrzuć na dowolny obiekt w scenie i przypnij `Handler Registry`. Rejestruje się sam w `OnEnable`.
Na obiekcie, którym sterujesz, dodaj `CommandTarget`, ustaw `Target Id` i przypnij `Registry`
(`TargetRegistry`). Zasady: jeden `CommandType` to jeden handler, jeden `TargetId` to jeden obiekt.

### D3. Odsyłanie stanu do kontrolera

Kod aplikacji **nie woła** `VisionProNativeBridge` bezpośrednio. Podnosisz gotowy JSON na
`HostMessageSendChannel`:

```csharp
[SerializeField] private StringEventChannel _hostMessageSendChannel;   // SO: HostMessageSendChannel

private void OnCompartmentChanged(string id) =>
    _hostMessageSendChannel.Raise(HostMessage.State("navigation", id).ToJson());
```

Albo bezpośrednio na komponencie: `visionProWebRtcHost.SetState("navigation", id)`.

Host cache'uje ostatnią wartość każdego tematu, także gdy nikt nie jest połączony, i wysyła je jednym
snapshotem po otwarciu DataChannelu. Stan capture (`starting`, `streaming`, `stopped`, `error` z kodem)
i `ack` na komendy z `requestId` idą automatycznie, nic z tym nie robisz.

Format koperty i pełna lista typów: [REMOTE_CONTROLLER.md](REMOTE_CONTROLLER.md) §1.

### D4. Build visionOS

Build robisz na Macu z Xcode. `RemoteControlVisionOSPostProcessor` sam dodaje pakiet SPM z WebRTC, linkuje
`ReplayKit`, `CoreMedia`, `CoreVideo` i wpisuje `NSLocalNetworkUsageDescription` oraz
`NSScreenCaptureUsageDescription` do Info.plist. W Xcode ustawiasz tylko Team i signing. Szczegóły i wariant
ze ScreenCaptureKit: [WEBGL_VISIONOS_REMOTE.md](WEBGL_VISIONOS_REMOTE.md) §4.

Pierwszy start capture pokazuje systemową zgodę na nagrywanie ekranu. Nie da się jej pominąć. Nagrywany
jest tylko obraz renderowany przez aplikację, bez passthrough.

W Edytorze host zarejestruje się w signalingu, ale **nie odpowie na offer**, bo nie ma natywnego WebRTC.
To normalne, w logu zobaczysz `peer-create-failed`.

---

## E. Kolejność uruchamiania i szybki test

1. Serwer: `curl /api/health?token=…` → `"state":"ok"`.
2. Host na Vision Pro: start aplikacji. W logu `[Signaling] Registered as "…"`.
   `curl /api/devices?token=…` pokazuje jeden wpis.
3. Kontroler: otwórz stronę po HTTPS. Twoja lista urządzeń dostaje wpis w ciągu paru sekund.
4. Connect. Log kontrolera: `[DataChannel] Opened`, potem `Snapshot received`, potem
   `[capture] value=streaming`. Pojawia się obraz.
5. Naciśnij swój przycisk. Log hosta: `[DataChannel] Received: [set_compartment] …`, potem
   `[OK] Executed 'set_compartment' on '…'`.
6. Zamknij kartę. Host w ciągu paru sekund przestaje nagrywać.

Sesja przeżywa wymuszone zerwanie socketa signalingowego, które na Vercelu zdarza się co najwyżej co 300 s.
Klienty łączą się ponownie z tymi samymi identyfikatorami, a rejestr i parowanie siedzą w Redisie. W logu
zobaczysz `[Signaling] reconnect in 1s` i nic więcej się nie dzieje.

---

## F. Diagnostyka

| Objaw | Sprawdź |
|---|---|
| Kontroler: `server-misconfigured:no-redis-url` | Deployment nie ma Redisa. `curl /api/health` powie dokładnie czego brakuje. Zmienne działają od nowego deployu |
| Kontroler: `unauthorized` albo `Signaling rejected` | `NetworkConfig.Signaling Token` różny od `ROOM_TOKEN` na serwerze |
| Kontroler: `origin-not-allowed` | Domena strony nie jest w `ALLOWED_ORIGINS` |
| Urządzenia nie ma na liście | Host pokazuje `[Signaling] Connected`? Ten sam adres i token po obu stronach? Ten sam pokój, jeśli używasz `ROOM_TOKENS`? |
| Lista mruga albo `Signaling reconnecting...` co 5 minut | Normalne na Vercelu, limit 300 s na socket. Sesja i lista nie są dotknięte. Jeśli są, serwer nie jest w wersji 2.0 |
| `error: device-busy` | Inny kontroler trzyma parowanie. Zwalnia je jawny `disconnect` albo wygaśnięcie lease'u po 30 s |
| Host: `Peer disconnect … (controller-gone)` | Lease parowania wygasł, czyli karta zniknęła bez `disconnect`. Host jest znowu wolny |
| ICE `failed` | Brak trasy między urządzeniami, czyli różne sieci. Potrzebny TURN. Na Vision Pro sprawdź zgodę na sieć lokalną |
| DataChannel się nie otwiera | Host w Edytorze nie odpowie na offer. Potrzebny build na urządzeniu |
| Biała strona po wrzuceniu builda | Brakuje nagłówków `Content-Encoding` dla plików `.br`. Skopiuj `vercel.json` z §C6 albo włącz Decompression Fallback |
| `capture-error` w logu hosta | `-5801` to odmowa zgody, `-5803` nieudany start, spróbuj po wyjściu i wejściu w immersive space |
| Kontroler w tle reaguje z opóźnieniem | Przeglądarki throttlują ukryte karty. Trzymaj kartę kontrolera widoczną |

Prefiksy logów: `[Discovery]`, `[Signaling]`, `[WebRTC]`, `[Video]`, `[DataChannel]`, `[ScreenCapture]`.
Wszystkie idą przez `LogChannel`, więc podłącz `DebugLogUI` z `TextMeshProUGUI` i masz je na ekranie
urządzenia.

---

## G. Ograniczenia, o których trzeba wiedzieć

- Jeden kontroler na hosta w danej chwili.
- Bez TURN oba urządzenia muszą mieć trasę między sobą, w praktyce ta sama sieć Wi-Fi. Telefon w LTE
  i headset w Wi-Fi się nie połączą.
- Vercel Hobby zamyka każdy socket po 300 s. Jest to obsłużone, ale Vision Pro gubi to, co próbowałby
  wysłać w swojej ~1-sekundowej przerwie na reconnect. Kandydaci ICE wygenerowani dokładnie wtedy przepadają;
  negocjacja ma ich wiele i 20 s timeout z ponowieniem, więc w praktyce to nie boli.
- Zgody Apple na nagrywanie ekranu nie da się pominąć ani zapamiętać za użytkownika.
- Pierwszy build visionOS wymaga sieci, bo SPM musi ściągnąć pakiet WebRTC.
