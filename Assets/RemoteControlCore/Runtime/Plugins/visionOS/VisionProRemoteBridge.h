// VisionProRemoteBridge.h
// C ABI between Unity C# (VisionProNativeBridge.cs) and the visionOS native plugin.
//
//   WebRtcHostBridge.mm     — native RTCPeerConnection, DataChannel, video source/encoder
//   ScreenCaptureBridge.mm  — ReplayKit capture (windowed apps) / UnityCamera hand-off
//
// Frames flow  capture → VPR_PushSampleBuffer → RTCVideoSource → encoder → browser
// entirely in native code. C# only sees small string events.

#pragma once

#import <Foundation/Foundation.h>
#import <CoreMedia/CoreMedia.h>
#import <CoreVideo/CoreVideo.h>

#ifdef __cplusplus
extern "C" {
#endif

/// (type, payload) — both UTF-8, valid only for the duration of the call. May be invoked on any thread.
typedef void (*VPR_EventCallback)(const char *type, const char *payload);

// ───────── lifecycle ─────────
void VPR_Initialize(VPR_EventCallback callback);
void VPR_SetVideoConfig(int width, int height, int fps, int bitrateKbps);
void VPR_SetCaptureBackend(int backend);          // 0 auto, 1 ReplayKit, 3 UnityCamera (2 was ScreenCaptureKit)

// ───────── peer ─────────
int  VPR_CreatePeer(const char *iceServersJson);  // JSON array of RTCIceServer objects {urls[],username?,credential?} (or legacy URL strings)
void VPR_HandleRemoteOffer(const char *sdp);      // → emits "answer"
void VPR_AddIceCandidate(const char *candidate, const char *sdpMid, int sdpMLineIndex);
int  VPR_SendData(const char *message);           // host → controller text over the DataChannel
void VPR_ClosePeer(void);
int  VPR_IsPeerConnected(void);

// ───────── capture ─────────
void VPR_StartCapture(void);
void VPR_StopCapture(void);
int  VPR_IsCapturing(void);

// ───────── capture: frames rendered by the app itself ─────────
// ReplayKit captures the app's *window*. A fully immersive Unity app renders through Compositor
// Services instead, and that composition is not exposed to any system capture API — the stream
// comes out uniformly dark. With backend 3 the app renders a spectator camera itself and hands the
// pixels over here.

/// One BGRA32 frame, `stride` bytes per row. `data` is only read during the call.
/// `flipVertically` copies the rows bottom-up — GPU readback row order is platform-dependent, and
/// reversing the copy costs nothing, unlike a full-screen blit on the way out.
void VPR_PushFrameBGRA(const void *data, int width, int height, int stride,
                       int flipVertically, int64_t timestampNs);

// ───────── internal (shared between the two .mm files) ─────────
void VPR_Emit(NSString *type, NSString *payload);
void VPR_PushSampleBuffer(CMSampleBufferRef sampleBuffer);
void VPR_GetVideoConfig(int *width, int *height, int *fps);

#ifdef __cplusplus
}
#endif
