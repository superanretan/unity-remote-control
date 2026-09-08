using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    // Shared by host and client. The Transport section drives the native UDP / Unity Transport
    // path; Signaling, ICE and Video drive the WebGL <-> Vision Pro WebRTC path.
    [CreateAssetMenu(
        fileName = "NetworkConfig",
        menuName = "Remote Control/Config/Network Config")]
    public class NetworkConfig : ScriptableObject, ISerializationCallbackReceiver
    {
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

        // 1.x: flat URL list without credentials. Migrated into _iceServerEntries on load.
        [SerializeField, HideInInspector] private string[] _iceServers = Array.Empty<string>();

        // Cost on the headset is roughly linear in width x height x fps: with the UnityCamera
        // backend it renders an extra pass at exactly this size and rate. Snapped down to a
        // multiple of 16 by the streamer.
        [Header("Video (Vision Pro → WebGL)")]
        [Tooltip("Width of the streamed frame. 960 is the recommended ceiling for a remote preview; " +
                 "the Vision Pro renders an extra pass at this size for every streamed frame.")]
        [Range(320, 1280)]
        [SerializeField] private int _videoWidth = 960;

        [Tooltip("Height of the streamed frame. 540 with a 960 width keeps 16:9.")]
        [Range(180, 720)]
        [SerializeField] private int _videoHeight = 540;

        [Tooltip("Frame rate of the stream. 15 is smooth enough to drive a remote UI and costs the " +
                 "headset half of what 30 does. Above 30 is never worth it here.")]
        [Range(5, 30)]
        [SerializeField] private int _videoFps = 15;

        [Tooltip("Maximum video bitrate in kbit/s. ~1200 is plenty at 960x540; raising it makes the " +
                 "hardware encoder and the radio work harder for a preview.")]
        [Range(200, 4000)]
        [SerializeField] private int _videoBitrateKbps = 1200;

        public ushort Port => _port;
        public int MaxConnections => _maxConnections;
        public string SignalingServerUrl => _signalingServerUrl;
        public string SignalingToken => _signalingToken;
        public string DeviceName => _deviceName;
        public float HeartbeatInterval => _heartbeatInterval;
        public float DeviceTimeout => _deviceTimeout;
        public IReadOnlyList<IceServerEntry> IceServerEntries => _iceServerEntries;

        // URL both peers connect to: the configured URL plus ?token=… when a token is set.
        public string SignalingConnectUrl => BuildConnectUrl(_signalingServerUrl, _signalingToken);

        // Flat URL list, credentials dropped. Kept for 1.x callers; prefer IceServerEntries.
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

        public int VideoWidth => _videoWidth;
        public int VideoHeight => _videoHeight;
        public int VideoFps => _videoFps;
        public int VideoBitrateKbps => _videoBitrateKbps;

        // JSON array of RTCIceServer objects, as consumed by the .jslib and the visionOS bridge:
        // [{"urls":["stun:..."]},{"urls":["turn:host:3478"],"username":"u","credential":"c"}]
        // Entries without a URL are skipped.
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

        // ───────── legacy migration: 1.x string[] _iceServers → IceServerEntry list ─────────

        void ISerializationCallbackReceiver.OnBeforeSerialize() { }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            if (_iceServers == null || _iceServers.Length == 0) return;

            // Only overwrite an untouched list (empty or still the code default), so no configured
            // value is lost, then clear the legacy field so the next save writes the new shape.
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
