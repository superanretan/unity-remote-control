using System;

namespace SuperAnretan.RemoteControl
{
    /// <summary>
    /// Simple C# interface over the native visionOS screen capture.
    /// Frames never reach C#: the native side pushes them straight into the WebRTC video source.
    ///
    /// <code>
    /// VisionScreenCapture.StartCapture();
    /// VisionScreenCapture.StopCapture();
    /// </code>
    ///
    /// Backend: ReplayKit <c>RPScreenRecorder.startCapture</c> (visionOS 1.0+, deprecated in 27)
    /// or ScreenCaptureKit <c>SCContentSharingPicker.presentForCurrentApplication</c> (visionOS 27+).
    /// Both capture only what the app renders — no passthrough, no enterprise entitlement.
    /// The first start shows Apple's consent UI; it cannot be bypassed.
    /// </summary>
    public static class VisionScreenCapture
    {
        /// <summary>"started" | "stopped" | "error:&lt;message&gt;" — raised on the main thread.</summary>
        public static event Action<string> OnStateChanged;

        private static bool _hooked;

        public static bool IsSupported => VisionProNativeBridge.IsSupported;
        public static bool IsCapturing => VisionProNativeBridge.IsCapturing;

        public static void StartCapture()
        {
            Hook();
            VisionProNativeBridge.StartCapture();
        }

        public static void StopCapture()
        {
            Hook();
            VisionProNativeBridge.StopCapture();
        }

        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            VisionProNativeBridge.Initialize();
            VisionProNativeBridge.OnEvent += (type, payload) =>
            {
                switch (type)
                {
                    case "capture-started": OnStateChanged?.Invoke("started"); break;
                    case "capture-stopped": OnStateChanged?.Invoke("stopped"); break;
                    case "capture-error":   OnStateChanged?.Invoke("error:" + payload); break;
                }
            };
        }
    }
}
