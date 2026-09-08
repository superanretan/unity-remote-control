using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    // Thin C# façade over WebGLRemoteBridge.jslib. Browser callbacks are queued and dispatched on
    // the main thread by PumpEvents. Outside a WebGL player every call is a no-op.
    public static class WebGLRemoteBridge
    {
        // (eventType, payload) — see the .jslib header for the event list.
        public static event Action<string, string> OnEvent;

        private delegate void EventCallback(IntPtr type, IntPtr payload);

        private static readonly Queue<(string type, string payload)> _queue = new();
        private static bool _initialized;
        // Keep the delegate alive for the lifetime of the app — JS holds its function-table index.
        private static EventCallback _callback;

#if UNITY_WEBGL && !UNITY_EDITOR
        public static bool IsSupported => true;

        [DllImport("__Internal")] private static extern void WebGLRemote_Init(EventCallback cb);
        [DllImport("__Internal")] private static extern void WebGLRemote_ConnectSignaling(string url);
        [DllImport("__Internal")] private static extern void WebGLRemote_DisconnectSignaling();
        [DllImport("__Internal")] private static extern int  WebGLRemote_IsSignalingOpen();
        [DllImport("__Internal")] private static extern int  WebGLRemote_RequestDeviceList();
        [DllImport("__Internal")] private static extern void WebGLRemote_Connect(string deviceId, string iceServersJson);
        [DllImport("__Internal")] private static extern void WebGLRemote_Disconnect();
        [DllImport("__Internal")] private static extern int  WebGLRemote_IsConnected();
        [DllImport("__Internal")] private static extern int  WebGLRemote_SendData(string message);
        [DllImport("__Internal")] private static extern int  WebGLRemote_HasVideo();
        [DllImport("__Internal")] private static extern int  WebGLRemote_GetVideoWidth();
        [DllImport("__Internal")] private static extern int  WebGLRemote_GetVideoHeight();
        [DllImport("__Internal")] private static extern int  WebGLRemote_UpdateTexture(int textureId, int width, int height);
        [DllImport("__Internal")] private static extern void WebGLRemote_SetOverlay(int enabled);
#else
        public static bool IsSupported => false;

        private static void WebGLRemote_Init(EventCallback cb) { }
        private static void WebGLRemote_ConnectSignaling(string url) { }
        private static void WebGLRemote_DisconnectSignaling() { }
        private static int  WebGLRemote_IsSignalingOpen() => 0;
        private static int  WebGLRemote_RequestDeviceList() => 0;
        private static void WebGLRemote_Connect(string deviceId, string iceServersJson) { }
        private static void WebGLRemote_Disconnect() { }
        private static int  WebGLRemote_IsConnected() => 0;
        private static int  WebGLRemote_SendData(string message) => 0;
        private static int  WebGLRemote_HasVideo() => 0;
        private static int  WebGLRemote_GetVideoWidth() => 0;
        private static int  WebGLRemote_GetVideoHeight() => 0;
        private static int  WebGLRemote_UpdateTexture(int textureId, int width, int height) => 0;
        private static void WebGLRemote_SetOverlay(int enabled) { }
#endif

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _callback = OnNativeEvent;
            WebGLRemote_Init(_callback);
        }

        [MonoPInvokeCallback(typeof(EventCallback))]
        private static void OnNativeEvent(IntPtr type, IntPtr payload)
        {
            string t = Marshal.PtrToStringUTF8(type) ?? string.Empty;
            string p = Marshal.PtrToStringUTF8(payload) ?? string.Empty;
            _queue.Enqueue((t, p));
        }

        public static void PumpEvents()
        {
            while (_queue.Count > 0)
            {
                var (t, p) = _queue.Dequeue();
                try { OnEvent?.Invoke(t, p); }
                catch (Exception e) { Debug.LogException(e); }
            }
        }

        // ───────── signaling ─────────
        public static void ConnectSignaling(string url) => WebGLRemote_ConnectSignaling(url);
        public static void DisconnectSignaling() => WebGLRemote_DisconnectSignaling();
        public static bool IsSignalingOpen => WebGLRemote_IsSignalingOpen() != 0;
        public static bool RequestDeviceList() => WebGLRemote_RequestDeviceList() != 0;

        // ───────── peer ─────────
        public static void Connect(string deviceId, string iceServersJson) => WebGLRemote_Connect(deviceId, iceServersJson);
        public static void Disconnect() => WebGLRemote_Disconnect();
        public static bool IsConnected => WebGLRemote_IsConnected() != 0;
        public static bool SendData(string message) => WebGLRemote_SendData(message) != 0;

        // ───────── video ─────────
        public static bool HasVideo => WebGLRemote_HasVideo() != 0;
        public static int VideoWidth => WebGLRemote_GetVideoWidth();
        public static int VideoHeight => WebGLRemote_GetVideoHeight();

        // width/height must be the size the texture was allocated with: Unity's WebGL2 textures are
        // immutable and only accept a same-size texSubImage2D, so a mismatched frame is skipped.
        public static bool UpdateTexture(IntPtr nativeTexturePtr, int width, int height) =>
            WebGLRemote_UpdateTexture(nativeTexturePtr.ToInt32(), width, height) != 0;

        public static void SetOverlay(bool enabled) => WebGLRemote_SetOverlay(enabled ? 1 : 0);
    }
}
