using UnityEngine;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Drop-in controller button: on click raises a <see cref="RemoteCommand"/> on the
    /// CommandSendChannel. No transport knowledge — works identically for the native
    /// (Unity Transport) and WebGL (WebRTC DataChannel) controllers.
    /// Optionally follows the connection state so the button is only clickable while connected.
    /// </summary>
    [RequireComponent(typeof(Button))]
    public class CommandButton : MonoBehaviour
    {
        [Header("Command")]
        [Tooltip("Handler key on the host, e.g. \"set_color\".")]
        [SerializeField] private string _commandType = "set_color";

        [Tooltip("CommandTarget.TargetId on the host, e.g. \"demo_cube\".")]
        [SerializeField] private string _targetId = "demo_cube";

        [Tooltip("Primary value, e.g. \"#FF0000\".")]
        [SerializeField] private string _value;

        [Tooltip("Optional extra JSON payload.")]
        [TextArea]
        [SerializeField] private string _payload;

        [Header("Event Channels")]
        [SerializeField] private CommandEventChannel _commandSendChannel;

        [Tooltip("Optional. When set, the button is interactable only while connected.")]
        [SerializeField] private VoidEventChannel _onConnectedChannel;
        [SerializeField] private VoidEventChannel _onDisconnectedChannel;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        private Button _button;
        private bool _isConnected;

        private void Awake()
        {
            _button = GetComponent<Button>();
        }

        private void OnEnable()
        {
            _button.onClick.AddListener(Send);
            if (_onConnectedChannel != null) _onConnectedChannel.OnRaised += OnConnected;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised += OnDisconnected;
            if (_onConnectedChannel != null) _button.interactable = _isConnected;
        }

        private void OnDisable()
        {
            _button.onClick.RemoveListener(Send);
            if (_onConnectedChannel != null) _onConnectedChannel.OnRaised -= OnConnected;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised -= OnDisconnected;
        }

        /// <summary>Raise the configured command (also callable from UnityEvents / code).</summary>
        public void Send()
        {
            if (_commandSendChannel == null)
            {
                Debug.LogWarning($"[CommandButton] CommandSendChannel not assigned on '{name}'.", this);
                return;
            }

            if (_onConnectedChannel != null && !_isConnected)
            {
                _logChannel?.Raise("[UI] Not connected.");
                return;
            }

            _commandSendChannel.Raise(new RemoteCommand(_commandType, _targetId, _value, _payload ?? string.Empty));
        }

        /// <summary>Change the value at runtime (e.g. from a slider) before the next click.</summary>
        public void SetValue(string value) => _value = value;

        private void OnConnected() { _isConnected = true; _button.interactable = true; }
        private void OnDisconnected() { _isConnected = false; _button.interactable = false; }
    }
}
