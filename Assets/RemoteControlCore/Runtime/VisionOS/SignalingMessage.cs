using System;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
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
        public float deviceTimeout;

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

        [NonSerialized] public int generation;

        public string ToJson() => JsonUtility.ToJson(this);

        public static SignalingMessage FromJson(string json)
        {
            try { return JsonUtility.FromJson<SignalingMessage>(json); }
            catch (Exception) { return null; }
        }
    }

    [Serializable]
    public class IceCandidatePayload
    {
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex = -1;
    }
}
