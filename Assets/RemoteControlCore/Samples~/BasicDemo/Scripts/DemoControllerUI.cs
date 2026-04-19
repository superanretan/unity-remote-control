using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl.Samples
{
    /// <summary>
    /// Controller scene UI controller.
    /// Wires buttons and input field to event channels for connect/send/disconnect.
    /// No direct references to TransportClient — everything goes through SO channels.
    /// </summary>
    public class DemoControllerUI : MonoBehaviour
    {
        [Header("Event Channels")]
        [Tooltip("Raise when user wants to send a command.")]
        [SerializeField] private CommandEventChannel _commandSendChannel;

        [Tooltip("Raise when user wants to connect. Payload = host IP.")]
        [SerializeField] private StringEventChannel _connectRequestChannel;

        [Tooltip("Raise when user wants to disconnect.")]
        [SerializeField] private VoidEventChannel _disconnectRequestChannel;

        [Tooltip("Raised by TransportClient when connected.")]
        [SerializeField] private VoidEventChannel _onConnectedChannel;

        [Tooltip("Raised by TransportClient when disconnected.")]
        [SerializeField] private VoidEventChannel _onDisconnectedChannel;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        [Header("UI References")]
        [SerializeField] private TMP_InputField _ipInputField;
        [SerializeField] private Button _connectButton;
        [SerializeField] private Button _disconnectButton;
        [SerializeField] private Button _redButton;
        [SerializeField] private Button _greenButton;
        [SerializeField] private Button _blueButton;

        [Header("Config")]
        [Tooltip("Target id to send commands to.")]
        [SerializeField] private string _targetId = "demo_cube";

        private bool _isConnected;

        private void OnEnable()
        {
            // UI button listeners
            _connectButton?.onClick.AddListener(OnConnectClicked);
            _disconnectButton?.onClick.AddListener(OnDisconnectClicked);
            _redButton?.onClick.AddListener(() => SendColor("#FF0000"));
            _greenButton?.onClick.AddListener(() => SendColor("#00FF00"));
            _blueButton?.onClick.AddListener(() => SendColor("#0000FF"));

            // Connection state listeners
            if (_onConnectedChannel != null)
                _onConnectedChannel.OnRaised += OnConnected;
            if (_onDisconnectedChannel != null)
                _onDisconnectedChannel.OnRaised += OnDisconnected;

            UpdateButtonStates();
        }

        private void OnDisable()
        {
            _connectButton?.onClick.RemoveListener(OnConnectClicked);
            _disconnectButton?.onClick.RemoveListener(OnDisconnectClicked);
            _redButton?.onClick.RemoveAllListeners();
            _greenButton?.onClick.RemoveAllListeners();
            _blueButton?.onClick.RemoveAllListeners();

            if (_onConnectedChannel != null)
                _onConnectedChannel.OnRaised -= OnConnected;
            if (_onDisconnectedChannel != null)
                _onDisconnectedChannel.OnRaised -= OnDisconnected;
        }

        // ───────── UI Callbacks ─────────

        private void OnConnectClicked()
        {
            string ip = _ipInputField != null ? _ipInputField.text.Trim() : "";
            if (string.IsNullOrEmpty(ip))
            {
                _logChannel?.Raise("[UI] Please enter a host IP address.");
                return;
            }

            _logChannel?.Raise($"[UI] Connecting to {ip}...");
            _connectRequestChannel?.Raise(ip);
        }

        private void OnDisconnectClicked()
        {
            _logChannel?.Raise("[UI] Disconnecting...");
            _disconnectRequestChannel?.Raise();
        }

        private void SendColor(string hexColor)
        {
            if (!_isConnected)
            {
                _logChannel?.Raise("[UI] Not connected — cannot send.");
                return;
            }

            var command = new RemoteCommand("set_color", _targetId, hexColor);
            _commandSendChannel?.Raise(command);
        }

        // ───────── Connection State ─────────

        private void OnConnected()
        {
            _isConnected = true;
            UpdateButtonStates();
        }

        private void OnDisconnected()
        {
            _isConnected = false;
            UpdateButtonStates();
        }

        private void UpdateButtonStates()
        {
            if (_connectButton != null) _connectButton.interactable = !_isConnected;
            if (_disconnectButton != null) _disconnectButton.interactable = _isConnected;
            if (_redButton != null) _redButton.interactable = _isConnected;
            if (_greenButton != null) _greenButton.interactable = _isConnected;
            if (_blueButton != null) _blueButton.interactable = _isConnected;

            if (_ipInputField != null) _ipInputField.interactable = !_isConnected;
        }
    }
}
