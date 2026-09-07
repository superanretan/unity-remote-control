using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// ScriptableObject configuration for network transport.
    /// Shared between Host and Client.
    /// The "Transport" section drives the native UDP / Unity Transport path,
    /// the "Signaling" and "Video" sections drive the WebGL ↔ Vision Pro WebRTC path.
    /// </summary>
    [CreateAssetMenu(
        fileName = "NetworkConfig",
        menuName = "Remote Control/Config/Network Config")]
    public class NetworkConfig : ScriptableObject
    {
        [Header("Transport (native — Unity Transport)")]
        [Tooltip("Port the host listens on and the client connects to.")]
        [SerializeField] private ushort _port = 7777;

        [Tooltip("Maximum number of client connections the host will accept.")]
        [SerializeField] private int _maxConnections = 4;

        [Header("Signaling (WebGL ↔ Vision Pro)")]
        [Tooltip("WebSocket URL of the signaling server. Use wss:// when the controller page is served over HTTPS; ws://host:8787 is fine for local development.")]
        [SerializeField] private string _signalingServerUrl = "ws://localhost:8787";

        [Tooltip("Human-readable name the host registers under (shown in the controller dropdown), e.g. \"Vision Pro Office\".")]
        [SerializeField] private string _deviceName = "Vision Pro";

        [Tooltip("Seconds between heartbeats sent by the host to the signaling server.")]
        [Min(0.5f)]
        [SerializeField] private float _heartbeatInterval = 2f;

        [Tooltip("Seconds without a heartbeat after which the signaling server drops a device from the list.")]
        [Min(1f)]
        [SerializeField] private float _deviceTimeout = 6f;

        [Tooltip("STUN/TURN servers passed to RTCPeerConnection on both peers. Leave empty for LAN-only host candidates.")]
        [SerializeField] private string[] _iceServers = { "stun:stun.l.google.com:19302" };

        [Header("Video (Vision Pro → WebGL)")]
        [Tooltip("Target width of the streamed frame. The native encoder scales the captured frame down to this size.")]
        [SerializeField] private int _videoWidth = 1280;

        [Tooltip("Target height of the streamed frame.")]
        [SerializeField] private int _videoHeight = 720;

        [Tooltip("Target frame rate of the stream. 20–30 keeps Vision Pro load low.")]
        [Range(5, 60)]
        [SerializeField] private int _videoFps = 24;

        [Tooltip("Maximum video bitrate in kbit/s. ~2500 is plenty for a 720p preview.")]
        [SerializeField] private int _videoBitrateKbps = 2500;

        /// <summary>Port number (default 7777).</summary>
        public ushort Port => _port;

        /// <summary>Max simultaneous client connections.</summary>
        public int MaxConnections => _maxConnections;

        /// <summary>WebSocket URL of the signaling server (ws:// or wss://).</summary>
        public string SignalingServerUrl => _signalingServerUrl;

        /// <summary>Display name the host registers under.</summary>
        public string DeviceName => _deviceName;

        /// <summary>Seconds between host heartbeats.</summary>
        public float HeartbeatInterval => _heartbeatInterval;

        /// <summary>Seconds without heartbeat before the server forgets a device.</summary>
        public float DeviceTimeout => _deviceTimeout;

        /// <summary>ICE server URLs (STUN/TURN).</summary>
        public string[] IceServers => _iceServers ?? System.Array.Empty<string>();

        /// <summary>Streamed video width.</summary>
        public int VideoWidth => _videoWidth;

        /// <summary>Streamed video height.</summary>
        public int VideoHeight => _videoHeight;

        /// <summary>Streamed video frame rate.</summary>
        public int VideoFps => _videoFps;

        /// <summary>Max video bitrate in kbit/s.</summary>
        public int VideoBitrateKbps => _videoBitrateKbps;

        /// <summary>
        /// ICE servers as a JSON array of URL strings — the format both the .jslib and the
        /// visionOS native bridge consume.
        /// </summary>
        public string IceServersJson()
        {
            var servers = IceServers;
            var sb = new System.Text.StringBuilder("[");
            for (int i = 0; i < servers.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(servers[i])) continue;
                if (sb.Length > 1) sb.Append(',');
                sb.Append('"').Append(servers[i].Replace("\"", "\\\"")).Append('"');
            }
            return sb.Append(']').ToString();
        }
    }
}
