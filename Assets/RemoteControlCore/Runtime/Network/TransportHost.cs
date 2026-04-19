using System;
using System.Collections.Generic;
using System.Text;
using Unity.Collections;
using Unity.Networking.Transport;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Unity Transport server.
    /// Listens for incoming connections, receives command JSON,
    /// deserializes to <see cref="RemoteCommand"/> and raises the command event channel.
    /// </summary>
    public class TransportHost : MonoBehaviour
    {
        [Header("Config")]
        [SerializeField] private NetworkConfig _networkConfig;
        [SerializeField] private bool _autoStart = true;

        [Header("Event Channels — Output")]
        [Tooltip("Raised when a valid command is received from a client.")]
        [SerializeField] private CommandEventChannel _commandReceivedChannel;

        [Tooltip("Raised when a client connects.")]
        [SerializeField] private VoidEventChannel _onClientConnectedChannel;

        [Tooltip("Raised when a client disconnects.")]
        [SerializeField] private VoidEventChannel _onClientDisconnectedChannel;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        private NetworkDriver _driver;
        private NativeList<NetworkConnection> _connections;
        private bool _isRunning;

        public bool IsRunning => _isRunning;

        private void Start()
        {
            if (_autoStart)
            {
                StartHost();
            }
        }

        /// <summary>
        /// Bind and listen on the configured port.
        /// </summary>
        public void StartHost()
        {
            if (_isRunning)
            {
                Log("[Host] Already running.");
                return;
            }

            _driver = NetworkDriver.Create();
            var endpoint = NetworkEndpoint.AnyIpv4.WithPort(_networkConfig.Port);

            if (_driver.Bind(endpoint) != 0)
            {
                Log($"[Host] ERROR — Failed to bind to port {_networkConfig.Port}.");
                _driver.Dispose();
                return;
            }

            _driver.Listen();
            _connections = new NativeList<NetworkConnection>(
                _networkConfig.MaxConnections, Allocator.Persistent);

            _isRunning = true;

            var localIp = NetworkUtility.GetLocalIPAddress();
            Log($"[Host] Listening on {localIp}:{_networkConfig.Port}");
        }

        /// <summary>
        /// Stop the host and disconnect all clients.
        /// </summary>
        public void StopHost()
        {
            if (!_isRunning) return;

            for (int i = 0; i < _connections.Length; i++)
            {
                if (_connections[i].IsCreated)
                {
                    _driver.Disconnect(_connections[i]);
                }
            }

            _connections.Dispose();
            _driver.Dispose();
            _isRunning = false;
            Log("[Host] Stopped.");
        }

        private void Update()
        {
            if (!_isRunning) return;

            _driver.ScheduleUpdate().Complete();

            CleanupStaleConnections();
            AcceptNewConnections();
            ProcessEvents();
        }

        private void CleanupStaleConnections()
        {
            for (int i = 0; i < _connections.Length; i++)
            {
                if (!_connections[i].IsCreated)
                {
                    _connections.RemoveAtSwapBack(i);
                    i--;
                }
            }
        }

        private void AcceptNewConnections()
        {
            NetworkConnection newConnection;
            while ((newConnection = _driver.Accept()) != default)
            {
                _connections.Add(newConnection);
                Log("[Host] Client connected.");
                _onClientConnectedChannel?.Raise();
            }
        }

        private void ProcessEvents()
        {
            for (int i = 0; i < _connections.Length; i++)
            {
                NetworkEvent.Type eventType;
                while ((eventType = _driver.PopEventForConnection(
                    _connections[i], out var reader)) != NetworkEvent.Type.Empty)
                {
                    switch (eventType)
                    {
                        case NetworkEvent.Type.Data:
                            HandleData(reader);
                            break;

                        case NetworkEvent.Type.Disconnect:
                            Log("[Host] Client disconnected.");
                            _connections[i] = default;
                            _onClientDisconnectedChannel?.Raise();
                            break;
                    }
                }
            }
        }

        private void HandleData(DataStreamReader reader)
        {
            // Protocol: [int32 length] [UTF8 bytes]
            int length = reader.ReadInt();
            if (length <= 0 || length > 65536)
            {
                Log($"[Host] Invalid message length: {length}");
                return;
            }

            var bytes = new NativeArray<byte>(length, Allocator.Temp);
            reader.ReadBytes(bytes);
            string json = Encoding.UTF8.GetString(bytes.ToArray());
            bytes.Dispose();

            var command = RemoteCommand.FromJson(json);
            if (command == null)
            {
                Log($"[Host] Failed to parse command JSON.");
                return;
            }

            Log($"[Host] Received: {command}");
            _commandReceivedChannel?.Raise(command);
        }

        private void OnDestroy()
        {
            StopHost();
        }

        private void Log(string message)
        {
            _logChannel?.Raise(message);
        }
    }
}
