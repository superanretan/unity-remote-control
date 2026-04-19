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
        [SerializeField] private CommandEventChannel _commandSendChannel;
        [SerializeField] private StringEventChannel _connectRequestChannel;
        [SerializeField] private VoidEventChannel _disconnectRequestChannel;
        [SerializeField] private VoidEventChannel _onConnectedChannel;
        [SerializeField] private VoidEventChannel _onDisconnectedChannel;
        [SerializeField] private StringEventChannel _logChannel;

        [Header("UI")]
        [SerializeField] private TMP_InputField _ipInputField;
        [SerializeField] private Button _connectButton;
        [SerializeField] private Button _disconnectButton;
        [SerializeField] private Button _redButton;
        [SerializeField] private Button _greenButton;
        [SerializeField] private Button _blueButton;

        [Header("Config")]
        [SerializeField] private string _targetId = "demo_cube";

        private bool _isConnected;

        private void OnEnable()
        {
            _connectButton?.onClick.AddListener(OnConnectClicked);
            _disconnectButton?.onClick.AddListener(OnDisconnectClicked);
            _redButton?.onClick.AddListener(() => SendColor("#FF0000"));
            _greenButton?.onClick.AddListener(() => SendColor("#00FF00"));
            _blueButton?.onClick.AddListener(() => SendColor("#0000FF"));

            if (_onConnectedChannel != null)    _onConnectedChannel.OnRaised    += OnConnected;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised += OnDisconnected;

            UpdateButtons();
        }

        private void OnDisable()
        {
            _connectButton?.onClick.RemoveListener(OnConnectClicked);
            _disconnectButton?.onClick.RemoveListener(OnDisconnectClicked);
            _redButton?.onClick.RemoveAllListeners();
            _greenButton?.onClick.RemoveAllListeners();
            _blueButton?.onClick.RemoveAllListeners();

            if (_onConnectedChannel != null)    _onConnectedChannel.OnRaised    -= OnConnected;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised -= OnDisconnected;
        }

        private void OnConnectClicked()
        {
            string ip = _ipInputField != null ? _ipInputField.text.Trim() : "";
            if (string.IsNullOrEmpty(ip))
            {
                _logChannel?.Raise("[UI] Wpisz IP hosta.");
                return;
            }
            _connectRequestChannel?.Raise(ip);
        }

        private void OnDisconnectClicked()
        {
            _disconnectRequestChannel?.Raise();
        }

        private void SendColor(string hexColor)
        {
            if (!_isConnected)
            {
                _logChannel?.Raise("[UI] Nie połączono.");
                return;
            }
            var cmd = new RemoteCommand("set_color", _targetId, hexColor);
            _commandSendChannel?.Raise(cmd);
        }

        private void OnConnected()    { _isConnected = true;  UpdateButtons(); }
        private void OnDisconnected() { _isConnected = false; UpdateButtons(); }

        private void UpdateButtons()
        {
            if (_connectButton    != null) _connectButton.interactable    = !_isConnected;
            if (_disconnectButton != null) _disconnectButton.interactable =  _isConnected;
            if (_redButton        != null) _redButton.interactable        =  _isConnected;
            if (_greenButton      != null) _greenButton.interactable      =  _isConnected;
            if (_blueButton       != null) _blueButton.interactable       =  _isConnected;
            if (_ipInputField     != null) _ipInputField.interactable     = !_isConnected;
        }
    }
}
