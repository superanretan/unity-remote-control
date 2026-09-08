using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using AOT;
using UnityEngine;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// P/Invoke façade over the visionOS native plugin (<c>Plugins/visionOS/*.mm</c>):
    /// native WebRTC peer (LiveKitWebRTC / WebRTC.xcframework) + screen capture (ReplayKit, or
    /// ScreenCaptureKit on visionOS 27+).
    ///
    /// Native events arrive on WebRTC / capture threads. They are copied into a thread-safe queue
    /// and re-raised on the Unity main thread from <see cref="PumpEvents"/>.
    ///
    /// Event types: answer(sdp), ice-candidate(json), connection-state(state),
    /// datachannel-open, datachannel-closed, datachannel-message(text),
    /// capture-started, capture-stopped, capture-error(msg), log(msg), error(msg)
    ///
    /// Outside a visionOS device build every call is a no-op that reports "unavailable",
    /// so the host scene can run in the Editor (signaling + discovery still work).
    /// </summary>
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
        /// <summary>True only inside a visionOS device build.</summary>
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
        [DllImport("__Internal")] private static extern void VPR_PushFrameBGRA(IntPtr data, int width, int height, int stride, long timestampNs);
#else
        /// <summary>True only inside a visionOS device build.</summary>
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
        private static void VPR_PushFrameBGRA(IntPtr data, int width, int height, int stride, long timestampNs) { }

        private static void Emit(string type, string payload) => _queue.Enqueue((type, payload));
#endif

        /// <summary>Which native capture API to use. Auto = ScreenCaptureKit when the OS has it, else ReplayKit.</summary>
        /// <summary>
        /// Where the streamed frames come from.
        /// <para><b>UnityCamera</b> is the only backend that works for a fully immersive app: Unity
        /// renders through Compositor Services, which neither ReplayKit nor ScreenCaptureKit can see,
        /// so a system capture yields uniformly dark frames. It needs a
        /// <see cref="VisionCameraStreamer"/> in the scene and shows no consent alert.</para>
        /// </summary>
        public enum CaptureBackend { Auto = 0, ReplayKit = 1, ScreenCaptureKit = 2, UnityCamera = 3 }

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

        /// <summary>Drain native events on the main thread. Call from Update.</summary>
        public static void PumpEvents()
        {
            while (_queue.TryDequeue(out var e))
            {
                try { OnEvent?.Invoke(e.type, e.payload); }
                catch (Exception ex) { Debug.LogException(ex); }
            }
        }

        /// <summary>
        /// Drops queued peer-related events (answer, ice-candidate, connection/datachannel state) that
        /// belong to a peer that has just been closed, keeping capture/log events. Main thread only.
        /// </summary>
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

        /// <summary>
        /// Hands one BGRA32 frame (top-left origin) to the native video source. <paramref name="data"/>
        /// is read synchronously and never retained, so the buffer may be reused straight after.
        /// Used by <see cref="VisionCameraStreamer"/> for the UnityCamera backend.
        /// </summary>
        public static void PushFrameBGRA(IntPtr data, int width, int height, int stride, long timestampNs) =>
            VPR_PushFrameBGRA(data, width, height, stride, timestampNs);
    }
}
