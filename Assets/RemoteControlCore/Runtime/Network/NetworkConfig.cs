using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// ScriptableObject configuration for network transport.
    /// Shared between Host and Client.
    /// The "Transport" section drives the native UDP / Unity Transport path,
    /// the "Signaling", "ICE" and "Video" sections drive the WebGL ↔ Vision Pro WebRTC path.
    /// </summary>
    [CreateAssetMenu(
        fileName = "NetworkConfig",
        menuName = "Remote Control/Config/Network Config")]
    public class NetworkConfig : ScriptableObject, ISerializationCallbackReceiver
    {
        /// <summary>
        /// One entry of <c>RTCPeerConnection.iceServers</c>. STUN needs only <see cref="urls"/>;
        /// TURN additionally needs <see cref="username"/> and <see cref="credential"/>.
        /// </summary>
        [Serializable]
        public class IceServerEntry
        {
            [Tooltip("One or more URLs sharing the same credentials, e.g. \"stun:stun.l.google.com:19302\" or \"turn:turn.example.com:3478?transport=udp\".")]
            public string[] urls = Array.Empty<string>();

            [Tooltip("TURN only. Leave empty for STUN.")]
            public string username = string.Empty;

            [Tooltip("TURN only. Note: anything stored here ships inside the public WebGL build — prefer short-lived credentials issued by the signaling server (TURN_URLS / TURN_SECRET).")]
            public string credential = string.Empty;

            public IceServerEntry() { }

            public IceServerEntry(string url, string username = "", string credential = "")
            {
                urls = new[] { url };
                this.username = username ?? string.Empty;
                this.credential = credential ?? string.Empty;
            }

            public bool HasCredentials => !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(credential);
        }

        [Header("Transport (native — Unity Transport)")]
        [Tooltip("Port the host listens on and the client connects to.")]
        [SerializeField] private ushort _port = 7777;

        [Tooltip("Maximum number of client connections the host will accept.")]
        [SerializeField] private int _maxConnections = 4;

        [Header("Signaling (WebGL ↔ Vision Pro)")]
        [Tooltip("WebSocket URL of the signaling server. Vercel: wss://<project>.vercel.app/api/signaling. Local dev: ws://<pc-ip>:8787. Use wss:// whenever the controller page is served over HTTPS.")]
        [SerializeField] private string _signalingServerUrl = "ws://localhost:8787";

        [Tooltip("Shared secret expected by the signaling server (ROOM_TOKEN). Appended to the URL as ?token=… on connect. Empty = server runs in open mode. This value ships inside the public WebGL build, so treat it as obfuscation, not authentication.")]
        [SerializeField] private string _signalingToken = string.Empty;

        [Tooltip("Human-readable name the host registers under (shown in the controller dropdown), e.g. \"Vision Pro Office\".")]
        [SerializeField] private string _deviceName = "Vision Pro";

        [Tooltip("Seconds between heartbeats sent by the host to the signaling server.")]
        [Min(0.5f)]
        [SerializeField] private float _heartbeatInterval = 2f;

        [Tooltip("Seconds without a heartbeat after which the signaling server drops this host from the list. Sent to the server in register-device; must comfortably exceed the reconnect gap (1 s … 10 s back-off) caused by Vercel's forced socket close.")]
        [Min(3f)]
        [SerializeField] private float _deviceTimeout = 15f;

        [Header("ICE (STUN / TURN)")]
        [Tooltip("ICE servers passed to RTCPeerConnection on both peers. STUN entries need only URLs; TURN entries need username + credential. Empty = LAN-only host candidates.")]
        [SerializeField] private List<IceServerEntry> _iceServerEntries = new()
        {
            new IceServerEntry("stun:stun.l.google.com:19302"),
        };

        // Legacy field (package 1.x): flat URL list without credentials. Migrated into _iceServerEntries on load.
        [SerializeField, HideInInspector] private string[] _iceServers = Array.Empty<string>();

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

        /// <summary>WebSocket URL of the signaling server exactly as configured (ws:// or wss://), without the token.</summary>
        public string SignalingServerUrl => _signalingServerUrl;

        /// <summary>Shared signaling token (ROOM_TOKEN). Empty when the server runs without authentication.</summary>
        public string SignalingToken => _signalingToken;

        /// <summary>
        /// URL the clients actually connect to: <see cref="SignalingServerUrl"/> plus <c>?token=…</c> when a
        /// token is configured and the URL does not already carry one. Both the browser and the Vision Pro use it.
        /// </summary>
        public string SignalingConnectUrl => BuildConnectUrl(_signalingServerUrl, _signalingToken);

        /// <summary>Display name the host registers under.</summary>
        public string DeviceName => _deviceName;

        /// <summary>Seconds between host heartbeats.</summary>
        public float HeartbeatInterval => _heartbeatInterval;

        /// <summary>Seconds without heartbeat before the server forgets a device (announced by the host on registration).</summary>
        public float DeviceTimeout => _deviceTimeout;

        /// <summary>ICE server entries (STUN/TURN with optional credentials).</summary>
        public IReadOnlyList<IceServerEntry> IceServerEntries => _iceServerEntries;

        /// <summary>Flat list of ICE URLs (credentials dropped). Kept for 1.x callers; prefer <see cref="IceServerEntries"/>.</summary>
        public string[] IceServers
        {
            get
            {
                var list = new List<string>();
                foreach (var e in _iceServerEntries)
                    if (e?.urls != null) foreach (var u in e.urls) if (!string.IsNullOrWhiteSpace(u)) list.Add(u);
                return list.ToArray();
            }
        }

        /// <summary>Streamed video width.</summary>
        public int VideoWidth => _videoWidth;

        /// <summary>Streamed video height.</summary>
        public int VideoHeight => _videoHeight;

        /// <summary>Streamed video frame rate.</summary>
        public int VideoFps => _videoFps;

        /// <summary>Max video bitrate in kbit/s.</summary>
        public int VideoBitrateKbps => _videoBitrateKbps;

        /// <summary>
        /// ICE servers as a JSON array of <c>RTCIceServer</c> objects — the format both the .jslib and the
        /// visionOS native bridge consume:
        /// <c>[{"urls":["stun:..."]},{"urls":["turn:host:3478?transport=udp"],"username":"u","credential":"c"}]</c>.
        /// Entries without any URL are skipped. (1.x produced a flat array of strings; both consumers still accept that.)
        /// </summary>
        public string IceServersJson()
        {
            var sb = new StringBuilder("[");
            foreach (var e in _iceServerEntries)
            {
                if (e?.urls == null) continue;
                var urls = new List<string>();
                foreach (var u in e.urls) if (!string.IsNullOrWhiteSpace(u)) urls.Add(u.Trim());
                if (urls.Count == 0) continue;

                if (sb.Length > 1) sb.Append(',');
                sb.Append("{\"urls\":[");
                for (int i = 0; i < urls.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendJsonString(sb, urls[i]);
                }
                sb.Append(']');
                if (e.HasCredentials)
                {
                    sb.Append(",\"username\":"); AppendJsonString(sb, e.username ?? string.Empty);
                    sb.Append(",\"credential\":"); AppendJsonString(sb, e.credential ?? string.Empty);
                }
                sb.Append('}');
            }
            return sb.Append(']').ToString();
        }

        /// <summary>Appends <c>?token=</c> (or <c>&amp;token=</c>) unless the URL already carries a token.</summary>
        public static string BuildConnectUrl(string url, string token)
        {
            if (string.IsNullOrWhiteSpace(url)) return url;
            url = url.Trim();
            if (string.IsNullOrWhiteSpace(token)) return url;
            if (url.IndexOf("token=", StringComparison.OrdinalIgnoreCase) >= 0) return url;
            string sep = url.IndexOf('?') >= 0 ? "&" : "?";
            return url + sep + "token=" + Uri.EscapeDataString(token.Trim());
        }

        private static void AppendJsonString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ───────── legacy migration (1.x string[] _iceServers → IceServerEntry list) ─────────

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (_iceServers == null || _iceServers.Length == 0) return;

            // Old asset: the entry list is either empty or still holds only the code default. Replace it with
            // the persisted URLs so no configured value is lost, then clear the legacy field so the next
            // save writes the new shape.
            bool listIsDefault = _iceServerEntries == null || _iceServerEntries.Count == 0 ||
                                 (_iceServerEntries.Count == 1 && _iceServerEntries[0] != null && !_iceServerEntries[0].HasCredentials &&
                                  _iceServerEntries[0].urls != null && _iceServerEntries[0].urls.Length == 1 &&
                                  _iceServerEntries[0].urls[0] == "stun:stun.l.google.com:19302");
            if (listIsDefault)
            {
                _iceServerEntries = new List<IceServerEntry>();
                foreach (var u in _iceServers)
                    if (!string.IsNullOrWhiteSpace(u)) _iceServerEntries.Add(new IceServerEntry(u.Trim()));
            }
            _iceServers = Array.Empty<string>();
        }
    }
}
