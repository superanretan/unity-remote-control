using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Flat JSON envelope used between the Vision Pro host and the signaling server.
    /// One class for every message type keeps JsonUtility happy (no polymorphism).
    /// Unused fields stay empty and are ignored by the server.
    /// </summary>
    [Serializable]
    public class SignalingMessage
    {
        public string type;
        public string fromId;
        public string targetId;
        public string sessionId;

        // register-device
        public string deviceId;
        public string deviceName;
        public string platform;
        public string status;

        // offer / answer
        public string sdp;

        // ice-candidate
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex = -1;

        // disconnect / error / registered
        public string reason;
        public string message;
        public string clientId;

        /// <summary>Local bookkeeping only (not serialized): which socket generation produced this message.</summary>
        [NonSerialized] public int generation;

        public string ToJson() => JsonUtility.ToJson(this);

        public static SignalingMessage FromJson(string json)
        {
            try { return JsonUtility.FromJson<SignalingMessage>(json); }
            catch (Exception) { return null; }
        }
    }

    /// <summary>Payload the native bridge emits for a local ICE candidate.</summary>
    [Serializable]
    public class IceCandidatePayload
    {
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex = -1;
    }
}
