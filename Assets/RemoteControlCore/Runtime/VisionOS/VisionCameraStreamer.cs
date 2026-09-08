using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Streams what the app renders, for hosts no system capture API can see.
    ///
    /// <para>A fully immersive Unity app on visionOS renders through Compositor Services, and no
    /// system capture API sees that composition — ReplayKit captures the app's window, which such an
    /// app never draws into, so the browser receives a steady stream of uniformly dark frames. This
    /// component sidesteps the system capture entirely: it renders a spectator camera into a
    /// <see cref="RenderTexture"/>, reads it back asynchronously and hands the pixels to the native
    /// WebRTC video source (<c>VPR_PushFrameBGRA</c>).</para>
    ///
    /// <para>Put it anywhere in the host scene. <c>VisionProWebRtcHost → Capture Backend</c> on
    /// <c>Auto</c> picks this path automatically once the component is present.</para>
    ///
    /// <para><b>What it costs, and what keeps it cheap.</b> The Vision Pro must not slow down for the
    /// sake of a preview, so nothing here runs at display rate:</para>
    /// <list type="bullet">
    /// <item>The camera is disabled and rendered <i>on demand</i>, only on the frames that are
    /// actually streamed — at 15 fps that is a quarter of the work a 60 fps camera would do.</item>
    /// <item>It renders at the stream resolution, so the encoder never rescales. 960x540 is a
    /// quarter of the pixels of 1080p and less than half of 720p.</item>
    /// <item>No HDR, no MSAA, no post-processing, no depth texture, and shadows off by default.</item>
    /// <item>One readback in flight at a time; a frame is dropped rather than queued.</item>
    /// <item>Vertical flip, when needed, reverses the row order in the copy the native side makes
    /// anyway, instead of costing a full-screen blit.</item>
    /// </list>
    ///
    /// <para>Resolution, frame rate and bitrate all come from <see cref="NetworkConfig"/>; the
    /// defaults there (960x540, 15 fps, 1200 kbit/s) are chosen for the headset's sake, and this
    /// component says so in the log if they are raised past what a preview needs.</para>
    /// </summary>
    [AddComponentMenu("SuperAnretan/Remote Control/Vision Camera Streamer")]
    public class VisionCameraStreamer : MonoBehaviour
    {
        /// <summary>Pixels-per-second budget (width x height x fps) above which the cost is flagged.</summary>
        private const long FrameBudgetPixelsPerSecond = 960L * 540L * 20L;

        [Header("Config")]
        [Tooltip("Frame size, rate and bitrate come from here. Leave empty to stream 960x540 at 15 fps.")]
        [SerializeField] private NetworkConfig _networkConfig;

        [Header("Source")]
        [Tooltip("Optional. Its camera settings are copied — never hijacked, this component always " +
                 "renders its own camera, so your scene camera is left alone.")]
        [SerializeField] private Camera _sourceCamera;

        [Tooltip("Optional. Pose to follow every streamed frame. Defaults to the source camera, " +
                 "then to Camera.main — so the operator sees roughly what the wearer sees.")]
        [SerializeField] private Transform _followTarget;

        [Tooltip("Vertical field of view when no source camera is assigned.")]
        [Range(30f, 120f)]
        [SerializeField] private float _fieldOfView = 70f;

        [Tooltip("Layers to render. Excluding what the operator does not need is the cheapest saving " +
                 "available here.")]
        [SerializeField] private LayerMask _cullingMask = ~0;

        [Tooltip("Background when no source camera is assigned. A solid colour is cheaper than a skybox.")]
        [SerializeField] private Color _backgroundColor = new Color(0.05f, 0.06f, 0.08f, 1f);

        [Header("Cost")]
        [Tooltip("Shadows in the streamed view. Off is noticeably cheaper and rarely missed in a preview.")]
        [SerializeField] private bool _renderShadows;

        [Tooltip("Turn on if the received picture is upside down. Free — it only reverses the row " +
                 "order of a copy the native side makes anyway.")]
        [SerializeField] private bool _flipVertically;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        private Camera _spectator;
        private RenderTexture _renderTarget;
        private NativeArray<byte> _pixels;
        private bool _pixelsAllocated;
        private bool _streaming;
        private bool _readbackPending;
        private bool _hooked;
        private int _width;
        private int _height;
        private int _fps;
        private float _nextFrameAt;
        private int _framesPushed;
        private bool _readbackFailureLogged;

        /// <summary>True while frames are being rendered and pushed to the video source.</summary>
        public bool IsStreaming => _streaming;

        /// <summary>Frames handed to the native video source since streaming started.</summary>
        public int FramesPushed => _framesPushed;

        private void OnEnable()
        {
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

            // Multiples of 16 keep the encoder happy and avoid a padded CVPixelBuffer stride.
            _width = Snap16(_networkConfig != null ? _networkConfig.VideoWidth : 960);
            _height = Snap16(_networkConfig != null ? _networkConfig.VideoHeight : 540);
            _fps = Mathf.Clamp(_networkConfig != null ? _networkConfig.VideoFps : 15, 1, 60);

            EnsureResources();
            _streaming = true;
            _framesPushed = 0;
            _nextFrameAt = 0f;
            _readbackFailureLogged = false;

            Log($"[Video] Streaming the app's own rendering: {_width}x{_height} @ {_fps} fps.");
            if ((long)_width * _height * _fps > FrameBudgetPixelsPerSecond)
            {
                Log($"[Video] NOTE — {_width}x{_height} @ {_fps} fps is a heavy preview for a Vision Pro. " +
                    "960x540 @ 15 fps costs the headset far less and is plenty for remote control.");
            }
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
            if (_pixelsAllocated) { _pixels.Dispose(); _pixelsAllocated = false; }
        }

        private static int Snap16(int value) => Mathf.Max(64, value - (value % 16));

        private void EnsureResources()
        {
            if (_renderTarget == null)
            {
                _renderTarget = new RenderTexture(_width, _height, 24, RenderTextureFormat.BGRA32,
                                                  RenderTextureReadWrite.sRGB)
                {
                    name = "RemoteSpectator",
                    antiAliasing = 1,
                    useMipMap = false,
                    autoGenerateMips = false,
                    useDynamicScale = false
                };
                _renderTarget.Create();
            }

            if (!_pixelsAllocated)
            {
                _pixels = new NativeArray<byte>(_width * _height * 4, Allocator.Persistent,
                                                NativeArrayOptions.UninitializedMemory);
                _pixelsAllocated = true;
            }

            if (_spectator == null)
            {
                var go = new GameObject("RemoteSpectatorCamera") { hideFlags = HideFlags.DontSave };
                go.transform.SetParent(transform, false);
                _spectator = go.AddComponent<Camera>();
            }

            // Copy the assigned camera's setup rather than taking it over, so the scene is untouched.
            if (_sourceCamera != null)
            {
                _spectator.CopyFrom(_sourceCamera);
            }
            else
            {
                _spectator.clearFlags = CameraClearFlags.SolidColor;
                _spectator.backgroundColor = _backgroundColor;
                _spectator.cullingMask = _cullingMask;
                _spectator.fieldOfView = _fieldOfView;
            }

            // Rendered on demand only, and never to the display: everything below is about not
            // paying for a preview twice.
            _spectator.enabled = false;
            _spectator.stereoTargetEye = StereoTargetEyeMask.None;
            _spectator.targetTexture = _renderTarget;
            _spectator.aspect = (float)_width / _height;
            _spectator.allowHDR = false;
            _spectator.allowMSAA = false;
            _spectator.allowDynamicResolution = false;
            _spectator.useOcclusionCulling = true;
            _spectator.depthTextureMode = DepthTextureMode.None;

            ConfigureScriptableRenderPipelineCamera();
        }

        /// <summary>
        /// Turns off post-processing, in-pipeline anti-aliasing and (by default) shadows on the
        /// spectator camera. Done by reflection on purpose: those knobs live on URP's
        /// <c>UniversalAdditionalCameraData</c>, and this package must not take a hard dependency on
        /// a render pipeline the consumer may not have installed. Runs once per stream start.
        /// </summary>
        private void ConfigureScriptableRenderPipelineCamera()
        {
            if (GraphicsSettings.currentRenderPipeline == null) return;

            var type = Type.GetType("UnityEngine.Rendering.Universal.UniversalAdditionalCameraData, " +
                                    "Unity.RenderPipelines.Universal.Runtime");
            if (type == null) return;

            var data = _spectator.GetComponent(type) ?? _spectator.gameObject.AddComponent(type);
            if (data == null) return;

            SetMember(data, "renderPostProcessing", false);
            SetMember(data, "renderShadows", _renderShadows);
            SetMember(data, "antialiasing", 0);      // AntialiasingMode.None
            SetMember(data, "allowXRRendering", false);
        }

        private static void SetMember(object target, string name, object value)
        {
            var property = target.GetType().GetProperty(name);
            if (property == null || !property.CanWrite) return;
            try
            {
                object converted = property.PropertyType.IsEnum
                    ? Enum.ToObject(property.PropertyType, value)
                    : Convert.ChangeType(value, property.PropertyType);
                property.SetValue(target, converted);
            }
            catch (Exception)
            {
                // A pipeline version without this knob is not worth failing a stream over.
            }
        }

        private void LateUpdate()
        {
            if (!_streaming || _readbackPending || _spectator == null) return;

            if (Time.unscaledTime < _nextFrameAt) return;
            _nextFrameAt = Time.unscaledTime + 1f / _fps;

            var pose = _followTarget != null ? _followTarget
                     : _sourceCamera != null ? _sourceCamera.transform
                     : Camera.main != null ? Camera.main.transform
                     : null;
            if (pose != null) _spectator.transform.SetPositionAndRotation(pose.position, pose.rotation);

            RenderSpectator();

            _readbackPending = true;
            AsyncGPUReadback.RequestIntoNativeArray(ref _pixels, _renderTarget, 0, OnReadbackComplete);
        }

        /// <summary>
        /// Renders the one frame about to be streamed. <c>Camera.Render()</c> is a legacy-pipeline
        /// call, so under any scriptable render pipeline the frame goes through a render request.
        /// </summary>
        private void RenderSpectator()
        {
            if (GraphicsSettings.currentRenderPipeline != null)
            {
                var request = new RenderPipeline.StandardRequest { destination = _renderTarget };
                if (RenderPipeline.SupportsRenderRequest(_spectator, request))
                {
                    RenderPipeline.SubmitRenderRequest(_spectator, request);
                    return;
                }
            }

            _spectator.Render();
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
            VisionProNativeBridge.PushFrameBGRA(data, _width, _height, _width * 4,
                                                _flipVertically, timestampNs);

            if (_framesPushed++ == 0) Log($"[Video] First spectator frame pushed ({_width}x{_height}).");
        }

        private void Log(string message)
        {
            if (_logChannel != null) _logChannel.Raise(message);
            else Debug.Log(message);
        }
    }
}
