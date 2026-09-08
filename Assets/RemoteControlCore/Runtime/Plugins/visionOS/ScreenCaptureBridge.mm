// ScreenCaptureBridge.mm
// Captures what the Unity app renders on visionOS and hands every CMSampleBuffer to the
// native WebRTC video source (VPR_PushSampleBuffer). Frames never touch C#.
//
// Backends (public APIs only, no passthrough, no enterprise entitlement):
//   • ReplayKit  RPScreenRecorder.startCapture  — visionOS 1.0+, deprecated in visionOS 27.
//                System consent alert on first start. Captures the app's *window*, so it only
//                suits a windowed app.
//   • UnityCamera — not a system capture at all: the app renders a spectator camera and pushes the
//                pixels through VPR_PushFrameBGRA. Measured: a fully immersive Unity app renders
//                through Compositor Services, ReplayKit captures its (empty) window instead, and
//                the browser receives a steady stream of uniformly dark frames. This backend is the
//                only one that streams what an immersive app actually draws.
//
// A ScreenCaptureKit backend used to live here. It was removed: it required the Xcode 27 SDK, was
// never compiled into a shipped build, and its source selection (screens / applications / windows)
// has nothing to offer an immersive app either.
//
// Apple never lets a remote controller silently start recording: the host user must accept the
// system consent UI the first time. Capture therefore starts only after the DataChannel is open
// and the host app is in the foreground.

#import "VisionProRemoteBridge.h"
#import <ReplayKit/ReplayKit.h>

typedef NS_ENUM(int, VPRCaptureBackend) {
    VPRCaptureBackendAuto = 0,
    VPRCaptureBackendReplayKit = 1,
    // 2 was ScreenCaptureKit, removed: it needs the Xcode 27 SDK, was never compiled into a build,
    // and selects screens/apps/windows — none of which an immersive Compositor Services app has.
    // A scene that still has 2 serialized falls through to ReplayKit, exactly as it did before.
    VPRCaptureBackendUnityCamera = 3,
};

static VPRCaptureBackend g_backend = VPRCaptureBackendAuto;
static BOOL g_capturing = NO;
static BOOL g_starting = NO;
static int g_activeBackend = 0;   // backend that actually started
// Bumped by every StopCapture. A start that completes with a stale generation was cancelled
// while pending (e.g. the peer disconnected during the consent alert) → stop it right away.
static uint32_t g_startGeneration = 0;

static void VPR_CaptureLog(NSString *message) { VPR_Emit(@"log", message); }

// Describes the first captured frame once: pixel format, size, and the mean of a 16x16 luma grid.
// A mean of ~0 proves the capture source itself is dark, which no amount of work downstream fixes.
static BOOL g_describedFrame = NO;

static void VPR_DescribeFirstFrame(CMSampleBufferRef sampleBuffer)
{
    if (g_describedFrame) return;
    g_describedFrame = YES;

    CVPixelBufferRef buffer = CMSampleBufferGetImageBuffer(sampleBuffer);
    if (!buffer) { VPR_CaptureLog(@"[ScreenCapture] First frame carries no image buffer."); return; }

    OSType fmt = CVPixelBufferGetPixelFormatType(buffer);
    size_t width = CVPixelBufferGetWidth(buffer);
    size_t height = CVPixelBufferGetHeight(buffer);
    size_t planes = CVPixelBufferGetPlaneCount(buffer);
    char fourcc[5] = { (char)((fmt >> 24) & 0xFF), (char)((fmt >> 16) & 0xFF),
                       (char)((fmt >> 8) & 0xFF), (char)(fmt & 0xFF), 0 };

    long mean = -1;
    if (CVPixelBufferLockBaseAddress(buffer, kCVPixelBufferLock_ReadOnly) == kCVReturnSuccess) {
        const uint8_t *base = (const uint8_t *)(planes > 0 ? CVPixelBufferGetBaseAddressOfPlane(buffer, 0)
                                                           : CVPixelBufferGetBaseAddress(buffer));
        size_t stride = planes > 0 ? CVPixelBufferGetBytesPerRowOfPlane(buffer, 0)
                                   : CVPixelBufferGetBytesPerRow(buffer);
        // 420v/420f keep luma in plane 0 at one byte per pixel; BGRA/ARGB take the first channel.
        size_t step = (fmt == kCVPixelFormatType_32BGRA || fmt == kCVPixelFormatType_32ARGB) ? 4 : 1;
        if (base && width > 16 && height > 16) {
            long sum = 0;
            for (int gy = 0; gy < 16; gy++) {
                for (int gx = 0; gx < 16; gx++) {
                    size_t x = (width  / 17) * (size_t)(gx + 1);
                    size_t y = (height / 17) * (size_t)(gy + 1);
                    sum += base[y * stride + x * step];
                }
            }
            mean = sum / 256;
        }
        CVPixelBufferUnlockBaseAddress(buffer, kCVPixelBufferLock_ReadOnly);
    }

    VPR_CaptureLog([NSString stringWithFormat:
        @"[ScreenCapture] First frame: %s %zux%zu, %zu plane(s), mean sample %ld "
        @"(near 0 means the captured surface itself is black).",
        fourcc, width, height, planes, mean]);
}

// ═════════════════════════════ ReplayKit ═════════════════════════════

#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"

static void VPR_StartReplayKit(void)
{
    RPScreenRecorder *recorder = [RPScreenRecorder sharedRecorder];
    if (!recorder.isAvailable) {
        VPR_Emit(@"capture-error", @"replaykit-unavailable");
        return;
    }

    recorder.microphoneEnabled = NO;
    recorder.cameraEnabled = NO;
    g_starting = YES;
    uint32_t generation = g_startGeneration;
    VPR_CaptureLog(@"[ScreenCapture] ReplayKit startCapture (system consent may appear)...");

    [recorder startCaptureWithHandler:^(CMSampleBufferRef sampleBuffer, RPSampleBufferType bufferType, NSError *error) {
        if (error) {
            VPR_Emit(@"capture-error", [@"replaykit: " stringByAppendingString:error.localizedDescription]);
            return;
        }
        if (bufferType == RPSampleBufferTypeVideo && g_capturing) {
            VPR_DescribeFirstFrame(sampleBuffer);
            VPR_PushSampleBuffer(sampleBuffer);
        }
    } completionHandler:^(NSError *error) {
        dispatch_async(dispatch_get_main_queue(), ^{
            g_starting = NO;
            if (error) {
                // -5801 = user declined the consent alert, -5803 = recording failed to start
                VPR_Emit(@"capture-error", [NSString stringWithFormat:@"replaykit-start-failed(%ld): %@",
                                             (long)error.code, error.localizedDescription]);
                return;
            }
            if (generation != g_startGeneration) {
                // Cancelled while the start was pending — never leave the recorder running unattended.
                VPR_CaptureLog(@"[ScreenCapture] Start completed after cancellation — stopping immediately.");
                [recorder stopCaptureWithHandler:^(NSError *stopError) {
                    (void)stopError;
                    VPR_Emit(@"capture-stopped", @"replaykit");
                }];
                return;
            }
            g_capturing = YES;
            g_activeBackend = VPRCaptureBackendReplayKit;
            VPR_Emit(@"capture-started", @"replaykit");
        });
    }];
}

static void VPR_StopReplayKit(void)
{
    RPScreenRecorder *recorder = [RPScreenRecorder sharedRecorder];
    if (!recorder.isRecording && !g_capturing) return;
    [recorder stopCaptureWithHandler:^(NSError *error) {
        dispatch_async(dispatch_get_main_queue(), ^{
            g_capturing = NO;
            g_activeBackend = 0;
            if (error) VPR_CaptureLog([@"[ScreenCapture] ReplayKit stop: " stringByAppendingString:error.localizedDescription]);
            VPR_Emit(@"capture-stopped", @"replaykit");
        });
    }];
}

#pragma clang diagnostic pop


// ═════════════════════════════ C API ═════════════════════════════

extern "C" {

void VPR_SetCaptureBackend(int backend)
{
    g_backend = (VPRCaptureBackend)backend;
}

void VPR_StartCapture(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        if (g_capturing || g_starting) return;

        if (g_backend == VPRCaptureBackendUnityCamera) {
            // No system capture and no consent alert: VisionCameraStreamer pushes rendered frames.
            g_capturing = YES;
            g_activeBackend = VPRCaptureBackendUnityCamera;
            VPR_Emit(@"capture-started", @"unity-camera");
            return;
        }

        VPR_StartReplayKit();
    });
}

void VPR_StopCapture(void)
{
    dispatch_async(dispatch_get_main_queue(), ^{
        g_startGeneration++;   // invalidates any start still waiting for consent / completion
        g_starting = NO;
        g_describedFrame = NO; // describe the first frame of the next session too
        if (g_activeBackend == VPRCaptureBackendUnityCamera) {
            g_capturing = NO;
            g_activeBackend = 0;
            VPR_Emit(@"capture-stopped", @"unity-camera");
            return;
        }
        VPR_StopReplayKit();
    });
}

int VPR_IsCapturing(void)
{
    return g_capturing ? 1 : 0;
}

} // extern "C"
