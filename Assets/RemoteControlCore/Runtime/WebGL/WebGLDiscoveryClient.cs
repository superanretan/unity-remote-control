using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Discovery backend for the WebGL controller. Instead of UDP broadcast (impossible in a
    /// browser) it keeps a WebSocket to the signaling server and mirrors the server's
    /// "device-list" pushes into <see cref="RemoteDiscoveryBase.Devices"/>.
    /// Hosts that stop heart-beating are pruned server-side and disappear from the list.
    /// </summary>
    public class WebGLDiscoveryClient : RemoteDiscoveryBase
    {
        [Header("Config")]
        [Tooltip("Signaling server URL is read from NetworkConfig.SignalingServerUrl.")]
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
            WebGLRemoteBridge.ConnectSignaling(_networkConfig.SignalingServerUrl);
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
                    SetDevices(null);
                    SetStatus("Signaling offline — reconnecting...");
                    Log($"[Signaling] Disconnected ({payload}).");
                    break;

                case "device-list":
                    var list = JsonUtility.FromJson<DiscoveredDeviceList>(payload);
                    int before = _devices.Count;
                    SetDevices(list?.devices);
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

        private readonly System.Collections.Generic.HashSet<string> KnownIds = new();
    }
}
