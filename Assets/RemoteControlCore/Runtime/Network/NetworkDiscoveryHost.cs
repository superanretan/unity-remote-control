using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Broadcasts the presence of this host over UDP on the local network.
    /// </summary>
    public class NetworkDiscoveryHost : MonoBehaviour
    {
        [SerializeField] private NetworkConfig networkConfig;
        [SerializeField] private string deviceName = "Unity Host";
        [SerializeField] private float broadcastInterval = 2f;

        private UdpClient _udpClient;
        private float _timer;
        private string _broadcastJson;

        private void Start()
        {
            if (networkConfig == null)
            {
                Debug.LogWarning("[NetworkDiscoveryHost] NetworkConfig is missing.");
                return;
            }

            _udpClient = new UdpClient();
            _udpClient.EnableBroadcast = true;
            
            // Allow multiple endpoints to bind to the same port
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            UpdateBroadcastMessage();
        }

        private void UpdateBroadcastMessage()
        {
            var msg = new NetworkDiscoveryMessage
            {
                HostName = deviceName,
                HostIP = NetworkUtility.GetLocalIPAddress(), // Always use actual local IP
                Port = networkConfig.Port
            };

            _broadcastJson = msg.ToJson();
        }

        private void Update()
        {
            if (_udpClient == null) return;

            _timer += Time.deltaTime;
            if (_timer >= broadcastInterval)
            {
                _timer = 0f;
                Broadcast();
            }
        }

        private void Broadcast()
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(_broadcastJson);
                var endpoint = new IPEndPoint(IPAddress.Broadcast, networkConfig.DiscoveryPort);
                _udpClient.Send(data, data.Length, endpoint);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[NetworkDiscoveryHost] Broadcast failed: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            if (_udpClient != null)
            {
                _udpClient.Close();
                _udpClient = null;
            }
        }
    }
}
