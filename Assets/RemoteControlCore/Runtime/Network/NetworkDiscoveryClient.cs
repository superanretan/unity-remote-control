using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Listens for UDP broadcasts from NetworkDiscoveryHost.
    /// Can be started and stopped on demand.
    /// </summary>
    public class NetworkDiscoveryClient : MonoBehaviour
    {
        [SerializeField] private NetworkConfig networkConfig;
        
        // Fired when a host is discovered or updated
        public event Action<NetworkDiscoveryMessage> OnHostDiscovered;

        private UdpClient _udpClient;
        private bool _isListening;
        
        private Dictionary<string, NetworkDiscoveryMessage> _discoveredHosts = new Dictionary<string, NetworkDiscoveryMessage>();

        public IReadOnlyCollection<NetworkDiscoveryMessage> DiscoveredHosts => _discoveredHosts.Values;

        /// <summary>
        /// Clears the current list and starts listening for broadcasts.
        /// </summary>
        public void StartDiscovery()
        {
            StopDiscovery();

            _discoveredHosts.Clear();
            
            try
            {
                _udpClient = new UdpClient();
                // Allow multiple bindings if necessary
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                var endpoint = new IPEndPoint(IPAddress.Any, networkConfig.DiscoveryPort);
                _udpClient.Client.Bind(endpoint);
                
                _isListening = true;
                Debug.Log($"[NetworkDiscoveryClient] Listening for broadcasts on port {networkConfig.DiscoveryPort}...");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[NetworkDiscoveryClient] Failed to start: {ex.Message}");
            }
        }

        public void StopDiscovery()
        {
            _isListening = false;
            if (_udpClient != null)
            {
                _udpClient.Close();
                _udpClient = null;
            }
        }

        private void Update()
        {
            if (!_isListening || _udpClient == null) return;

            // Process all available packets
            while (_udpClient.Available > 0)
            {
                try
                {
                    IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = _udpClient.Receive(ref remoteEP);
                    string json = Encoding.UTF8.GetString(data);
                    
                    var msg = NetworkDiscoveryMessage.FromJson(json);
                    if (msg != null && !string.IsNullOrEmpty(msg.HostIP))
                    {
                        // Use IP + Port as a unique key
                        string key = $"{msg.HostIP}:{msg.Port}";
                        if (!_discoveredHosts.ContainsKey(key))
                        {
                            _discoveredHosts[key] = msg;
                            OnHostDiscovered?.Invoke(msg);
                            Debug.Log($"[NetworkDiscoveryClient] Discovered Host: {msg.HostName} at {msg.HostIP}:{msg.Port}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[NetworkDiscoveryClient] Error receiving broadcast: {ex.Message}");
                }
            }
        }

        private void OnDestroy()
        {
            StopDiscovery();
        }
    }
}
