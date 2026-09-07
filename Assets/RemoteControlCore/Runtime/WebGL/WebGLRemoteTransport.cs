using System;
using System.Collections;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// WebGL counterpart of <see cref="TransportClient"/>.
    /// Same event-channel contract (ConnectRequest → OnConnected/OnDisconnected, CommandSend),
    /// but the connect payload is a signaling <c>deviceId</c> and commands travel over an
    /// RTCDataChannel opened by <c>WebGLRemoteBridge.jslib</c>.
    /// UI code keeps calling <c>commandSendChannel.Raise(command)</c> and never sees WebRTC.
    ///
    /// Return channel: every text message the host sends over the DataChannel is raised verbatim on
    /// <c>HostMessageReceivedChannel</c> (a <see cref="StringEventChannel"/>). UI code parses it with
    /// <see cref="HostMessage.FromJson"/> and reacts (highlight the active compartment, show capture state, match acks).
    /// </summary>
    public class WebGLRemoteTransport : MonoBehaviour
    {
        [Header("Config")]
        [SerializeField] private NetworkConfig _networkConfig;

        [Header("Event Channels — Input")]
        [Tooltip("Listen for commands to send to the host.")]
        [SerializeField] private CommandEventChannel _commandSendChannel;

        [Tooltip("Listen for connect requests. Payload = signaling deviceId.")]
        [SerializeField] private StringEventChannel _connectRequestChannel;

        [Tooltip("Listen for disconnect requests.")]
        [SerializeField] private VoidEventChannel _disconnectRequestChannel;

        [Header("Event Channels — Output")]
        [SerializeField] private VoidEventChannel _onConnectedChannel;
        [SerializeField] private VoidEventChannel _onDisconnectedChannel;

        [Tooltip("Raised with the raw HostMessage JSON for every message the host sends back over the DataChannel.")]
        [SerializeField] private StringEventChannel _hostMessageReceivedChannel;

        [Header("Commands")]
        [Tooltip("Stamp every outgoing command that has no requestId with a fresh one, so the host answers each with an 'ack' HostMessage.")]
        [SerializeField] private bool _autoRequestId = false;

        [Header("Reconnect")]
        [Tooltip("Retry the last device after an unexpected drop (ICE failure, host Wi-Fi hiccup).")]
        [SerializeField] private bool _autoReconnect = true;
        [SerializeField] private int _maxReconnectAttempts = 3;
        [SerializeField] private float _reconnectDelay = 2f;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        private string _lastDeviceId;
        private bool _isConnecting;
        private bool _isConnected;
        private bool _userRequestedDisconnect;
        private int _reconnectAttempts;
        private Coroutine _reconnectRoutine;
        private uint _requestCounter;

        public bool IsConnected => _isConnected;

        /// <summary>Typed convenience over <c>HostMessageReceivedChannel</c> for code that prefers C# events.</summary>
        public event Action<HostMessage> OnHostMessage;

        private void OnEnable()
        {
            WebGLRemoteBridge.Initialize();
            WebGLRemoteBridge.OnEvent += OnBridgeEvent;

            if (_connectRequestChannel != null) _connectRequestChannel.OnRaised += OnConnectRequest;
            if (_disconnectRequestChannel != null) _disconnectRequestChannel.OnRaised += Disconnect;
            if (_commandSendChannel != null) _commandSendChannel.OnRaised += SendCommand;

            if (!WebGLRemoteBridge.IsSupported)
                Log("[WebRTC] WebGLRemoteTransport is a no-op outside a WebGL player.");
        }

        private void OnDisable()
        {
            if (_connectRequestChannel != null) _connectRequestChannel.OnRaised -= OnConnectRequest;
            if (_disconnectRequestChannel != null) _disconnectRequestChannel.OnRaised -= Disconnect;
            if (_commandSendChannel != null) _commandSendChannel.OnRaised -= SendCommand;

            CancelReconnectTimer();

            // Don't leave the browser peer (and the host's "busy" flag) alive without a listener.
            if (_isConnected || _isConnecting)
            {
                _userRequestedDisconnect = true;
                WebGLRemoteBridge.Disconnect();
                WebGLRemoteBridge.PumpEvents();     // deliver the resulting "disconnected" while still subscribed
                _isConnected = false;
                _isConnecting = false;
            }

            WebGLRemoteBridge.OnEvent -= OnBridgeEvent;
        }

        private void Update() => WebGLRemoteBridge.PumpEvents();

        // ───────── Public API ─────────

        /// <summary>User/UI initiated connect — resets the reconnect budget.</summary>
        private void OnConnectRequest(string deviceId)
        {
            _reconnectAttempts = 0;
            Connect(deviceId);
        }

        public void Connect(string deviceId)
        {
            if (!WebGLRemoteBridge.IsSupported)
            {
                Log("[WebRTC] Connect ignored — not running in WebGL.");
                _onDisconnectedChannel?.Raise();
                return;
            }

            if (_isConnected || _isConnecting)
            {
                Log("[WebRTC] Already connected or connecting.");
                return;
            }

            CancelReconnectTimer();
            _userRequestedDisconnect = false;
            _lastDeviceId = deviceId;
            _isConnecting = true;
            Log($"[WebRTC] Connecting to device {deviceId}...");
            WebGLRemoteBridge.Connect(deviceId, _networkConfig != null ? _networkConfig.IceServersJson() : "[]");
        }

        public void Disconnect()
        {
            _userRequestedDisconnect = true;
            CancelReconnectTimer();
            _reconnectAttempts = 0;
            if (!_isConnected && !_isConnecting) return;
            Log("[WebRTC] Disconnect requested.");
            WebGLRemoteBridge.Disconnect();   // → "disconnected" event → channel raise
        }

        public void SendCommand(RemoteCommand command)
        {
            if (!_isConnected)
            {
                Log("[DataChannel] Cannot send — not connected.");
                return;
            }

            if (_autoRequestId && string.IsNullOrEmpty(command.requestId))
                command.requestId = NextRequestId();

            // Same JSON as the Unity Transport path — the host parses it with RemoteCommand.FromJson.
            if (WebGLRemoteBridge.SendData(command.ToJson()))
                Log($"[DataChannel] Sent: {command}");
            else
                Log("[DataChannel] Send failed — channel not open.");
        }

        /// <summary>Fresh correlation id for <see cref="RemoteCommand.requestId"/>.</summary>
        public string NextRequestId() => $"r{++_requestCounter}-{Time.frameCount}";

        // ───────── Bridge events ─────────

        private void OnBridgeEvent(string type, string payload)
        {
            switch (type)
            {
                case "ice-state":
                    Log($"[WebRTC] Connection state: {payload}");
                    break;

                case "datachannel-open":
                    Log("[DataChannel] Opened.");
                    break;

                case "datachannel-closed":
                    Log("[DataChannel] Closed.");
                    break;

                case "connected":
                    _isConnecting = false;
                    _isConnected = true;
                    _reconnectAttempts = 0;
                    Log("[WebRTC] Connected.");
                    _onConnectedChannel?.Raise();
                    break;

                case "disconnected":
                    bool wasActive = _isConnected || _isConnecting;
                    _isConnected = false;
                    _isConnecting = false;
                    if (!wasActive) break;

                    Log($"[WebRTC] Disconnected — reason: {payload}");
                    _onDisconnectedChannel?.Raise();

                    if (_autoReconnect && !_userRequestedDisconnect && ShouldRetry(payload))
                        _reconnectRoutine = StartCoroutine(ReconnectLater());
                    break;

                case "datachannel-message":
                    OnDataChannelMessage(payload);
                    break;
            }
        }

        private void OnDataChannelMessage(string json)
        {
            _hostMessageReceivedChannel?.Raise(json);

            var msg = HostMessage.FromJson(json);
            if (msg == null)
            {
                Log($"[DataChannel] Received (unrecognised): {json}");
                return;
            }
            if (msg.IsSnapshot) Log($"[DataChannel] Snapshot received ({msg.SnapshotEntries().Count} topics).");
            else Log($"[DataChannel] Received: {msg}");
            OnHostMessage?.Invoke(msg);
        }

        private static bool ShouldRetry(string reason)
        {
            // Don't hammer a host that explicitly refused/went away, or after a user-initiated close.
            switch (reason)
            {
                case "user":
                case "host-disconnected":
                case "host-timeout":
                case "signaling-error":
                case "peer-create-failed":
                case "native-error":
                case "busy":
                case "replaced":
                    return false;
                default:
                    return true;
            }
        }

        private IEnumerator ReconnectLater()
        {
            if (_reconnectAttempts >= _maxReconnectAttempts || string.IsNullOrEmpty(_lastDeviceId))
            {
                Log("[WebRTC] Reconnect attempts exhausted.");
                _reconnectRoutine = null;
                yield break;
            }

            _reconnectAttempts++;
            Log($"[WebRTC] Reconnect attempt {_reconnectAttempts}/{_maxReconnectAttempts} in {_reconnectDelay:0.#}s");
            yield return new WaitForSecondsRealtime(_reconnectDelay);

            _reconnectRoutine = null;
            if (_userRequestedDisconnect || _isConnected || _isConnecting) yield break;

            if (!WebGLRemoteBridge.IsSignalingOpen)
            {
                Log("[WebRTC] Signaling offline — reconnect postponed.");
                _reconnectRoutine = StartCoroutine(ReconnectLater());
                yield break;
            }

            Connect(_lastDeviceId);
        }

        /// <summary>Cancels a pending retry without touching the retry budget.</summary>
        private void CancelReconnectTimer()
        {
            if (_reconnectRoutine != null)
            {
                StopCoroutine(_reconnectRoutine);
                _reconnectRoutine = null;
            }
        }

        private void Log(string message) => _logChannel?.Raise(message);
    }
}
