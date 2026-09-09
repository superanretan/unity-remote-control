using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    // Controller-side fan-out for everything the host sends back.
    //
    // Why this exists: on connect the host does NOT replay individual 'state' messages — it sends
    // ONE 'snapshot' containing every cached topic (VisionProWebRtcHost.SendSnapshot). A controller
    // that only handles msg.IsState therefore shows nothing until something changes on the host,
    // which looks exactly like a broken connection. This flattens both forms into one stream so
    // that mistake is impossible.
    //
    // Drop it next to WebGLRemoteTransport and point it at the same HostMessageReceivedChannel the
    // transport raises for every inbound message.
    [DisallowMultipleComponent]
    [AddComponentMenu("Remote Control/Host State Router")]
    public class HostStateRouter : MonoBehaviour
    {
        [Header("Event Channels — Input")]
        [Tooltip("HostMessageReceivedChannel.asset — the raw JSON of every message the host sends back.")]
        [SerializeField] private StringEventChannel _hostMessageReceivedChannel;

        [Tooltip("Optional. Clears the cache so a reconnect cannot show stale rows from the last session.")]
        [SerializeField] private VoidEventChannel _onDisconnectedChannel;

        [Header("Options")]
        [SerializeField] private bool _clearOnDisconnect = true;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        // Last value per topic, insertion-ordered like the host's own cache.
        private readonly Dictionary<string, HostStateEntry> _state = new();
        private readonly List<string> _order = new();

        // topic, value, payload
        public event Action<string, string, string> StateChanged;

        // Raised once after every entry of a snapshot has been cached and fanned out. Subscribers
        // that need a coherent view (e.g. apply roster before selection) should rebuild here and
        // skip individual StateChanged while IsApplyingSnapshot is true.
        public event Action SnapshotApplied;

        // state, detail — "DataChannel open" is not "video flowing", so capture is reported apart.
        public event Action<string, string> CaptureChanged;

        public bool IsApplyingSnapshot { get; private set; }

        public string CaptureState { get; private set; } = HostMessage.CaptureStopped;
        public string CaptureDetail { get; private set; } = string.Empty;

        public IReadOnlyList<string> Topics => _order;

        private void OnEnable()
        {
            if (_hostMessageReceivedChannel != null) _hostMessageReceivedChannel.OnRaised += OnHostMessageJson;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised += OnDisconnected;
        }

        private void OnDisable()
        {
            if (_hostMessageReceivedChannel != null) _hostMessageReceivedChannel.OnRaised -= OnHostMessageJson;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised -= OnDisconnected;
        }

        public bool TryGet(string topic, out HostStateEntry entry)
        {
            if (!string.IsNullOrEmpty(topic)) return _state.TryGetValue(topic, out entry);
            entry = null;
            return false;
        }

        public string GetValue(string topic, string fallback = "") =>
            TryGet(topic, out var entry) ? entry.value : fallback;

        public string GetPayload(string topic, string fallback = "") =>
            TryGet(topic, out var entry) ? entry.payload : fallback;

        // Convenience for the common case: a topic whose payload is a RemoteListPayload.
        public RemoteListPayload GetList(string topic)
        {
            if (!TryGet(topic, out var entry) || string.IsNullOrEmpty(entry.payload)) return null;
            var payload = RemoteListPayload.FromJson(entry.payload);
            if (payload != null && !payload.IsValid)
            {
                Log($"[StateRouter] Topic '{topic}' carries an invalid list payload ({payload}).");
                return null;
            }
            return payload;
        }

        public void Clear()
        {
            _state.Clear();
            _order.Clear();
            CaptureState = HostMessage.CaptureStopped;
            CaptureDetail = string.Empty;
        }

        private void OnDisconnected()
        {
            if (_clearOnDisconnect) Clear();
        }

        private void OnHostMessageJson(string json)
        {
            var msg = HostMessage.FromJson(json);
            if (msg == null) return;   // the transport already logs unrecognised traffic

            if (msg.IsSnapshot)
            {
                ApplySnapshot(msg);
                return;
            }

            if (msg.IsState)
            {
                Cache(msg.topic, msg.value, msg.payload);
                Raise(msg.topic, msg.value, msg.payload);
                return;
            }

            if (msg.IsCapture)
            {
                CaptureState = msg.value;
                CaptureDetail = msg.payload ?? string.Empty;
                try { CaptureChanged?.Invoke(CaptureState, CaptureDetail); }
                catch (Exception e) { Log($"[StateRouter] CaptureChanged subscriber threw: {e.Message}"); }
            }

            // Unknown messageTypes (and 'ack') are ignored on purpose so the schema can grow.
        }

        private void ApplySnapshot(HostMessage msg)
        {
            List<HostStateEntry> entries = msg.SnapshotEntries();

            // Cache everything BEFORE fanning out, so a subscriber reacting to the first topic can
            // already read the rest via TryGet instead of seeing a half-applied view.
            for (int i = 0; i < entries.Count; i++)
            {
                HostStateEntry entry = entries[i];
                if (entry == null || string.IsNullOrEmpty(entry.topic)) continue;
                Cache(entry.topic, entry.value, entry.payload);
            }

            IsApplyingSnapshot = true;
            try
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    HostStateEntry entry = entries[i];
                    if (entry == null || string.IsNullOrEmpty(entry.topic)) continue;
                    Raise(entry.topic, entry.value, entry.payload);
                }
            }
            finally
            {
                IsApplyingSnapshot = false;
            }

            try { SnapshotApplied?.Invoke(); }
            catch (Exception e) { Log($"[StateRouter] SnapshotApplied subscriber threw: {e.Message}"); }
        }

        private void Cache(string topic, string value, string payload)
        {
            if (string.IsNullOrEmpty(topic)) return;
            if (!_state.ContainsKey(topic)) _order.Add(topic);
            _state[topic] = new HostStateEntry(topic, value, payload ?? string.Empty);
        }

        // One throwing subscriber must not stop the remaining topics from being delivered.
        private void Raise(string topic, string value, string payload)
        {
            try { StateChanged?.Invoke(topic, value, payload ?? string.Empty); }
            catch (Exception e) { Log($"[StateRouter] StateChanged subscriber threw on '{topic}': {e.Message}"); }
        }

        private void Log(string message) => _logChannel?.Raise(message);
    }
}
