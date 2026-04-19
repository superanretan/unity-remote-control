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
        [SerializeField] private ushort _port = 7777;

        [Tooltip("Maximum number of client connections the host will accept.")]
        [SerializeField] private int _maxConnections = 4;

        /// <summary>Port number (default 7777).</summary>
        public ushort Port => _port;

        /// <summary>Max simultaneous client connections.</summary>
        public int MaxConnections => _maxConnections;
    }
}
