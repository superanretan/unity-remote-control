using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    // P/Invoke façade over the visionOS native plugin (Plugins/visionOS/*.mm).
    // Native events arrive on WebRTC / capture threads, are queued and re-raised on the Unity main
    // thread from PumpEvents: answer(sdp), ice-candidate(json), connection-state(state),
    // datachannel-open/-closed/-message(text), capture-started/-stopped/-error(msg), log(msg), error(msg).
    // Outside a visionOS device build every call is a no-op reporting "unavailable".
    public static class VisionProNativeBridge
    {
        public static event Action<string, string> OnEvent;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void EventCallback(IntPtr type, IntPtr payload);

        private static readonly ConcurrentQueue<(string type, string payload)> _queue = new();
        private static bool _initialized;
        // Keep the delegate alive — the native side stores the raw function pointer.
        private static EventCallback _callback;

#if UNITY_VISIONOS && !UNITY_EDITOR
        public static bool IsSupported => true;

        [DllImport("__Internal")] private static extern void VPR_Initialize(EventCallback cb);
        [DllImport("__Internal")] private static extern void VPR_SetVideoConfig(int width, int height, int fps, int bitrateKbps);
        [DllImport("__Internal")] private static extern void VPR_SetCaptureBackend(int backend);
        [DllImport("__Internal")] private static extern int  VPR_CreatePeer(string iceServersJson);
        [DllImport("__Internal")] private static extern void VPR_HandleRemoteOffer(string sdp);
        [DllImport("__Internal")] private static extern void VPR_AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex);
        [DllImport("__Internal")] private static extern int  VPR_SendData(string message);
        [DllImport("__Internal")] private static extern void VPR_ClosePeer();
        [DllImport("__Internal")] private static extern int  VPR_IsPeerConnected();
        [DllImport("__Internal")] private static extern void VPR_StartCapture();
        [DllImport("__Internal")] private static extern void VPR_StopCapture();
        [DllImport("__Internal")] private static extern int  VPR_IsCapturing();
        [DllImport("__Internal")] private static extern void VPR_PushFrameBGRA(IntPtr data, int width, int height, int stride, int flipVertically, long timestampNs);
#else
        public static bool IsSupported => false;

        private static void VPR_Initialize(EventCallback cb) { }
        private static void VPR_SetVideoConfig(int width, int height, int fps, int bitrateKbps) { }
        private static void VPR_SetCaptureBackend(int backend) { }
        private static int  VPR_CreatePeer(string iceServersJson) { Emit("error", "native-unavailable"); return 0; }
        private static void VPR_HandleRemoteOffer(string sdp) { Emit("error", "native-unavailable"); }
        private static void VPR_AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex) { }
        private static int  VPR_SendData(string message) => 0;
        private static void VPR_ClosePeer() { }
        private static int  VPR_IsPeerConnected() => 0;
        private static void VPR_StartCapture() { Emit("capture-error", "native-unavailable"); }
        private static void VPR_StopCapture() { }
        private static int  VPR_IsCapturing() => 0;
        private static void VPR_PushFrameBGRA(IntPtr data, int width, int height, int stride, int flipVertically, long timestampNs) { }

        private static void Emit(string type, string payload) => _queue.Enqueue((type, payload));
#endif

        // UnityCamera is the only backend that works for a fully immersive app: Unity renders
        // through Compositor Services, which system capture APIs cannot see, so ReplayKit captures
        // the app's empty window. It needs a VisionCameraStreamer in the scene; Auto picks it when
        // one is present. 2 was ScreenCaptureKit and is gone.
        public enum CaptureBackend { Auto = 0, ReplayKit = 1, UnityCamera = 3 }

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            _callback = OnNativeEvent;
            VPR_Initialize(_callback);
        }

        [MonoPInvokeCallback(typeof(EventCallback))]
        private static void OnNativeEvent(IntPtr type, IntPtr payload)
        {
            // Copy immediately — the native strings are only valid for the duration of the call.
            string t = Marshal.PtrToStringUTF8(type) ?? string.Empty;
            string p = Marshal.PtrToStringUTF8(payload) ?? string.Empty;
            _queue.Enqueue((t, p));
        }

        public static void PumpEvents()
        {
            while (_queue.TryDequeue(out var e))
            {
                try { OnEvent?.Invoke(e.type, e.payload); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        // Drops queued peer events belonging to a peer that has just been closed, keeping
        // capture/log events. Main thread only.
        public static void DiscardQueuedPeerEvents()
        {
            var keep = new System.Collections.Generic.List<(string type, string payload)>();
            while (_queue.TryDequeue(out var e))
            {
                switch (e.type)
                {
                    case "answer":
                    case "ice-candidate":
                    case "connection-state":
                    case "datachannel-open":
                    case "datachannel-closed":
                    case "datachannel-message":
                        break;                      // stale — drop
                    default:
                        keep.Add(e);                // capture-*, log, error
                        break;
                }
            }
            foreach (var e in keep) _queue.Enqueue(e);
        }

        public static void SetVideoConfig(int width, int height, int fps, int bitrateKbps) => VPR_SetVideoConfig(width, height, fps, bitrateKbps);
        public static void SetCaptureBackend(CaptureBackend backend) => VPR_SetCaptureBackend((int)backend);

        public static bool CreatePeer(string iceServersJson) => VPR_CreatePeer(iceServersJson) != 0;
        public static void HandleRemoteOffer(string sdp) => VPR_HandleRemoteOffer(sdp);
        public static void AddIceCandidate(string candidate, string sdpMid, int sdpMLineIndex) => VPR_AddIceCandidate(candidate, sdpMid ?? string.Empty, sdpMLineIndex);
        public static bool SendData(string message) => VPR_SendData(message) != 0;
        public static void ClosePeer() => VPR_ClosePeer();
        public static bool IsPeerConnected => VPR_IsPeerConnected() != 0;

        public static void StartCapture() => VPR_StartCapture();
        public static void StopCapture() => VPR_StopCapture();
        public static bool IsCapturing => VPR_IsCapturing() != 0;

        // One BGRA32 frame. `data` is read synchronously and never retained, so the buffer may be
        // reused right after. flipVertically reverses the row order inside the copy the native side
        // makes anyway — cheaper than blitting the render texture.
        public static void PushFrameBGRA(IntPtr data, int width, int height, int stride,
                                         bool flipVertically, long timestampNs) =>
            VPR_PushFrameBGRA(data, width, height, stride, flipVertically ? 1 : 0, timestampNs);
    }
}
