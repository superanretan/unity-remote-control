using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    // Host -> controller envelope over the DataChannel. Flat and JsonUtility-friendly:
    // {"messageType":"state","schemaVersion":1,"topic":"navigation","value":"a","payload":"","requestId":""}
    // Receivers must ignore unknown messageTypes so the schema can grow.
    [Serializable]
    public class HostMessage
    {
        public const int CurrentSchemaVersion = 1;

        public const string TypeState = "state";
        public const string TypeSnapshot = "snapshot";
        public const string TypeCapture = "capture";
        public const string TypeAck = "ack";

        public const string TopicCapture = "capture";

        public const string CaptureStarting = "starting";
        public const string CaptureStreaming = "streaming";
        public const string CaptureStopped = "stopped";
        public const string CaptureError = "error";

        public const string AckDispatched = "dispatched";
        public const string AckRejected = "rejected";

        public string messageType;
        public int schemaVersion = CurrentSchemaVersion;
        public string topic;
        public string value;
        public string payload = string.Empty;
        public string requestId = string.Empty;

        public HostMessage() { }

        public HostMessage(string messageType, string topic, string value, string payload = "", string requestId = "")
        {
            this.messageType = messageType;
            this.topic = topic;
            this.value = value;
            this.payload = payload ?? string.Empty;
            this.requestId = requestId ?? string.Empty;
        }

        public static HostMessage State(string topic, string value, string payload = "") =>
            new HostMessage(TypeState, topic, value, payload);

        public static HostMessage Capture(string state, string detail = "") =>
            new HostMessage(TypeCapture, TopicCapture, state, detail);

        public static HostMessage Ack(RemoteCommand command, string result, string detail = "") =>
            new HostMessage(TypeAck, command?.commandType ?? string.Empty, result, detail, command?.requestId ?? string.Empty);

        public static HostMessage Snapshot(IEnumerable<HostStateEntry> entries)
        {
            var snap = new HostStateSnapshot();
            if (entries != null) snap.entries.AddRange(entries);
            return new HostMessage(TypeSnapshot, string.Empty, string.Empty, JsonUtility.ToJson(snap));
        }

        public bool IsState => messageType == TypeState;
        public bool IsSnapshot => messageType == TypeSnapshot;
        public bool IsCapture => messageType == TypeCapture;
        public bool IsAck => messageType == TypeAck;

        public List<HostStateEntry> SnapshotEntries()
        {
            if (!IsSnapshot || string.IsNullOrEmpty(payload)) return new List<HostStateEntry>();
            try { return JsonUtility.FromJson<HostStateSnapshot>(payload)?.entries ?? new List<HostStateEntry>(); }
            catch (Exception) { return new List<HostStateEntry>(); }
        }

        public string ToJson() => JsonUtility.ToJson(this);

        public static HostMessage FromJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var msg = JsonUtility.FromJson<HostMessage>(json);
                return msg != null && !string.IsNullOrEmpty(msg.messageType) ? msg : null;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[HostMessage] Failed to deserialize: {e.Message}");
                return null;
            }
        }

        public override string ToString() =>
            $"[{messageType}] topic={topic} value={value}{(string.IsNullOrEmpty(requestId) ? "" : $" req={requestId}")}";
    }

    [Serializable]
    public class HostStateEntry
    {
        public string topic;
        public string value;
        public string payload = string.Empty;

        public HostStateEntry() { }

        public HostStateEntry(string topic, string value, string payload = "")
        {
            this.topic = topic;
            this.value = value;
            this.payload = payload ?? string.Empty;
        }
    }

    [Serializable]
    public class HostStateSnapshot
    {
        public List<HostStateEntry> entries = new();
    }
}
