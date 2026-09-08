using System;

namespace SuperAnretan.RemoteControl
{
    // Starts and stops the native visionOS capture. Frames never reach C#: the native side pushes
    // them straight into the WebRTC video source.
    public static class VisionScreenCapture
    {
        // "started" | "stopped" | "error:<message>" — raised on the main thread.
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
