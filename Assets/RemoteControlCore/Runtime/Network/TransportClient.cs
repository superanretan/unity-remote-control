using System;
using System.Text;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Unity Transport client.
    /// Connects to a host, listens for send-command requests via event channel,
    /// and transmits serialized commands over the network.
    /// </summary>
    public class TransportClient : MonoBehaviour
    {
        [Header("Config")]
        [SerializeField] private NetworkConfig _networkConfig;

        [Header("Event Channels — Input")]
        [Tooltip("Listen for commands to send to the host.")]
        [SerializeField] private CommandEventChannel _commandSendChannel;

        [Tooltip("Listen for connect requests. Payload = host IP string.")]
        [SerializeField] private StringEventChannel _connectRequestChannel;

        [Tooltip("Listen for disconnect requests.")]
        [SerializeField] private VoidEventChannel _disconnectRequestChannel;

        [Header("Event Channels — Output")]
        [Tooltip("Raised when successfully connected to the host.")]
        [SerializeField] private VoidEventChannel _onConnectedChannel;

        [Tooltip("Raised when disconnected from the host.")]
        [SerializeField] private VoidEventChannel _onDisconnectedChannel;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        private NetworkDriver _driver;
        private NetworkConnection _connection;
        private bool _isConnecting;
        private bool _isConnected;

        public bool IsConnected => _isConnected;

        private void OnEnable()
        {
            if (_connectRequestChannel != null)
                _connectRequestChannel.OnRaised += OnConnectRequest;

            if (_disconnectRequestChannel != null)
                _disconnectRequestChannel.OnRaised += OnDisconnectRequest;

            if (_commandSendChannel != null)
                _commandSendChannel.OnRaised += OnSendCommandRequest;
        }

        private void OnDisable()
        {
            if (_connectRequestChannel != null)
                _connectRequestChannel.OnRaised -= OnConnectRequest;

            if (_disconnectRequestChannel != null)
                _disconnectRequestChannel.OnRaised -= OnDisconnectRequest;

            if (_commandSendChannel != null)
                _commandSendChannel.OnRaised -= OnSendCommandRequest;
        }

        // ───────── Channel callbacks ─────────

        private void OnConnectRequest(string hostIp)
        {
            Connect(hostIp);
        }

        private void OnDisconnectRequest()
        {
            Disconnect();
        }

        private void OnSendCommandRequest(RemoteCommand command)
        {
            SendCommand(command);
        }

        // ───────── Public API ─────────

        /// <summary>
        /// Connect to the host at the given IP address using the configured port.
        /// </summary>
        public void Connect(string hostIp)
        {
            if (_isConnected || _isConnecting)
            {
                Log("[Client] Already connected or connecting.");
                return;
            }

            _driver = NetworkDriver.Create();

            if (!NetworkEndpoint.TryParse(hostIp, _networkConfig.Port, out var endpoint))
            {
                Log($"[Client] ERROR — Invalid IP: {hostIp}");
                _driver.Dispose();
                return;
            }

            _connection = _driver.Connect(endpoint);
            _isConnecting = true;

            Log($"[Client] Connecting to {hostIp}:{_networkConfig.Port}...");
        }

        /// <summary>
        /// Disconnect from the host.
        /// </summary>
        public void Disconnect()
        {
            if (!_isConnected && !_isConnecting) return;

            if (_connection.IsCreated)
            {
                _driver.Disconnect(_connection);
            }

            Cleanup();
            Log("[Client] Disconnected.");
            _onDisconnectedChannel?.Raise();
        }

        /// <summary>
        /// Send a command to the host. Ignored if not connected.
        /// </summary>
        public void SendCommand(RemoteCommand command)
        {
            if (!_isConnected)
            {
                Log("[Client] Cannot send — not connected.");
                return;
            }

            string json = command.ToJson();
            byte[] bytes = Encoding.UTF8.GetBytes(json);

            int status = _driver.BeginSend(_connection, out var writer);
            if (status != 0)
            {
                Log($"[Client] BeginSend failed (status={status}).");
                return;
            }

            // Protocol: [int32 length] [UTF8 bytes]
            writer.WriteInt(bytes.Length);
            var nativeBytes = new NativeArray<byte>(bytes, Allocator.Temp);
            writer.WriteBytes(nativeBytes);
            nativeBytes.Dispose();
            _driver.EndSend(writer);

            Log($"[Client] Sent: {command}");
        }

        // ───────── Update loop ─────────

        private void Update()
        {
            if (!_isConnecting && !_isConnected) return;
            if (!_driver.IsCreated) return;

            _driver.ScheduleUpdate().Complete();

            NetworkEvent.Type eventType;
            while ((eventType = _driver.PopEventForConnection(
                _connection, out var reader)) != NetworkEvent.Type.Empty)
            {
                switch (eventType)
                {
                    case NetworkEvent.Type.Connect:
                        _isConnecting = false;
                        _isConnected = true;
                        Log("[Client] Connected to host.");
                        _onConnectedChannel?.Raise();
                        break;

                    case NetworkEvent.Type.Disconnect:
                        _isConnecting = false;
                        _isConnected = false;
                        Log("[Client] Disconnected by host.");
                        Cleanup();
                        _onDisconnectedChannel?.Raise();
                        break;

                    case NetworkEvent.Type.Data:
                        // Host-to-client messages could be handled here in the future.
                        break;
                }
            }
        }

        private void Cleanup()
        {
            _isConnecting = false;
            _isConnected = false;
            _connection = default;

            if (_driver.IsCreated)
            {
                _driver.Dispose();
            }
        }

        private void OnDestroy()
        {
            Disconnect();
        }

        private void Log(string message)
        {
            _logChannel?.Raise(message);
        }
    }
}
