# Host-owned lists (roster sync) — package 2.3.0

How to make a list that lives on the host — a roster of workers, robots, cameras, zones — appear on
the controller, be clickable there, and stay in agreement with the host in both directions.

This document has three parts:

- **§1–§4** the model, the wire contract, and the API this package adds. Read before writing code.
- **§5 PART A** the full implementation for the **Vision Pro host** (`guardian_ui_vision_pro`).
- **§6 PART B** the full implementation for the **controller**
  (`Guardian_VisionPro_Controller`).

Both parts are copy-paste ready and assume this package is already released and both projects have
bumped `Packages/manifest.json` to the tag containing **2.3.0**. Nothing in either part compiles
before that bump — the types in §3 will not exist.

`INTEGRATION.md` covers getting the transport working in the first place; this document only covers
the list feature on top of it.

---

## 1. The model: the host is authoritative

There is exactly one owner of the truth, and it is the host.

```
controller click  ──▶ RemoteCommand ("this is a REQUEST")
                          │
                          ▼
                     host applies it through its OWN normal path
                          │
                          ▼
host publishes  ──▶ HostMessage 'state' ("this is what IS")
                          │
                          ▼
                 controller renders that state
```

The controller never decides what is selected. It renders what the host says is selected. A local
optimistic highlight on click is fine, but the echo always overrides it.

Get this wrong — let the controller keep its own idea of the selection — and the two screens drift
apart the first time anything unexpected happens (a reconnect, a rejected command, a selection
cleared by some other host-side rule), with no way to tell which one is lying.

**Three properties come free from the existing core. Do not rebuild them:**

| Property | Where it comes from |
|---|---|
| A controller connecting late still sees the current list | `state` topics are cached per topic and replayed as one `snapshot` on `datachannel-open` |
| A reconnect restores the list with no interaction | `EndSession` deliberately does not clear `_stateCache` |
| State published while nobody is connected is not lost | `TrySend` caches the topic even when the send fails |

---

## 2. The wire contract

Nothing in the envelopes changes. `commandType`, `messageType` and `topic` are free strings and
handler registration is a runtime dictionary, so a new list needs **no core modification**.

### 2.1 Host → controller — three `state` topics

Topic names are namespaced so two subsystems cannot collide.

**The list.** `value` is the generation, `payload` is a `RemoteListPayload`:

```json
{
  "messageType": "state",
  "schemaVersion": 1,
  "topic": "guardian.npc.roster",
  "value": "7",
  "payload": "{\"protocolVersion\":1,\"generation\":\"7\",\"contextId\":\"compartment-a\",\"items\":[{\"id\":\"a1b2c3d4e5f6\",\"group\":\"workers\",\"label\":\"Piotr M.\",\"sublabel\":\"\",\"presentationKey\":\"worker:Piotr M.\",\"ordinal\":\"\"}]}",
  "requestId": ""
}
```

**The selection.** `value` is the item id, or empty for "nothing selected":

```json
{ "messageType":"state", "schemaVersion":1, "topic":"guardian.npc.selection",
  "value":"a1b2c3d4e5f6", "payload":"{\"protocolVersion\":1,\"generation\":\"7\"}", "requestId":"" }
```

**The active tab:**

```json
{ "messageType":"state", "schemaVersion":1, "topic":"guardian.npc.tab",
  "value":"workers", "payload":"", "requestId":"" }
```

One roster message feeds every tab — each item carries its own `group`, so there is no second topic
to keep in sync.

### 2.2 Controller → host

`targetId` is `"app"` — the convention for actions belonging to the application rather than to one
scene object.

| `commandType` | `value` | `payload` |
|---|---|---|
| `select_npc` | item id | `RemoteListSelectArgs`, e.g. `{"protocolVersion":1,"generation":"7"}` |
| `clear_npc_selection` | — | — |
| `set_npc_tab` | `workers` \| `robots` | — |

---

## 3. What the package adds in 2.3.0

Five types. Use them on both sides; **do not hand-copy the DTOs into either project** —
`JsonUtility` matches by field name, so a copy that drifts by one field drops that field with no
exception and no log.

```csharp
// Runtime/Core/RemoteListPayload.cs
[Serializable] public class RemoteListItem {
    public string id;              // stable identity. The ONLY field that may address the entity.
    public string group;           // which tab / sub-list
    public string label;           // display name, exactly as the host renders it
    public string sublabel;
    public string presentationKey; // controller resolves this to its OWN local sprite
    public string ordinal;         // display number only, NEVER identity
}

[Serializable] public class RemoteListPayload {
    public const int CurrentProtocolVersion = 1;
    public int protocolVersion;
    public string generation;
    public string contextId;
    public List<RemoteListItem> items;
    public bool IsValid { get; }   // version matches AND generation is non-empty
    public string ToJson();
    public static RemoteListPayload FromJson(string json);
}

[Serializable] public class RemoteListSelectArgs {
    public int protocolVersion;
    public string generation;
    public bool IsValid { get; }
    public string ToJson();
    public static RemoteListSelectArgs FromJson(string json);
}
```

```csharp
// Runtime/Core/HostStateRouter.cs   (MonoBehaviour, controller side)
public event Action<string, string, string> StateChanged;  // topic, value, payload
public event Action SnapshotApplied;
public event Action<string, string> CaptureChanged;        // state, detail
public bool IsApplyingSnapshot { get; }
public string CaptureState { get; }
public bool TryGet(string topic, out HostStateEntry entry);
public string GetValue(string topic, string fallback = "");
public string GetPayload(string topic, string fallback = "");
public RemoteListPayload GetList(string topic);            // null when absent or invalid
public void Clear();
```

```csharp
// Runtime/UI/RemoteListView.cs, RemoteListRow.cs, RemoteTabBar.cs, RemoteListIconSet.cs
// Controller-side widgets, prefab-driven and domain-agnostic. See PART B for how they wire up.
```

**A malformed payload does not throw.** `JsonUtility` deserializes every missing field to its
default, so `{"garbage":1}` parses into a `RemoteListPayload` with a null generation and zero items.
Always check `IsValid` before trusting anything you read.

Also new: `VisionProWebRtcHost._maxOutboundWarnBytes` (default 49152) logs before an oversized
outbound message is dropped, and `SendSnapshot` logs the serialized snapshot size. See §7.4.

---

## 4. Identity and generation

### 4.1 Why a stable id

The tempting options are all wrong, and they fail quietly:

| Scheme | Why not |
|---|---|
| Display name | Not unique, and may be assigned at runtime |
| Index in the list | The list is rebuilt when filtered; index 2 then names a different entity |
| Type + ordinal (`Harvester 01`) | Presentation, recomputed per build, shifts when filtered |
| Scene hierarchy path, or a hash of it | A rename, reparent or reorder silently reassigns identity, and a hash hides collisions |

**Use an author-time serialized id.** Two traps when authoring them:

- **Never write the id onto a prefab asset.** Every instance would share one id. Generate only for
  scene instances, guarded with `PrefabUtility.IsPartOfPrefabAsset`.
- **Duplicating a GameObject copies the id.** `OnValidate` cannot tell duplication from a scene
  load, so there is no automatic fix. Ship a validation button that reports duplicates, and run it
  after any duplication.

### 4.2 Why a generation too

A stable id says *which* entity. The generation says *which list the user was looking at*.

Without it: the host rebuilds its filtered list, the controller has not received the new roster yet,
the user clicks a row that no longer exists — and the host either does nothing (looks broken) or,
with any positional scheme, acts on the wrong entity.

The rule:

1. Every roster push carries a `generation`. Bump it whenever the addressable set changes.
2. Every click echoes the generation the row was rendered from.
3. The host **rejects a mismatch and immediately re-publishes the roster.**

Step 3 is the important half. Rejecting alone leaves the controller stuck on a stale list forever;
re-publishing makes it self-heal without the user doing anything.

---

# 5. PART A — Vision Pro host (`guardian_ui_vision_pro`)

Ten steps. Do them in order; step A8 needs A2–A7 compiled.

Naming note: that project uses `camelCase` for `[SerializeField]` fields (no underscore), unlike
this package. The code below follows the project's convention.

## A1. Bump the package pin

`Packages/manifest.json` — point at the tag containing 2.3.0:

```json
"com.superanretan.remotecontrol": "https://github.com/superanretan/unity-remote-control.git?path=/Assets/RemoteControlCore#<tag>",
```

## A2. `HoverableNpcTarget` — add the stable id

`Assets/Scripts/Compartments/Npc/Views/HoverableNpcTarget.cs`.

Insert before the `[Header("Type")]` block:

```csharp
    [Header("Remote Control")]
    // The only stable identity this NPC has. Display names are randomised per run by
    // WorkerNameAssigner and robot numbers are positional, so neither can address an NPC
    // from the web controller.
    [Tooltip("Stable id the web controller uses to address this NPC. Generated once by " +
             "'Assign Remote Ids' on NpcListBuilder — do not type it by hand, and never let it " +
             "reach the prefab asset, or every instance would share one id.")]
    [SerializeField] private string remoteId;
```

And next to `ResolvedDisplayName` / `ResolvedSprite`:

```csharp
    public string RemoteId => remoteId;

#if UNITY_EDITOR
    // Editor-only writer for the id assigner. Runtime code must treat the id as immutable:
    // changing it mid-session would strand every row the controller has already rendered.
    public void EditorSetRemoteId(string value) => remoteId = value;
#endif
```

## A3. `NpcListBuilder` — a `Built` event and the id assigner

`Assets/Scripts/UI/NpcList/Controllers/NpcListBuilder.cs`.

Add `using System;` at the top. Then next to `_spawned`:

```csharp
        // Raised with the source set once a build has finished. Anything that needs to mirror the
        // list must listen here rather than to the compartment channels NpcListCompartmentFilter
        // uses: subscriber order on an SO channel is not a contract, so a second listener would win
        // the race half the time and read the list from before the rebuild. Subscribers also get
        // the guarantee that SetResolvedPresentation has already run, which is what makes the
        // per-run random worker names readable.
        public event Action<IReadOnlyList<HoverableNpcTarget>> Built;
```

At the very end of `Build(IReadOnlyList<HoverableNpcTarget> source)`, after the existing debug log:

```csharp
            // One throwing subscriber must not leave the rest of the app believing no build happened.
            try { Built?.Invoke(source); }
            catch (Exception e) { Debug.LogError($"[NpcListBuilder] Built subscriber threw: {e}", this); }
```

Then add the authoring button after `CollectTargetsFromScene()`:

```csharp
        // Run this after Collect Targets From Scene, and again after duplicating any NPC.
        // Existing ids are never rewritten unless they collide, so controller-side ids stay valid
        // across re-runs.
#if ODIN_INSPECTOR
        [Button("Assign Remote Ids", ButtonSizes.Large), GUIColor(0.45f, 0.9f, 0.6f)]
#endif
        [ContextMenu("Assign Remote Ids")]
        public void AssignRemoteIds()
        {
#if UNITY_EDITOR
            int assigned = 0, skipped = 0;
            var seen = new Dictionary<string, HoverableNpcTarget>();

            for (int i = 0; i < targets.Count; i++)
            {
                HoverableNpcTarget target = targets[i];
                if (target == null) continue;

                // An id serialized into the prefab ASSET would be shared by every instance of it,
                // so only scene instances get one.
                if (UnityEditor.PrefabUtility.IsPartOfPrefabAsset(target))
                {
                    Debug.LogWarning($"[NpcListBuilder] Skipped '{target.name}' — prefab asset, not a scene instance.", target);
                    skipped++;
                    continue;
                }

                bool duplicate = !string.IsNullOrEmpty(target.RemoteId) && seen.ContainsKey(target.RemoteId);

                if (string.IsNullOrEmpty(target.RemoteId) || duplicate)
                {
                    // A duplicate is almost always a duplicated GameObject: Unity copies the
                    // serialized id, and at runtime nothing can tell that apart from a scene load.
                    if (duplicate)
                        Debug.LogWarning($"[NpcListBuilder] '{target.name}' shared id '{target.RemoteId}' " +
                                         $"with '{seen[target.RemoteId].name}' — reassigning.", target);

                    target.EditorSetRemoteId(Guid.NewGuid().ToString("N").Substring(0, 12));
                    UnityEditor.EditorUtility.SetDirty(target);
                    assigned++;
                }

                seen[target.RemoteId] = target;
            }

            UnityEditor.EditorUtility.SetDirty(this);
            Debug.Log($"[NpcListBuilder] Remote ids: {assigned} assigned, " +
                      $"{targets.Count - assigned - skipped} kept, {skipped} skipped. " +
                      "Save the scene to persist them.", this);
#endif
        }
```

## A4. `NpcListTabSwitcher` — durable, non-clobbering tab state

Two problems today. `Apply(bool)` is public but has no getter and no event, so the tab cannot be
published. Worse, `OnEnable` calls `Apply(workersActiveByDefault)`, so disabling and re-enabling the
panel **silently resets a synchronized tab back to Workers.**

Replace `Assets/Scripts/UI/NpcList/Controllers/NpcListTabSwitcher.cs` with:

```csharp
// Two buttons flip between Workers and Robots list contents via SetActive on each
// content root; the active tab's button graphic is tinted with the selected colour.
//
// The active tab is durable state, not just a pair of SetActive calls, because the web controller
// mirrors it: WorkersActive is the truth and TabChanged is what the remote-control publisher
// reports. RequestTab is idempotent, so an incoming remote command that matches the current tab
// raises nothing and cannot ping-pong against its own echo.

using System;
using UnityEngine;
using UnityEngine.UI;

namespace Guardian.VisionPro.UI
{
    [DisallowMultipleComponent]
    public class NpcListTabSwitcher : MonoBehaviour
    {
        [Header("Buttons")]
        [SerializeField] private Button workersButton;
        [SerializeField] private Button robotsButton;

        [Header("List contents (the GO that holds the spawned rows)")]
        [SerializeField] private GameObject workersContent;
        [SerializeField] private GameObject robotsContent;

        [Tooltip("Only used for the very first initialization — re-enabling the panel keeps whichever tab is active.")]
        [SerializeField] private bool workersActiveByDefault = true;

        [Header("Tab colours")]
        [Tooltip("Colour of the SELECTED (active) tab button.")]
        [SerializeField] private Color selectedColor = Color.white;
        [Tooltip("Colour of the UNSELECTED tab button.")]
        [SerializeField] private Color unselectedColor = new Color(0.5f, 0.5f, 0.5f, 1f);
        [Tooltip("Graphic tinted on the WORKERS button. Empty => the button's Target Graphic.")]
        [SerializeField] private Graphic workersGraphic;
        [Tooltip("Graphic tinted on the ROBOTS button. Empty => the button's Target Graphic.")]
        [SerializeField] private Graphic robotsGraphic;
        [Tooltip("Force the buttons' Transition to None so these colours aren't overwritten by the button's own hover/press tint.")]
        [SerializeField] private bool overrideButtonTransition = true;

        private bool _initialized;

        public bool WorkersActive { get; private set; }

        public event Action<bool> TabChanged;

        private void OnEnable()
        {
            if (workersButton != null) workersButton.onClick.AddListener(OnWorkersClicked);
            if (robotsButton != null) robotsButton.onClick.AddListener(OnRobotsClicked);

            ResolveGraphics();
            if (overrideButtonTransition)
            {
                if (workersButton != null) workersButton.transition = Selectable.Transition.None;
                if (robotsButton != null) robotsButton.transition = Selectable.Transition.None;
            }

            if (!_initialized)
            {
                _initialized = true;
                WorkersActive = workersActiveByDefault;
            }

            // Render the CURRENT tab, never the default: disabling and re-enabling the panel must
            // not silently undo a tab the controller already switched to.
            Render(WorkersActive);
        }

        private void OnDisable()
        {
            if (workersButton != null) workersButton.onClick.RemoveListener(OnWorkersClicked);
            if (robotsButton != null) robotsButton.onClick.RemoveListener(OnRobotsClicked);
        }

        private void OnWorkersClicked() { RequestTab(true); }
        private void OnRobotsClicked() { RequestTab(false); }

        // Show workers (true) or robots (false). Safe to call from anywhere, local or remote.
        public void RequestTab(bool showWorkers)
        {
            bool changed = !_initialized || WorkersActive != showWorkers;

            _initialized = true;
            WorkersActive = showWorkers;
            Render(showWorkers);

            if (!changed) return;

            try { TabChanged?.Invoke(showWorkers); }
            catch (Exception e) { Debug.LogError($"[NpcListTabSwitcher] TabChanged subscriber threw: {e}", this); }
        }

        // Kept so existing inspector hookups and callers keep working.
        public void Apply(bool showWorkers) => RequestTab(showWorkers);

        private void Render(bool showWorkers)
        {
            if (workersContent != null) workersContent.SetActive(showWorkers);
            if (robotsContent != null) robotsContent.SetActive(!showWorkers);

            ResolveGraphics();
            if (workersGraphic != null) workersGraphic.color = showWorkers ? selectedColor : unselectedColor;
            if (robotsGraphic != null) robotsGraphic.color = showWorkers ? unselectedColor : selectedColor;
        }

        private void ResolveGraphics()
        {
            if (workersGraphic == null && workersButton != null) workersGraphic = workersButton.targetGraphic;
            if (robotsGraphic == null && robotsButton != null) robotsGraphic = robotsButton.targetGraphic;
        }
    }
}
```

## A5. The publisher

New file: `Assets/Scripts/RemoteControl/NpcRoster/Controllers/RemoteNpcRosterPublisher.cs`.
It is modelled on the existing `RemoteCompartmentNavigator` — same publish idiom, same de-dup
guard, same `Start()` seeding.

```csharp
// Single place that mirrors the worker/robot list to the web controller and resolves what the
// controller clicks back to a live scene object.
//
// Outbound — three HostMessage 'state' topics:
//   guardian.npc.roster     value = generation, payload = RemoteListPayload (both groups at once)
//   guardian.npc.selection  value = remote id, empty when nothing is selected
//   guardian.npc.tab        value = workers | robots
// VisionProWebRtcHost caches each topic and replays them as one snapshot when a controller
// connects, so a controller joining mid-session is correct without touching anything.
//
// Inbound — the handlers next to this file call TryResolve and then raise the SAME SO channels the
// on-device UI raises, so a remote click and a pinch on the panel are indistinguishable downstream.
//
// Two ordering rules this component exists to respect:
//
// 1. It listens to NpcListBuilder.Built, NOT to the compartment channels. NpcListCompartmentFilter
//    rebuilds the list from those channels, and SO-channel subscriber order is not a contract — a
//    second listener would publish the pre-rebuild list about half the time. Built also guarantees
//    SetResolvedPresentation has run, which is the only reason the per-run random worker names are
//    readable here.
//
// 2. It publishes continuously, including while nothing is connected. The host raises
//    OnClientConnected BEFORE it sends the snapshot, so state built inside a connected-callback
//    would arrive ahead of the snapshot and then be overwritten by the snapshot's older values.

using System;
using System.Collections.Generic;
using Guardian.VisionPro.Compartments;
using Guardian.VisionPro.UI;
using SuperAnretan.RemoteControl;
using UnityEngine;

namespace Guardian.VisionPro.RemoteControl
{
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(600)]
    [AddComponentMenu("Guardian/Remote Control/Remote Npc Roster Publisher")]
    public class RemoteNpcRosterPublisher : MonoBehaviour
    {
        public const string RosterTopic = "guardian.npc.roster";
        public const string SelectionTopic = "guardian.npc.selection";
        public const string TabTopic = "guardian.npc.tab";

        public const string GroupWorkers = "workers";
        public const string GroupRobots = "robots";

        [Header("Sources")]
        [Tooltip("The builder that spawns the panel rows. Its Built event is what triggers a roster push.")]
        [SerializeField] private NpcListBuilder listBuilder;

        [Tooltip("Owns which tab is active. Optional — leave empty to not publish the tab topic.")]
        [SerializeField] private NpcListTabSwitcher tabSwitcher;

        [Tooltip("Broadcast of the applied selection. This, never a select request, is what gets published.")]
        [SerializeField] private NpcSelectionChangedEventChannel selectionChangedChannel;

        [Tooltip("Read-only, used to label the roster with the compartment it was built for. Optional.")]
        [SerializeField] private RemoteCompartmentNavigator navigator;

        [Header("Report State To Controller")]
        [Tooltip("HostMessageSendChannel.asset. Leave empty to stay silent.")]
        [SerializeField] private StringEventChannel hostMessageSendChannel;

        [Header("Debug")]
        [SerializeField] private bool debugLogs;

        private readonly Dictionary<string, HoverableNpcTarget> _byId = new();
        private IReadOnlyList<HoverableNpcTarget> _lastSource;
        private int _generation;
        private string _publishedSelection;
        private string _publishedTab;

        public string Generation => _generation.ToString();

        public int Count => _byId.Count;

        private void OnEnable()
        {
            // Subscribed in OnEnable so the builder's very first Build() in Start() is not missed.
            if (listBuilder != null) listBuilder.Built += OnListBuilt;
            else Debug.LogError("[RemoteRoster] listBuilder is not assigned — the controller will never get a list.", this);

            if (selectionChangedChannel != null) selectionChangedChannel.OnRaised += OnSelectionChanged;
            if (tabSwitcher != null) tabSwitcher.TabChanged += OnTabChanged;
        }

        private void OnDisable()
        {
            if (listBuilder != null) listBuilder.Built -= OnListBuilt;
            if (selectionChangedChannel != null) selectionChangedChannel.OnRaised -= OnSelectionChanged;
            if (tabSwitcher != null) tabSwitcher.TabChanged -= OnTabChanged;
        }

        // Seeds the topics that have no event of their own yet, so a controller connecting before
        // any interaction still finds them in its snapshot. Execution order 600 puts this after
        // NpcListBuilder (500), so the roster is already out by now.
        private void Start()
        {
            PublishSelection(null);
            if (tabSwitcher != null) PublishTab(tabSwitcher.WorkersActive);
        }

        // ───────── Inbound resolution (used by the handlers) ─────────

        // Resolves a controller click. A stale generation is refused AND the roster is re-published,
        // so the controller heals itself instead of sitting on a list that can no longer be clicked.
        public bool TryResolve(string id, string generation, out HoverableNpcTarget target, out string error)
        {
            target = null;

            if (string.IsNullOrWhiteSpace(id))
            {
                error = "empty npc id";
                return false;
            }

            if (generation != Generation)
            {
                error = $"stale roster generation '{generation}' (current '{Generation}')";
                RepublishRoster();
                return false;
            }

            if (!_byId.TryGetValue(id, out target) || target == null)
            {
                error = $"unknown npc id '{id}' in generation '{Generation}'";
                return false;
            }

            error = null;
            return true;
        }

        // Re-sends the current roster without bumping the generation: the addressable set has not
        // changed, the controller just needs to see it again.
        public void RepublishRoster()
        {
            if (_lastSource == null) return;
            PublishRoster(_lastSource, bumpGeneration: false);
        }

        // ───────── Outbound ─────────

        private void OnListBuilt(IReadOnlyList<HoverableNpcTarget> source)
        {
            _lastSource = source;
            PublishRoster(source, bumpGeneration: true);
        }

        private void PublishRoster(IReadOnlyList<HoverableNpcTarget> source, bool bumpGeneration)
        {
            if (bumpGeneration) _generation++;

            _byId.Clear();

            var payload = new RemoteListPayload(Generation, navigator != null ? navigator.CurrentRemoteId : string.Empty);

            int missingId = 0;
            var duplicates = new List<string>();

            for (int i = 0; i < source.Count; i++)
            {
                HoverableNpcTarget target = source[i];
                if (target == null) continue;

                string id = target.RemoteId;
                if (string.IsNullOrEmpty(id))
                {
                    missingId++;
                    continue;
                }

                if (_byId.ContainsKey(id))
                {
                    duplicates.Add($"{target.name} / {_byId[id].name} (id '{id}')");
                    continue;
                }

                _byId.Add(id, target);

                bool isWorker = target.npcPanelType == NpcPanelType.Worker;
                string label = !string.IsNullOrEmpty(target.ResolvedDisplayName)
                    ? target.ResolvedDisplayName
                    : target.npcName;

                payload.items.Add(new RemoteListItem(id, isWorker ? GroupWorkers : GroupRobots, label)
                {
                    // The controller ships its own sprites and looks them up by this key.
                    presentationKey = isWorker ? $"worker:{label}" : $"robot:{target.npcPanelType}",
                });
            }

            // Loud, because either problem silently removes an NPC from the controller entirely.
            if (missingId > 0)
                Debug.LogError($"[RemoteRoster] {missingId} NPC(s) have no remoteId and were left out of the " +
                               "roster. Run 'Assign Remote Ids' on NpcListBuilder and save the scene.", this);

            if (duplicates.Count > 0)
                Debug.LogError($"[RemoteRoster] Duplicate remoteId(s), later ones dropped: " +
                               $"{string.Join("; ", duplicates)}. Run 'Assign Remote Ids' to fix.", this);

            Send(HostMessage.State(RosterTopic, payload.generation, payload.ToJson()));

            if (debugLogs)
                Debug.Log($"[RemoteRoster] published roster gen={payload.generation} " +
                          $"context='{payload.contextId}' items={payload.items.Count}", this);

            // A rebuild replaces the addressable set, and the host clears the selection on any
            // focus change — so whatever the controller was highlighting is no longer valid.
            if (bumpGeneration && !_byId.ContainsKey(_publishedSelection ?? string.Empty))
                PublishSelection(null);
        }

        private void OnSelectionChanged(HoverableNpcTarget selected)
        {
            PublishSelection(selected);
        }

        private void PublishSelection(HoverableNpcTarget selected)
        {
            string id = selected != null ? selected.RemoteId : string.Empty;

            if (selected != null && string.IsNullOrEmpty(id))
                Debug.LogError($"[RemoteRoster] '{selected.name}' was selected but has no remoteId — " +
                               "the controller cannot highlight it.", selected);

            if (id == _publishedSelection) return;
            _publishedSelection = id;

            Send(HostMessage.State(SelectionTopic, id, new RemoteListSelectArgs(Generation).ToJson()));

            if (debugLogs) Debug.Log($"[RemoteRoster] published selection = '{id}'", this);
        }

        private void OnTabChanged(bool workersActive) => PublishTab(workersActive);

        private void PublishTab(bool workersActive)
        {
            string tab = workersActive ? GroupWorkers : GroupRobots;
            if (tab == _publishedTab) return;
            _publishedTab = tab;

            Send(HostMessage.State(TabTopic, tab));

            if (debugLogs) Debug.Log($"[RemoteRoster] published tab = {tab}", this);
        }

        private void Send(HostMessage message)
        {
            if (hostMessageSendChannel == null) return;
            hostMessageSendChannel.Raise(message.ToJson());
        }
    }
}
```

## A6. The three command handlers

All three go in `Assets/Scripts/RemoteControl/NpcRoster/Handlers/`. They follow the §9 checklist in
that project's `REMOTE_CONTROL_COMMANDS.md`, and they **throw on bad input** — a silent `return`
makes `CommandProcessor` log `[OK] Executed` for a command that did nothing, and debugging the
controller becomes guesswork.

### `RemoteSelectNpcHandler.cs`

```csharp
// Remote command: select one worker or robot from the pushed roster.
//
//   { "commandType": "select_npc", "targetId": "app",
//     "value": "a1b2c3d4e5f6", "payload": "{\"protocolVersion\":1,\"generation\":\"7\"}" }
//
// `value` is the NPC's stable remoteId; the payload echoes the roster generation the controller
// rendered that row from. A mismatch means the list has been rebuilt since — the publisher refuses
// it and re-sends the roster, rather than acting on whatever now sits under that id.
//
// It raises the same NpcSelectEventChannel a row click on the headset raises, so everything
// downstream — row tint, info panel, NPC light, follower camera — behaves identically and cannot
// disagree with the on-device UI.

using System;
using Guardian.VisionPro.Compartments;
using SuperAnretan.RemoteControl;
using UnityEngine;

namespace Guardian.VisionPro.RemoteControl
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Guardian/Remote Control/Remote Select Npc Handler")]
    public class RemoteSelectNpcHandler : CommandHandlerBase
    {
        public const string Command = "select_npc";

        [Header("Roster")]
        [Tooltip("Owns the id -> NPC mapping and the current roster generation.")]
        [SerializeField] private RemoteNpcRosterPublisher publisher;

        [Header("Event Channels")]
        [Tooltip("Select-request channel — the same asset a panel row click raises.")]
        [SerializeField] private NpcSelectEventChannel selectChannel;

        public override string CommandType => Command;

        public override void Handle(RemoteCommand command, GameObject target)
        {
            if (publisher == null)
                throw new InvalidOperationException("publisher is not assigned on RemoteSelectNpcHandler");
            if (selectChannel == null)
                throw new InvalidOperationException("selectChannel is not assigned on RemoteSelectNpcHandler");
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            // JsonUtility never throws on junk — it fills in defaults — so the shape is checked here.
            RemoteListSelectArgs args = RemoteListSelectArgs.FromJson(command.payload);
            if (args == null || !args.IsValid)
                throw new ArgumentException($"'{command.payload}' is not a valid select payload " +
                                            $"(need protocolVersion {RemoteListPayload.CurrentProtocolVersion} and a generation)");

            if (!publisher.TryResolve(command.value, args.generation, out HoverableNpcTarget npc, out string error))
                throw new ArgumentException(error);

            selectChannel.Raise(npc);
        }
    }
}
```

### `RemoteClearNpcSelectionHandler.cs`

```csharp
// Remote command: drop the current worker/robot selection.
//
//   { "commandType": "clear_npc_selection", "targetId": "app", "value": "" }
//
// Raises the same clear-request channel the info panel's back button raises. Clearing when nothing
// is selected is a no-op in NpcHoverClickSystem, so this is safe to send at any time — but note it
// then broadcasts nothing, so the controller must not wait for an echo to confirm it.

using System;
using SuperAnretan.RemoteControl;
using UnityEngine;

// Both namespaces define a VoidEventChannel, so an unqualified name here is CS0104. The clear
// channel is GuardianCore's — same aliasing trick RemoteCompartmentNavigator already uses.
using GuardianVoidEventChannel = Guardian.Core.UI.Events.VoidEventChannel;

namespace Guardian.VisionPro.RemoteControl
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Guardian/Remote Control/Remote Clear Npc Selection Handler")]
    public class RemoteClearNpcSelectionHandler : CommandHandlerBase
    {
        public const string Command = "clear_npc_selection";

        [Header("Event Channels")]
        [Tooltip("NpcClearSelectionChannel.asset — the same asset the info panel's back button raises.")]
        [SerializeField] private GuardianVoidEventChannel clearSelectionChannel;

        public override string CommandType => Command;

        public override void Handle(RemoteCommand command, GameObject target)
        {
            if (clearSelectionChannel == null)
                throw new InvalidOperationException("clearSelectionChannel is not assigned on RemoteClearNpcSelectionHandler");

            clearSelectionChannel.Raise();
        }
    }
}
```

### `RemoteSetNpcTabHandler.cs`

```csharp
// Remote command: switch the panel between the Workers and Robots tab.
//
//   { "commandType": "set_npc_tab", "targetId": "app", "value": "workers" }
//
// RequestTab is idempotent and only raises TabChanged on a real change, so a command carrying the
// tab that is already active publishes nothing back and cannot ping-pong against its own echo.

using System;
using Guardian.VisionPro.UI;
using SuperAnretan.RemoteControl;
using UnityEngine;

namespace Guardian.VisionPro.RemoteControl
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Guardian/Remote Control/Remote Set Npc Tab Handler")]
    public class RemoteSetNpcTabHandler : CommandHandlerBase
    {
        public const string Command = "set_npc_tab";

        [Header("UI")]
        [Tooltip("Owns which tab is active.")]
        [SerializeField] private NpcListTabSwitcher tabSwitcher;

        public override string CommandType => Command;

        public override void Handle(RemoteCommand command, GameObject target)
        {
            if (tabSwitcher == null)
                throw new InvalidOperationException("tabSwitcher is not assigned on RemoteSetNpcTabHandler");

            string value = command != null ? command.value : null;

            if (string.Equals(value, RemoteNpcRosterPublisher.GroupWorkers, StringComparison.OrdinalIgnoreCase))
                tabSwitcher.RequestTab(true);
            else if (string.Equals(value, RemoteNpcRosterPublisher.GroupRobots, StringComparison.OrdinalIgnoreCase))
                tabSwitcher.RequestTab(false);
            else
                throw new ArgumentException($"'{value}' is not a tab " +
                                            $"(expected '{RemoteNpcRosterPublisher.GroupWorkers}' or " +
                                            $"'{RemoteNpcRosterPublisher.GroupRobots}')");
        }
    }
}
```

## A7. Assembly references

`Assets/Scripts/RemoteControl/Guardian.VisionPro.RemoteControl.asmdef` references only
`SuperAnretan.RemoteControl.Runtime` and `Eternal.GuardianCore.Runtime`. Add two:

```json
"references": [
    "SuperAnretan.RemoteControl.Runtime",
    "Eternal.GuardianCore.Runtime",
    "Guardian.VisionPro.Compartments",
    "Guardian.VisionPro.UI"
]
```

`Guardian.VisionPro.Compartments` brings `HoverableNpcTarget`, `NpcSelectEventChannel`,
`NpcSelectionChangedEventChannel` and `NpcPanelType`; `Guardian.VisionPro.UI` brings
`NpcListBuilder` and `NpcListTabSwitcher`. No cycle — nothing references
`Guardian.VisionPro.RemoteControl`.

## A8. Scene wiring

In `Assets/Scenes/VisionProScene.unity`, on the existing `/RemoteControl/RemoteControl_App`
(it already carries `CommandTarget` with `targetId = "app"`, and it is never disabled — which
matters, because a disabled handler unregisters itself in `OnDisable`).

Add four components:

**`RemoteNpcRosterPublisher`**

| Field | Value |
|---|---|
| List Builder | `Controllers/GrabbableUIController/NpcList/NpcListBuilder` |
| Tab Switcher | the `NpcListTabSwitcher` on `Controllers/GrabbableUIController/NpcList` |
| Selection Changed Channel | `Assets/ScriptableObjects/Events/Npc/NpcSelectionChangedEventChannel.asset` |
| Navigator | the `RemoteCompartmentNavigator` on this same object |
| Host Message Send Channel | `Assets/RemoteControl/SO/HostMessageSendChannel.asset` |
| Debug Logs | on, until it is verified |

**`RemoteSelectNpcHandler`**

| Field | Value |
|---|---|
| Handler Registry | `Assets/RemoteControl/SO/HandlerRegistry.asset` |
| Publisher | the publisher above |
| Select Channel | `Assets/ScriptableObjects/Events/Npc/NpcSelectEventChannel.asset` |

**`RemoteClearNpcSelectionHandler`**

| Field | Value |
|---|---|
| Handler Registry | `Assets/RemoteControl/SO/HandlerRegistry.asset` |
| Clear Selection Channel | `Assets/ScriptableObjects/Events/Npc/NpcClearSelectionChannel.asset` |

**`RemoteSetNpcTabHandler`**

| Field | Value |
|---|---|
| Handler Registry | `Assets/RemoteControl/SO/HandlerRegistry.asset` |
| Tab Switcher | the same `NpcListTabSwitcher` |

`HandlerRegistry.asset` must be the **same asset** `CommandProcessor` on
`/RemoteControl/RemoteControl_Host` uses, or the handler is never found and the host logs
`[WARN] Unknown command type`. Do not substitute the package's `Runtime/DefaultSetup/SO/` defaults.

## A9. Assign the ids

Select `Controllers/GrabbableUIController/NpcList/NpcListBuilder` and, in order:

1. **Collect Targets From Scene** — repopulates the target list (65 NPCs today).
2. **Assign Remote Ids** — the new button. Expect `65 assigned, 0 kept, 0 skipped` the first time.
3. **Save the scene.** The ids are prefab overrides on scene instances; unsaved means lost.

Re-run steps 1–3 after adding or duplicating any NPC. Existing ids are preserved; only empty and
colliding ones are written.

## A10. Update the contract document

`REMOTE_CONTROL_COMMANDS.md` in that project is the declared network contract, and its own stated
rule is that a new command lands in the §3 table in the same commit as its handler. Add:

- §1: rows for the tabs, the rows, the selection highlight and Clear selection.
- §3: commands 5–7 (`select_npc`, `clear_npc_selection`, `set_npc_tab`) plus a subsection each.
  For `select_npc`, spell out that `payload` is **required** and that a stale generation is
  rejected *and* triggers a roster re-send.
- §4: the three `guardian.npc.*` topics.
- §6: the video row is stale — it says 1280×720 @ 24 fps / 2500 kbit/s, but `NetworkConfig.asset`
  and the 2.2.0 defaults are 960×540 @ 15 fps / 1200 kbit/s.
- §10: a changelog row.
- Header: the package version (it still says 2.0.0 / tag `vpwebgl1.2`).

---

# 6. PART B — the controller (`Guardian_VisionPro_Controller`)

That project generates its whole scene from
`Assets/RemoteControl/Editor/RemoteControlControllerSceneBuilder.cs`
(*Tools > Guardian > Rebuild WebGL Controller Scene*), so the list is added by extending the
builder rather than by hand-editing the scene. No new runtime script is needed — the package's
`RemoteListView`, `RemoteTabBar` and `RemoteListRow` cover it.

## B1. Bump the package pin

Same as A1, in that project's `Packages/manifest.json`.

## B2. New file: the contract constants

`Assets/RemoteControl/Scripts/GuardianRemoteContract.cs`

```csharp
namespace Guardian.RemoteControl
{
    // The network contract with the Vision Pro host, in one place.
    //
    // These strings are matched by value on the other side, character for character. The
    // authoritative table is REMOTE_CONTROL_COMMANDS.md in guardian_ui_vision_pro; keep both in
    // step, and see ROSTER_SYNC.md in the remote-control package for how the list feature works.
    public static class GuardianRemoteContract
    {
        // Global target on the host: /RemoteControl/RemoteControl_App in VisionProScene.
        public const string TargetApp = "app";

        // ── Topics the host publishes (HostMessage 'state') ──
        public const string TopicNavigation = "navigation";
        public const string TopicNpcRoster = "guardian.npc.roster";
        public const string TopicNpcSelection = "guardian.npc.selection";
        public const string TopicNpcTab = "guardian.npc.tab";

        // ── Commands the controller sends (RemoteCommand) ──
        public const string CommandFocusCompartment = "focus_compartment";
        public const string CommandGoHome = "go_home";
        public const string CommandSelectNpc = "select_npc";
        public const string CommandClearNpcSelection = "clear_npc_selection";
        public const string CommandSetNpcTab = "set_npc_tab";

        // ── Values ──
        public const string NavigationHome = "home";
        public const string GroupWorkers = "workers";
        public const string GroupRobots = "robots";
    }
}
```

## B3. Scene builder changes

### B3.1 Constants

Next to `SoDir` / `ScenePath`:

```csharp
    private const string PrefabsDir = RootDir + "/Prefabs";
    private const string NpcRowPrefabPath = PrefabsDir + "/NpcListRow.prefab";

    // Height the vertical layout gives each list row.
    private const float NpcRowHeight = 44f;
```

### B3.2 The icon set asset

Add the field next to `_hostMessageReceived`:

```csharp
    private static RemoteListIconSet _npcIconSet;
```

and create it at the end of `CreateSoAssets()`, before `AssetDatabase.SaveAssets()`:

```csharp
        // Sprites for the worker/robot rows. Created empty on purpose: the host only names a
        // presentationKey, and the actual images ship with THIS build — drop them in here.
        _npcIconSet = EnsureAsset<RemoteListIconSet>("NpcIconSet");
```

### B3.3 The state router

In `CreateControllerPanel`, immediately after `Stretch(root.GetComponent<RectTransform>());`:

```csharp
        // Flattens the host's return channel for every state-driven widget below. On connect the
        // host sends ONE 'snapshot' rather than individual 'state' messages, so anything reading
        // only 'state' would stay blank until the host changed something. Created first so the
        // list and the tab bar can be wired to it explicitly.
        var stateRouter = root.AddComponent<HostStateRouter>();
        SetRef(stateRouter, "_hostMessageReceivedChannel", _hostMessageReceived);
        SetRef(stateRouter, "_onDisconnectedChannel", _onDisconnected);
        SetRef(stateRouter, "_logChannel", _logChannel);
```

The existing `GuardianHostStateReader` does its own snapshot flattening for the navigation markers
and the capture label. Leave it alone — two subscribers on the same channel is fine. Folding it onto
the router later is a tidy-up, not a requirement.

### B3.4 Make room, then add the panel

Three anchor changes plus one call:

```csharp
// videoFrame: was (0.03,0.30)-(0.66,0.78)
Anchor(videoFrame.GetComponent<RectTransform>(), new Vector2(0.03f, 0.30f), new Vector2(0.44f, 0.78f));

// insert immediately before the "── Guardian commands ──" block
CreateNpcListPanel(root.transform, res, stateRouter);

// log panel: was (0.03,0.02)-(0.97,0.27)
var logText = CreateLogPanel(root.transform, new Vector2(0.03f, 0.02f), new Vector2(0.44f, 0.27f));
```

Resulting layout: video top-left, log under it, the NPC list as a tall column at x 0.46–0.66, and
the existing compartment buttons untouched on the right.

### B3.5 New builder methods

Add before the `// ───────── build settings ─────────` section:

```csharp
    // ───────── workers / robots list ─────────

    // Mirror of the grabbable panel on the headset: two tabs over one pushed roster.
    //
    // Both RemoteListViews read the SAME roster topic and differ only by group, because the host
    // sends one roster carrying every group — there is no second topic to keep in step. Which tab
    // shows is host state, not local UI state, so the two screens cannot drift apart.
    private static void CreateNpcListPanel(Transform parent, TMP_DefaultControls.Resources res, HostStateRouter router)
    {
        var rowPrefab = EnsureNpcRowPrefab(res);

        var panel = new GameObject("NpcListPanel", typeof(RectTransform), typeof(Image));
        panel.transform.SetParent(parent, false);
        Anchor(panel.GetComponent<RectTransform>(), new Vector2(0.46f, 0.02f), new Vector2(0.66f, 0.78f));
        panel.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.35f);

        var tabsRow = new GameObject("Tabs", typeof(RectTransform));
        tabsRow.transform.SetParent(panel.transform, false);
        Anchor(tabsRow.GetComponent<RectTransform>(), new Vector2(0f, 0.93f), new Vector2(1f, 1f));

        var workersBtn = CreateButton(tabsRow.transform, "WorkersTab", "Workers", res, new Vector2(0f, 0f), new Vector2(0.49f, 1f));
        var robotsBtn = CreateButton(tabsRow.transform, "RobotsTab", "Robots", res, new Vector2(0.51f, 0f), new Vector2(1f, 1f));
        SetTmpFontSize(workersBtn, 18f);
        SetTmpFontSize(robotsBtn, 18f);

        var workersList = CreateScrollList(panel.transform, "WorkersList", new Vector2(0f, 0.07f), new Vector2(1f, 0.92f),
            rowPrefab, router, GuardianRemoteContract.GroupWorkers);
        var robotsList = CreateScrollList(panel.transform, "RobotsList", new Vector2(0f, 0.07f), new Vector2(1f, 0.92f),
            rowPrefab, router, GuardianRemoteContract.GroupRobots);

        var tabBar = panel.AddComponent<RemoteTabBar>();
        SetRef(tabBar, "_stateRouter", router);
        SetRef(tabBar, "_commandSendChannel", _commandSend);
        SetRef(tabBar, "_logChannel", _logChannel);
        SetString(tabBar, "_tabTopic", GuardianRemoteContract.TopicNpcTab);
        SetString(tabBar, "_setTabCommandType", GuardianRemoteContract.CommandSetNpcTab);
        SetString(tabBar, "_targetId", GuardianTargetId);
        SetString(tabBar, "_defaultValue", GuardianRemoteContract.GroupWorkers);
        SetTabs(tabBar, new[]
        {
            (GuardianRemoteContract.GroupWorkers, workersBtn.GetComponent<Button>(), workersList),
            (GuardianRemoteContract.GroupRobots, robotsBtn.GetComponent<Button>(), robotsList),
        });

        var clearBtn = CreateButton(panel.transform, "ClearSelectionButton", "Clear selection", res,
            new Vector2(0f, 0f), new Vector2(1f, 0.06f));
        SetTmpFontSize(clearBtn, 15f);
        ColorButton(clearBtn, new Color(0.35f, 0.37f, 0.42f));
        AddCommandButton(clearBtn, GuardianRemoteContract.CommandClearNpcSelection, string.Empty);
    }

    private static GameObject CreateScrollList(Transform parent, string name, Vector2 anchorMin, Vector2 anchorMax,
        RemoteListRow rowPrefab, HostStateRouter router, string group)
    {
        var scrollGo = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(ScrollRect));
        scrollGo.transform.SetParent(parent, false);
        Anchor(scrollGo.GetComponent<RectTransform>(), anchorMin, anchorMax);
        scrollGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);

        var viewport = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
        viewport.transform.SetParent(scrollGo.transform, false);
        Stretch(viewport.GetComponent<RectTransform>());

        var content = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        content.transform.SetParent(viewport.transform, false);
        var contentRt = content.GetComponent<RectTransform>();
        contentRt.anchorMin = new Vector2(0f, 1f);
        contentRt.anchorMax = new Vector2(1f, 1f);
        contentRt.pivot = new Vector2(0.5f, 1f);
        contentRt.offsetMin = Vector2.zero;
        contentRt.offsetMax = Vector2.zero;

        var layout = content.GetComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(4, 4, 4, 4);
        layout.spacing = 3f;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var fitter = content.GetComponent<ContentSizeFitter>();
        fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scroll = scrollGo.GetComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 24f;
        scroll.viewport = viewport.GetComponent<RectTransform>();
        scroll.content = contentRt;

        var view = scrollGo.AddComponent<RemoteListView>();
        SetRef(view, "_stateRouter", router);
        SetRef(view, "_commandSendChannel", _commandSend);
        SetRef(view, "_onDisconnectedChannel", _onDisconnected);
        SetRef(view, "_rowPrefab", rowPrefab);
        SetRef(view, "_content", contentRt);
        SetRef(view, "_icons", _npcIconSet);
        SetRef(view, "_logChannel", _logChannel);
        SetString(view, "_rosterTopic", GuardianRemoteContract.TopicNpcRoster);
        SetString(view, "_selectionTopic", GuardianRemoteContract.TopicNpcSelection);
        SetString(view, "_selectCommandType", GuardianRemoteContract.CommandSelectNpc);
        SetString(view, "_targetId", GuardianTargetId);
        SetString(view, "_group", group);
        SetBool(view, "_optimisticSelection", true);
        SetBool(view, "_requestAck", true);

        return scrollGo;
    }

    // Regenerated every rebuild so the row layout stays described by this file and not by an asset
    // nobody remembers editing.
    private static RemoteListRow EnsureNpcRowPrefab(TMP_DefaultControls.Resources res)
    {
        EnsureFolder(PrefabsDir);

        var temp = new GameObject("NpcListRow", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
        temp.GetComponent<RectTransform>().sizeDelta = new Vector2(200f, NpcRowHeight);

        var background = temp.GetComponent<Image>();
        background.sprite = res.standard;
        background.type = Image.Type.Sliced;
        background.color = new Color(0.42f, 0.45f, 0.50f);

        var layoutElement = temp.GetComponent<LayoutElement>();
        layoutElement.minHeight = NpcRowHeight;
        layoutElement.preferredHeight = NpcRowHeight;

        var iconGo = new GameObject("Icon", typeof(RectTransform), typeof(Image));
        iconGo.transform.SetParent(temp.transform, false);
        var iconRt = iconGo.GetComponent<RectTransform>();
        iconRt.anchorMin = new Vector2(0f, 0.5f);
        iconRt.anchorMax = new Vector2(0f, 0.5f);
        iconRt.pivot = new Vector2(0f, 0.5f);
        iconRt.anchoredPosition = new Vector2(6f, 0f);
        iconRt.sizeDelta = new Vector2(NpcRowHeight - 10f, NpcRowHeight - 10f);
        var icon = iconGo.GetComponent<Image>();
        icon.preserveAspect = true;
        // RemoteListRow enables it only when the item's presentationKey resolves to a sprite.
        icon.enabled = false;

        var labelRt = CreateTmpText(temp.transform, "Label", "Row", 18f, TextAlignmentOptions.Left);
        Anchor(labelRt, new Vector2(0f, 0f), new Vector2(1f, 1f));
        labelRt.offsetMin = new Vector2(NpcRowHeight, 2f);
        labelRt.offsetMax = new Vector2(-6f, -2f);

        var row = temp.AddComponent<RemoteListRow>();
        SetRef(row, "_label", labelRt.GetComponent<TextMeshProUGUI>());
        SetRef(row, "_icon", icon);
        SetRef(row, "_button", temp.GetComponent<Button>());
        SetRef(row, "_selectionGraphic", background);
        SetColor(row, "_selectedColor", new Color(0.35f, 0.75f, 0.45f));
        SetColor(row, "_unselectedColor", new Color(0.42f, 0.45f, 0.50f));
        SetBool(row, "_overrideButtonTransition", true);

        var saved = PrefabUtility.SaveAsPrefabAsset(temp, NpcRowPrefabPath, out bool success);
        UnityEngine.Object.DestroyImmediate(temp);
        Report.AppendLine(success ? "prefab saved: " + NpcRowPrefabPath : "PREFAB SAVE FAILED: " + NpcRowPrefabPath);

        return saved != null ? saved.GetComponent<RemoteListRow>() : null;
    }

    private static void SetTabs(UnityEngine.Object target, (string value, Button button, GameObject content)[] tabs)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty("_tabs");
        if (prop == null) { Report.AppendLine("MISSING FIELD RemoteTabBar._tabs"); return; }

        prop.arraySize = tabs.Length;
        for (int i = 0; i < tabs.Length; i++)
        {
            var element = prop.GetArrayElementAtIndex(i);
            element.FindPropertyRelative("value").stringValue = tabs[i].value;
            element.FindPropertyRelative("button").objectReferenceValue = tabs[i].button;
            element.FindPropertyRelative("content").objectReferenceValue = tabs[i].content;
            // Left empty: RemoteTabBar falls back to the button's Target Graphic in OnEnable.
            element.FindPropertyRelative("graphic").objectReferenceValue = null;
        }
        so.ApplyModifiedPropertiesWithoutUndo();
    }
```

### B3.6 One new serialized-field helper

Next to `SetInt`:

```csharp
    private static void SetColor(UnityEngine.Object target, string field, Color value)
    {
        var so = new SerializedObject(target);
        var prop = so.FindProperty(field);
        if (prop == null) { Report.AppendLine("MISSING FIELD " + target.GetType().Name + "." + field); return; }
        prop.colorValue = value;
        so.ApplyModifiedPropertiesWithoutUndo();
    }
```

## B4. Rebuild and fill in the sprites

1. *Tools > Guardian > Rebuild WebGL Controller Scene*. The report should include
   `prefab saved: Assets/RemoteControl/Prefabs/NpcListRow.prefab` and no `MISSING FIELD` lines —
   a `MISSING FIELD` means a serialized name in the builder no longer matches the package.
2. Open `Assets/RemoteControl/SO/NpcIconSet.asset` and add one entry per key the host sends:
   - workers: `worker:<name>` for each of the 10 names in the host's `WorkerNameDatabase`
     (`Piotr M.`, `Jakub T.`, `Dawid I.`, `Aneta I.`, `Kinga L.`, `Dorin N.`, `Sorin O.`,
     `Elena R.`, `Alina M.`, `Daria N.`)
   - robots: `robot:Harvester`, `robot:Transporter`, `robot:Deleafer`
   Unknown or missing keys fall back to the set's `_fallback`, and rows render text-only if that is
   empty too — nothing breaks, the icon is just absent.

## B5. If you write your own list UI instead of using `RemoteListView`

Still use `HostStateRouter`, and get these four things right:

- **Apply a snapshot as one transaction.** While `IsApplyingSnapshot` is true, record what changed
  and rebuild on `SnapshotApplied`, in dependency order (roster → navigation → selection → tab).
  Reacting entry by entry paints a new list with the previous session's highlight for a frame.
- **Keep the selected id in view state, not on the row object,** and re-apply the highlight *after*
  rows are rebuilt. A selection update can arrive while rows are still being created.
- **Reuse row objects rather than destroying them.** `Destroy` is deferred to end-of-frame, so
  fresh rows share a layout group with the outgoing ones for one frame and visibly jump.
- **Disable row input until a valid roster has arrived** (`RemoteListPayload.IsValid`).

---

## 7. Ordering rules you must not break

Properties of the existing core, verified in its source. Each one has silently broken an
integration before.

1. **`OnClientConnected` fires BEFORE the snapshot is sent**
   (`VisionProWebRtcHost`: `_onClientConnectedChannel?.Raise()` then `SendSnapshot()`).
   Anything published from a connected-callback arrives *ahead* of the snapshot and is then
   overwritten by the snapshot's older cached value.
   **Rule: publish state continuously — seed in `Start()` and publish on change. Never build your
   state inside the connected callback.**

2. **A redundant command produces no echo.** If the host is already in the requested state, its
   change-broadcast does not fire, so no `state` goes out.
   **Rule: never block the controller waiting for an echo.**

3. **A rejected command also produces no echo.** The `ack` does not help — `dispatched` means
   "parsed and raised", nothing about the outcome.
   **Rule: make rejection observable some other way. Re-publishing the roster is the cheap option
   and is what PART A does; a per-`requestId` result topic is the thorough one.**

4. **The snapshot double-escapes every payload.** The list JSON is escaped once into
   `HostStateEntry.payload`, then the whole snapshot is escaped again into `HostMessage.payload`.
   On a 65-item roster ~5.2 KB raw becomes ~7 KB inside the snapshot, and it worsens with nesting.
   Individual `state` sends can succeed while the aggregate replay is what breaks.
   **Rule: watch the `[DataChannel] Snapshot: N topic(s), B bytes` log. Browsers cap a DataChannel
   message near 64 KB and drop an oversized one with no error at all.**

5. **Handlers must be idempotent.** A reconnect replays the whole snapshot, so every state you
   publish will be applied again.

6. **The return channel exists only on the WebRTC path.** `TransportHost` (the native LAN
   transport) has no `TrySend` and no state cache. A host-pushed list works on the
   WebGL ↔ Vision Pro link only.

7. **Only one controller at a time.** The host refuses a second offer while connected, reason
   `busy`. There is no collaborative-editing model here.

8. **Compartment focus clears the NPC selection.** On the Guardian host, `NpcHoverClickSystem`
   clears on both `OnFocusedCompartmentChanged` and the Global state event. So **"focus compartment
   B then select X" cannot be two chained commands** — the focus can be deferred by
   `RemoteCompartmentNavigator`'s existing `DeferredFocus` queue, the select then resolves against
   the old roster, and the focus clears it. Send them as separate user actions. A real compound
   action needs a host-side transaction that waits on focus confirmation *and* the matching roster
   rebuild, not a frame delay.

---

## 8. Checklists

Host:

- [ ] Package pin bumped to a tag containing 2.3.0
- [ ] `remoteId` on `HoverableNpcTarget`, plus the editor-only writer
- [ ] `Assign Remote Ids` run after `Collect Targets From Scene`, **scene saved**
- [ ] `NpcListBuilder.Built` raised at the end of `Build`
- [ ] `NpcListTabSwitcher` exposes `WorkersActive` / `TabChanged` / `RequestTab`, and `OnEnable`
      re-renders the current tab instead of the default
- [ ] Publisher listens to `Built`, not to the compartment channels
- [ ] Publisher publishes the selection from the change-**broadcast**, never from the request
- [ ] Publisher seeded in `Start()` and publishing while disconnected
- [ ] Three handlers, throwing on bad input, raising the app's existing SO channels
- [ ] All handlers on `/RemoteControl/RemoteControl_App` (never disabled) with `targetId = "app"`
- [ ] Every handler points at the **same** `HandlerRegistry.asset` `CommandProcessor` uses
- [ ] Generation mismatch rejected **and** the roster re-published
- [ ] asmdef references added
- [ ] `REMOTE_CONTROL_COMMANDS.md` updated in the same commit

Controller:

- [ ] Package pin bumped
- [ ] `HostStateRouter` in the scene, on the same `HostMessageReceivedChannel` the transport raises
- [ ] Snapshot applied as one transaction, in dependency order
- [ ] Row input disabled until a valid roster with a non-empty generation has arrived
- [ ] Highlight driven by the host's echo; any optimistic highlight is overridable
- [ ] Rows cleared on disconnect
- [ ] `NpcIconSet` filled in, or accepted as text-only rows
- [ ] Topic names and command types identical to the host's, character for character

---

## 9. Verification

**In-Editor, host side.** `VisionProNativeBridge.IsSupported` is false in the Editor, so the Editor
host can never answer a WebRTC offer — **there is no in-Editor loopback for this path.** What can
still be checked by log inspection, with the publisher's `Debug Logs` on:

- Enter play mode in `VisionProScene`. Expect `[NpcListBuilder] Built N worker row(s) + M robot
  row(s)` followed by exactly one `[RemoteRoster] published roster gen=1 context='home' items=N+M`,
  plus a published selection and tab. No `[RemoteRoster]` errors about missing or duplicate ids.
- Click a row on the panel → one `published selection = '<id>'`.
- Focus compartment B → filtered rebuild → exactly one new roster with `gen=2` and
  `context='compartment-b'`.
- Read the `[DataChannel] Snapshot: N topic(s), B bytes` line and confirm B is far under 49152.

Selection-by-command and the generation rejection are only reachable over a real DataChannel. If
testing those on device proves painful, the cheapest way to pull them back into the Editor is a
small editor button that raises `RemoteCommand`s directly on
`Assets/RemoteControl/SO/CommandReceivedChannel.asset`.

**End to end, device required.** Vision Pro build plus the WebGL controller:

- Connect after the headset has been running a while → the list, highlight and tab appear from the
  snapshot replay before touching anything.
- Click a worker in the browser → it selects on the headset (info panel expands, NPC light turns
  green); the browser highlight is confirmed by the echo, not by the local click.
- Select on the headset → the browser highlight moves.
- Switch tabs from the browser and from the headset, both directions.
- Focus compartment B from the browser, then click a row from the *old* list → host logs
  `[ERROR] Handler 'select_npc' threw: stale roster generation ...`, a fresh roster is pushed, and
  the controller heals itself.
- Disconnect / reconnect → list, highlight and tab all come back without interaction.
- Rapid repeated clicks and tab toggles → no duplicate rows, no stuck highlight.
- Check the robots tab actually populates. `RobotsList` is inactive by default in the headset scene
  and that tab has apparently never been exercised on device.

---

## 10. Troubleshooting

| Symptom | Cause |
|---|---|
| Controller list is empty until something changes on the host | Only `state` is handled, not `snapshot`. Use `HostStateRouter`. |
| Controller list is empty forever | `RemoteListPayload.IsValid` is false — usually an empty `generation`, or a protocol-version mismatch. |
| Host logs `[WARN] Unknown command type` | Handler not registered: wrong `HandlerRegistry.asset`, or its GameObject is disabled. |
| Host logs `[WARN] Target not found` | No `CommandTarget` with that `targetId`, or its object is disabled. |
| Host logs `[OK] Executed` but nothing happened | A handler returned silently instead of throwing. |
| Click does nothing, no host log at all | The message never arrived: check the DataChannel is open and the ids match exactly. |
| `[RemoteRoster] N NPC(s) have no remoteId` | `Assign Remote Ids` was not run, or the scene was not saved after it. |
| `[RemoteRoster] Duplicate remoteId(s)` | A duplicated GameObject copied its id. Re-run `Assign Remote Ids`. |
| An NPC is missing from the controller only | It has no `remoteId`, or its id collided and was dropped. See the two rows above. |
| Highlight flickers back to the old row | The controller is trusting its own click over the host's echo. |
| Tab silently jumps back to Workers | Something disabled and re-enabled the panel, and `OnEnable` re-applied the default. See A4. |
| First state after connect is overwritten by older data | State is being published from a connected-callback. See §7.1. |
| Works for a small list, breaks as it grows | Snapshot size. See §7.4 and the snapshot byte log. |
| Selection resets right after being set | A focus change cleared it. See §7.8. |
| Rows show no icons | `NpcIconSet` has no entry for that `presentationKey`, and no fallback. Harmless. |
