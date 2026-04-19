using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Data object representing a remote command.
    /// Serialized to/from JSON for network transport.
    /// </summary>
    [Serializable]
    public class RemoteCommand
    {
        /// <summary>
        /// Handler lookup key — e.g. "set_color", "toggle_object", "custom_action".
        /// </summary>
        public string commandType;

        /// <summary>
        /// Runtime target lookup key — matches <see cref="CommandTarget.TargetId"/>.
        /// </summary>
        public string targetId;

        /// <summary>
        /// Primary value — interpretation depends on the handler.
        /// </summary>
        public string value;

        /// <summary>
        /// Optional extra JSON blob for complex payloads.
        /// </summary>
        public string payload;

        public RemoteCommand() { }

        public RemoteCommand(string commandType, string targetId, string value, string payload = "")
        {
            this.commandType = commandType;
            this.targetId = targetId;
            this.value = value;
            this.payload = payload;
        }

        /// <summary>
        /// Serialize to JSON string.
        /// </summary>
        public string ToJson()
        {
            return JsonUtility.ToJson(this);
        }

        /// <summary>
        /// Deserialize from JSON string. Returns null on failure.
        /// </summary>
        public static RemoteCommand FromJson(string json)
        {
            try
            {
                return JsonUtility.FromJson<RemoteCommand>(json);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RemoteCommand] Failed to deserialize: {e.Message}");
                return null;
            }
        }

        public override string ToString()
        {
            return $"[{commandType}] target={targetId} value={value}";
        }
    }
}
