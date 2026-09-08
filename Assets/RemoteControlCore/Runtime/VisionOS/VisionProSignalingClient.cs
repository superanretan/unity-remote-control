using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    // Host-side signaling client (Vision Pro -> server) on ClientWebSocket: registers the device and
    // heart-beats, relays SDP offer/answer and ICE, and reconnects with exponential back-off.
    // Vercel force-closes every socket after <=300 s; the server keeps registry and pairing across
    // the gap, so an established WebRTC session is unaffected. All events are raised on the main
    // thread (Update pumps an inbox). Pure C#, so it also runs in the Editor.
    public class VisionProSignalingClient : MonoBehaviour
    {
        // Stable per-install id so the host keeps its identity across restarts.
        private const string DeviceIdPrefKey = "RemoteControl.DeviceId";

        [Header("Config")]
        [SerializeField] private NetworkConfig _networkConfig;
        [SerializeField] private bool _autoConnect = true;

        [Tooltip("Override NetworkConfig.DeviceName for this instance. Leave empty to use the config value.")]
        [SerializeField] private string _deviceNameOverride;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        public string DeviceId { get; private set; }
        public string DeviceName => string.IsNullOrWhiteSpace(_deviceNameOverride) ? _networkConfig?.DeviceName : _deviceNameOverride;
        public bool IsConnected { get; private set; }
        public string Status { get; private set; } = "available";

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<SignalingMessage> OnOffer;
        public event Action<SignalingMessage> OnIceCandidate;
        // "disconnect" from the paired controller, or from the server on its behalf.
        public event Action<SignalingMessage> OnPeerDisconnect;

        private readonly ConcurrentQueue<SignalingMessage> _inbox = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private CancellationTokenSource _cts;
        private ClientWebSocket _socket;
        // Incremented per Connect(); events from an older run loop are ignored on dispatch.
        private int _generation;

        private void Awake()
        {
            DeviceId = PlayerPrefs.GetString(DeviceIdPrefKey, string.Empty);
            if (string.IsNullOrEmpty(DeviceId))
            {
                DeviceId = Guid.NewGuid().ToString("N");
                PlayerPrefs.SetString(DeviceIdPrefKey, DeviceId);
                PlayerPrefs.Save();
            }
        }

        private void Start()
        {
            if (_autoConnect) Connect();
        }

        private void OnDisable() => Disconnect();

        private void Update()
        {
            while (_inbox.TryDequeue(out var msg)) Dispatch(msg);
        }

        // ───────── Public API ─────────

        public void Connect()
        {
            if (_cts != null) return;
            if (_networkConfig == null || string.IsNullOrWhiteSpace(_networkConfig.SignalingServerUrl))
            {
                Log("[Signaling] ERROR — NetworkConfig.SignalingServerUrl not set.");
                return;
            }

            _cts = new CancellationTokenSource();
            _generation++;
            _ = RunAsync(_cts.Token, _generation);
        }

        public void Disconnect()
        {
            if (_cts == null) return;
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
            try { _socket?.Abort(); } catch { /* ignore */ }
            _socket = null;
            if (IsConnected)
            {
                IsConnected = false;
                OnDisconnected?.Invoke();
            }
        }

        public void SetStatus(string status)
        {
            Status = status;
            Send(new SignalingMessage { type = "set-status", status = status });
        }

        public void SendAnswer(string targetId, string sessionId, string sdp)
        {
            Log("[Signaling] SDP answer sent");
            Send(new SignalingMessage { type = "answer", targetId = targetId, sessionId = sessionId, sdp = sdp });
        }

        public void SendIceCandidate(string targetId, string sessionId, string candidate, string sdpMid, int sdpMLineIndex)
        {
            Send(new SignalingMessage
            {
                type = "ice-candidate", targetId = targetId, sessionId = sessionId,
                candidate = candidate, sdpMid = sdpMid ?? string.Empty, sdpMLineIndex = sdpMLineIndex
            });
        }

        public void SendDisconnect(string targetId, string reason)
        {
            Send(new SignalingMessage { type = "disconnect", targetId = targetId, reason = reason });
        }

        // ───────── Connection loop (background) ─────────

        private async Task RunAsync(CancellationToken ct, int generation)
        {
            // The token is a shared secret, so log the URL without it.
            var uri = new Uri(_networkConfig.SignalingConnectUrl);
            string displayUrl = _networkConfig.SignalingServerUrl;
            float heartbeat = Mathf.Max(0.5f, _networkConfig.HeartbeatInterval);
            float deviceTimeout = _networkConfig.DeviceTimeout;
            int delayMs = 1000;

            while (!ct.IsCancellationRequested)
            {
                var ws = new ClientWebSocket();
                _socket = ws;
                try
                {
                    Enqueue("__log", $"[Signaling] Connecting to {displayUrl}", generation);
                    await ws.ConnectAsync(uri, ct);
                    delayMs = 1000;
                    Enqueue("__open", null, generation);

                    await SendAsync(ws, new SignalingMessage
                    {
                        type = "register-device",
                        deviceId = DeviceId,
                        deviceName = DeviceName,
                        platform = PlatformName(),
                        status = Status,
                        deviceTimeout = deviceTimeout
                    }, ct);

                    using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var heartbeatTask = HeartbeatAsync(ws, heartbeat, loopCts.Token);
                    await ReceiveLoopAsync(ws, loopCts.Token, generation);
                    loopCts.Cancel();
                    try { await heartbeatTask; } catch { /* cancelled */ }
                }
                catch (OperationCanceledException) { }
                catch (Exception e)
                {
                    Enqueue("__log", $"[Signaling] Connection error: {e.Message}", generation);
                }
                finally
                {
                    try { ws.Dispose(); } catch { /* ignore */ }
                    if (_socket == ws) _socket = null;
                    Enqueue("__closed", null, generation);
                }

                if (ct.IsCancellationRequested) break;
                Enqueue("__log", $"[Signaling] Reconnect in {delayMs / 1000f:0.#}s", generation);
                try { await Task.Delay(delayMs, ct); } catch (OperationCanceledException) { break; }
                delayMs = Math.Min(delayMs * 2, 10000);
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct, int generation)
        {
            var buffer = new byte[16 * 1024];
            using var accumulator = new MemoryStream();

            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                accumulator.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        // Server-initiated close: surface the reason ("replaced", "unauthorized", ...).
                        string why = string.IsNullOrEmpty(ws.CloseStatusDescription) ? ws.CloseStatus?.ToString() : ws.CloseStatusDescription;
                        if (!string.IsNullOrEmpty(why)) Enqueue("__log", $"[Signaling] Server closed the socket: {why}", generation);
                        return;
                    }
                    accumulator.Write(buffer, 0, result.Count);
                    if (accumulator.Length > 1024 * 1024) throw new InvalidDataException("Signaling message too large");
                }
                while (!result.EndOfMessage);

                var json = Encoding.UTF8.GetString(accumulator.GetBuffer(), 0, (int)accumulator.Length);
                var msg = SignalingMessage.FromJson(json);
                if (msg != null && !string.IsNullOrEmpty(msg.type))
                {
                    msg.generation = generation;
                    _inbox.Enqueue(msg);
                }
            }
        }

        private async Task HeartbeatAsync(ClientWebSocket ws, float intervalSeconds, CancellationToken ct)
        {
            var hb = new SignalingMessage { type = "heartbeat" };
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), ct);
                await SendAsync(ws, hb, ct);
            }
        }

        private void Send(SignalingMessage msg)
        {
            var ws = _socket;
            if (ws == null || ws.State != WebSocketState.Open)
            {
                Log($"[Signaling] Cannot send '{msg.type}' — not connected.");
                return;
            }
            _ = SendAsync(ws, msg, _cts?.Token ?? CancellationToken.None);
        }

        private async Task SendAsync(ClientWebSocket ws, SignalingMessage msg, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(msg.ToJson());
            await _sendLock.WaitAsync(ct);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
            }
            catch (Exception e)
            {
                Enqueue("__log", $"[Signaling] Send failed: {e.Message}", _generation);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ───────── Main-thread dispatch ─────────

        private void Enqueue(string type, string payload, int generation) =>
            _inbox.Enqueue(new SignalingMessage { type = type, message = payload, generation = generation });

        private void Dispatch(SignalingMessage msg)
        {
            // A cancelled run loop may still flush its "__closed"/messages after a newer Connect().
            if (msg.generation != _generation) return;

            switch (msg.type)
            {
                case "__log":
                    Log(msg.message);
                    break;

                case "__open":
                    IsConnected = true;
                    Log($"[Signaling] Connected — registering \"{DeviceName}\" ({DeviceId})");
                    OnConnected?.Invoke();
                    break;

                case "__closed":
                    if (!IsConnected) break;
                    IsConnected = false;
                    Log("[Signaling] Disconnected.");
                    OnDisconnected?.Invoke();
                    break;

                case "registered":
                    Log($"[Signaling] Registered as \"{DeviceName}\" — waiting for controller.");
                    break;

                case "offer":
                    Log($"[Signaling] SDP offer received from {msg.fromId}");
                    OnOffer?.Invoke(msg);
                    break;

                case "ice-candidate":
                    OnIceCandidate?.Invoke(msg);
                    break;

                case "disconnect":
                    Log($"[Signaling] Peer disconnect from {msg.fromId} ({msg.reason})");
                    OnPeerDisconnect?.Invoke(msg);
                    break;

                case "error":
                    Log(msg.message == "unauthorized"
                        ? "[Signaling] ERROR — server rejected the connection: unauthorized. Check NetworkConfig.SignalingToken against the server's ROOM_TOKEN."
                        : $"[Signaling] Server error: {msg.message}");
                    break;
            }
        }

        private static string PlatformName()
        {
#if UNITY_VISIONOS
            return "visionOS";
#else
            return Application.platform.ToString();
#endif
        }

        private void Log(string message) => _logChannel?.Raise(message);
    }
}
