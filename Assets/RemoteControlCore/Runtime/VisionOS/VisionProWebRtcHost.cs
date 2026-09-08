using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    // Vision Pro host session coordinator — the WebRTC counterpart of TransportHost.
    //   signaling offer -> native peer answers -> DataChannel opens -> screen capture starts
    //   DataChannel JSON -> RemoteCommand.FromJson -> CommandReceivedChannel -> CommandProcessor
    //   HostMessageSendChannel -> TrySend -> DataChannel -> controller
    // 'state' messages are cached per topic and replayed as one 'snapshot' when the DataChannel
    // opens. Capture state is reported separately, since "DataChannel open" is not "video flowing".
    // App code must go through TrySend and never touch VisionProNativeBridge directly.
    public class VisionProWebRtcHost : MonoBehaviour
    {
        [Header("Config")]
        [SerializeField] private NetworkConfig _networkConfig;
        [SerializeField] private VisionProSignalingClient _signaling;

        [Header("Event Channels — Output")]
        [Tooltip("Raised when a valid command is received from the controller.")]
        [SerializeField] private CommandEventChannel _commandReceivedChannel;
        [SerializeField] private VoidEventChannel _onClientConnectedChannel;
        [SerializeField] private VoidEventChannel _onClientDisconnectedChannel;

        [Header("Event Channels — Input")]
        [Tooltip("Host → controller return channel. Raise HostMessage.ToJson() here; the host sends it over the DataChannel when connected and caches 'state' topics for the snapshot.")]
        [SerializeField] private StringEventChannel _hostMessageSendChannel;

        [Header("Capture")]
        [Tooltip("Auto picks UnityCamera when a VisionCameraStreamer is in the scene, otherwise " +
                 "ReplayKit. ReplayKit captures the app's window, which a fully immersive app never " +
                 "draws into — the stream would come out black. UnityCamera streams what the app renders.")]
        [SerializeField] private VisionProNativeBridge.CaptureBackend _captureBackend = VisionProNativeBridge.CaptureBackend.Auto;

        [Tooltip("Start screen capture as soon as the DataChannel opens (the system consent UI appears on first use).")]
        [SerializeField] private bool _autoStartCapture = true;

        [Header("Return channel")]
        [Tooltip("Send the cached state snapshot + capture state to the controller right after the DataChannel opens.")]
        [SerializeField] private bool _sendSnapshotOnConnect = true;

        [Tooltip("Answer commands that carry a requestId with an 'ack' HostMessage.")]
        [SerializeField] private bool _ackCommands = true;

        [Header("Limits")]
        [Tooltip("Seconds allowed between receiving the offer and the DataChannel opening.")]
        [SerializeField] private float _connectTimeout = 20f;

        [Tooltip("Seconds to wait in the ICE 'disconnected' state before giving up on the session.")]
        [SerializeField] private float _disconnectedGrace = 5f;

        [Tooltip("Max DataChannel message size accepted for a command.")]
        [SerializeField] private int _maxCommandBytes = 65536;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        private string _controllerId;
        private string _sessionId;
        private bool _connected;
        private float _connectDeadline;
        private float _disconnectedDeadline;

        // Last 'state' message per topic — replayed as a snapshot on connect. Insertion order preserved.
        private readonly Dictionary<string, HostStateEntry> _stateCache = new();
        private readonly List<string> _stateOrder = new();
        private string _captureState = HostMessage.CaptureStopped;
        private string _captureDetail = string.Empty;

        public bool HasSession => _controllerId != null;
        public bool IsConnected => _connected;

        public string CaptureState => _captureState;

        private void OnEnable()
        {
            VisionProNativeBridge.Initialize();
            VisionProNativeBridge.OnEvent += OnNativeEvent;
            VisionScreenCapture.OnStateChanged += OnCaptureState;

            if (_signaling != null)
            {
                _signaling.OnOffer += OnOffer;
                _signaling.OnIceCandidate += OnRemoteIceCandidate;
                _signaling.OnPeerDisconnect += OnPeerDisconnect;
            }

            if (_hostMessageSendChannel != null) _hostMessageSendChannel.OnRaised += OnHostMessageRequested;

            if (!VisionProNativeBridge.IsSupported)
                Log("[WebRTC] Native bridge unavailable on this platform — host will register but cannot answer offers.");
        }

        private void OnDisable()
        {
            VisionProNativeBridge.OnEvent -= OnNativeEvent;
            VisionScreenCapture.OnStateChanged -= OnCaptureState;

            if (_signaling != null)
            {
                _signaling.OnOffer -= OnOffer;
                _signaling.OnIceCandidate -= OnRemoteIceCandidate;
                _signaling.OnPeerDisconnect -= OnPeerDisconnect;
            }

            if (_hostMessageSendChannel != null) _hostMessageSendChannel.OnRaised -= OnHostMessageRequested;

            EndSession("host-disabled");
        }

        private void Update()
        {
            VisionProNativeBridge.PumpEvents();

            if (!HasSession) return;

            if (!_connected && Time.unscaledTime > _connectDeadline)
                EndSession("timeout");
            else if (_disconnectedDeadline > 0 && Time.unscaledTime > _disconnectedDeadline)
                EndSession("peer-disconnected");
        }

        // ───────── Public API: host → controller ─────────

        // Returns false when no DataChannel is open (the message is dropped, but 'state' topics are
        // still cached for the next snapshot).
        public bool TrySend(HostMessage message)
        {
            if (message == null || string.IsNullOrEmpty(message.messageType)) return false;
            if (message.IsState && !string.IsNullOrEmpty(message.topic)) CacheState(message.topic, message.value, message.payload);
            return TrySendRaw(message.ToJson());
        }

        public bool TrySend(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            var msg = HostMessage.FromJson(json);
            if (msg == null)
            {
                Log("[DataChannel] Refusing to send — not a valid HostMessage JSON.");
                return false;
            }
            if (msg.IsState && !string.IsNullOrEmpty(msg.topic)) CacheState(msg.topic, msg.value, msg.payload);
            return TrySendRaw(json);
        }

        public bool SetState(string topic, string value, string payload = "") => TrySend(HostMessage.State(topic, value, payload));

        public void ClearState(string topic)
        {
            if (_stateCache.Remove(topic)) _stateOrder.Remove(topic);
        }

        private bool TrySendRaw(string json)
        {
            if (!_connected)
            {
                Log("[DataChannel] Cannot send — no controller connected (state cached for snapshot).");
                return false;
            }
            if (VisionProNativeBridge.SendData(json)) return true;
            Log("[DataChannel] Send failed — channel not open.");
            return false;
        }

        private void OnHostMessageRequested(string json) => TrySend(json);

        private void CacheState(string topic, string value, string payload)
        {
            if (!_stateCache.ContainsKey(topic)) _stateOrder.Add(topic);
            _stateCache[topic] = new HostStateEntry(topic, value, payload ?? string.Empty);
        }

        private void SendSnapshot()
        {
            var entries = new List<HostStateEntry>(_stateOrder.Count);
            foreach (var topic in _stateOrder) entries.Add(_stateCache[topic]);
            TrySendRaw(HostMessage.Snapshot(entries).ToJson());
            TrySendRaw(HostMessage.Capture(_captureState, _captureDetail).ToJson());
        }

        private void PublishCaptureState(string state, string detail = "")
        {
            _captureState = state;
            _captureDetail = detail ?? string.Empty;
            if (_connected) TrySendRaw(HostMessage.Capture(state, _captureDetail).ToJson());
        }

        // ───────── Signaling → host ─────────

        private void OnOffer(SignalingMessage msg)
        {
            if (string.IsNullOrEmpty(msg.fromId) || string.IsNullOrEmpty(msg.sdp)) return;

            if (HasSession)
            {
                if (_connected && msg.fromId != _controllerId)
                {
                    Log($"[WebRTC] Offer from {msg.fromId} rejected — busy with {_controllerId}.");
                    _signaling.SendDisconnect(msg.fromId, "busy");
                    return;
                }
                // Stale / re-offered session from the same or a not-yet-connected controller: start over.
                EndSession("replaced", notifyController: msg.fromId != _controllerId);
            }

            _controllerId = msg.fromId;
            _sessionId = msg.sessionId ?? string.Empty;
            _connectDeadline = Time.unscaledTime + _connectTimeout;
            _disconnectedDeadline = 0;

            if (_networkConfig != null)
                VisionProNativeBridge.SetVideoConfig(_networkConfig.VideoWidth, _networkConfig.VideoHeight,
                    _networkConfig.VideoFps, _networkConfig.VideoBitrateKbps);
            VisionProNativeBridge.SetCaptureBackend(ResolveCaptureBackend());

            Log($"[WebRTC] Creating peer for controller {_controllerId}");
            if (!VisionProNativeBridge.CreatePeer(_networkConfig != null ? _networkConfig.IceServersJson() : "[]"))
            {
                Log("[WebRTC] ERROR — could not create peer connection.");
                EndSession("peer-create-failed");
                return;
            }

            _signaling.SetStatus("busy");
            VisionProNativeBridge.HandleRemoteOffer(msg.sdp);
        }

        private void OnRemoteIceCandidate(SignalingMessage msg)
        {
            if (!HasSession || msg.fromId != _controllerId) return;
            if (!string.IsNullOrEmpty(msg.sessionId) && msg.sessionId != _sessionId) return;
            if (string.IsNullOrEmpty(msg.candidate)) return;
            VisionProNativeBridge.AddIceCandidate(msg.candidate, msg.sdpMid, msg.sdpMLineIndex);
        }

        private void OnPeerDisconnect(SignalingMessage msg)
        {
            if (!HasSession) return;
            if (msg.fromId != _controllerId && !string.IsNullOrEmpty(msg.fromId)) return;
            EndSession(string.IsNullOrEmpty(msg.reason) ? "controller-disconnected" : msg.reason, notifyController: false);
        }

        // ───────── Native → host ─────────

        private void OnNativeEvent(string type, string payload)
        {
            switch (type)
            {
                case "log":
                    Log(payload);
                    break;

                case "answer":
                    if (!HasSession) break;
                    _signaling.SendAnswer(_controllerId, _sessionId, payload);
                    break;

                case "ice-candidate":
                    if (!HasSession) break;
                    var ice = JsonUtility.FromJson<IceCandidatePayload>(payload);
                    if (ice != null && !string.IsNullOrEmpty(ice.candidate))
                        _signaling.SendIceCandidate(_controllerId, _sessionId, ice.candidate, ice.sdpMid, ice.sdpMLineIndex);
                    break;

                case "connection-state":
                    Log($"[WebRTC] Connection state: {payload}");
                    switch (payload)
                    {
                        case "connected":
                            Log("[WebRTC] ICE connected.");
                            _disconnectedDeadline = 0;
                            break;
                        case "disconnected":
                            if (_disconnectedDeadline <= 0) _disconnectedDeadline = Time.unscaledTime + _disconnectedGrace;
                            break;
                        case "failed":
                        case "closed":
                            EndSession("peer-" + payload);
                            break;
                    }
                    break;

                case "datachannel-open":
                    if (!HasSession) break;
                    _connected = true;
                    Log("[DataChannel] Opened — controller connected.");
                    _onClientConnectedChannel?.Raise();
                    if (_sendSnapshotOnConnect) SendSnapshot();
                    if (_autoStartCapture)
                    {
                        Log("[ScreenCapture] Starting capture...");
                        PublishCaptureState(HostMessage.CaptureStarting);
                        VisionScreenCapture.StartCapture();
                    }
                    break;

                case "datachannel-closed":
                    if (HasSession) EndSession("datachannel-closed");
                    break;

                case "datachannel-message":
                    HandleCommandJson(payload);
                    break;

                case "error":
                    Log($"[WebRTC] Native error: {payload}");
                    if (HasSession && !_connected) EndSession("native-error");
                    break;
            }
        }

        // Auto means "stream what this app can actually show": a VisionCameraStreamer in the scene
        // decides, because ReplayKit would capture an immersive app's empty window. An explicitly
        // chosen backend is never overridden.
        private VisionProNativeBridge.CaptureBackend ResolveCaptureBackend()
        {
            bool hasStreamer = FindAnyObjectByType<VisionCameraStreamer>() != null;

            if (_captureBackend == VisionProNativeBridge.CaptureBackend.Auto && hasStreamer)
            {
                Log("[ScreenCapture] Auto → UnityCamera (VisionCameraStreamer found in the scene).");
                return VisionProNativeBridge.CaptureBackend.UnityCamera;
            }

            if (_captureBackend == VisionProNativeBridge.CaptureBackend.UnityCamera && !hasStreamer)
            {
                Log("[ScreenCapture] WARNING — capture backend is UnityCamera but no VisionCameraStreamer " +
                    "is in the scene: capture will report streaming and no frame will ever be sent.");
            }

            return _captureBackend;
        }

        private void OnCaptureState(string state)
        {
            switch (state)
            {
                case "started":
                    Log("[Video] Screen capture started — streaming.");
                    PublishCaptureState(HostMessage.CaptureStreaming);
                    break;
                case "stopped":
                    Log("[Video] Screen capture stopped.");
                    PublishCaptureState(HostMessage.CaptureStopped);
                    break;
                default:
                    Log($"[ScreenCapture] {state}");
                    if (state.StartsWith("error:")) PublishCaptureState(HostMessage.CaptureError, state.Substring("error:".Length));
                    break;
            }
        }

        // ───────── Commands ─────────

        private void HandleCommandJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            if (Encoding.UTF8.GetByteCount(json) > _maxCommandBytes)
            {
                Log("[DataChannel] Command dropped — message too large.");
                return;
            }

            var command = RemoteCommand.FromJson(json);
            if (command == null || string.IsNullOrEmpty(command.commandType))
            {
                Log("[DataChannel] Failed to parse command JSON.");
                // A requestId may still be recoverable so the controller is not left waiting.
                if (command != null && _ackCommands && !string.IsNullOrEmpty(command.requestId))
                    TrySendRaw(HostMessage.Ack(command, HostMessage.AckRejected, "invalid-command").ToJson());
                return;
            }

            Log($"[DataChannel] Received: {command}");
            _commandReceivedChannel?.Raise(command);

            // "dispatched" only means parsed and raised. Handler outcomes are CommandProcessor's
            // business; apps report them through 'state' topics.
            if (_ackCommands && !string.IsNullOrEmpty(command.requestId))
                TrySendRaw(HostMessage.Ack(command, HostMessage.AckDispatched).ToJson());
        }

        // ───────── Teardown ─────────

        private void EndSession(string reason, bool notifyController = true)
        {
            if (!HasSession) return;

            Log($"[WebRTC] Session ended — reason: {reason}");

            // Always stop: a capture start may still be pending (consent alert) even if IsCapturing is false.
            VisionScreenCapture.StopCapture();
            VisionProNativeBridge.ClosePeer();
            // Events the old peer already queued (answer, candidates) must not leak into the next session.
            VisionProNativeBridge.DiscardQueuedPeerEvents();

            if (notifyController && _signaling != null && _signaling.IsConnected)
                _signaling.SendDisconnect(_controllerId, reason);

            bool wasConnected = _connected;
            _controllerId = null;
            _sessionId = null;
            _connected = false;
            _disconnectedDeadline = 0;
            _captureState = HostMessage.CaptureStopped;
            _captureDetail = string.Empty;

            if (wasConnected) _onClientDisconnectedChannel?.Raise();

            _signaling?.SetStatus("available");
            Log("[Discovery] Host available again.");
        }

        private void Log(string message) => _logChannel?.Raise(message);
    }
}
