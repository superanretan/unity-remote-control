using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Device picker UI: dropdown + Refresh + Connect + Disconnect + status line.
    /// Talks only to <see cref="RemoteDiscoveryBase"/> and the SO event channels, so the same
    /// panel works with any discovery/transport pair (signaling+WebRTC or UDP+Unity Transport).
    ///
    /// States:
    ///   Searching  → dropdown empty, Connect disabled
    ///   Found      → dropdown populated, Connect enabled
    ///   Connecting → controls locked
    ///   Connected  → dropdown/Refresh/Connect locked, Disconnect enabled, "_showWhenConnected" objects active
    /// </summary>
    public class NetworkDiscoveryPanel : MonoBehaviour
    {
        [Header("Discovery backend")]
        [Tooltip("Any RemoteDiscoveryBase implementation (WebGLDiscoveryClient on WebGL).")]
        [SerializeField] private RemoteDiscoveryBase _discovery;

        [Header("Event Channels — Output")]
        [Tooltip("Raised with the selected device's connect key (deviceId or IP).")]
        [SerializeField] private StringEventChannel _connectRequestChannel;
        [SerializeField] private VoidEventChannel _disconnectRequestChannel;

        [Header("Event Channels — Input")]
        [SerializeField] private VoidEventChannel _onConnectedChannel;
        [SerializeField] private VoidEventChannel _onDisconnectedChannel;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        [Header("UI")]
        [SerializeField] private TMP_Dropdown _deviceDropdown;
        [SerializeField] private Button _refreshButton;
        [SerializeField] private Button _connectButton;
        [SerializeField] private Button _disconnectButton;
        [SerializeField] private TextMeshProUGUI _statusText;

        [Tooltip("Objects shown only while connected (control buttons, video view).")]
        [SerializeField] private GameObject[] _showWhenConnected;

        [Tooltip("Objects hidden while connected (e.g. the picker row itself). Optional.")]
        [SerializeField] private GameObject[] _hideWhenConnected;

        private enum State { Searching, Found, Connecting, Connected }

        private State _state = State.Searching;
        private readonly List<DiscoveredDevice> _shown = new();
        private string _discoveryStatus = "Searching for devices...";

        public bool HasDiscovery => _discovery != null;

        /// <summary>Swap the discovery backend at runtime (used by <see cref="DiscoveryBinder"/>).</summary>
        public void SetDiscovery(RemoteDiscoveryBase discovery)
        {
            if (_discovery == discovery) return;

            if (isActiveAndEnabled) UnsubscribeDiscovery();
            _discovery = discovery;
            if (isActiveAndEnabled)
            {
                SubscribeDiscovery();
                OnDevicesChanged(_discovery != null ? _discovery.Devices : System.Array.Empty<DiscoveredDevice>());
            }
        }

        private void SubscribeDiscovery()
        {
            if (_discovery == null) return;
            _discovery.DevicesChanged += OnDevicesChanged;
            _discovery.StatusChanged += OnDiscoveryStatus;
        }

        private void UnsubscribeDiscovery()
        {
            if (_discovery == null) return;
            _discovery.DevicesChanged -= OnDevicesChanged;
            _discovery.StatusChanged -= OnDiscoveryStatus;
        }

        private void OnEnable()
        {
            SubscribeDiscovery();

            if (_onConnectedChannel != null) _onConnectedChannel.OnRaised += OnConnected;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised += OnDisconnected;

            _refreshButton?.onClick.AddListener(OnRefreshClicked);
            _connectButton?.onClick.AddListener(OnConnectClicked);
            _disconnectButton?.onClick.AddListener(OnDisconnectClicked);

            // Reset first, then let the current device list decide between Searching and Found.
            ApplyState(State.Searching);
            OnDevicesChanged(_discovery != null ? _discovery.Devices : System.Array.Empty<DiscoveredDevice>());
        }

        private void OnDisable()
        {
            UnsubscribeDiscovery();

            if (_onConnectedChannel != null) _onConnectedChannel.OnRaised -= OnConnected;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised -= OnDisconnected;

            _refreshButton?.onClick.RemoveListener(OnRefreshClicked);
            _connectButton?.onClick.RemoveListener(OnConnectClicked);
            _disconnectButton?.onClick.RemoveListener(OnDisconnectClicked);
        }

        // ───────── Discovery callbacks ─────────

        private void OnDevicesChanged(IReadOnlyList<DiscoveredDevice> devices)
        {
            // Keep the current selection if the device is still there.
            string selectedId = SelectedDevice()?.deviceId;

            _shown.Clear();
            foreach (var d in devices)
                if (d != null && !string.IsNullOrEmpty(d.ConnectKey)) _shown.Add(d);

            if (_deviceDropdown != null)
            {
                var options = new List<TMP_Dropdown.OptionData>(_shown.Count);
                foreach (var d in _shown)
                {
                    string label = string.IsNullOrEmpty(d.deviceName) ? d.ConnectKey : d.deviceName;
                    if (!d.IsAvailable) label += " (busy)";
                    options.Add(new TMP_Dropdown.OptionData(label));
                }

                _deviceDropdown.ClearOptions();
                _deviceDropdown.AddOptions(options);

                int idx = _shown.FindIndex(d => d.deviceId == selectedId);
                _deviceDropdown.SetValueWithoutNotify(idx >= 0 ? idx : 0);
                _deviceDropdown.RefreshShownValue();
            }

            if (_state == State.Searching || _state == State.Found)
                ApplyState(_shown.Count > 0 ? State.Found : State.Searching);
        }

        private void OnDiscoveryStatus(string status)
        {
            _discoveryStatus = status;
            if (_state == State.Searching || _state == State.Found) RefreshStatusText();
        }

        // ───────── Connection callbacks ─────────

        private void OnConnected() => ApplyState(State.Connected);

        private void OnDisconnected()
        {
            ApplyState(_shown.Count > 0 ? State.Found : State.Searching);
        }

        // ───────── Buttons ─────────

        private void OnRefreshClicked()
        {
            Log("[Discovery] Refresh requested.");
            _discovery?.Refresh();
        }

        private void OnConnectClicked()
        {
            var device = SelectedDevice();
            if (device == null)
            {
                Log("[Discovery] No device selected.");
                return;
            }

            if (!device.IsAvailable)
            {
                Log($"[Discovery] '{device.deviceName}' is busy.");
                return;
            }

            Log($"[Discovery] Connecting to '{device.deviceName}' ({device.ConnectKey})...");
            ApplyState(State.Connecting);
            _connectRequestChannel?.Raise(device.ConnectKey);
        }

        private void OnDisconnectClicked()
        {
            _disconnectRequestChannel?.Raise();
        }

        // ───────── Helpers ─────────

        private DiscoveredDevice SelectedDevice()
        {
            if (_deviceDropdown == null || _shown.Count == 0) return null;
            int i = _deviceDropdown.value;
            return i >= 0 && i < _shown.Count ? _shown[i] : null;
        }

        private void ApplyState(State state)
        {
            _state = state;

            bool connected = state == State.Connected;
            bool idle = state == State.Searching || state == State.Found;

            if (_deviceDropdown != null) _deviceDropdown.interactable = idle && _shown.Count > 0;
            if (_refreshButton != null) _refreshButton.interactable = idle;
            if (_connectButton != null)
            {
                _connectButton.interactable = state == State.Found;
                _connectButton.gameObject.SetActive(!connected);
            }
            if (_disconnectButton != null) _disconnectButton.interactable = connected;

            if (_showWhenConnected != null)
                foreach (var go in _showWhenConnected) if (go != null) go.SetActive(connected);

            if (_hideWhenConnected != null)
                foreach (var go in _hideWhenConnected) if (go != null) go.SetActive(!connected);

            RefreshStatusText();
        }

        private void RefreshStatusText()
        {
            if (_statusText == null) return;

            _statusText.text = _state switch
            {
                State.Searching => _discoveryStatus,
                State.Found => $"{_shown.Count} device(s) found",
                State.Connecting => "Connecting...",
                State.Connected => $"Connected to {SelectedDevice()?.deviceName ?? "host"}",
                _ => string.Empty
            };
        }

        private void Log(string message) => _logChannel?.Raise(message);
    }
}
