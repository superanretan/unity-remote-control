// ScreenCaptureBridge.mm
// Captures what the Unity app renders on visionOS and hands every CMSampleBuffer to the
// native WebRTC video source (VPR_PushSampleBuffer). Frames never touch C#.
//
// Backends (public APIs only, no passthrough, no enterprise entitlement):
//   • ReplayKit  RPScreenRecorder.startCapture  — visionOS 1.0+, deprecated in visionOS 27
//                (system consent alert on first start; captures the app's own rendered content)
//   • ScreenCaptureKit  SCContentSharingPicker.presentPickerForCurrentApplication + SCStream
//                — visionOS 27+ (beta). Requires Xcode 27 SDK. Enable with the preprocessor define
//                VPR_ENABLE_SCREENCAPTUREKIT=1 (set by RemoteControlVisionOSPostProcessor).
//                Shows Apple's system picker limited to this app's windows/layers.
//   • UnityCamera — not a system capture at all: the app renders a spectator camera and pushes the
//                pixels through VPR_PushFrameBGRA. Measured: a fully immersive Unity app renders
//                through Compositor Services, ReplayKit captures its (empty) window instead, and
//                the browser receives a steady stream of uniformly dark frames. This backend is the
//                only one that streams what an immersive app actually draws.
//
// Apple never lets a remote controller silently start recording: the host user must accept the
// system consent UI the first time. Capture therefore starts only after the DataChannel is open
// and the host app is in the foreground.

#import "VisionProRemoteBridge.h"
#import <ReplayKit/ReplayKit.h>

#if !defined(VPR_ENABLE_SCREENCAPTUREKIT)
  #define VPR_ENABLE_SCREENCAPTUREKIT 0
#endif

#if VPR_ENABLE_SCREENCAPTUREKIT && __has_include(<ScreenCaptureKit/ScreenCaptureKit.h>)
  #import <ScreenCaptureKit/ScreenCaptureKit.h>
  #define VPR_HAS_SCK 1
#else
  #define VPR_HAS_SCK 0
#endif

typedef NS_ENUM(int, VPRCaptureBackend) {
    VPRCaptureBackendAuto = 0,
    VPRCaptureBackendReplayKit = 1,
    VPRCaptureBackendScreenCaptureKit = 2,
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

// ═════════════════════════════ ScreenCaptureKit (visionOS 27+) ═════════════════════════════

#if VPR_HAS_SCK

API_AVAILABLE(visionos(27.0), ios(27.0))
@interface VPRScreenCaptureKitSource : NSObject <SCContentSharingPickerObserver, SCStreamDelegate, SCStreamOutput>
@property (nonatomic, strong) SCStream *stream;
@property (nonatomic, strong) dispatch_queue_t sampleQueue;
- (void)start;
- (void)stop;
@end

@implementation VPRScreenCaptureKitSource

- (instancetype)init
{
    if ((self = [super init])) {
        _sampleQueue = dispatch_queue_create("com.superanretan.remotecontrol.sck", DISPATCH_QUEUE_SERIAL);
    }
    return self;
}

- (void)start
{
    SCContentSharingPicker *picker = SCContentSharingPicker.sharedPicker;
    [picker addObserver:self];
    picker.maximumStreamCount = @1;
    picker.active = YES;
    VPR_CaptureLog(@"[ScreenCapture] ScreenCaptureKit picker (current application only)...");
    [picker presentPickerForCurrentApplication];
}

- (void)stop
{
    SCContentSharingPicker *picker = SCContentSharingPicker.sharedPicker;
    picker.active = NO;
    [picker removeObserver:self];

    SCStream *stream = self.stream;
    self.stream = nil;
    if (!stream) {
        g_capturing = NO;
        g_activeBackend = 0;
        return;
    }
    [stream stopCaptureWithCompletionHandler:^(NSError *error) {
        dispatch_async(dispatch_get_main_queue(), ^{
            g_capturing = NO;
            g_activeBackend = 0;
            VPR_Emit(@"capture-stopped", @"screencapturekit");
        });
    }];
}

// SCContentSharingPickerObserver

- (void)contentSharingPicker:(SCContentSharingPicker *)picker didUpdateWithFilter:(SCContentFilter *)filter forStream:(SCStream *)stream
{
    if (stream != nil) return; // update for an already running stream

    int width, height, fps;
    VPR_GetVideoConfig(&width, &height, &fps);

    SCStreamConfiguration *config = [[SCStreamConfiguration alloc] init];
    config.width = width;
    config.height = height;
    config.minimumFrameInterval = CMTimeMake(1, MAX(fps, 1));
    config.pixelFormat = kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange;
    config.queueDepth = 3;
    config.showsCursor = NO;

    SCStream *newStream = [[SCStream alloc] initWithFilter:filter configuration:config delegate:self];
    NSError *error = nil;
    if (![newStream addStreamOutput:self type:SCStreamOutputTypeScreen sampleHandlerQueue:self.sampleQueue error:&error]) {
        g_starting = NO;
        VPR_Emit(@"capture-error", [@"sck-add-output: " stringByAppendingString:error.localizedDescription ?: @"unknown"]);
        return;
    }

    self.stream = newStream;
    uint32_t generation = g_startGeneration;
    [newStream startCaptureWithCompletionHandler:^(NSError *error) {
        dispatch_async(dispatch_get_main_queue(), ^{
            g_starting = NO;
            if (error) {
                self.stream = nil;
                VPR_Emit(@"capture-error", [@"sck-start: " stringByAppendingString:error.localizedDescription]);
                return;
            }
            if (generation != g_startGeneration) {
                VPR_CaptureLog(@"[ScreenCapture] SCK start completed after cancellation — stopping immediately.");
                [self stop];
                return;
            }
            g_capturing = YES;
            g_activeBackend = VPRCaptureBackendScreenCaptureKit;
            VPR_Emit(@"capture-started", @"screencapturekit");
        });
    }];
}

- (void)contentSharingPicker:(SCContentSharingPicker *)picker didCancelForStream:(SCStream *)stream
{
    g_starting = NO;
    VPR_Emit(@"capture-error", @"sck-picker-cancelled");
}

- (void)contentSharingPickerStartDidFailWithError:(NSError *)error
{
    g_starting = NO;
    VPR_Emit(@"capture-error", [@"sck-picker: " stringByAppendingString:error.localizedDescription]);
}

// SCStreamOutput

- (void)stream:(SCStream *)stream didOutputSampleBuffer:(CMSampleBufferRef)sampleBuffer ofType:(SCStreamOutputType)type
{
    if (type != SCStreamOutputTypeScreen || !g_capturing) return;
    if (!CMSampleBufferGetImageBuffer(sampleBuffer)) return; // idle/blank frames carry no image
    VPR_PushSampleBuffer(sampleBuffer);
}

// SCStreamDelegate

- (void)stream:(SCStream *)stream didStopWithError:(NSError *)error
{
    dispatch_async(dispatch_get_main_queue(), ^{
        self.stream = nil;
        g_capturing = NO;
        g_activeBackend = 0;
        VPR_Emit(@"capture-error", [@"sck-stopped: " stringByAppendingString:error.localizedDescription ?: @"unknown"]);
        VPR_Emit(@"capture-stopped", @"screencapturekit");
    });
}

@end

static VPRScreenCaptureKitSource *g_sckSource API_AVAILABLE(visionos(27.0), ios(27.0)) = nil;

static BOOL VPR_ScreenCaptureKitAvailable(void)
{
    if (@available(visionOS 27.0, iOS 27.0, *)) {
        return SCContentSharingPicker.sharedPicker.isAvailable;
    }
    return NO;
}

#else

static BOOL VPR_ScreenCaptureKitAvailable(void) { return NO; }

#endif // VPR_HAS_SCK

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

        BOOL useSck = (g_backend == VPRCaptureBackendScreenCaptureKit) ||
                      (g_backend == VPRCaptureBackendAuto && VPR_ScreenCaptureKitAvailable());

#if VPR_HAS_SCK
        if (useSck) {
            if (@available(visionOS 27.0, iOS 27.0, *)) {
                g_starting = YES;
                if (!g_sckSource) g_sckSource = [[VPRScreenCaptureKitSource alloc] init];
                [g_sckSource start];
                return;
            }
        }
#else
        if (g_backend == VPRCaptureBackendScreenCaptureKit) {
            VPR_CaptureLog(@"[ScreenCapture] ScreenCaptureKit not compiled in (VPR_ENABLE_SCREENCAPTUREKIT=0) — falling back to ReplayKit.");
        }
#endif
        (void)useSck;
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
#if VPR_HAS_SCK
        if (g_activeBackend == VPRCaptureBackendScreenCaptureKit) {
            if (@available(visionOS 27.0, iOS 27.0, *)) { [g_sckSource stop]; }
            return;
        }
#endif
        VPR_StopReplayKit();
    });
}

int VPR_IsCapturing(void)
{
    return g_capturing ? 1 : 0;
}

} // extern "C"
