using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    [Serializable]
    public class RemoteCommand
    {
        public string commandType;
        public string targetId;
        public string value;
        public string payload;

        // Non-empty asks a WebRTC host for a HostMessage 'ack' carrying the same id.
        // Empty = fire-and-forget (1.x behaviour).
        public string requestId = string.Empty;

        public RemoteCommand() { }

        public RemoteCommand(string commandType, string targetId, string value, string payload = "")
        {
            this.commandType = commandType;
            this.targetId = targetId;
            this.value = value;
            this.payload = payload;
        }

        public string ToJson()
        {
            return JsonUtility.ToJson(this);
        }

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
