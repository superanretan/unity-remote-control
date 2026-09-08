using System;

namespace SuperAnretan.RemoteControl
{
    // Field names match the signaling server JSON so JsonUtility can read it directly.
    [Serializable]
    public class DiscoveredDevice
    {
        public string deviceId;
        public string deviceName;
        public string platform;
        public string status;
        public string address;   // set by IP-based (UDP) backends, empty for signaling

        public bool IsAvailable => string.IsNullOrEmpty(status) || status == "available";

        public string ConnectKey => string.IsNullOrEmpty(address) ? deviceId : address;

        public override string ToString() => $"{deviceName} [{platform}] ({status})";
    }

    [Serializable]
    public class DiscoveredDeviceList
    {
        public DiscoveredDevice[] devices = Array.Empty<DiscoveredDevice>();
    }
}
