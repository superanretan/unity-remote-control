using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// ScriptableObject configuration for network transport.
    /// Shared between Host and Client.
    /// </summary>
    [CreateAssetMenu(
        fileName = "NetworkConfig",
        menuName = "Remote Control/Config/Network Config")]
    public class NetworkConfig : ScriptableObject
    {
        [Header("Transport")]
        [Tooltip("Port the host listens on and the client connects to.")]
        [SerializeField] private ushort port = 7777;

        [Tooltip("The UDP port used for local network discovery broadcasts.")]
        [SerializeField] private int discoveryPort = 7778;

        [Tooltip("Maximum number of client connections the host will accept.")]
        [SerializeField] private int maxConnections = 4;

        [Tooltip("Hardcoded Host IP. The controller will connect to this IP by default.")]
        [SerializeField] private string hostIP = "192.168.1.X";

        /// <summary>Port number (default 7777).</summary>
        public ushort Port => port;

        /// <summary>UDP port for discovery (default 7778).</summary>
        public int DiscoveryPort => discoveryPort;

        /// <summary>Max simultaneous client connections.</summary>
        public int MaxConnections => maxConnections;

        /// <summary>Hardcoded Host IP.</summary>
        public string HostIP => hostIP;
    }
}
