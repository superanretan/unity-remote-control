using System.Text;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Vision Pro host session coordinator — the WebRTC counterpart of <see cref="TransportHost"/>.
    ///
    ///   signaling offer ──► native RTCPeerConnection (answer) ──► DataChannel open
    ///        │                                                        │
    ///        │                                          start screen capture → video track
    ///        ▼
    ///   DataChannel JSON ──► RemoteCommand.FromJson ──► CommandReceivedChannel.Raise ──► CommandProcessor
    ///
    /// Handlers, targets and registries are untouched: they only see the CommandReceivedChannel.
    /// After any disconnect the capture stops, the peer is closed and the host stays discoverable.
    /// </summary>
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

        [Header("Capture")]
        [Tooltip("Auto = ScreenCaptureKit on visionOS 27+, otherwise ReplayKit.")]
        [SerializeField] private VisionProNativeBridge.CaptureBackend _captureBackend = VisionProNativeBridge.CaptureBackend.Auto;

        [Tooltip("Start screen capture as soon as the DataChannel opens (the system consent UI appears on first use).")]
        [SerializeField] private bool _autoStartCapture = true;

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

        public bool HasSession => _controllerId != null;
        public bool IsConnected => _connected;

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
            VisionProNativeBridge.SetCaptureBackend(_captureBackend);

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
                    if (_autoStartCapture)
                    {
                        Log("[ScreenCapture] Starting capture...");
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

        private void OnCaptureState(string state)
        {
            switch (state)
            {
                case "started": Log("[Video] Screen capture started — streaming."); break;
                case "stopped": Log("[Video] Screen capture stopped."); break;
                default:        Log($"[ScreenCapture] {state}"); break;
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
                return;
            }

            Log($"[DataChannel] Received: {command}");
            _commandReceivedChannel?.Raise(command);
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

            if (wasConnected) _onClientDisconnectedChannel?.Raise();

            _signaling?.SetStatus("available");
            Log("[Discovery] Host available again.");
        }

        private void Log(string message) => _logChannel?.Raise(message);
    }
}
