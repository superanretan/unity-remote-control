using System;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// A remotely controllable host found by a discovery backend.
    /// Field names match the signaling server JSON so it can be deserialized with JsonUtility.
    /// </summary>
    [Serializable]
    public class DiscoveredDevice
    {
        /// <summary>Stable unique id — the value sent on the ConnectRequestChannel for signaling backends.</summary>
        public string deviceId;

        /// <summary>Display name shown in the dropdown, e.g. "Vision Pro Office".</summary>
        public string deviceName;

        /// <summary>"visionOS", "Windows", ... — informational.</summary>
        public string platform;

        /// <summary>"available" or "busy".</summary>
        public string status;

        /// <summary>Optional address for backends that connect by IP (UDP discovery). Empty for signaling.</summary>
        public string address;

        public bool IsAvailable => string.IsNullOrEmpty(status) || status == "available";

        /// <summary>
        /// The string a transport needs to connect to this device: deviceId for signaling
        /// backends, IP address for UDP backends.
        /// </summary>
        public string ConnectKey => string.IsNullOrEmpty(address) ? deviceId : address;

        public override string ToString() => $"{deviceName} [{platform}] ({status})";
    }

    /// <summary>JsonUtility wrapper for arrays.</summary>
    [Serializable]
    public class DiscoveredDeviceList
    {
        public DiscoveredDevice[] devices = Array.Empty<DiscoveredDevice>();
    }
}
