using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Self-contained UI panel for network discovery + connect/disconnect + status log.
    /// Drop this prefab into any Canvas — just assign the SO event channels.
    /// </summary>
    public class NetworkDiscoveryPanel : MonoBehaviour
    {
        [Header("Network")]
        [SerializeField] private NetworkDiscoveryClient discoveryClient;
        [SerializeField] private NetworkConfig networkConfig;

        [Header("Event Channels")]
        [Tooltip("Raised to request a connection. Payload = host IP.")]
        [SerializeField] private StringEventChannel connectRequestChannel;

        [Tooltip("Raised to request disconnection.")]
        [SerializeField] private VoidEventChannel disconnectRequestChannel;

        [Tooltip("Listened — fires when connected.")]
        [SerializeField] private VoidEventChannel onConnectedChannel;

        [Tooltip("Listened — fires when disconnected.")]
        [SerializeField] private VoidEventChannel onDisconnectedChannel;

        [Tooltip("Listened — transport log messages.")]
        [SerializeField] private StringEventChannel logChannel;

        [Header("UI References")]
        [SerializeField] private TMP_Dropdown hostDropdown;
        [SerializeField] private Button refreshButton;
        [SerializeField] private Button connectButton;
        [SerializeField] private Button disconnectButton;
        [SerializeField] private TextMeshProUGUI statusText;
        [SerializeField] private TextMeshProUGUI logText;

        [Header("Settings")]
        [SerializeField] private int maxLogLines = 12;

        private List<NetworkDiscoveryMessage> _currentHosts = new List<NetworkDiscoveryMessage>();
        private string _selectedHostIP;
        private bool _isConnected;
        private readonly Queue<string> _logLines = new();

        // ───────── Lifecycle ─────────

        private void OnEnable()
        {
            // Buttons
            if (refreshButton != null) refreshButton.onClick.AddListener(OnRefreshClicked);
            if (connectButton != null) connectButton.onClick.AddListener(OnConnectClicked);
            if (disconnectButton != null) disconnectButton.onClick.AddListener(OnDisconnectClicked);

            // Dropdown
            if (hostDropdown != null)
                hostDropdown.onValueChanged.AddListener(OnDropdownValueChanged);

            // Discovery
            if (discoveryClient != null)
                discoveryClient.OnHostDiscovered += OnHostDiscovered;

            // Event channels
            if (onConnectedChannel != null)    onConnectedChannel.OnRaised    += OnConnected;
            if (onDisconnectedChannel != null) onDisconnectedChannel.OnRaised += OnDisconnected;
            if (logChannel != null)            logChannel.OnRaised            += OnLogMessage;

            // Initial state
            _isConnected = false;
            RefreshButtonStates();
            SetStatusLabel("Disconnected");
            WriteLog("Panel ready. Press Refresh to discover hosts.");

            // Auto-start discovery
            OnRefreshClicked();
        }

        private void OnDisable()
        {
            if (refreshButton != null) refreshButton.onClick.RemoveListener(OnRefreshClicked);
            if (connectButton != null) connectButton.onClick.RemoveListener(OnConnectClicked);
            if (disconnectButton != null) disconnectButton.onClick.RemoveListener(OnDisconnectClicked);

            if (hostDropdown != null)
                hostDropdown.onValueChanged.RemoveListener(OnDropdownValueChanged);

            if (discoveryClient != null)
            {
                discoveryClient.OnHostDiscovered -= OnHostDiscovered;
                discoveryClient.StopDiscovery();
            }

            if (onConnectedChannel != null)    onConnectedChannel.OnRaised    -= OnConnected;
            if (onDisconnectedChannel != null) onDisconnectedChannel.OnRaised -= OnDisconnected;
            if (logChannel != null)            logChannel.OnRaised            -= OnLogMessage;
        }

        // ───────── Discovery ─────────

        private void OnRefreshClicked()
        {
            if (discoveryClient == null) return;

            hostDropdown.ClearOptions();
            _currentHosts.Clear();

            hostDropdown.options.Add(new TMP_Dropdown.OptionData("Searching for hosts..."));
            hostDropdown.value = 0;
            hostDropdown.RefreshShownValue();

            _selectedHostIP = null;
            RefreshButtonStates();
            WriteLog("Scanning network...");
            discoveryClient.StartDiscovery();
        }

        private void OnHostDiscovered(NetworkDiscoveryMessage msg)
        {
            _currentHosts = discoveryClient.DiscoveredHosts.ToList();

            hostDropdown.ClearOptions();
            var options = new List<string>();
            foreach (var host in _currentHosts)
            {
                options.Add($"{host.HostName} ({host.HostIP})");
            }
            hostDropdown.AddOptions(options);

            OnDropdownValueChanged(hostDropdown.value);
            hostDropdown.RefreshShownValue();

            WriteLog($"Host found: {msg.HostName} @ {msg.HostIP}:{msg.Port}");
        }

        private void OnDropdownValueChanged(int index)
        {
            if (index >= 0 && index < _currentHosts.Count)
            {
                _selectedHostIP = _currentHosts[index].HostIP;
                RefreshButtonStates();
            }
        }

        // ───────── Connect / Disconnect ─────────

        private void OnConnectClicked()
        {
            string ip = _selectedHostIP;

            // Fallback: use hardcoded IP from config
            if (string.IsNullOrEmpty(ip))
            {
                ip = networkConfig != null ? networkConfig.HostIP : null;
            }

            if (string.IsNullOrEmpty(ip))
            {
                WriteLog("ERROR: No host selected and no default IP configured.");
                return;
            }

            SetStatusLabel($"Connecting to {ip}...");
            WriteLog($"Requesting connection to {ip}...");
            connectRequestChannel?.Raise(ip);
        }

        private void OnDisconnectClicked()
        {
            SetStatusLabel("Disconnecting...");
            WriteLog("Requesting disconnect...");
            disconnectRequestChannel?.Raise();
        }

        // ───────── Event channel callbacks ─────────

        private void OnConnected()
        {
            _isConnected = true;
            SetStatusLabel("Connected");
            WriteLog("Successfully connected to host.");
            RefreshButtonStates();
        }

        private void OnDisconnected()
        {
            _isConnected = false;
            SetStatusLabel("Disconnected");
            WriteLog("Disconnected from host.");
            RefreshButtonStates();
        }

        private void OnLogMessage(string message)
        {
            WriteLog(message);
        }

        // ───────── UI helpers ─────────

        private void RefreshButtonStates()
        {
            bool hasHost = !string.IsNullOrEmpty(_selectedHostIP);

            if (connectButton != null)    connectButton.interactable    = !_isConnected && hasHost;
            if (disconnectButton != null) disconnectButton.interactable =  _isConnected;
            if (refreshButton != null)    refreshButton.interactable    = !_isConnected;
            if (hostDropdown != null)     hostDropdown.interactable     = !_isConnected;
        }

        private void SetStatusLabel(string status)
        {
            if (statusText == null) return;

            string color = "#F44336"; // red = disconnected
            if (_isConnected) color = "#4CAF50"; // green = connected
            else if (status.Contains("Connecting")) color = "#FF9800"; // orange = connecting

            statusText.text = $"<color={color}>\u25CF</color> {status}";
        }

        private void WriteLog(string message)
        {
            string timestamped = $"<color=#888>[{System.DateTime.Now:HH:mm:ss}]</color> {message}";
            _logLines.Enqueue(timestamped);
            while (_logLines.Count > maxLogLines)
                _logLines.Dequeue();

            if (logText != null)
                logText.text = string.Join("\n", _logLines);
        }
    }
}
