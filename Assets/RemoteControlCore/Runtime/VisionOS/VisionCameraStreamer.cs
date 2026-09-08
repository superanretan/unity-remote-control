using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Streams what the app renders, for hosts that ReplayKit cannot capture.
    ///
    /// <para>A fully immersive Unity app on visionOS renders through Compositor Services. Neither
    /// ReplayKit nor ScreenCaptureKit sees that composition — they capture the app's window, which
    /// such an app never draws into, so the browser receives a steady stream of uniformly dark
    /// frames. This component sidesteps the system capture entirely: it renders a spectator camera
    /// into a <see cref="RenderTexture"/>, reads it back asynchronously and hands the pixels to the
    /// native WebRTC video source (<c>VPR_PushFrameBGRA</c>).</para>
    ///
    /// <para>Put it anywhere in the host scene and set
    /// <c>VisionProWebRtcHost → Capture Backend</c> to <c>UnityCamera</c>. It follows
    /// <see cref="_followTarget"/> (the main camera by default, so the operator sees roughly what
    /// the wearer sees) or renders a camera you assign to <see cref="_sourceCamera"/>. Never assign
    /// the XR camera itself: giving it a <c>targetTexture</c> breaks stereo rendering, which is why
    /// this component drives a clone.</para>
    ///
    /// <para>Cost: one extra scene render plus one GPU readback per streamed frame, both at the
    /// configured resolution and frame rate, not at display rate.</para>
    /// </summary>
    [AddComponentMenu("SuperAnretan/Remote Control/Vision Camera Streamer")]
    public class VisionCameraStreamer : MonoBehaviour
    {
        [Header("Config")]
        [Tooltip("Frame size and rate come from here. Leave empty to stream 1280x720 at 24 fps.")]
        [SerializeField] private NetworkConfig _networkConfig;

        [Header("Source")]
        [Tooltip("Optional. A camera to render as-is. Leave empty to clone the followed transform's view.")]
        [SerializeField] private Camera _sourceCamera;

        [Tooltip("Optional. Pose the spectator camera copies every frame. Defaults to Camera.main.")]
        [SerializeField] private Transform _followTarget;

        [Tooltip("Vertical field of view of the spectator camera when no source camera is assigned.")]
        [Range(30f, 120f)]
        [SerializeField] private float _fieldOfView = 70f;

        [Tooltip("Layers the spectator camera renders when no source camera is assigned.")]
        [SerializeField] private LayerMask _cullingMask = ~0;

        [Tooltip("Background of the spectator camera when no source camera is assigned.")]
        [SerializeField] private Color _backgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);

        [Header("Output")]
        [Tooltip("Turn on if the received picture is upside down. Costs one full-screen blit per frame.")]
        [SerializeField] private bool _flipVertically;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        private Camera _spectator;
        private RenderTexture _renderTarget;
        private RenderTexture _flipTarget;
        private NativeArray<byte> _pixels;
        private bool _pixelsAllocated;
        private bool _streaming;
        private bool _readbackPending;
        private bool _hooked;
        private int _width;
        private int _height;
        private float _nextFrameAt;
        private int _framesPushed;
        private bool _readbackFailureLogged;

        /// <summary>True while frames are being rendered and pushed to the video source.</summary>
        public bool IsStreaming => _streaming;

        /// <summary>Frames handed to the native video source since streaming started.</summary>
        public int FramesPushed => _framesPushed;

        private void OnEnable()
        {
            _width = _networkConfig != null ? Mathf.Max(64, _networkConfig.VideoWidth) : 1280;
            _height = _networkConfig != null ? Mathf.Max(64, _networkConfig.VideoHeight) : 720;

            VisionProNativeBridge.Initialize();
            if (!_hooked)
            {
                _hooked = true;
                VisionProNativeBridge.OnEvent += OnNativeEvent;
            }
        }

        private void OnDisable()
        {
            if (_hooked)
            {
                _hooked = false;
                VisionProNativeBridge.OnEvent -= OnNativeEvent;
            }
            StopStreaming();
        }

        private void OnNativeEvent(string type, string payload)
        {
            switch (type)
            {
                // Only this backend concerns us: a ReplayKit start must not spin up a second source.
                case "capture-started" when payload == "unity-camera":
                    StartStreaming();
                    break;

                case "capture-stopped":
                case "capture-error":
                    StopStreaming();
                    break;
            }
        }

        private void StartStreaming()
        {
            if (_streaming) return;

            EnsureResources();
            _streaming = true;
            _framesPushed = 0;
            _nextFrameAt = 0f;
            _readbackFailureLogged = false;
            Log($"[Video] Streaming the app's own rendering ({_width}x{_height}).");
        }

        private void StopStreaming()
        {
            if (!_streaming && !_pixelsAllocated && _renderTarget == null) return;
            _streaming = false;

            // A readback still in flight writes into _pixels, so it must finish before the array goes.
            if (_readbackPending) AsyncGPUReadback.WaitAllRequests();
            _readbackPending = false;

            if (_spectator != null)
            {
                _spectator.targetTexture = null;
                Destroy(_spectator.gameObject);
                _spectator = null;
            }
            if (_renderTarget != null) { _renderTarget.Release(); Destroy(_renderTarget); _renderTarget = null; }
            if (_flipTarget != null) { _flipTarget.Release(); Destroy(_flipTarget); _flipTarget = null; }
            if (_pixelsAllocated) { _pixels.Dispose(); _pixelsAllocated = false; }
        }

        private void EnsureResources()
        {
            if (_renderTarget == null)
            {
                _renderTarget = new RenderTexture(_width, _height, 24, RenderTextureFormat.BGRA32)
                {
                    name = "RemoteSpectator",
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false
                };
                _renderTarget.Create();
            }

            if (_flipVertically && _flipTarget == null)
            {
                _flipTarget = new RenderTexture(_renderTarget.descriptor) { name = "RemoteSpectatorFlipped" };
                _flipTarget.Create();
            }

            if (!_pixelsAllocated)
            {
                _pixels = new NativeArray<byte>(_width * _height * 4, Allocator.Persistent,
                                                NativeArrayOptions.UninitializedMemory);
                _pixelsAllocated = true;
            }

            if (_sourceCamera != null)
            {
                _sourceCamera.targetTexture = _renderTarget;
                return;
            }

            if (_spectator == null)
            {
                var go = new GameObject("RemoteSpectatorCamera") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);
                _spectator = go.AddComponent<Camera>();
                _spectator.stereoTargetEye = StereoTargetEyeMask.None;   // never render this one to the display
                _spectator.enabled = false;                              // driven by explicit Render() calls
                _spectator.clearFlags = CameraClearFlags.SolidColor;
                _spectator.backgroundColor = _backgroundColor;
            }
            _spectator.cullingMask = _cullingMask;
            _spectator.fieldOfView = _fieldOfView;
            _spectator.aspect = (float)_width / _height;
            _spectator.targetTexture = _renderTarget;
        }

        private void LateUpdate()
        {
            if (!_streaming || _readbackPending) return;

            int fps = _networkConfig != null ? Mathf.Max(1, _networkConfig.VideoFps) : 24;
            if (Time.unscaledTime < _nextFrameAt) return;
            _nextFrameAt = Time.unscaledTime + 1f / fps;

            var target = RenderFrame();
            if (target == null) return;

            _readbackPending = true;
            AsyncGPUReadback.RequestIntoNativeArray(ref _pixels, target, 0, OnReadbackComplete);
        }

        private RenderTexture RenderFrame()
        {
            if (_sourceCamera != null)
            {
                _sourceCamera.Render();
            }
            else
            {
                if (_spectator == null) return null;
                var pose = _followTarget != null ? _followTarget : (Camera.main != null ? Camera.main.transform : null);
                if (pose != null) _spectator.transform.SetPositionAndRotation(pose.position, pose.rotation);
                _spectator.Render();
            }

            if (!_flipVertically) return _renderTarget;

            // Readback row order is platform-dependent; the blit makes it a one-checkbox fix.
            Graphics.Blit(_renderTarget, _flipTarget, new Vector2(1f, -1f), new Vector2(0f, 1f));
            return _flipTarget;
        }

        private unsafe void OnReadbackComplete(AsyncGPUReadbackRequest request)
        {
            _readbackPending = false;
            if (!_streaming || !_pixelsAllocated) return;

            if (request.hasError)
            {
                if (!_readbackFailureLogged)
                {
                    _readbackFailureLogged = true;
                    Log("[Video] GPU readback failed — the spectator frame could not be read back.");
                }
                return;
            }

            // The native side copies the rows out synchronously, so handing it the buffer is safe.
            IntPtr data = (IntPtr)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(_pixels);
            long timestampNs = (long)(Time.realtimeSinceStartupAsDouble * 1e9);
            VisionProNativeBridge.PushFrameBGRA(data, _width, _height, _width * 4, timestampNs);

            if (_framesPushed++ == 0) Log($"[Video] First spectator frame pushed ({_width}x{_height}).");
        }

        private void Log(string message)
        {
            if (_logChannel != null) _logChannel.Raise(message);
            else Debug.Log(message);
        }
    }
}
