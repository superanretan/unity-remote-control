using System;
using UnityEngine;
using UnityEngine.UI;

namespace SuperAnretan.RemoteControl
{
    // Shows the incoming WebRTC video on a RawImage.
    // MediaStream -> HTMLVideoElement -> GL upload into this Texture2D (WebGLRemote_UpdateTexture
    // in the .jslib) -> RawImage. The texture is recreated when the incoming resolution changes.
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

        [Tooltip("Diagnostics: fill a freshly created texture with magenta and log why frames are not " +
                 "arriving. Magenta on screen proves the RawImage really shows this texture, so a missing " +
                 "picture is an upload problem, not a UI one.")]
        [SerializeField] private bool _diagnostics = true;

        private Texture2D _texture;
        private IntPtr _nativePtr;
        private bool _hasVideo;
        private int _uploads;
        private float _nextDiagAt;

        public bool HasVideo => _hasVideo;

        private void OnEnable()
        {
            WebGLRemoteBridge.Initialize();
            WebGLRemoteBridge.OnEvent += OnBridgeEvent;
            if (_onDisconnectedChannel != null) _onDisconnectedChannel.OnRaised += ClearVideo;

            // Under diagnostics the raw <video> is drawn over the canvas too: a picture there above
            // a magenta quad means WebRTC delivers frames and only the GL upload is broken.
            if (WebGLRemoteBridge.IsSupported) WebGLRemoteBridge.SetOverlay(_useHtmlOverlay || _diagnostics);
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

            if (WebGLRemoteBridge.UpdateTexture(_nativePtr, _texture.width, _texture.height))
            {
                if (_uploads++ == 0)
                    Log($"[Video] First frame uploaded ({_texture.width}x{_texture.height}).");
                return;
            }

            // Nothing has ever landed in this texture: report what both sides believe.
            if (_diagnostics && _uploads == 0 && Time.unscaledTime >= _nextDiagAt)
            {
                _nextDiagAt = Time.unscaledTime + 2f;
                Log($"[Video] No frame uploaded yet — texture {_texture.width}x{_texture.height}, " +
                    $"glId={_nativePtr.ToInt64()}, bridge reports {w}x{h}.");
            }
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
            // A fresh Texture2D holds uninitialized pixels (flat grey), so clear it. Magenta under
            // diagnostics proves the RawImage is bound to this texture.
            var fill = _diagnostics ? new Color32(255, 0, 255, 255) : new Color32(0, 0, 0, 255);
            var pixels = new Color32[w * h];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fill;
            _texture.SetPixels32(pixels);
            _texture.Apply(false, false);           // allocate the GL texture before grabbing its id
            _nativePtr = _texture.GetNativeTexturePtr();
            _uploads = 0;
            _nextDiagAt = 0f;

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
            _uploads = 0;
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
