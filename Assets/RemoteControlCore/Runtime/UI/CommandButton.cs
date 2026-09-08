using UnityEngine;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl
{
    // Drop-in controller button: raises a RemoteCommand on the CommandSendChannel. Transport-agnostic,
    // so it works with both the native and the WebRTC controller.
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

        public void SetValue(string value) => _value = value;

        private void OnConnected() { _isConnected = true; _button.interactable = true; }
        private void OnDisconnected() { _isConnected = false; _button.interactable = false; }
    }
}
