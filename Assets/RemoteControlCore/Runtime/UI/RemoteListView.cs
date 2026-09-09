using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    // Controller-side mirror of a list the host owns: renders the rows the host pushed on a 'state'
    // topic, and sends a command when one is clicked. Transport-agnostic and domain-agnostic — it
    // only knows RemoteListPayload, so the same component serves any host-owned list.
    //
    // The host stays authoritative. A click is a REQUEST: the row highlight follows the selection
    // topic the host publishes back, never the local click. Optional optimistic highlighting is
    // available via _optimisticSelection, but the echo always wins.
    //
    // For a tabbed list, place one RemoteListView per tab with the same _rosterTopic and a
    // different _group — a single roster push then feeds every tab.
    [DisallowMultipleComponent]
    [AddComponentMenu("Remote Control/Remote List View")]
    public class RemoteListView : MonoBehaviour
    {
        [Header("Sources")]
        [Tooltip("Flattens the host's snapshot and state messages into one stream.")]
        [SerializeField] private HostStateRouter _stateRouter;

        [Tooltip("CommandSendChannel.asset — where a row click is published.")]
        [SerializeField] private CommandEventChannel _commandSendChannel;

        [Tooltip("Optional. Clears the rows so a reconnect cannot leave the previous session's list on screen.")]
        [SerializeField] private VoidEventChannel _onDisconnectedChannel;

        [Header("Topics (must match the host)")]
        [SerializeField] private string _rosterTopic = "roster";

        [Tooltip("Host topic carrying the selected item id, or an empty value for no selection.")]
        [SerializeField] private string _selectionTopic = "selection";

        [Header("Command sent on click")]
        [SerializeField] private string _selectCommandType = "select_entity";

        [Tooltip("CommandTarget.TargetId on the host. 'app' is the convention for global actions.")]
        [SerializeField] private string _targetId = "app";

        [Tooltip("Stamp a requestId so the host answers with an 'ack'. The ack only confirms dispatch — the selection topic is the real result.")]
        [SerializeField] private bool _requestAck = false;

        [Header("Rows")]
        [SerializeField] private RemoteListRow _rowPrefab;
        [SerializeField] private Transform _content;

        [Tooltip("Render only items whose group matches. Empty => every item.")]
        [SerializeField] private string _group;

        [Tooltip("Maps RemoteListItem.presentationKey to a local sprite. Optional.")]
        [SerializeField] private RemoteListIconSet _icons;

        [Header("Behaviour")]
        [Tooltip("Highlight the clicked row immediately, before the host confirms. The host's echo still overrides it.")]
        [SerializeField] private bool _optimisticSelection = true;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        private readonly List<RemoteListRow> _rows = new();
        private RemoteListPayload _roster;
        private string _selectedId = string.Empty;
        private bool _rebuildPending;
        private bool _selectionPending;
        private uint _requestCounter;

        // Current generation, or empty when no valid roster has arrived yet.
        public string Generation => _roster != null ? _roster.generation : string.Empty;

        public bool HasRoster => _roster != null && _roster.IsValid;

        public string SelectedId => _selectedId;

        public event Action<RemoteListPayload> RosterChanged;
        public event Action<string> SelectionChanged;

        private void OnEnable()
        {
            if (_stateRouter != null)
            {
                _stateRouter.StateChanged += OnStateChanged;
                _stateRouter.SnapshotApplied += OnSnapshotApplied;
            }
            else Debug.LogWarning($"[RemoteListView] HostStateRouter not assigned on '{name}'.", this);

            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised += OnDisconnected;

            // The router may already hold a roster from a snapshot that arrived before this enabled.
            AdoptRouterState();
        }

        private void OnDisable()
        {
            if (_stateRouter != null)
            {
                _stateRouter.StateChanged -= OnStateChanged;
                _stateRouter.SnapshotApplied -= OnSnapshotApplied;
            }

            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised -= OnDisconnected;
        }

        private void AdoptRouterState()
        {
            if (_stateRouter == null) return;

            RemoteListPayload roster = _stateRouter.GetList(_rosterTopic);
            if (roster != null)
            {
                _roster = roster;
                Rebuild();
            }

            if (_stateRouter.TryGet(_selectionTopic, out HostStateEntry selection))
                ApplySelection(selection.value);
        }

        private void OnStateChanged(string topic, string value, string payload)
        {
            if (topic == _rosterTopic)
            {
                RemoteListPayload roster = RemoteListPayload.FromJson(payload);
                if (roster == null || !roster.IsValid)
                {
                    Log($"[RemoteList] Ignoring an invalid roster on '{topic}'.");
                    return;
                }

                _roster = roster;

                // Inside a snapshot, wait: the selection topic may still be queued behind us, and
                // rebuilding per entry would show a roster with the previous session's highlight.
                if (_stateRouter.IsApplyingSnapshot) _rebuildPending = true;
                else Rebuild();
                return;
            }

            if (topic == _selectionTopic)
            {
                _selectedId = value ?? string.Empty;
                if (_stateRouter.IsApplyingSnapshot) _selectionPending = true;
                else ApplySelection(_selectedId);
            }
        }

        private void OnSnapshotApplied()
        {
            if (_rebuildPending)
            {
                _rebuildPending = false;
                Rebuild();
            }

            if (_selectionPending || _rows.Count > 0)
            {
                _selectionPending = false;
                ApplySelection(_selectedId);
            }
        }

        private void OnDisconnected()
        {
            _roster = null;
            _selectedId = string.Empty;
            _rebuildPending = false;
            _selectionPending = false;
            Rebuild();
        }

        // Reuses row objects instead of destroying them: Destroy is deferred to end-of-frame, so
        // fresh rows would share a layout group with the outgoing ones for a frame and visibly jump.
        private void Rebuild()
        {
            if (_rowPrefab == null || _content == null)
            {
                Debug.LogError($"[RemoteListView] Assign Row Prefab and Content on '{name}'.", this);
                return;
            }

            int used = 0;

            if (_roster != null && _roster.IsValid)
            {
                for (int i = 0; i < _roster.items.Count; i++)
                {
                    RemoteListItem item = _roster.items[i];
                    if (item == null || string.IsNullOrEmpty(item.id)) continue;
                    if (!string.IsNullOrEmpty(_group) && item.group != _group) continue;

                    RemoteListRow row = RowAt(used);
                    row.gameObject.SetActive(true);
                    row.Bind(item, _icons != null ? _icons.Resolve(item.presentationKey) : null, OnRowClicked);
                    used++;
                }
            }

            for (int i = used; i < _rows.Count; i++)
                if (_rows[i] != null) _rows[i].gameObject.SetActive(false);

            ApplySelection(_selectedId);

            try { RosterChanged?.Invoke(_roster); }
            catch (Exception e) { Log($"[RemoteList] RosterChanged subscriber threw: {e.Message}"); }
        }

        private RemoteListRow RowAt(int index)
        {
            while (_rows.Count <= index)
            {
                RemoteListRow created = Instantiate(_rowPrefab, _content);
                created.transform.localScale = Vector3.one;
                _rows.Add(created);
            }
            return _rows[index];
        }

        private void ApplySelection(string id)
        {
            _selectedId = id ?? string.Empty;

            for (int i = 0; i < _rows.Count; i++)
            {
                RemoteListRow row = _rows[i];
                if (row == null || !row.gameObject.activeSelf || row.Item == null) continue;
                row.SetSelected(!string.IsNullOrEmpty(_selectedId) && row.Item.id == _selectedId);
            }

            try { SelectionChanged?.Invoke(_selectedId); }
            catch (Exception e) { Log($"[RemoteList] SelectionChanged subscriber threw: {e.Message}"); }
        }

        private void OnRowClicked(RemoteListItem item)
        {
            if (item == null) return;

            if (!HasRoster)
            {
                Log("[RemoteList] Click ignored — no valid roster yet.");
                return;
            }

            if (_commandSendChannel == null)
            {
                Debug.LogWarning($"[RemoteListView] CommandSendChannel not assigned on '{name}'.", this);
                return;
            }

            var command = new RemoteCommand(
                _selectCommandType,
                _targetId,
                item.id,
                new RemoteListSelectArgs(_roster.generation).ToJson());

            if (_requestAck) command.requestId = $"list{++_requestCounter}-{Time.frameCount}";

            _commandSendChannel.Raise(command);

            if (_optimisticSelection) ApplySelection(item.id);
        }

        private void Log(string message) => _logChannel?.Raise(message);
    }
}
