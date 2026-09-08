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

Kolejność jest wymuszona: najpierw A, bo adres serwera jest potrzebny w B, a build WebGL wypala go w sobie.
Sekcja A prowadzi od pustego konta Vercela do dwóch działających projektów.

---

## A. Vercel od zera

Powstaną **dwa projekty** na jednym koncie:

| Projekt | Co to jest | Skąd wdrażany |
|---|---|---|
| signaling | funkcja Node z WebSocketem, pośrednik do zestawienia połączenia | folder `SignalingServer/` |
| kontroler | statyczna strona, czyli build WebGL | folder z buildem, np. `Builds/WebGL/` |

Dlaczego dwa, a nie jeden: nowy build kontrolera nie może redeployować signalingu, bo stare połączenia
przechodzą wtedy na nowy deployment. Przy dwóch projektach signaling stawiasz raz i nigdy go nie ruszasz.

**Kolejność jest wymuszona przez zależności.** Build WebGL wypala w sobie adres signalingu, a signaling
potrzebuje domeny kontrolera do allowlisty Origin. Dlatego: najpierw signaling, potem adres do Unity,
potem build i upload kontrolera, na końcu domena kontrolera z powrotem do signalingu.

Po co w ogóle signaling: przeglądarka i Vision Pro muszą wymienić SDP oraz kandydatów ICE, zanim zestawią
połączenie peer-to-peer. Statyczny hosting tego nie zrobi, bo nie utrzyma WebSocketa. Sam build WebGL nigdy
nie wystarczy. Po zestawieniu połączenia serwer nie widzi już ani komend, ani obrazu.

### Ściągawka bez teorii

Jeśli nie chcesz czytać wyjaśnień, rób dokładnie to. Uzasadnienia są w podsekcjach niżej.

Trzy wartości, które zapisujesz sobie w trakcie i wpisujesz w kilku miejscach:

| Wartość | Skąd | Gdzie potem |
|---|---|---|
| adres signalingu | krok 7 | Unity, krok 24 |
| `ROOM_TOKEN` | wymyślasz w kroku 18 | Vercel krok 18, health check krok 20, Unity krok 25 |
| adres kontrolera | krok 34 | Vercel krok 36 |

**Przygotowanie**

1. Wejdź na vercel.com, kliknij Sign Up, zrób konto, wybierz plan Hobby.
2. Otwórz terminal.
3. Wpisz `npx vercel login` i potwierdź logowanie w przeglądarce.

**Serwer signalingowy**

4. `cd D:\SUPERANRETAN\unity-remote-control\SignalingServer`
5. `npx vercel --prod`
6. Odpowiedz na pytania: **Which team** swoje konto, **Which project** `Create a new project`,
   **Name** `remote-signaling`, **Connect this Git repository** `no`, **Customize settings** `no`.
7. Zapisz adres z linii **Production**, np. `https://remote-signaling.vercel.app`.
8. W panelu Vercela otwórz projekt `remote-signaling`.
9. Settings → Functions. Upewnij się, że **Fluid Compute** jest włączone. Jeśli nie, włącz i zapisz.
10. Zakładka Storage → Marketplace → **Upstash for Redis** → Install.
11. Plan **Free**.
12. Region **eu-central-1** Frankfurt → Create.
13. **Connect to Project** → `remote-signaling` → zaznacz Production i Preview → zatwierdź.
14. Settings → Environment Variables. Popatrz na listę zmiennych.
15. Szukasz wartości zaczynającej się od `rediss://`. Jeśli jakakolwiek zmienna ją ma, idź do kroku 18.
16. Jeśli widzisz tylko `KV_REST_API_URL` i `KV_REST_API_TOKEN`, wróć do Storage, otwórz kartę bazy, przejdź
    do konsoli Upstasha i skopiuj **TCP** albo **RESP** connection string. Wygląda tak:
    `rediss://default:haslo@nazwa.upstash.io:6379`.
17. Wróć do Environment Variables → Add New. Key `REDIS_URL`, Value to co skopiowałeś, zaznacz Production
    i Preview, Save.
18. Add New. Key `ROOM_TOKEN`, Value wymyślony ciąg minimum 20 znaków. Zapisz go sobie. Production
    i Preview, Save.
19. `npx vercel --prod` jeszcze raz, bo zmienne działają dopiero od nowego deployu.
20. `curl "https://remote-signaling.vercel.app/api/health?token=TWOJ_ROOM_TOKEN"`
21. Musisz zobaczyć `"state": "ok"` i `"store": "redis"`. Jeśli nie, przeczytaj pole `detail`, jest tam
    napisane czego brakuje.

**Unity**

22. Otwórz projekt kontrolera w Unity.
23. Zaznacz `NetworkConfig.asset`.
24. `Signaling Server Url` = `wss://remote-signaling.vercel.app/api/signaling`
25. `Signaling Token` = `ROOM_TOKEN` z kroku 18
26. `Device Timeout` = `15`
27. Zapisz projekt.

**Build i wrzucenie kontrolera**

28. File → Build Profiles → Web. W liście scen zostaw tylko scenę kontrolera.
29. Build, wskaż folder `Builds\WebGL`.
30. `cp "Assets/RemoteControlCore/Deploy~/webgl-vercel.json" "Builds/WebGL/vercel.json"`
31. `cd Builds\WebGL`
32. `npx vercel --prod`
33. Odpowiedz jak w kroku 6, tylko **Name** to `remote-controller`.
34. Zapisz adres kontrolera z linii **Production**.
35. Otwórz ten adres w przeglądarce. Scena Unity musi wstać.

**Domknięcie**

36. Panel → `remote-signaling` → Settings → Environment Variables → Add New. Key `ALLOWED_ORIGINS`,
    Value adres kontrolera z kroku 34 bez ukośnika na końcu. Production i Preview, Save.
37. `cd D:\SUPERANRETAN\unity-remote-control\SignalingServer` i `npx vercel --prod`.
38. Koniec. Vercel jest ustawiony i nie wracasz do niego, dopóki czegoś nie zmienisz. Co robić przy
    zmianach: §A8.

Dalej: host na Vision Pro dostaje ten sam adres i token w swoim `NetworkConfig`, sekcja D.

### A0. Konto i CLI

1. Konto na [vercel.com](https://vercel.com), plan **Hobby**, darmowy. Zaloguj się przez GitHub albo e-mail.
2. Node jest już potrzebny, minimum 20. Sprawdź `node --version`.
3. Zaloguj CLI raz na maszynie:

```bash
npx vercel login
```

Nic nie instalujesz globalnie, `npx` ściąga CLI na czas komendy. Repo nie importujesz i Gita nie
podłączasz: `vercel` wysyła zawartość wskazanego folderu z dysku.

### A1. Projekt signalingu

```bash
cd D:\SUPERANRETAN\unity-remote-control\SignalingServer && npx vercel --prod
```

Pierwsze uruchomienie zapyta o kilka rzeczy. Dokładne brzmienie zależy od wersji CLI; w 59.x jest tak:

| Pytanie | Odpowiedź |
|---|---|
| Which team? | Twoje konto lub zespół |
| Which project? | `Create a new project` |
| Name? | np. `remote-signaling` |
| Connect this Git repository to automatically deploy changes on every push? | `no` |
| Customize settings? | `no` |

`Customize settings` zawsze `no`. Ustawienia biorą się z `vercel.json`, który jest w folderze, a wejście
w kreator kazałoby tylko wpisać ręcznie to samo.

Pierwszy deploy zawsze idzie na produkcję. CLI wypisze dwa adresy: `Inspect` to panel z logami, a
**`Production`** to adres serwera, np. `https://remote-signaling.vercel.app`. Ten drugi zapisz, jest
potrzebny w Unity.

Projekt jest skonfigurowany tak, że Vercel buduje z niego **funkcje z katalogu `api/`**, a nie aplikację
serwerową. Dlatego `vercel.json` ma `"framework": null`, a `package.json` nie ma pola `main`. Gdyby
`main` wskazywał `server.js`, Vercel próbowałby uruchomić ten plik jako serwer i deploy padałby na
`No entrypoint found`, bo `.vercelignore` celowo nie wysyła `server.js`.

Powstał katalog `.vercel` w `SignalingServer/`. Trzyma powiązanie z projektem, więc kolejne
`npx vercel --prod` z tego folderu nie pytają już o nic.

Plik `.vercelignore` pilnuje, żeby `server.js` nie poszedł na Vercela. To nie kosmetyka: Vercel traktuje
`server.js` w katalogu głównym jako wejście serwera Node i skierowałby do niego cały ruch, omijając funkcję
`api/signaling.js` i jej `maxDuration`. Lokalnie `npm start` dalej działa.

Sprawdź jeszcze w panelu, że Settings → Functions → **Fluid Compute** jest włączone. Dla nowych projektów
jest domyślnie, a bez tego WebSockety nie działają. `vercel.json` w repo też to ustawia.

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

Dodaj jeszcze `ROOM_TOKEN`: losowy string, minimum 20 znaków, np. z `openssl rand -hex 16`. Ten sam wpiszesz
w Unity. Drugą zmienną, `ALLOWED_ORIGINS`, dopiszesz w §A7, kiedy będziesz już znał domenę kontrolera.

Z terminala to samo robi się tak, jeśli nie chcesz klikać w panelu:

```bash
npx vercel env add ROOM_TOKEN production
```

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

Na tym etapie serwer jest gotowy i więcej go nie ruszasz, poza jednorazowym dopisaniem allowlisty w §A7.

### A5. Adres do Unity

Zanim zbudujesz kontrolera, wpisz dane serwera do `NetworkConfig` w swoim projekcie. Szczegóły w §B3,
w skrócie: `Signaling Server Url` = `wss://TWOJ-SIGNALING.vercel.app/api/signaling`,
`Signaling Token` = `ROOM_TOKEN`, `Device Timeout` = 15.

Adres jest **wypalany w buildzie**, więc każda jego zmiana wymaga nowego builda WebGL.

### A6. Projekt kontrolera, czyli wrzucenie builda

Zbuduj kontrolera: Build Profiles → **Web** → w liście scen tylko scena kontrolera → Build, np. do
`Builds/WebGL`.

Jeśli build jest skompresowany, a tak jest domyślnie, skopiuj obok `index.html` plik nagłówków z paczki
i nazwij go `vercel.json`:

```bash
cp "Assets/RemoteControlCore/Deploy~/webgl-vercel.json" "Builds/WebGL/vercel.json"
```

Bez tego pliku strona pokaże białe tło, bo przeglądarka dostanie skompresowane pliki bez nagłówka
`Content-Encoding`. Alternatywa: włącz **Decompression Fallback** w Player Settings, wtedy nagłówki są
zbędne, kosztem trochę większego pobrania. Sprawdzić, co masz, możesz w Player Settings → Publishing
Settings → Compression Format.

Wrzuć folder jako drugi projekt:

```bash
cd Builds\WebGL && npx vercel --prod
```

Odpowiedzi na pytania jak w §A1, tylko nazwa inna, np. `remote-controller`. Vercel rozpozna statyczną
stronę sam, bez build commanda. Na końcu dostaniesz adres kontrolera, np.
`https://remote-controller.vercel.app`. Otwórz go, powinna wstać scena Unity.

Uwaga praktyczna: Unity przy kolejnym buildzie potrafi wyczyścić folder wyjściowy razem z `vercel.json`
i katalogiem `.vercel`. Jeśli tak się stanie, po prostu skopiuj `vercel.json` ponownie, a `npx vercel --prod`
zapyta o projekt i wtedy wybierasz **Link to existing project** i nazwę `remote-controller`. Kto woli mieć
z tym spokój, trzyma stały folder wdrożeniowy poza Unity i kopiuje do niego wynik builda.

### A7. Domknięcie: allowlista Origin

Wróć do projektu signalingu, Settings → Environment Variables, i dodaj:

| Zmienna | Wartość |
|---|---|
| `ALLOWED_ORIGINS` | `https://remote-controller.vercel.app`, czyli sam origin kontrolera, bez ścieżki i bez ukośnika na końcu. Kilka domen po przecinku |

Potem redeploy signalingu, bo zmienne działają od nowego deployu:

```bash
cd D:\SUPERANRETAN\unity-remote-control\SignalingServer && npx vercel --prod
```

Od tego momentu przeglądarka z innej domeny nie podłączy się do Twojego signalingu. Vision Pro nie wysyła
nagłówka `Origin`, więc jego to nie dotyczy; jego bramką jest token.

### A8. Aktualizacje później

| Co zmieniłeś | Co robisz |
|---|---|
| UI kontrolera, sceny, cokolwiek w Unity | build WebGL, skopiuj `vercel.json`, `npx vercel --prod` w folderze builda |
| adres lub token signalingu | popraw `NetworkConfig`, potem build i upload jak wyżej |
| zmienną środowiskową na serwerze | zmień w panelu, potem `npx vercel --prod` w `SignalingServer` |
| kod serwera signalingowego | `npx vercel --prod` w `SignalingServer`, nigdy w trakcie prezentacji |

### A9. Zasady eksploatacji

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

Komendy i pytania CLI są w **§A6**, żeby cała ścieżka wdrożeniowa była w jednym miejscu. Tutaj tylko rzeczy
specyficzne dla builda kontrolera:

- W liście scen buildu ma być **tylko** scena kontrolera.
- Adres signalingu z `NetworkConfig` jest wypalany w buildzie. Zmiana adresu albo tokenu to nowy build.
- Kompresja: domyślnie Brotli z wyłączonym fallbackiem, co wymaga nagłówków `Content-Encoding` po stronie
  hostingu. Gotowy plik leży w paczce jako `Deploy~/webgl-vercel.json` i kopiujesz go do folderu builda pod
  nazwą `vercel.json`. Prostsza alternatywa: włącz **Decompression Fallback** w Player Settings i zapomnij
  o nagłówkach.
- Pliki `.br` bez tych nagłówków dają białą stronę bez żadnego sensownego błędu w konsoli.
- Strona po HTTPS może otwierać tylko `wss://`. Dlatego lokalny dev po LAN-ie robi się na stronie po zwykłym
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
| Deploy serwera pada na `No entrypoint found in "/vercel/path0"` | `package.json` nie może mieć pola `main`, a `vercel.json` musi mieć `"framework": null`. Vercel bierze wtedy funkcje z `api/`, zamiast szukać aplikacji serwerowej. Jeśli projekt powstał wcześniej z presetem Node, zmień go w panelu: Settings → Build & Deployment → Framework Preset → **Other** |
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
