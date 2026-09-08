// WebRtcHostBridge.mm
// Native WebRTC host peer for the Vision Pro: answers the browser's offer, exposes the browser's
// "commands" DataChannel to C#, and owns the dormant video source that screen capture feeds.
//
// WebRTC library: LiveKitWebRTC.xcframework (SPM, adds visionOS slices; classes prefixed LKRTC…)
// or a plain WebRTC.xcframework built for visionOS (classes RTC…). RC_RTC(Name) resolves the prefix.

#import "VisionProRemoteBridge.h"

#if __has_include(<LiveKitWebRTC/LiveKitWebRTC.h>)
  #import <LiveKitWebRTC/LiveKitWebRTC.h>
  #define RC_RTC(Name) LKRTC##Name
#elif __has_include(<WebRTC/WebRTC.h>)
  #import <WebRTC/WebRTC.h>
  #define RC_RTC(Name) RTC##Name
#else
  #error "No WebRTC framework found. Add the LiveKitWebRTC Swift package (see WEBGL_VISIONOS_REMOTE.md) or a visionOS WebRTC.xcframework."
#endif

#import <mach/mach_time.h>

// ───────── shared state ─────────

static VPR_EventCallback g_callback = NULL;
static int g_videoWidth = 1280;
static int g_videoHeight = 720;
static int g_videoFps = 24;
static int g_videoBitrateKbps = 2500;

void VPR_Emit(NSString *type, NSString *payload)
{
    VPR_EventCallback cb = g_callback;
    if (!cb) return;
    cb(type.UTF8String, payload ? payload.UTF8String : "");
}

static void VPR_Log(NSString *message)
{
    VPR_Emit(@"log", message);
}

void VPR_GetVideoConfig(int *width, int *height, int *fps)
{
    if (width) *width = g_videoWidth;
    if (height) *height = g_videoHeight;
    if (fps) *fps = g_videoFps;
}

// ───────── peer host ─────────

@interface RCPeerHost : NSObject <RC_RTC(PeerConnectionDelegate), RC_RTC(DataChannelDelegate)>
@property (nonatomic, strong) RC_RTC(PeerConnectionFactory) *factory;
@property (nonatomic, strong) RC_RTC(PeerConnection) *peer;
@property (nonatomic, strong) RC_RTC(DataChannel) *dataChannel;
@property (nonatomic, strong) RC_RTC(VideoSource) *videoSource;
@property (nonatomic, strong) RC_RTC(VideoCapturer) *videoCapturer;
@property (nonatomic, strong) RC_RTC(VideoTrack) *videoTrack;
@property (nonatomic, strong) RC_RTC(RtpSender) *videoSender;
@property (atomic, assign) BOOL connected;
@property (atomic, assign) BOOL remoteDescriptionSet;
@property (nonatomic, strong) NSMutableArray<RC_RTC(IceCandidate) *> *pendingCandidates;
@property (atomic, assign) int64_t lastFrameNs;
- (BOOL)createPeerWithIceServers:(NSArray<RC_RTC(IceServer) *> *)iceServers;
- (void)handleRemoteOffer:(NSString *)sdp;
- (void)addIceCandidate:(NSString *)candidate sdpMid:(NSString *)sdpMid sdpMLineIndex:(int)index;
- (void)applyIceCandidate:(RC_RTC(IceCandidate) *)ice toPeer:(RC_RTC(PeerConnection) *)pc;
- (BOOL)sendText:(NSString *)text;
- (void)close;
- (void)pushSampleBuffer:(CMSampleBufferRef)sampleBuffer;
- (void)pushFrameBGRA:(const void *)data width:(int)width height:(int)height
               stride:(int)stride timestampNs:(int64_t)timestampNs;
@end

static RCPeerHost *g_host = nil;
static NSObject *g_lock = nil;

static RC_RTC(PeerConnectionFactory) *RCMakeFactory(void)
{
    RC_RTC(DefaultVideoEncoderFactory) *encoder = [[RC_RTC(DefaultVideoEncoderFactory) alloc] init];
    // Prefer hardware H.264 — cheapest for the Vision Pro and universally decodable in browsers.
    for (RC_RTC(VideoCodecInfo) *codec in [RC_RTC(DefaultVideoEncoderFactory) supportedCodecs]) {
        if ([codec.name isEqualToString:@"H264"]) { encoder.preferredCodec = codec; break; }
    }
    RC_RTC(DefaultVideoDecoderFactory) *decoder = [[RC_RTC(DefaultVideoDecoderFactory) alloc] init];
    return [[RC_RTC(PeerConnectionFactory) alloc] initWithEncoderFactory:encoder decoderFactory:decoder];
}

static NSString *RCConnectionStateName(RC_RTC(PeerConnectionState) state)
{
    switch (state) {
        case RC_RTC(PeerConnectionStateNew):          return @"new";
        case RC_RTC(PeerConnectionStateConnecting):   return @"connecting";
        case RC_RTC(PeerConnectionStateConnected):    return @"connected";
        case RC_RTC(PeerConnectionStateDisconnected): return @"disconnected";
        case RC_RTC(PeerConnectionStateFailed):       return @"failed";
        case RC_RTC(PeerConnectionStateClosed):       return @"closed";
    }
    return @"unknown";
}

// ───────── BGRA frame pool (app-rendered frames) ─────────
// One pool, recreated when the frame size changes. IOSurface-backed so WebRTC can hand the buffer
// to the encoder without another copy.

static CVPixelBufferPoolRef g_bgraPool = NULL;
static int g_bgraPoolW = 0;
static int g_bgraPoolH = 0;

static CVPixelBufferRef RCTakePooledBGRA(int width, int height)
{
    if (!g_bgraPool || g_bgraPoolW != width || g_bgraPoolH != height) {
        if (g_bgraPool) { CVPixelBufferPoolRelease(g_bgraPool); g_bgraPool = NULL; }
        NSDictionary *bufferAttrs = @{
            (id)kCVPixelBufferPixelFormatTypeKey:   @(kCVPixelFormatType_32BGRA),
            (id)kCVPixelBufferWidthKey:             @(width),
            (id)kCVPixelBufferHeightKey:            @(height),
            (id)kCVPixelBufferIOSurfacePropertiesKey: @{},
            (id)kCVPixelBufferMetalCompatibilityKey:  @YES,
        };
        NSDictionary *poolAttrs = @{ (id)kCVPixelBufferPoolMinimumBufferCountKey: @3 };
        if (CVPixelBufferPoolCreate(kCFAllocatorDefault,
                                    (__bridge CFDictionaryRef)poolAttrs,
                                    (__bridge CFDictionaryRef)bufferAttrs,
                                    &g_bgraPool) != kCVReturnSuccess) {
            g_bgraPool = NULL;
            return NULL;
        }
        g_bgraPoolW = width;
        g_bgraPoolH = height;
    }

    CVPixelBufferRef buffer = NULL;
    if (CVPixelBufferPoolCreatePixelBuffer(kCFAllocatorDefault, g_bgraPool, &buffer) != kCVReturnSuccess) {
        return NULL;
    }
    return buffer;
}

@implementation RCPeerHost

- (instancetype)init
{
    if ((self = [super init])) {
        _factory = RCMakeFactory();
    }
    return self;
}

- (BOOL)createPeerWithIceServers:(NSArray<RC_RTC(IceServer) *> *)iceServers
{
    [self close];

    RC_RTC(Configuration) *config = [[RC_RTC(Configuration) alloc] init];
    config.sdpSemantics = RC_RTC(SdpSemanticsUnifiedPlan);
    config.continualGatheringPolicy = RC_RTC(ContinualGatheringPolicyGatherContinually);
    if (iceServers.count > 0) {
        // One RTCIceServer per NetworkConfig entry — TURN entries carry their own username/credential.
        config.iceServers = iceServers;
    }

    RC_RTC(MediaConstraints) *constraints =
        [[RC_RTC(MediaConstraints) alloc] initWithMandatoryConstraints:nil optionalConstraints:nil];

    self.peer = [self.factory peerConnectionWithConfiguration:config constraints:constraints delegate:self];
    if (!self.peer) {
        VPR_Emit(@"error", @"peer-connection-create-failed");
        return NO;
    }

    // Dormant video source: negotiated now, fed by screen capture only after the DataChannel opens.
    self.videoSource = [self.factory videoSource];
    [self.videoSource adaptOutputFormatToWidth:g_videoWidth height:g_videoHeight fps:g_videoFps];
    self.videoCapturer = [[RC_RTC(VideoCapturer) alloc] initWithDelegate:self.videoSource];
    self.videoTrack = [self.factory videoTrackWithSource:self.videoSource trackId:@"unity-screen-video"];
    self.connected = NO;
    self.remoteDescriptionSet = NO;
    self.pendingCandidates = [NSMutableArray array];
    self.lastFrameNs = 0;

    VPR_Log(@"[WebRTC] Peer created.");
    return YES;
}

- (void)attachVideoTrack
{
    RC_RTC(PeerConnection) *pc = self.peer;
    if (!pc || !self.videoTrack) return;

    // The browser offered a recvonly video m-line: bind our track to that transceiver so the
    // answer becomes sendonly without renegotiation.
    for (RC_RTC(RtpTransceiver) *transceiver in pc.transceivers) {
        if (transceiver.mediaType != RC_RTC(RtpMediaTypeVideo)) continue;
        if (transceiver.sender.track != nil) continue;
        [transceiver.sender setTrack:self.videoTrack];
        NSError *error = nil;
        [transceiver setDirection:RC_RTC(RtpTransceiverDirectionSendOnly) error:&error];
        self.videoSender = transceiver.sender;
        VPR_Log(@"[WebRTC] Video track attached to offered transceiver.");
        return;
    }

    self.videoSender = [pc addTrack:self.videoTrack streamIds:@[@"unity-screen"]];
    VPR_Log(@"[WebRTC] Video track added via addTrack (no recvonly transceiver in offer).");
}

- (void)applyEncodingParameters
{
    RC_RTC(RtpSender) *sender = self.videoSender;
    if (!sender) return;
    RC_RTC(RtpParameters) *params = sender.parameters;
    for (RC_RTC(RtpEncodingParameters) *encoding in params.encodings) {
        encoding.maxBitrateBps = @(g_videoBitrateKbps * 1000);
        encoding.maxFramerate = @(g_videoFps);
    }
    sender.parameters = params;
}

- (void)handleRemoteOffer:(NSString *)sdp
{
    RC_RTC(PeerConnection) *pc = self.peer;
    if (!pc) { VPR_Emit(@"error", @"no-peer"); return; }

    RC_RTC(SessionDescription) *offer = [[RC_RTC(SessionDescription) alloc] initWithType:RC_RTC(SdpTypeOffer) sdp:sdp];
    RC_RTC(MediaConstraints) *constraints =
        [[RC_RTC(MediaConstraints) alloc] initWithMandatoryConstraints:nil optionalConstraints:nil];

    __weak RCPeerHost *weakSelf = self;
    [pc setRemoteDescription:offer completionHandler:^(NSError *error) {
        RCPeerHost *self = weakSelf;
        if (!self || self.peer != pc) return;
        if (error) { VPR_Emit(@"error", [@"set-remote-description: " stringByAppendingString:error.localizedDescription]); return; }

        // Browser candidates that trickled in before the offer was installed can be applied now.
        NSArray *queued;
        @synchronized (self) {
            self.remoteDescriptionSet = YES;
            queued = [self.pendingCandidates copy];
            [self.pendingCandidates removeAllObjects];
        }
        for (RC_RTC(IceCandidate) *ice in queued) [self applyIceCandidate:ice toPeer:pc];

        [self attachVideoTrack];

        [pc answerForConstraints:constraints completionHandler:^(RC_RTC(SessionDescription) *answer, NSError *error) {
            if (self.peer != pc) return;
            if (error || !answer) { VPR_Emit(@"error", [@"create-answer: " stringByAppendingString:error.localizedDescription ?: @"nil"]); return; }

            [pc setLocalDescription:answer completionHandler:^(NSError *error) {
                if (self.peer != pc) return;
                if (error) { VPR_Emit(@"error", [@"set-local-description: " stringByAppendingString:error.localizedDescription]); return; }
                [self applyEncodingParameters];
                VPR_Log(@"[WebRTC] SDP answer created.");
                VPR_Emit(@"answer", answer.sdp);
            }];
        }];
    }];
}

- (void)addIceCandidate:(NSString *)candidate sdpMid:(NSString *)sdpMid sdpMLineIndex:(int)index
{
    RC_RTC(PeerConnection) *pc = self.peer;
    if (!pc || candidate.length == 0) return;
    RC_RTC(IceCandidate) *ice = [[RC_RTC(IceCandidate) alloc] initWithSdp:candidate
                                                             sdpMLineIndex:(index >= 0 ? index : 0)
                                                                    sdpMid:(sdpMid.length ? sdpMid : nil)];
    @synchronized (self) {
        if (!self.remoteDescriptionSet) {
            // libwebrtc rejects candidates until a remote description exists — hold them.
            [self.pendingCandidates addObject:ice];
            return;
        }
    }
    [self applyIceCandidate:ice toPeer:pc];
}

- (void)applyIceCandidate:(RC_RTC(IceCandidate) *)ice toPeer:(RC_RTC(PeerConnection) *)pc
{
    [pc addIceCandidate:ice completionHandler:^(NSError *error) {
        if (error) VPR_Log([@"[WebRTC] addIceCandidate failed: " stringByAppendingString:error.localizedDescription]);
    }];
}

- (BOOL)sendText:(NSString *)text
{
    RC_RTC(DataChannel) *dc = self.dataChannel;
    if (!dc || dc.readyState != RC_RTC(DataChannelStateOpen)) return NO;
    RC_RTC(DataBuffer) *buffer = [[RC_RTC(DataBuffer) alloc] initWithData:[text dataUsingEncoding:NSUTF8StringEncoding] isBinary:NO];
    return [dc sendData:buffer];
}

- (void)close
{
    RC_RTC(DataChannel) *dc = self.dataChannel;
    RC_RTC(PeerConnection) *pc = self.peer;
    self.dataChannel = nil;
    self.peer = nil;
    self.videoSender = nil;
    self.connected = NO;
    @synchronized (self) {
        self.remoteDescriptionSet = NO;
        [self.pendingCandidates removeAllObjects];
    }

    if (dc) { dc.delegate = nil; [dc close]; }
    if (pc) { [pc close]; VPR_Log(@"[WebRTC] Peer closed."); }

    self.videoTrack = nil;
    self.videoCapturer = nil;
    self.videoSource = nil;
}

- (void)pushSampleBuffer:(CMSampleBufferRef)sampleBuffer
{
    RC_RTC(VideoSource) *source = self.videoSource;
    RC_RTC(VideoCapturer) *capturer = self.videoCapturer;
    if (!source || !capturer || !self.connected) return;

    CVPixelBufferRef pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer);
    if (!pixelBuffer) return;

    CMTime pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer);
    int64_t timestampNs;
    if (CMTIME_IS_NUMERIC(pts)) {
        timestampNs = (int64_t)(CMTimeGetSeconds(pts) * 1e9);
    } else {
        static mach_timebase_info_data_t timebase = {0, 0};
        if (timebase.denom == 0) mach_timebase_info(&timebase);
        timestampNs = (int64_t)(mach_absolute_time() * timebase.numer / timebase.denom);
    }

    // Frame pacing: ReplayKit delivers up to 60-90 fps; drop frames above the configured rate
    // before they hit the encoder to keep the Vision Pro cool.
    int64_t minIntervalNs = (int64_t)(1e9 / (double)MAX(g_videoFps, 1)) * 9 / 10;
    if (self.lastFrameNs != 0 && timestampNs - self.lastFrameNs < minIntervalNs) return;
    self.lastFrameNs = timestampNs;

    RC_RTC(CVPixelBuffer) *rtcBuffer = [[RC_RTC(CVPixelBuffer) alloc] initWithPixelBuffer:pixelBuffer];
    RC_RTC(VideoFrame) *frame = [[RC_RTC(VideoFrame) alloc] initWithBuffer:rtcBuffer
                                                                 rotation:RC_RTC(VideoRotation_0)
                                                              timeStampNs:timestampNs];
    [source capturer:capturer didCaptureVideoFrame:frame];
}

- (void)pushFrameBGRA:(const void *)data width:(int)width height:(int)height
               stride:(int)stride timestampNs:(int64_t)timestampNs
{
    RC_RTC(VideoSource) *source = self.videoSource;
    RC_RTC(VideoCapturer) *capturer = self.videoCapturer;
    if (!source || !capturer || !self.connected) return;
    if (!data || width <= 0 || height <= 0 || stride < width * 4) return;

    // Same pacing as the ReplayKit path: never hand the encoder more than the configured rate.
    int64_t minIntervalNs = (int64_t)(1e9 / (double)MAX(g_videoFps, 1)) * 9 / 10;
    if (self.lastFrameNs != 0 && timestampNs - self.lastFrameNs < minIntervalNs) return;
    self.lastFrameNs = timestampNs;

    CVPixelBufferRef pixelBuffer = RCTakePooledBGRA(width, height);
    if (!pixelBuffer) return;

    if (CVPixelBufferLockBaseAddress(pixelBuffer, 0) != kCVReturnSuccess) {
        CVPixelBufferRelease(pixelBuffer);
        return;
    }
    uint8_t *dst = (uint8_t *)CVPixelBufferGetBaseAddress(pixelBuffer);
    size_t dstStride = CVPixelBufferGetBytesPerRow(pixelBuffer);
    const uint8_t *src = (const uint8_t *)data;
    if (dst) {
        size_t rowBytes = (size_t)width * 4;
        for (int y = 0; y < height; y++) {
            memcpy(dst + (size_t)y * dstStride, src + (size_t)y * (size_t)stride, rowBytes);
        }
    }
    CVPixelBufferUnlockBaseAddress(pixelBuffer, 0);

    if (dst) {
        RC_RTC(CVPixelBuffer) *rtcBuffer = [[RC_RTC(CVPixelBuffer) alloc] initWithPixelBuffer:pixelBuffer];
        RC_RTC(VideoFrame) *frame = [[RC_RTC(VideoFrame) alloc] initWithBuffer:rtcBuffer
                                                                     rotation:RC_RTC(VideoRotation_0)
                                                                  timeStampNs:timestampNs];
        [source capturer:capturer didCaptureVideoFrame:frame];
    }
    CVPixelBufferRelease(pixelBuffer);
}

// ───────── RTCPeerConnectionDelegate ─────────

- (void)peerConnection:(RC_RTC(PeerConnection) *)peerConnection didChangeSignalingState:(RC_RTC(SignalingState))stateChanged {}
- (void)peerConnection:(RC_RTC(PeerConnection) *)peerConnection didAddStream:(RC_RTC(MediaStream) *)stream {}
- (void)peerConnection:(RC_RTC(PeerConnection) *)peerConnection didRemoveStream:(RC_RTC(MediaStream) *)stream {}
- (void)peerConnectionShouldNegotiate:(RC_RTC(PeerConnection) *)peerConnection {}
- (void)peerConnection:(RC_RTC(PeerConnection) *)peerConnection didChangeIceConnectionState:(RC_RTC(IceConnectionState))newState {}
- (void)peerConnection:(RC_RTC(PeerConnection) *)peerConnection didChangeIceGatheringState:(RC_RTC(IceGatheringState))newState {}
- (void)peerConnection:(RC_RTC(PeerConnection) *)peerConnection didRemoveIceCandidates:(NSArray<RC_RTC(IceCandidate) *> *)candidates {}

- (void)peerConnection:(RC_RTC(PeerConnection) *)peerConnection didChangeConnectionState:(RC_RTC(PeerConnectionState))newState
{
    if (peerConnection != self.peer) return;
    self.connected = (newState == RC_RTC(PeerConnectionStateConnected));
    VPR_Emit(@"connection-state", RCConnectionStateName(newState));
}

- (void)peerConnection:(RC_RTC(PeerConnection) *)peerConnection didGenerateIceCandidate:(RC_RTC(IceCandidate) *)candidate
{
    if (peerConnection != self.peer) return;
    NSDictionary *json = @{
        @"candidate": candidate.sdp ?: @"",
        @"sdpMid": candidate.sdpMid ?: @"",
        @"sdpMLineIndex": @(candidate.sdpMLineIndex)
    };
    NSData *data = [NSJSONSerialization dataWithJSONObject:json options:0 error:nil];
    if (data) VPR_Emit(@"ice-candidate", [[NSString alloc] initWithData:data encoding:NSUTF8StringEncoding]);
}

- (void)peerConnection:(RC_RTC(PeerConnection) *)peerConnection didOpenDataChannel:(RC_RTC(DataChannel) *)dataChannel
{
    if (peerConnection != self.peer) return;
    self.dataChannel = dataChannel;
    dataChannel.delegate = self;
    VPR_Log([NSString stringWithFormat:@"[DataChannel] '%@' announced by controller.", dataChannel.label]);
    if (dataChannel.readyState == RC_RTC(DataChannelStateOpen)) VPR_Emit(@"datachannel-open", dataChannel.label);
}

// ───────── RTCDataChannelDelegate ─────────

- (void)dataChannelDidChangeState:(RC_RTC(DataChannel) *)dataChannel
{
    if (dataChannel != self.dataChannel) return;
    switch (dataChannel.readyState) {
        case RC_RTC(DataChannelStateOpen):   VPR_Emit(@"datachannel-open", dataChannel.label); break;
        case RC_RTC(DataChannelStateClosed): VPR_Emit(@"datachannel-closed", dataChannel.label); break;
        default: break;
    }
}

- (void)dataChannel:(RC_RTC(DataChannel) *)dataChannel didReceiveMessageWithBuffer:(RC_RTC(DataBuffer) *)buffer
{
    if (dataChannel != self.dataChannel || buffer.isBinary) return;
    NSString *text = [[NSString alloc] initWithData:buffer.data encoding:NSUTF8StringEncoding];
    if (text) VPR_Emit(@"datachannel-message", text);
}

@end

// ───────── C API ─────────

extern "C" {

void VPR_Initialize(VPR_EventCallback callback)
{
    g_callback = callback;
    if (!g_lock) g_lock = [[NSObject alloc] init];
    VPR_Log(@"[WebRTC] Native bridge initialized.");
}

void VPR_SetVideoConfig(int width, int height, int fps, int bitrateKbps)
{
    g_videoWidth = width > 0 ? width : 1280;
    g_videoHeight = height > 0 ? height : 720;
    g_videoFps = fps > 0 ? fps : 24;
    g_videoBitrateKbps = bitrateKbps > 0 ? bitrateKbps : 2500;
}

// Parses NetworkConfig.IceServersJson():
//   2.x: [{"urls":["stun:..."]},{"urls":["turn:host:3478?transport=udp"],"username":"u","credential":"c"}]
//   1.x: ["stun:...", "turn:..."]   (still accepted; no credentials)
static NSArray<RC_RTC(IceServer) *> *RCParseIceServers(const char *iceServersJson)
{
    NSMutableArray<RC_RTC(IceServer) *> *servers = [NSMutableArray array];
    if (!iceServersJson) return servers;
    NSString *jsonString = [NSString stringWithUTF8String:iceServersJson];
    NSData *data = [jsonString dataUsingEncoding:NSUTF8StringEncoding];
    id parsed = data ? [NSJSONSerialization JSONObjectWithData:data options:0 error:nil] : nil;
    if (![parsed isKindOfClass:[NSArray class]]) return servers;

    for (id item in (NSArray *)parsed) {
        if ([item isKindOfClass:[NSString class]]) {
            if ([item length]) [servers addObject:[[RC_RTC(IceServer) alloc] initWithURLStrings:@[item]]];
            continue;
        }
        if (![item isKindOfClass:[NSDictionary class]]) continue;
        NSDictionary *entry = (NSDictionary *)item;

        NSMutableArray<NSString *> *urls = [NSMutableArray array];
        id rawUrls = entry[@"urls"];
        if ([rawUrls isKindOfClass:[NSString class]] && [rawUrls length]) [urls addObject:rawUrls];
        else if ([rawUrls isKindOfClass:[NSArray class]])
            for (id u in (NSArray *)rawUrls) if ([u isKindOfClass:[NSString class]] && [u length]) [urls addObject:u];
        if (urls.count == 0) continue;

        NSString *username = [entry[@"username"] isKindOfClass:[NSString class]] ? entry[@"username"] : @"";
        NSString *credential = [entry[@"credential"] isKindOfClass:[NSString class]] ? entry[@"credential"] : @"";
        if (username.length > 0 || credential.length > 0)
            [servers addObject:[[RC_RTC(IceServer) alloc] initWithURLStrings:urls username:username credential:credential]];
        else
            [servers addObject:[[RC_RTC(IceServer) alloc] initWithURLStrings:urls]];
    }
    return servers;
}

int VPR_CreatePeer(const char *iceServersJson)
{
    if (!g_lock) g_lock = [[NSObject alloc] init];
    NSArray<RC_RTC(IceServer) *> *iceServers = RCParseIceServers(iceServersJson);

    @synchronized (g_lock) {
        if (!g_host) g_host = [[RCPeerHost alloc] init];
        return [g_host createPeerWithIceServers:iceServers] ? 1 : 0;
    }
}

void VPR_HandleRemoteOffer(const char *sdp)
{
    if (!sdp) return;
    RCPeerHost *host = g_host;
    [host handleRemoteOffer:[NSString stringWithUTF8String:sdp]];
}

void VPR_AddIceCandidate(const char *candidate, const char *sdpMid, int sdpMLineIndex)
{
    if (!candidate) return;
    RCPeerHost *host = g_host;
    [host addIceCandidate:[NSString stringWithUTF8String:candidate]
                   sdpMid:(sdpMid ? [NSString stringWithUTF8String:sdpMid] : nil)
            sdpMLineIndex:sdpMLineIndex];
}

int VPR_SendData(const char *message)
{
    if (!message) return 0;
    RCPeerHost *host = g_host;
    return [host sendText:[NSString stringWithUTF8String:message]] ? 1 : 0;
}

void VPR_ClosePeer(void)
{
    if (!g_lock) return;
    @synchronized (g_lock) {
        [g_host close];
    }
}

int VPR_IsPeerConnected(void)
{
    RCPeerHost *host = g_host;
    return (host && host.connected) ? 1 : 0;
}

void VPR_PushSampleBuffer(CMSampleBufferRef sampleBuffer)
{
    RCPeerHost *host = g_host;
    if (host && sampleBuffer) [host pushSampleBuffer:sampleBuffer];
}

void VPR_PushFrameBGRA(const void *data, int width, int height, int stride, int64_t timestampNs)
{
    RCPeerHost *host = g_host;
    if (host) [host pushFrameBGRA:data width:width height:height stride:stride timestampNs:timestampNs];
}

} // extern "C"
