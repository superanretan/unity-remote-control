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
        [SerializeField] private CommandEventChannel commandSendChannel;

        [Tooltip("Raise when user wants to connect. Payload = host IP.")]
        [SerializeField] private StringEventChannel connectRequestChannel;

        [Tooltip("Raise when user wants to disconnect.")]
        [SerializeField] private VoidEventChannel disconnectRequestChannel;

        [Tooltip("Raised by TransportClient when connected.")]
        [SerializeField] private VoidEventChannel onConnectedChannel;

        [Tooltip("Raised by TransportClient when disconnected.")]
        [SerializeField] private VoidEventChannel onDisconnectedChannel;

        [Header("Logging")]
        [SerializeField] private StringEventChannel logChannel;

        [Header("UI References")]
        [SerializeField] private TMP_InputField ipInputField;
        [SerializeField] private Button connectButton;
        [SerializeField] private Button disconnectButton;
        [SerializeField] private Button redButton;
        [SerializeField] private Button greenButton;
        [SerializeField] private Button blueButton;

        [Header("Config")]
        [Tooltip("Target id to send commands to.")]
        [SerializeField] private string targetId = "demo_cube";

        private bool _isConnected;

        private void OnEnable()
        {
            // UI button listeners
            connectButton?.onClick.AddListener(OnConnectClicked);
            disconnectButton?.onClick.AddListener(OnDisconnectClicked);
            redButton?.onClick.AddListener(() => SendColor("#FF0000"));
            greenButton?.onClick.AddListener(() => SendColor("#00FF00"));
            blueButton?.onClick.AddListener(() => SendColor("#0000FF"));

            // Connection state listeners
            if (onConnectedChannel != null)
                onConnectedChannel.OnRaised += OnConnected;
            if (onDisconnectedChannel != null)
                onDisconnectedChannel.OnRaised += OnDisconnected;

            UpdateButtonStates();
        }

        private void OnDisable()
        {
            connectButton?.onClick.RemoveListener(OnConnectClicked);
            disconnectButton?.onClick.RemoveListener(OnDisconnectClicked);
            redButton?.onClick.RemoveAllListeners();
            greenButton?.onClick.RemoveAllListeners();
            blueButton?.onClick.RemoveAllListeners();

            if (onConnectedChannel != null)
                onConnectedChannel.OnRaised -= OnConnected;
            if (onDisconnectedChannel != null)
                onDisconnectedChannel.OnRaised -= OnDisconnected;
        }

        // ───────── UI Callbacks ─────────

        private void OnConnectClicked()
        {
            string ip = ipInputField != null ? ipInputField.text.Trim() : "";
            if (string.IsNullOrEmpty(ip))
            {
                logChannel?.Raise("[UI] Please enter a host IP address.");
                return;
            }

            logChannel?.Raise($"[UI] Connecting to {ip}...");
            connectRequestChannel?.Raise(ip);
        }

        private void OnDisconnectClicked()
        {
            logChannel?.Raise("[UI] Disconnecting...");
            disconnectRequestChannel?.Raise();
        }

        private void SendColor(string hexColor)
        {
            if (!_isConnected)
            {
                logChannel?.Raise("[UI] Not connected — cannot send.");
                return;
            }

            var command = new RemoteCommand("set_color", targetId, hexColor);
            commandSendChannel?.Raise(command);
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
            if (connectButton != null) connectButton.interactable = !_isConnected;
            if (disconnectButton != null) disconnectButton.interactable = _isConnected;
            if (redButton != null) redButton.interactable = _isConnected;
            if (greenButton != null) greenButton.interactable = _isConnected;
            if (blueButton != null) blueButton.interactable = _isConnected;

            if (ipInputField != null) ipInputField.interactable = !_isConnected;
        }
    }
}
