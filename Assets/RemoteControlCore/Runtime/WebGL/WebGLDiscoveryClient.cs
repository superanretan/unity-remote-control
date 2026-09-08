using System.Collections.Generic;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    // Discovery for the WebGL controller. UDP broadcast is impossible in a browser, so this mirrors
    // the signaling server's "device-list" pushes. Identical consecutive lists are ignored so the
    // dropdown never rebuilds for nothing.
    public class WebGLDiscoveryClient : RemoteDiscoveryBase
    {
        [Header("Config")]
        [Tooltip("Signaling server URL (+ token) is read from NetworkConfig.")]
        [SerializeField] private NetworkConfig _networkConfig;

        [Tooltip("Ask the server for the device list at this interval as a safety net (seconds). 0 = pushes only.")]
        [SerializeField] private float _pollInterval = 5f;

        private bool _signalingOpen;
        private float _nextPoll;

        public override bool IsSearching => _signalingOpen;

        private void OnEnable()
        {
            WebGLRemoteBridge.Initialize();
            WebGLRemoteBridge.OnEvent += OnBridgeEvent;

            if (!WebGLRemoteBridge.IsSupported)
            {
                SetStatus("Discovery available in WebGL build only");
                Log("[Discovery] WebGLDiscoveryClient is a no-op outside a WebGL player.");
                return;
            }

            if (_networkConfig == null || string.IsNullOrWhiteSpace(_networkConfig.SignalingServerUrl))
            {
                SetStatus("Signaling URL missing");
                Log("[Discovery] ERROR — NetworkConfig.SignalingServerUrl not set.");
                return;
            }

            SetStatus("Connecting to signaling...");
            Log($"[Signaling] Connecting to {_networkConfig.SignalingServerUrl}");
            WebGLRemoteBridge.ConnectSignaling(_networkConfig.SignalingConnectUrl);
        }

        private void OnDisable()
        {
            WebGLRemoteBridge.OnEvent -= OnBridgeEvent;
            // The transport shares the socket; only close it if nothing is connected.
            if (WebGLRemoteBridge.IsSupported && !WebGLRemoteBridge.IsConnected)
                WebGLRemoteBridge.DisconnectSignaling();
            _signalingOpen = false;
        }

        private void Update()
        {
            WebGLRemoteBridge.PumpEvents();

            if (_signalingOpen && _pollInterval > 0 && Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + _pollInterval;
                WebGLRemoteBridge.RequestDeviceList();
            }
        }

        public override void Refresh()
        {
            if (!WebGLRemoteBridge.IsSupported) return;
            if (!WebGLRemoteBridge.RequestDeviceList())
                Log("[Discovery] Signaling offline — cannot refresh.");
        }

        private void OnBridgeEvent(string type, string payload)
        {
            switch (type)
            {
                case "signaling-open":
                    _signalingOpen = true;
                    _nextPoll = Time.unscaledTime + _pollInterval;
                    SetStatus("Searching for devices...");
                    Log("[Signaling] Connected.");
                    break;

                case "signaling-closed":
                    _signalingOpen = false;
                    // Keep the last known list: the drop is routine (Vercel max duration) and the
                    // server still has the registry. A gone host disappears on the next list.
                    SetStatus(_devices.Count > 0 ? "Signaling reconnecting..." : "Signaling offline — reconnecting...");
                    Log($"[Signaling] Disconnected ({payload}).");
                    break;

                case "signaling-rejected":
                    _signalingOpen = false;
                    SetDevices(null);
                    SetStatus("Signaling rejected: " + payload);
                    Log(payload == "unauthorized"
                        ? "[Signaling] ERROR — server rejected the connection: unauthorized. Check NetworkConfig.SignalingToken against the server's ROOM_TOKEN."
                        : $"[Signaling] ERROR — server rejected the connection: {payload}");
                    break;

                case "device-list":
                    var list = JsonUtility.FromJson<DiscoveredDeviceList>(payload);
                    var incoming = list?.devices ?? System.Array.Empty<DiscoveredDevice>();
                    if (SameList(_devices, incoming)) break;          // no churn → no dropdown rebuild
                    int before = _devices.Count;
                    SetDevices(incoming);
                    if (_devices.Count != before)
                        Log($"[Discovery] {_devices.Count} host(s) available.");
                    foreach (var d in _devices)
                        if (!KnownIds.Contains(d.deviceId)) { KnownIds.Add(d.deviceId); Log($"[Discovery] Host found: {d}"); }
                    KnownIds.RemoveWhere(id => _devices.FindIndex(d => d.deviceId == id) < 0);
                    break;

                case "log":
                    Log(payload);
                    break;
            }
        }

        private static bool SameList(List<DiscoveredDevice> current, DiscoveredDevice[] incoming)
        {
            if (current.Count != incoming.Length) return false;
            for (int i = 0; i < incoming.Length; i++)
            {
                var a = current[i]; var b = incoming[i];
                if (a == null || b == null) return false;
                if (a.deviceId != b.deviceId || a.deviceName != b.deviceName || a.platform != b.platform || a.status != b.status || a.address != b.address)
                    return false;
            }
            return true;
        }

        private readonly HashSet<string> KnownIds = new();
    }
}
