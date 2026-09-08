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
    /// Backend: ReplayKit <c>RPScreenRecorder.startCapture</c> (visionOS 1.0+, deprecated in 27),
    /// which captures the app's window and therefore only suits a windowed app; the first start
    /// shows Apple's consent UI and it cannot be bypassed. An immersive app renders through
    /// Compositor Services, which no system capture API sees — it streams its own spectator camera
    /// instead (<see cref="VisionCameraStreamer"/>), and then this class only starts and stops the
    /// hand-off. No passthrough in any case, and no enterprise entitlement.
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
