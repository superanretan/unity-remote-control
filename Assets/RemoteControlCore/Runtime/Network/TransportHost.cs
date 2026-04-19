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
        [SerializeField] private NetworkConfig networkConfig;
        [SerializeField] private bool autoStart = true;

        [Header("Event Channels — Output")]
        [Tooltip("Raised when a valid command is received from a client.")]
        [SerializeField] private CommandEventChannel commandReceivedChannel;

        [Tooltip("Raised when a client connects.")]
        [SerializeField] private VoidEventChannel onClientConnectedChannel;

        [Tooltip("Raised when a client disconnects.")]
        [SerializeField] private VoidEventChannel onClientDisconnectedChannel;

        [Header("Logging")]
        [SerializeField] private StringEventChannel logChannel;

        private NetworkDriver _driver;
        private NativeList<NetworkConnection> _connections;
        private bool _isRunning;

        public bool IsRunning => _isRunning;

        private void Start()
        {
            if (autoStart)
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
            var endpoint = NetworkEndpoint.AnyIpv4.WithPort(networkConfig.Port);

            if (_driver.Bind(endpoint) != 0)
            {
                Log($"[Host] ERROR — Failed to bind to port {networkConfig.Port}.");
                _driver.Dispose();
                return;
            }

            _driver.Listen();
            _connections = new NativeList<NetworkConnection>(
                networkConfig.MaxConnections, Allocator.Persistent);

            _isRunning = true;

            var localIp = NetworkUtility.GetLocalIPAddress();
            Log($"[Host] Listening on {localIp}:{networkConfig.Port}");
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
                onClientConnectedChannel?.Raise();
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
                            onClientDisconnectedChannel?.Raise();
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
            commandReceivedChannel?.Raise(command);
        }

        private void OnDestroy()
        {
            StopHost();
        }

        private void Log(string message)
        {
            logChannel?.Raise(message);
        }
    }
}
