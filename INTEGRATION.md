# Integration and deployment — from nothing to a working controller

This document covers the whole path: standing up the signaling server on Vercel, installing the
`com.superanretan.remotecontrol` package in **your target Unity project**, writing **your own controller
UI**, setting up the host on the Vision Pro, and uploading the WebGL build.

The package is a library, not an application. It does not impose a UI: everything goes through
ScriptableObject channels, so you wire your own controller interface up without touching the package's code
and without inheriting from anything.

Companion documents: [WEBGL_VISIONOS_REMOTE.md](WEBGL_VISIONOS_REMOTE.md) (architecture),
[REMOTE_CONTROLLER.md](REMOTE_CONTROLLER.md) (what 2.0 brings and what is breaking),
[SETUP_HOST_CLIENT.md](SETUP_HOST_CLIENT.md) (buttons → commands),
[SignalingServer/README.md](SignalingServer/README.md) (the server in detail).

The order is forced: A comes first, because the server address is needed in B and the WebGL build bakes it
in. Section A takes you from an empty Vercel account to two working projects.

---

## A. Vercel from scratch

You end up with **two projects** on one account:

| Project | What it is | Deployed from |
|---|---|---|
| signaling | a Node function with a WebSocket, the broker that sets the connection up | the `SignalingServer/` folder |
| controller | a static page, i.e. the WebGL build | the build folder, e.g. `Builds/WebGL/` |

Why two and not one: a new controller build must not redeploy the signaling server, because existing
connections would then move to a new deployment. With two projects you stand the signaling server up once and
never touch it again.

**The order is forced by the dependencies.** The WebGL build bakes in the signaling address, and the
signaling server needs the controller's domain for the Origin allowlist. So: signaling first, then the
address into Unity, then build and upload the controller, and finally the controller's domain back into
signaling.

Why signaling at all: the browser and the Vision Pro have to exchange SDP and ICE candidates before they can
establish a peer-to-peer connection. Static hosting cannot do that, because it cannot hold a WebSocket open.
A WebGL build on its own is never enough. Once the connection is up, the server sees neither the commands nor
the video.

### Cheat sheet, no theory

If you do not want to read the explanations, do exactly this. The reasoning is in the subsections below.

Three values you write down along the way and enter in several places:

| Value | From | Used later in |
|---|---|---|
| signaling address | step 7 | Unity, step 24 |
| `ROOM_TOKEN` | you invent it in step 18 | Vercel step 18, health check step 20, Unity step 25 |
| controller address | step 34 | Vercel step 36 |

**Preparation**

1. Go to vercel.com, click Sign Up, create an account, pick the Hobby plan.
2. Open a terminal.
3. Run `npx vercel login` and confirm the login in the browser.

**Signaling server**

4. `cd D:\SUPERANRETAN\unity-remote-control\SignalingServer`
5. `npx vercel --prod`
6. Answer the questions: **Which team** your account, **Which project** `Create a new project`,
   **Name** `remote-signaling`, **Connect this Git repository** `no`, **Customize settings** `no`.
7. Write down the address from the **Production** line, e.g. `https://remote-signaling.vercel.app`.
8. In the Vercel dashboard open the `remote-signaling` project.
9. Settings → Functions. Make sure **Fluid Compute** is enabled. If not, enable it and save.
10. Storage tab → Marketplace → **Upstash for Redis** → Install.
11. Plan **Free**.
12. Region **eu-central-1** Frankfurt → Create.
13. **Connect to Project** → `remote-signaling` → tick Production and Preview → confirm.
14. Settings → Environment Variables. Look at the list of variables.
15. You are looking for a value starting with `rediss://`. If any variable has one, jump to step 18.
16. If you only see `KV_REST_API_URL` and `KV_REST_API_TOKEN`, go back to Storage, open the database card,
    go to the Upstash console and copy the **TCP** or **RESP** connection string. It looks like this:
    `rediss://default:password@name.upstash.io:6379`.
17. Back in Environment Variables → Add New. Key `REDIS_URL`, Value what you copied, tick Production and
    Preview, Save.
18. Add New. Key `ROOM_TOKEN`, Value a made-up string of at least 20 characters. Write it down. Production
    and Preview, Save.
19. `npx vercel --prod` again, because variables only take effect from a new deployment.
20. `curl "https://remote-signaling.vercel.app/api/health?token=YOUR_ROOM_TOKEN"`
21. You must see `"state": "ok"` and `"store": "redis"`. If not, read the `detail` field — it says what is
    missing.

**Unity**

22. Open the controller project in Unity.
23. Select `NetworkConfig.asset`.
24. `Signaling Server Url` = `wss://remote-signaling.vercel.app/api/signaling`
25. `Signaling Token` = the `ROOM_TOKEN` from step 18
26. `Device Timeout` = `15`
27. Save the project.

**Building and uploading the controller**

28. File → Build Profiles → Web. Leave only the controller scene in the scene list.
29. Build, pointing at the `Builds\WebGL` folder.
30. `cp "Assets/RemoteControlCore/Deploy~/webgl-vercel.json" "Builds/WebGL/vercel.json"`
31. `cd Builds\WebGL`
32. `npx vercel --prod`
33. Answer as in step 6, except **Name** is `remote-controller`.
34. Write down the controller address from the **Production** line.
35. Open that address in a browser. The Unity scene must come up.

**Closing the loop**

36. Dashboard → `remote-signaling` → Settings → Environment Variables → Add New. Key `ALLOWED_ORIGINS`,
    Value the controller address from step 34 without a trailing slash. Production and Preview, Save.
37. `cd D:\SUPERANRETAN\unity-remote-control\SignalingServer` and `npx vercel --prod`.
38. Done. Vercel is configured and you do not go back to it until something changes. What to do when it
    does: §A8.

Next: the Vision Pro host gets the same address and token in its own `NetworkConfig`, section D.

### A0. Account and CLI

1. An account on [vercel.com](https://vercel.com), **Hobby** plan, free. Sign in with GitHub or e-mail.
2. Node is needed from here on, version 20 minimum. Check with `node --version`.
3. Log the CLI in once per machine:

```bash
npx vercel login
```

Nothing is installed globally — `npx` fetches the CLI for the duration of the command. You do not import the
repo and do not connect Git: `vercel` uploads the contents of the folder you point it at.

### A1. The signaling project

```bash
cd D:\SUPERANRETAN\unity-remote-control\SignalingServer && npx vercel --prod
```

The first run asks a few things. The exact wording depends on the CLI version; in 59.x it is:

| Question | Answer |
|---|---|
| Which team? | your account or team |
| Which project? | `Create a new project` |
| Name? | e.g. `remote-signaling` |
| Connect this Git repository to automatically deploy changes on every push? | `no` |
| Customize settings? | `no` |

`Customize settings` is always `no`. The settings come from the `vercel.json` in the folder, and going into
the wizard would only have you type the same thing by hand.

The first deploy always goes to production. The CLI prints two addresses: `Inspect` is the dashboard with the
logs, and **`Production`** is the server address, e.g. `https://remote-signaling.vercel.app`. Write the second
one down — you need it in Unity.

The project is configured so that Vercel builds **functions from the `api/` directory** rather than a server
application. That is why `vercel.json` has `"framework": null` and `package.json` has no `main` field. If
`main` pointed at `server.js`, Vercel would try to run that file as a server and the deploy would fail with
`No entrypoint found`, because `.vercelignore` deliberately does not upload `server.js`.

A `.vercel` directory appears in `SignalingServer/`. It holds the link to the project, so later
`npx vercel --prod` runs from that folder ask nothing.

The `.vercelignore` file makes sure `server.js` never reaches Vercel. This is not cosmetic: Vercel treats a
root-level `server.js` as a Node server entry point and would route all traffic to it, bypassing the
`api/signaling.js` function and its `maxDuration`. Locally, `npm start` still works.

One more thing to check in the dashboard: Settings → Functions → **Fluid Compute** must be enabled. It is the
default for new projects, and without it WebSockets do not work. The repo's `vercel.json` sets it too.

Alternative, if you prefer Git: Add New → Project → import the repo → **Root Directory `SignalingServer`** →
Framework *Other*, with Build Command and Output left empty.

### A2. Redis

Redis is **not a separate server to maintain**. It is a database created from the Vercel dashboard, on the
free plan, billed through Vercel, with no machines and no administration.

It is required because Vercel functions are stateless and there is no client-to-instance pinning. The socket
from the headset lands on instance A, the socket from the browser on B, and those are two separate processes
sharing no variable. Without shared storage the browser's `offer` would never reach the host.

Vercel dashboard → the signaling project → **Storage** → **Marketplace** → **Upstash for Redis** →
**Free** plan → region **eu-central-1 (Frankfurt)**, because the functions are pinned to `fra1` in
`vercel.json` → **Create** → **Connect to Project**, environments **Production** and **Preview**.

The other Marketplace option is **Redis Cloud**: 30 MB, 100 operations per second, no monthly cap. Our usage
is a few operations per second, so it is enough as well. The code handles both without changes.

### A3. Environment variables

Settings → **Environment Variables**, environments Production and Preview.

**First check whether there is a TCP connection string.** You are looking for a value starting with
`rediss://` under any of these names: `REDIS_URL`, `KV_URL`, `REDIS_TLS_URL`, `UPSTASH_REDIS_URL`. The server
checks them in that order, so if one is there, you do nothing.

If you only see `KV_REST_API_URL` and `KV_REST_API_TOKEN`, those are **REST** credentials, and REST does not
support `SUBSCRIBE`, which the server uses as a doorbell between instances. In that case: Storage tab → open
the provider console → copy the **TCP / RESP connection string** in the form
`rediss://default:PASSWORD@name.upstash.io:6379` → add it manually as **`REDIS_URL`**. A REST URL put into
those variables is rejected at startup with a message saying what to copy instead, so it cannot be missed.

Add `ROOM_TOKEN` as well: a random string, at least 20 characters, e.g. from `openssl rand -hex 16`. You will
enter the same one in Unity. The second variable, `ALLOWED_ORIGINS`, comes in §A7 once you know the
controller's domain.

From the terminal the same thing looks like this, if you would rather not click around the dashboard:

```bash
npx vercel env add ROOM_TOKEN production
```

The full list of variables, including the optional `ROOM_TOKENS` (several independent rooms on one server),
`DEVICE_TIMEOUT`, `PAIR_LEASE_SECONDS` and TURN, is in
[SignalingServer/README.md](SignalingServer/README.md) §2.3.

The token ships in the public WebGL page, so it is obfuscation, not authentication. It keeps accidental
visitors and crawlers away. Rotate it per event.

### A4. Redeploy and verification

Variables only take effect from a new deployment:

```bash
cd D:\SUPERANRETAN\unity-remote-control\SignalingServer && npx vercel --prod
```

One command checks everything:

```bash
curl "https://YOUR-SIGNALING.vercel.app/api/health?token=YOUR_ROOM_TOKEN"
```

You expect:

| Field | Value |
|---|---|
| `state` | `ok` |
| `store` | `redis` |
| `pubsub` | `ok` |
| `lua` | `ok` |
| `misconfigured` | `null` |

`redisUrlSource` says which variable the connection came from, and `ping` is the latency to the database in
milliseconds. Any other result has its reason in `detail` or `misconfiguredDetail`. State `degraded` means
pub/sub is down and negotiation will be lazy. State `error` means Redis is unreachable or a Lua script was
rejected; the startup self-test dry-runs every script, so a typo shows up immediately.

You can inspect the device registry with
`curl "https://YOUR-SIGNALING.vercel.app/api/devices?token=YOUR_ROOM_TOKEN"`. Before the host starts, it
returns `[]`.

At this point the server is ready and you do not touch it again, apart from adding the allowlist once in §A7.

### A5. The address into Unity

Before you build the controller, put the server details into `NetworkConfig` in your project. Details in §B3;
in short: `Signaling Server Url` = `wss://YOUR-SIGNALING.vercel.app/api/signaling`,
`Signaling Token` = `ROOM_TOKEN`, `Device Timeout` = 15.

The address is **baked into the build**, so any change to it needs a new WebGL build.

### A6. The controller project, i.e. uploading the build

Build the controller: Build Profiles → **Web** → only the controller scene in the scene list → Build, e.g.
into `Builds/WebGL`.

If the build is compressed, which it is by default, copy the header file from the package next to
`index.html` and name it `vercel.json`:

```bash
cp "Assets/RemoteControlCore/Deploy~/webgl-vercel.json" "Builds/WebGL/vercel.json"
```

Without that file the page shows a white background, because the browser receives compressed files with no
`Content-Encoding` header. The alternative is to enable **Decompression Fallback** in Player Settings, which
makes the headers unnecessary at the cost of a slightly larger download. You can see which one you have under
Player Settings → Publishing Settings → Compression Format.

Upload the folder as a second project:

```bash
cd Builds\WebGL && npx vercel --prod
```

Answer the questions as in §A1, only with a different name, e.g. `remote-controller`. Vercel recognises a
static page by itself, with no build command. At the end you get the controller address, e.g.
`https://remote-controller.vercel.app`. Open it — the Unity scene should come up.

A practical note: on the next build Unity may clear the output folder along with `vercel.json` and the
`.vercel` directory. If that happens, simply copy `vercel.json` again; `npx vercel --prod` will then ask
about the project, and you choose **Link to existing project** and the name `remote-controller`. If you would
rather not deal with that, keep a permanent deployment folder outside Unity and copy the build result into it.

### A7. Closing the loop: the Origin allowlist

Go back to the signaling project, Settings → Environment Variables, and add:

| Variable | Value |
|---|---|
| `ALLOWED_ORIGINS` | `https://remote-controller.vercel.app`, i.e. the controller's origin only, no path and no trailing slash. Several domains separated by commas |

Then redeploy signaling, because variables take effect from a new deployment:

```bash
cd D:\SUPERANRETAN\unity-remote-control\SignalingServer && npx vercel --prod
```

From then on a browser on another domain cannot connect to your signaling server. The Vision Pro does not
send an `Origin` header, so this does not apply to it; its gate is the token.

### A8. Updates later on

| What you changed | What you do |
|---|---|
| controller UI, scenes, anything in Unity | build for WebGL, copy `vercel.json`, `npx vercel --prod` in the build folder |
| the signaling address or token | fix `NetworkConfig`, then build and upload as above |
| an environment variable on the server | change it in the dashboard, then `npx vercel --prod` in `SignalingServer` |
| the signaling server code | `npx vercel --prod` in `SignalingServer`, never mid-presentation |

### A9. Operating rules

- **Do not deploy the server during a presentation.** Existing connections stay on the old deployment until
  they close, new ones go to the new deployment. The Redis data is shared, so it is survivable, but not in
  front of an audience.
- Keep the function region and the database region together. Redis single-region, never Global.
- Vercel Hobby is, per its terms, for non-commercial use. If the presentation is commercial, that is a
  licensing question, not a technical one.
- Optional: Project → Firewall → a rate limit on the `/api/signaling` path, e.g. 30 requests per minute per
  IP.

---

## B. Installing the package in the controller project

### B1. Adding the package

Window → Package Manager → **+** → **Add package from git URL**:

```
https://github.com/superanretan/unity-remote-control.git?path=Assets/RemoteControlCore#vpwebgl2.2
```

`vpwebgl2.2` is the newest tag and the one to use — it resolves to package version **2.2.0**. The tag
history of the 2.x line is in [REMOTE_CONTROLLER.md](REMOTE_CONTROLLER.md) §6.

You can point at a branch instead of the tag, but for production stick to the tag. Package requirements:
Unity 6000.0+, `com.unity.transport` 2.4+, `com.unity.ugui`, TextMeshPro. The dependencies come along by
themselves from `package.json`.

### B2. Generate your own ScriptableObject assets

**This is the step that is easy to miss.** A package installed from a Git URL lives in `Library/PackageCache`
and is **read-only**. The package's own SO assets cannot be edited, and `NetworkConfig` has to receive your
server address and token.

Menu: **Tools ▸ Remote Control ▸ WebRTC ▸ Create SO Assets**

It creates a full set in `Assets/RemoteControl/SO/` in your project: `NetworkConfig`, `CommandSendChannel`,
`CommandReceivedChannel`, `ConnectRequestChannel`, `DisconnectRequestChannel`, `OnConnectedChannel`,
`OnDisconnectedChannel`, `OnClientConnectedChannel`, `OnClientDisconnectedChannel`, `LogChannel`,
`HostMessageSendChannel`, `HostMessageReceivedChannel`, `HandlerRegistry`, `TargetRegistry`.
It never overwrites an existing asset, so you can run it repeatedly.

Individual assets can also be made through **Create ▸ Remote Control ▸ …**.

If you want the ready-made demo prefabs and scenes in your project:
**Tools ▸ Remote Control ▸ WebRTC ▸ Create Prefabs** and **Build WebGL Controller Scene**. They are not
needed for your own UI, but they are handy as a reference.

### B3. Configuring `NetworkConfig`

Select `Assets/RemoteControl/SO/NetworkConfig.asset` and set:

| Field | Value |
|---|---|
| **Signaling Server Url** | `wss://YOUR-SIGNALING.vercel.app/api/signaling` |
| **Signaling Token** | the same as `ROOM_TOKEN` on the server |
| **Device Name** | the host's name in the list, e.g. `Vision Pro Office` |
| **Heartbeat Interval** | 2 |
| **Video Width / Height** | 960 / 540 — the ceiling for a preview; the headset renders an extra pass at this size |
| **Video Fps** | 15 — smooth enough, and costs half of what 30 does |
| **Video Bitrate Kbps** | 1200 |
| **Device Timeout** | 15 |
| **Ice Server Entries** | one STUN entry is enough on the same Wi-Fi network |

The same asset goes into the Vision Pro host project. The address is **baked into the build**, so set it
before building. The token is appended automatically as `?token=…` — do not add it to the URL by hand.

The serialized `_signalingServerUrl` field deliberately kept its name between 1.x and 2.0, so if your project
overrides it at runtime by field name, that mechanism still works.

TURN: only needed when the browser and the headset are on different networks. Add an entry with `username`
and `credential` under `Ice Server Entries`, but remember that it ships in the public build. The better
option is short-lived credentials issued by the server, described in
[REMOTE_CONTROLLER.md](REMOTE_CONTROLLER.md) §3.2.

---

## C. A WebGL controller with your own UI

### C1. What must be in the scene

Two components, which can both sit on one empty GameObject:

| Component | Role | Fields to wire |
|---|---|---|
| `WebGLDiscoveryClient` | holds the WebSocket to signaling and mirrors the device list | `Network Config`, `Log Channel`, `Poll Interval` (5) |
| `WebGLRemoteTransport` | WebRTC, DataChannel, commands both ways | `Network Config`, `Command Send Channel`, `Connect Request Channel`, `Disconnect Request Channel`, `On Connected Channel`, `On Disconnected Channel`, `Host Message Received Channel`, `Log Channel` |

`WebGLRemoteTransport` also has `Auto Request Id`, `Auto Reconnect`, `Max Reconnect Attempts` and
`Reconnect Delay`. The defaults are fine.

Do **not** add `RemoteControl_ClientCore` or `TransportClient` to WebGL scenes. Unity Transport over UDP does
not work in a browser.

Outside WebGL both components are a safe no-op, so the scene runs in the Editor without errors, just without
a connection.

### C2. The device list in your own UI

`WebGLDiscoveryClient` derives from `RemoteDiscoveryBase`, so the UI needs to know nothing about WebRTC:

```csharp
using System.Collections.Generic;
using SuperAnretan.RemoteControl;
using UnityEngine;

public class MyDevicePicker : MonoBehaviour
{
    [SerializeField] private RemoteDiscoveryBase _discovery;              // WebGLDiscoveryClient from the scene
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
        // Rebuild your list here. d.deviceName for the label, d.IsAvailable for the "busy" lock.
    }

    private void OnStatus(string status) { /* "Searching for devices...", "Signaling reconnecting..." */ }

    public void Connect(DiscoveredDevice device) => _connectRequestChannel.Raise(device.ConnectKey);
    public void Refresh() => _discovery.Refresh();
    public void Disconnect() => _disconnectRequestChannel.Raise();
}
```

`DiscoveredDevice` has `deviceId`, `deviceName`, `platform`, `status`, `IsAvailable` and `ConnectKey`.
`ConnectKey` is the value you raise on `ConnectRequestChannel`.

If you would rather not look the component up in the Inspector: add the `DiscoveryBinder` component to the UI
object — it finds the backend in the scene at startup and injects it into `NetworkDiscoveryPanel`. For your
own UI it is simpler to wire the reference by hand, or call `FindFirstObjectByType<RemoteDiscoveryBase>()`
once.

Connection state: `OnConnectedChannel` and `OnDisconnectedChannel` (`VoidEventChannel`). Use them to show and
hide your control panel.

### C3. Sending commands

Zero dependency on the transport. You raise a `RemoteCommand` on `CommandSendChannel`:

```csharp
_commandSendChannel.Raise(new RemoteCommand("set_color", "demo_cube", "#FF0000"));
```

The fields: `commandType` is the handler key on the host, `targetId` is the `CommandTarget.TargetId` of an
object in the host scene, and `value` and `payload` are interpreted by the handler.

Without code: add the **`CommandButton`** component to a button and fill in `Command Type`, `Target Id`,
`Value`, `Payload` and `Command Send Channel`. The optional `On Connected Channel` and
`On Disconnected Channel` make the button clickable only while connected. The value can be changed at runtime
through `CommandButton.SetValue`.

Acknowledgements: set `RemoteCommand.requestId`, or enable `Auto Request Id` on `WebGLRemoteTransport`. The
host answers with an `ack` envelope carrying the same `requestId`.

### C4. Receiving state from the host

The host sends `HostMessage`s over the DataChannel. Every message lands verbatim on
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

        if (msg.IsSnapshot)                                  // full state right after connecting
            foreach (var e in msg.SnapshotEntries()) Apply(e.topic, e.value);
        else if (msg.IsState)                                // incremental change
            Apply(msg.topic, msg.value);
        else if (msg.IsCapture)                              // starting | streaming | stopped | error
            ShowCaptureBadge(msg.value, msg.payload);
        else if (msg.IsAck)                                  // reply to a command with a requestId
            ClearPending(msg.requestId);
    }

    private void Apply(string topic, string value) { /* e.g. highlight the active compartment */ }
    private void ShowCaptureBadge(string state, string detail) { }
    private void ClearPending(string requestId) { }
}
```

Two things worth stressing. The **snapshot** arrives automatically once the DataChannel opens and contains the
last value of every topic, so the UI never starts from an empty state and you never have to ask for
anything. And **capture state is independent of the DataChannel**: "connected" does not mean "video is
flowing" — only `capture` = `streaming` means that.

If you prefer a C# event over an SO channel, `WebGLRemoteTransport` exposes
`event Action<HostMessage> OnHostMessage`.

### C5. Previewing the headset's view

Add a `RawImage` to the Canvas and the **`RemoteVideoView`** component on the same object. Wire up `Target`
(that `RawImage`) and optionally `Aspect Fitter` (`AspectRatioFitter`), `On Disconnected Channel` and
`Log Channel`. The texture is created by itself at the stream resolution. The `Use Html Overlay` field draws
the raw `<video>` element over the canvas and is only for diagnostics.

### C6. WebGL build and upload to Vercel

The commands and the CLI questions are in **§A6**, so the whole deployment path stays in one place. Here,
only the things specific to the controller build:

- The build's scene list must contain **only** the controller scene.
- The signaling address from `NetworkConfig` is baked into the build. Changing the address or the token means
  a new build.
- Compression: Brotli by default, with fallback disabled, which requires `Content-Encoding` headers from the
  hosting. The ready-made file is in the package as `Deploy~/webgl-vercel.json` and you copy it into the
  build folder as `vercel.json`. The simpler alternative: enable **Decompression Fallback** in Player
  Settings and forget about headers.
- `.br` files without those headers give a white page with no meaningful error in the console.
- A page served over HTTPS can only open `wss://`. That is why local LAN development is done on a plain
  `http://` page with `ws://<pc-ip>:8787`, not on the Vercel version.

---

## D. The Vision Pro host

### D1. Components in the scene

The simplest route is to drop in the `RemoteControl_VisionProHost` prefab. By hand it is three components on
one object:

| Component | Fields |
|---|---|
| `VisionProSignalingClient` | `Network Config`, `Log Channel`, `Auto Connect`, optionally `Device Name Override` |
| `VisionProWebRtcHost` | `Network Config`, `Signaling` (the component above), `Command Received Channel`, `On Client Connected Channel`, `On Client Disconnected Channel`, `Host Message Send Channel`, `Log Channel`, `Capture Backend` = Auto, `Auto Start Capture` = on, `Send Snapshot On Connect` = on, `Ack Commands` = on |
| `CommandProcessor` | `Command Received Channel`, `Handler Registry`, `Target Registry`, `Log Channel` |
| `VisionCameraStreamer` | `Network Config`, `Log Channel`; optionally `Source Camera`, `Follow Target`, `Field Of View`, `Culling Mask` |

**If the host application is fully immersive (Metal / Compositor Services), the picture has to come from
`VisionCameraStreamer`.** ReplayKit captures the app's *window*, and an immersive app never draws into it —
the stream then comes out uniformly black even though the browser counts decoded frames. Adding the component
to the scene is enough: `Capture Backend` = `Auto` then picks `UnityCamera` **by itself** and says so in the
log. By default the spectator camera follows `Camera.main`, so the operator sees roughly what the wearer sees;
`Source Camera` gives a fixed view instead (its settings are copied, and your scene camera is left
untouched). For a **windowed** app do not add the streamer — `Auto` stays with ReplayKit.

That stream costs the headset one extra render per frame, so keep `NetworkConfig` low: **960x540, 15 fps,
1200 kbit/s** is the ceiling for a preview, and the streamer's `Culling Mask` is the cheapest saving there is
here. The spectator camera is disabled and rendered only on the frames that actually go to the browser — no
post-processing, no MSAA, no HDR and no shadows.

`NetworkConfig` must be **the same** asset as in the controller, with the same address and token. If the host
and the controller are two different Unity projects, just set identical values in both.

The host's `Device Id` is persistent: it is generated once and stored in `PlayerPrefs`, so the host comes back
to the list under the same entry after a restart and after a forced reconnect.

### D2. Commands: handler and target

```csharp
using SuperAnretan.RemoteControl;
using UnityEngine;

public class SetCompartmentHandler : CommandHandlerBase
{
    public override string CommandType => "set_compartment";      // must match the controller's commandType

    public override void Handle(RemoteCommand command, GameObject target)
    {
        target.GetComponent<MyCompartmentView>()?.Show(command.value);
    }
}
```

Put the handler on any object in the scene and assign `Handler Registry`. It registers itself in `OnEnable`.
On the object you are controlling, add `CommandTarget`, set `Target Id` and assign `Registry`
(`TargetRegistry`). Rules: one `CommandType` is one handler, one `TargetId` is one object.

### D3. Sending state back to the controller

Application code **does not call** `VisionProNativeBridge` directly. You raise ready-made JSON on
`HostMessageSendChannel`:

```csharp
[SerializeField] private StringEventChannel _hostMessageSendChannel;   // SO: HostMessageSendChannel

private void OnCompartmentChanged(string id) =>
    _hostMessageSendChannel.Raise(HostMessage.State("navigation", id).ToJson());
```

Or directly on the component: `visionProWebRtcHost.SetState("navigation", id)`.

The host caches the last value of every topic, even when nobody is connected, and sends them as one snapshot
once the DataChannel opens. Capture state (`starting`, `streaming`, `stopped`, `error` with a code) and
`ack`s for commands carrying a `requestId` are sent automatically — you do nothing about them.

The envelope format and the full list of types: [REMOTE_CONTROLLER.md](REMOTE_CONTROLLER.md) §1.

### D4. visionOS build

You build on a Mac with Xcode. `RemoteControlVisionOSPostProcessor` adds the WebRTC SPM package by itself,
links `ReplayKit`, `CoreMedia` and `CoreVideo`, and writes `NSLocalNetworkUsageDescription` and
`NSScreenCaptureUsageDescription` into the Info.plist. In Xcode you only set Team and signing. Details and the
capture-backend variants: [WEBGL_VISIONOS_REMOTE.md](WEBGL_VISIONOS_REMOTE.md) §4.

The first capture start shows the system screen-recording consent dialog — but **only with the ReplayKit
backend**. `UnityCamera` needs no consent, because it does not capture the screen: the app renders its own
spectator camera. Passthrough never reaches the stream in either variant.

In the Editor the host registers with signaling but **does not answer an offer**, because there is no native
WebRTC there. That is expected; the log shows `peer-create-failed`.

---

## E. Startup order and a quick test

1. Server: `curl /api/health?token=…` → `"state":"ok"`.
2. Host on the Vision Pro: start the app. The log shows `[Signaling] Registered as "…"`.
   `curl /api/devices?token=…` shows one entry.
3. Controller: open the page over HTTPS. Your device list gets an entry within a few seconds.
4. Connect. Controller log: `[DataChannel] Opened`, then `Snapshot received`, then
   `[capture] value=streaming`. The picture appears.
5. Press your button. Host log: `[DataChannel] Received: [set_compartment] …`, then
   `[OK] Executed 'set_compartment' on '…'`.
6. Close the tab. The host stops recording within a few seconds.

The session survives the forced signaling socket close, which on Vercel happens at most every 300 s. The
clients reconnect with the same identifiers, and the registry and the pairing live in Redis. The log shows
`[Signaling] reconnect in 1s` and nothing else happens.

---

## F. Diagnostics

| Symptom | Check |
|---|---|
| Server deploy fails with `No entrypoint found in "/vercel/path0"` | `package.json` must not have a `main` field, and `vercel.json` must have `"framework": null`. Vercel then takes the functions from `api/` instead of looking for a server application. If the project was created earlier with a Node preset, change it in the dashboard: Settings → Build & Deployment → Framework Preset → **Other** |
| Controller: `server-misconfigured:no-redis-url` | The deployment has no Redis. `curl /api/health` says exactly what is missing. Variables take effect from a new deployment |
| Controller: `unauthorized` or `Signaling rejected` | `NetworkConfig.Signaling Token` differs from `ROOM_TOKEN` on the server |
| Controller: `origin-not-allowed` | The page's domain is not in `ALLOWED_ORIGINS` |
| The device is not in the list | Does the host show `[Signaling] Connected`? Same address and token on both sides? Same room, if you use `ROOM_TOKENS`? |
| The list flickers, or `Signaling reconnecting...` every 5 minutes | Normal on Vercel, the 300 s per-socket limit. The session and the list are unaffected. If they are affected, the server is not on 2.0 |
| `error: device-busy` | Another controller holds the pairing. It is released by an explicit `disconnect` or by lease expiry after 30 s |
| Host: `Peer disconnect … (controller-gone)` | The pairing lease expired, i.e. the tab went away without a `disconnect`. The host is free again |
| ICE `failed` | No route between the devices, i.e. different networks. TURN is needed. On the Vision Pro, check the local-network permission |
| The DataChannel never opens | A host in the Editor does not answer an offer. An on-device build is needed |
| White page after uploading the build | The `Content-Encoding` headers for `.br` files are missing. Copy `vercel.json` as in §C6, or enable Decompression Fallback |
| `capture-error` in the host log | `-5801` is consent declined, `-5803` a failed start — try again after leaving and re-entering the immersive space |
| The backgrounded controller reacts with a delay | Browsers throttle hidden tabs. Keep the controller tab visible |

Log prefixes: `[Discovery]`, `[Signaling]`, `[WebRTC]`, `[Video]`, `[DataChannel]`, `[ScreenCapture]`.
They all go through `LogChannel`, so wire up `DebugLogUI` with a `TextMeshProUGUI` and you have them on the
device's screen.

---

## G. Limitations worth knowing about

- One controller per host at a time.
- Without TURN both devices need a route to each other, in practice the same Wi-Fi network. A phone on LTE and
  a headset on Wi-Fi will not connect.
- Vercel Hobby closes every socket after 300 s. This is handled, but the Vision Pro loses whatever it would
  have sent during its ~1-second reconnect gap. ICE candidates generated exactly then are lost; negotiation
  has many of them plus a 20 s timeout with a retry, so in practice it does not hurt.
- Apple's screen-recording consent cannot be skipped or remembered on the user's behalf.
- The first visionOS build needs network access, because SPM has to download the WebRTC package.
