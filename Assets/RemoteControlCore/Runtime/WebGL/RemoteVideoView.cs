using System;
using UnityEngine;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Shows the incoming WebRTC video on a <see cref="RawImage"/>.
    /// Pipeline: MediaStream → HTMLVideoElement → gl.texImage2D into the Texture2D created here
    /// (see <c>WebGLRemote_UpdateTexture</c> in the .jslib) → RawImage.
    /// The texture is (re)created whenever the incoming resolution changes.
    /// Set <see cref="_useHtmlOverlay"/> to debug with the raw video element drawn over the canvas.
    /// </summary>
    public class RemoteVideoView : MonoBehaviour
    {
        [Header("UI")]
        [Tooltip("RawImage that receives the video texture.")]
        [SerializeField] private RawImage _target;

        [Tooltip("Optional. Its aspect ratio is updated to the video aspect.")]
        [SerializeField] private AspectRatioFitter _aspectFitter;

        [Header("Event Channels — Input")]
        [SerializeField] private VoidEventChannel _onDisconnectedChannel;

        [Header("Logging")]
        [SerializeField] private StringEventChannel _logChannel;

        [Header("Debug")]
        [Tooltip("Fallback/debug: also show the raw HTMLVideoElement on top of the WebGL canvas.")]
        [SerializeField] private bool _useHtmlOverlay;

        private Texture2D _texture;
        private IntPtr _nativePtr;
        private bool _hasVideo;

        public bool HasVideo => _hasVideo;

        private void OnEnable()
        {
            WebGLRemoteBridge.Initialize();
            WebGLRemoteBridge.OnEvent += OnBridgeEvent;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised += ClearVideo;

            if (WebGLRemoteBridge.IsSupported) WebGLRemoteBridge.SetOverlay(_useHtmlOverlay);
            if (_target != null) _target.enabled = false;
        }

        private void OnDisable()
        {
            WebGLRemoteBridge.OnEvent -= OnBridgeEvent;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised -= ClearVideo;
            if (WebGLRemoteBridge.IsSupported) WebGLRemoteBridge.SetOverlay(false);
            ClearVideo();
        }

        private void Update()
        {
            WebGLRemoteBridge.PumpEvents();
            if (!_hasVideo || !WebGLRemoteBridge.HasVideo) return;

            int w = WebGLRemoteBridge.VideoWidth;
            int h = WebGLRemoteBridge.VideoHeight;
            if (w <= 0 || h <= 0) return;

            if (_texture == null || _texture.width != w || _texture.height != h)
                CreateTexture(w, h);

            if (_nativePtr == IntPtr.Zero) return;

            WebGLRemoteBridge.UpdateTexture(_nativePtr, _texture.width, _texture.height);
        }

        private void OnBridgeEvent(string type, string payload)
        {
            switch (type)
            {
                case "video-started":
                    _hasVideo = true;
                    if (_target != null) _target.enabled = true;
                    Log($"[Video] Started ({payload}).");
                    break;

                case "video-size":
                    Log($"[Video] Resolution changed ({payload}).");
                    break;

                case "video-stopped":
                    Log("[Video] Stopped.");
                    ClearVideo();
                    break;
            }
        }

        private void CreateTexture(int w, int h)
        {
            // Drop the old id first: Destroy() frees the GL texture and the browser may hand the
            // same id back for something else, so an upload must never target a stale pointer.
            _nativePtr = IntPtr.Zero;
            if (_texture != null) Destroy(_texture);

            _texture = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "RemoteVideo",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            _texture.Apply(false, false);           // allocate the GL texture before grabbing its id
            _nativePtr = _texture.GetNativeTexturePtr();

            if (_target != null)
            {
                _target.texture = _texture;
                _target.uvRect = new Rect(0, 0, 1, 1);
            }
            if (_aspectFitter != null) _aspectFitter.aspectRatio = (float)w / h;

            Log($"[Video] Texture {w}x{h} created.");
        }

        private void ClearVideo()
        {
            _hasVideo = false;
            if (_target != null)
            {
                _target.texture = null;
                _target.enabled = false;
            }
            if (_texture != null)
            {
                Destroy(_texture);
                _texture = null;
            }
            _nativePtr = IntPtr.Zero;
        }

        private void Log(string message) => _logChannel?.Raise(message);
    }
}
