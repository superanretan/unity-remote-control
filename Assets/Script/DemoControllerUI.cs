using TMPro;
using UnityEngine;
using UnityEngine.UI;
using SuperAnretan.RemoteControl;

namespace SuperAnretan.RemoteControl.Samples
{
    /// <summary>
    /// UI kontrolera. Łączy przyciski i input field z kanałami SO.
    /// Zero bezpośrednich referencji do TransportClient.
    /// </summary>
    public class DemoControllerUI : MonoBehaviour
    {
        [Header("Event Channels")]
        [SerializeField] private CommandEventChannel commandSendChannel;
        [SerializeField] private StringEventChannel connectRequestChannel;
        [SerializeField] private VoidEventChannel disconnectRequestChannel;
        [SerializeField] private VoidEventChannel onConnectedChannel;
        [SerializeField] private VoidEventChannel onDisconnectedChannel;
        [SerializeField] private StringEventChannel logChannel;
        [SerializeField] private StringEventChannel onHostSelectedChannel;
        [SerializeField] private NetworkConfig networkConfig;

        [Header("UI")]
        [SerializeField] private TMP_Text ipText;
        [SerializeField] private Button connectButton;
        [SerializeField] private Button disconnectButton;
        [SerializeField] private Button redButton;
        [SerializeField] private Button greenButton;
        [SerializeField] private Button blueButton;

        [Header("Config")]
        [SerializeField] private string targetId = "demo_cube";

        private bool _isConnected;

        private void OnEnable()
        {
            connectButton?.onClick.AddListener(OnConnectClicked);
            disconnectButton?.onClick.AddListener(OnDisconnectClicked);
            redButton?.onClick.AddListener(() => SendColor("#FF0000"));
            greenButton?.onClick.AddListener(() => SendColor("#00FF00"));
            blueButton?.onClick.AddListener(() => SendColor("#0000FF"));

            if (onConnectedChannel != null)    onConnectedChannel.OnRaised    += OnConnected;
            if (onDisconnectedChannel != null) onDisconnectedChannel.OnRaised += OnDisconnected;
            if (onHostSelectedChannel != null) onHostSelectedChannel.OnRaised += SetTargetIP;

            if (ipText != null)
            {
                string currentText = ipText.text.Replace("\u200B", "").Trim();
                if (string.IsNullOrEmpty(currentText))
                {
                    ipText.text = networkConfig != null ? networkConfig.HostIP : NetworkUtility.GetLocalIPAddress();
                }
            }

            UpdateButtons();
        }

        private void OnDisable()
        {
            connectButton?.onClick.RemoveListener(OnConnectClicked);
            disconnectButton?.onClick.RemoveListener(OnDisconnectClicked);
            redButton?.onClick.RemoveAllListeners();
            greenButton?.onClick.RemoveAllListeners();
            blueButton?.onClick.RemoveAllListeners();

            if (onConnectedChannel != null)    onConnectedChannel.OnRaised    -= OnConnected;
            if (onDisconnectedChannel != null) onDisconnectedChannel.OnRaised -= OnDisconnected;
            if (onHostSelectedChannel != null) onHostSelectedChannel.OnRaised -= SetTargetIP;
        }

        private void OnConnectClicked()
        {
            string ip = ipText != null ? ipText.text.Replace("\u200B", "").Trim() : "";
            if (string.IsNullOrEmpty(ip))
            {
                ip = networkConfig != null ? networkConfig.HostIP : NetworkUtility.GetLocalIPAddress();
                if (ipText != null) ipText.text = ip;
            }
            
            logChannel?.Raise($"[UI] Próba połączenia z IP: {ip}...");
            connectRequestChannel?.Raise(ip);
        }

        private void OnDisconnectClicked()
        {
            disconnectRequestChannel?.Raise();
        }

        private void SendColor(string hexColor)
        {
            if (!_isConnected)
            {
                logChannel?.Raise("[UI] Nie połączono.");
                return;
            }
            var cmd = new RemoteCommand("set_color", targetId, hexColor);
            commandSendChannel?.Raise(cmd);
        }

        private void OnConnected()    
        { 
            _isConnected = true;  
            logChannel?.Raise("[UI] Połączono z hostem pomyślnie.");
            UpdateButtons(); 
        }
        private void OnDisconnected() 
        { 
            _isConnected = false; 
            logChannel?.Raise("[UI] Rozłączono.");
            UpdateButtons(); 
        }

        private void UpdateButtons()
        {
            if (connectButton    != null) connectButton.interactable    = !_isConnected;
            if (disconnectButton != null) disconnectButton.interactable =  _isConnected;
            if (redButton        != null) redButton.interactable        =  _isConnected;
            if (greenButton      != null) greenButton.interactable      =  _isConnected;
            if (blueButton       != null) blueButton.interactable       =  _isConnected;
        }
        public void SetTargetIP(string ip)
        {
            if (ipText != null)
            {
                ipText.text = ip;
            }
        }
    }
}
