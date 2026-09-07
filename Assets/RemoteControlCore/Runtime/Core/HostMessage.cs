using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Versioned envelope for everything the host sends back to the controller over the DataChannel —
    /// the return channel that <see cref="RemoteCommand"/> (controller → host) never had.
    ///
    /// <code>
    /// {"messageType":"state","schemaVersion":1,"topic":"navigation","value":"compartment-a","payload":"","requestId":""}
    /// </code>
    ///
    /// <list type="table">
    ///   <item><term>state</term><description>one topic changed: <c>topic</c> + <c>value</c> (+ optional <c>payload</c> JSON)</description></item>
    ///   <item><term>snapshot</term><description>full state right after the DataChannel opens: <c>payload</c> = <see cref="HostStateSnapshot"/> JSON</description></item>
    ///   <item><term>capture</term><description>screen-capture pipeline state, independent of the DataChannel: <c>value</c> = starting | streaming | stopped | error, <c>payload</c> = error code</description></item>
    ///   <item><term>ack</term><description>reply to a <see cref="RemoteCommand"/> that carried a <c>requestId</c>: <c>topic</c> = commandType, <c>value</c> = dispatched | rejected, <c>payload</c> = reason</description></item>
    /// </list>
    ///
    /// Same conventions as <see cref="RemoteCommand"/>: flat, JsonUtility-friendly, <see cref="ToJson"/> / <see cref="FromJson"/>.
    /// Unknown <c>messageType</c>s must be ignored by receivers so the schema can grow without breaking old controllers.
    /// </summary>
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

        /// <summary>state | snapshot | capture | ack (see class remarks).</summary>
        public string messageType;

        /// <summary>Schema version of this envelope. Receivers should accept any version ≤ their own.</summary>
        public int schemaVersion = CurrentSchemaVersion;

        /// <summary>What changed — e.g. "navigation", "capture", or the commandType for an ack.</summary>
        public string topic;

        /// <summary>Primary value — interpretation depends on the topic.</summary>
        public string value;

        /// <summary>Optional extra JSON blob (snapshot entries, error details, ...).</summary>
        public string payload = string.Empty;

        /// <summary>Correlates an <c>ack</c> with the <see cref="RemoteCommand.requestId"/> it answers. Empty otherwise.</summary>
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

        /// <summary>Snapshot entries when <see cref="IsSnapshot"/>; empty list otherwise.</summary>
        public List<HostStateEntry> SnapshotEntries()
        {
            if (!IsSnapshot || string.IsNullOrEmpty(payload)) return new List<HostStateEntry>();
            try { return JsonUtility.FromJson<HostStateSnapshot>(payload)?.entries ?? new List<HostStateEntry>(); }
            catch (Exception) { return new List<HostStateEntry>(); }
        }

        public string ToJson() => JsonUtility.ToJson(this);

        /// <summary>Deserialize from JSON. Returns null on failure or when <c>messageType</c> is missing.</summary>
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

    /// <summary>One (topic, value, payload) triple inside a snapshot.</summary>
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

    /// <summary>JsonUtility wrapper — the payload of a <c>snapshot</c> message.</summary>
    [Serializable]
    public class HostStateSnapshot
    {
        public List<HostStateEntry> entries = new();
    }
}
