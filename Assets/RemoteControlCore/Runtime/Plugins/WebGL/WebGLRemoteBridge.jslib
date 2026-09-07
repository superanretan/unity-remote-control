// WebGLRemoteBridge.jslib
// Browser side of the WebGL controller: WebSocket signaling + RTCPeerConnection + RTCDataChannel
// + incoming video track rendered into an HTMLVideoElement that is copied into a Unity texture.
//
// All events reach C# through ONE callback: cb(typeUtf8Ptr, payloadUtf8Ptr).
// Event types:
//   signaling-open, signaling-closed(reason), device-list(json {devices:[...]}),
//   connecting(deviceId), connected, disconnected(reason),
//   datachannel-open, datachannel-closed, datachannel-message(text),
//   video-started("w,h"), video-size("w,h"), video-stopped, ice-state(state), log(text)

var WebGLRemoteBridgeLib = {

  $WebGLRemote: {
    cb: 0,
    ws: null,
    wsUrl: null,
    wantSignaling: false,
    reconnectDelay: 1000,
    reconnectTimer: null,
    clientId: null,

    pc: null,
    dc: null,
    targetDeviceId: null,
    sessionId: null,
    pendingCandidates: [],
    connectTimer: null,
    connected: false,
    userDisconnect: false,

    video: null,
    stream: null,
    hasVideo: false,
    frameDirty: false,
    videoW: 0,
    videoH: 0,
    overlay: false,

    // ───────── util ─────────
    emit: function (type, payload) {
      var cb = WebGLRemote.cb;
      if (!cb) return;
      var t = stringToNewUTF8(type);
      var p = stringToNewUTF8(payload == null ? "" : String(payload));
      try {
        {{{ makeDynCall('vii', 'cb') }}}(t, p);
      } catch (e) {
        console.error("[WebGLRemote] callback threw", e);
      }
      _free(t);
      _free(p);
    },

    log: function (msg) {
      WebGLRemote.emit("log", msg);
    },

    send: function (obj) {
      var ws = WebGLRemote.ws;
      if (!ws || ws.readyState !== 1) return false;
      ws.send(JSON.stringify(obj));
      return true;
    },

    // ───────── signaling ─────────
    connectSignaling: function (url) {
      var S = WebGLRemote;
      S.wsUrl = url;
      S.wantSignaling = true;
      S.openSocket();
    },

    openSocket: function () {
      var S = WebGLRemote;
      if (S.ws && (S.ws.readyState === 0 || S.ws.readyState === 1)) return;
      if (S.reconnectTimer) { clearTimeout(S.reconnectTimer); S.reconnectTimer = null; }

      var ws;
      try {
        ws = new WebSocket(S.wsUrl);
      } catch (e) {
        S.emit("signaling-closed", "invalid-url");
        S.scheduleReconnect();
        return;
      }
      S.ws = ws;

      ws.onopen = function () {
        S.reconnectDelay = 1000;
        S.send({ type: "register-controller" });
        S.send({ type: "list-devices" });
        S.emit("signaling-open", S.wsUrl);
      };

      ws.onmessage = function (ev) {
        var msg;
        try { msg = JSON.parse(ev.data); } catch (e) { return; }
        S.handleSignal(msg);
      };

      ws.onerror = function () { /* onclose follows */ };

      ws.onclose = function (ev) {
        if (S.ws !== ws) return;
        S.ws = null;
        S.emit("signaling-closed", "code=" + ev.code);
        // A signaling drop does not kill an established peer connection, but a pending
        // negotiation cannot complete without it.
        if (S.pc && !S.connected) S.teardownPeer("signaling-lost");
        S.scheduleReconnect();
      };
    },

    scheduleReconnect: function () {
      var S = WebGLRemote;
      if (!S.wantSignaling || S.reconnectTimer) return;
      S.log("[Signaling] reconnect in " + (S.reconnectDelay / 1000) + "s");
      S.reconnectTimer = setTimeout(function () {
        S.reconnectTimer = null;
        if (S.wantSignaling) S.openSocket();
      }, S.reconnectDelay);
      S.reconnectDelay = Math.min(S.reconnectDelay * 2, 10000);
    },

    disconnectSignaling: function () {
      var S = WebGLRemote;
      S.wantSignaling = false;
      if (S.reconnectTimer) { clearTimeout(S.reconnectTimer); S.reconnectTimer = null; }
      if (S.ws) { var ws = S.ws; S.ws = null; try { ws.close(); } catch (e) {} }
    },

    handleSignal: function (msg) {
      var S = WebGLRemote;
      switch (msg.type) {
        case "registered":
          S.clientId = msg.clientId || null;
          break;

        case "device-list":
          S.emit("device-list", JSON.stringify({ devices: msg.devices || [] }));
          break;

        case "answer": {
          var apc = S.pc;
          if (!apc) return;
          if (msg.sessionId && msg.sessionId !== S.sessionId) { S.log("[WebRTC] stale answer ignored"); return; }
          S.log("[WebRTC] SDP answer received");
          apc.setRemoteDescription({ type: "answer", sdp: msg.sdp }).then(function () {
            if (S.pc !== apc) return;                       // replaced while awaiting
            var q = S.pendingCandidates; S.pendingCandidates = [];
            q.forEach(function (c) { apc.addIceCandidate(c).catch(function () {}); });
          }).catch(function (e) {
            if (S.pc !== apc) return;
            S.log("[WebRTC] setRemoteDescription failed: " + e);
            S.teardownPeer("bad-answer");
          });
          break;
        }

        case "ice-candidate":
          if (!S.pc) return;
          if (msg.sessionId && msg.sessionId !== S.sessionId) return;
          if (msg.fromId && S.targetDeviceId && msg.fromId !== S.targetDeviceId) return;
          if (!msg.candidate) return;   // end-of-candidates marker — nothing to add
          var cand = { candidate: msg.candidate };
          if (typeof msg.sdpMid === "string" && msg.sdpMid !== "") cand.sdpMid = msg.sdpMid;
          if (typeof msg.sdpMLineIndex === "number" && msg.sdpMLineIndex >= 0) cand.sdpMLineIndex = msg.sdpMLineIndex;
          if (S.pc.remoteDescription) S.pc.addIceCandidate(cand).catch(function () {});
          else S.pendingCandidates.push(cand);
          break;

        case "disconnect":
          if (S.pc) S.teardownPeer(msg.reason || "host-disconnected");
          break;

        case "error":
          S.log("[Signaling] error: " + msg.message);
          if (S.pc && !S.connected) S.teardownPeer("signaling-error");
          break;
      }
    },

    // ───────── peer ─────────
    connectPeer: function (deviceId, iceServersJson) {
      var S = WebGLRemote;
      if (S.pc) S.teardownPeer("reconnect");
      if (!S.ws || S.ws.readyState !== 1) {
        S.emit("disconnected", "signaling-offline");
        return;
      }

      var urls = [];
      try { urls = JSON.parse(iceServersJson || "[]"); } catch (e) {}
      var cfg = { iceServers: urls.map(function (u) { return { urls: u }; }) };

      S.targetDeviceId = deviceId;
      S.sessionId = (typeof crypto !== "undefined" && crypto.randomUUID) ? crypto.randomUUID()
                    : (Date.now().toString(36) + Math.random().toString(36).slice(2));
      S.userDisconnect = false;
      S.connected = false;
      S.pendingCandidates = [];
      S.emit("connecting", deviceId);

      var pc = new RTCPeerConnection(cfg);
      S.pc = pc;

      var dc = pc.createDataChannel("commands", { ordered: true });
      S.dc = dc;
      dc.onopen = function () {
        if (S.dc !== dc) return;
        S.connected = true;
        if (S.connectTimer) { clearTimeout(S.connectTimer); S.connectTimer = null; }
        S.emit("datachannel-open", "");
        S.emit("connected", deviceId);
      };
      dc.onclose = function () {
        if (S.dc !== dc) return;
        S.emit("datachannel-closed", "");
        if (S.pc) S.teardownPeer(S.userDisconnect ? "user" : "datachannel-closed");
      };
      dc.onmessage = function (ev) {
        if (typeof ev.data === "string") S.emit("datachannel-message", ev.data);
      };

      pc.addTransceiver("video", { direction: "recvonly" });

      pc.onicecandidate = function (ev) {
        if (!ev.candidate) return;
        S.send({
          type: "ice-candidate", targetId: deviceId, sessionId: S.sessionId,
          candidate: ev.candidate.candidate, sdpMid: ev.candidate.sdpMid, sdpMLineIndex: ev.candidate.sdpMLineIndex
        });
      };

      pc.onconnectionstatechange = function () {
        if (S.pc !== pc) return;
        S.emit("ice-state", pc.connectionState);
        if (pc.connectionState === "failed" || pc.connectionState === "closed" ||
            pc.connectionState === "disconnected") {
          S.teardownPeer("peer-" + pc.connectionState);
        }
      };

      pc.ontrack = function (ev) {
        if (S.pc !== pc || ev.track.kind !== "video") return;
        var stream = (ev.streams && ev.streams[0]) || new MediaStream([ev.track]);
        S.attachVideo(stream);
        ev.track.onended = function () { if (S.pc === pc) S.detachVideo(); };
        ev.track.onmute = function () { S.hasVideo = false; };
        ev.track.onunmute = function () { if (S.video && S.video.readyState >= 2) S.hasVideo = true; };
      };

      S.connectTimer = setTimeout(function () {
        S.connectTimer = null;
        if (S.pc === pc && !S.connected) S.teardownPeer("timeout");
      }, 20000);

      pc.createOffer().then(function (offer) {
        if (S.pc !== pc) return null;
        return pc.setLocalDescription(offer);
      }).then(function () {
        if (S.pc !== pc) return;
        S.log("[WebRTC] SDP offer sent");
        S.send({ type: "offer", targetId: deviceId, sessionId: S.sessionId, sdp: pc.localDescription.sdp });
      }).catch(function (e) {
        if (S.pc !== pc) return;                            // an old peer's failure must not kill the new one
        S.log("[WebRTC] createOffer failed: " + e);
        S.teardownPeer("offer-failed");
      });
    },

    teardownPeer: function (reason) {
      var S = WebGLRemote;
      var pc = S.pc, dc = S.dc;
      var wasActive = !!pc;
      S.pc = null; S.dc = null;
      if (S.connectTimer) { clearTimeout(S.connectTimer); S.connectTimer = null; }

      if (S.targetDeviceId && wasActive) S.send({ type: "disconnect", targetId: S.targetDeviceId, reason: reason });

      S.detachVideo();
      if (dc) { try { dc.onopen = dc.onclose = dc.onmessage = null; dc.close(); } catch (e) {} }
      if (pc) {
        try {
          pc.onicecandidate = pc.onconnectionstatechange = pc.ontrack = null;
          pc.close();
        } catch (e) {}
      }

      var wasConnected = S.connected;
      S.connected = false;
      S.targetDeviceId = null;
      S.sessionId = null;
      S.pendingCandidates = [];
      if (wasActive || wasConnected) S.emit("disconnected", reason);
    },

    disconnectPeer: function () {
      var S = WebGLRemote;
      S.userDisconnect = true;
      if (S.pc) S.teardownPeer("user");
    },

    // ───────── video ─────────
    ensureVideoElement: function () {
      var S = WebGLRemote;
      if (S.video) return S.video;
      var v = document.createElement("video");
      v.autoplay = true;
      v.muted = true;           // muted → autoplay allowed without a user gesture
      v.playsInline = true;
      v.setAttribute("playsinline", "");
      v.style.position = "absolute";
      v.style.left = "0"; v.style.top = "0";
      v.style.width = "1px"; v.style.height = "1px";
      v.style.opacity = "0";
      v.style.pointerEvents = "none";
      document.body.appendChild(v);
      S.video = v;
      S.applyOverlayStyle();

      v.addEventListener("loadedmetadata", function () {
        S.videoW = v.videoWidth; S.videoH = v.videoHeight;
        S.hasVideo = S.videoW > 0 && S.videoH > 0;
        if (S.hasVideo) S.emit("video-started", S.videoW + "," + S.videoH);
      });
      v.addEventListener("resize", function () {
        if (v.videoWidth !== S.videoW || v.videoHeight !== S.videoH) {
          S.videoW = v.videoWidth; S.videoH = v.videoHeight;
          S.emit("video-size", S.videoW + "," + S.videoH);
        }
      });
      // Mark a new frame only when the browser actually decoded one.
      if ("requestVideoFrameCallback" in v) {
        var onFrame = function () { S.frameDirty = true; if (S.video === v) v.requestVideoFrameCallback(onFrame); };
        v.requestVideoFrameCallback(onFrame);
      } else {
        v.addEventListener("timeupdate", function () { S.frameDirty = true; });
      }
      return v;
    },

    attachVideo: function (stream) {
      var S = WebGLRemote;
      var v = S.ensureVideoElement();
      S.stream = stream;
      v.srcObject = stream;
      var p = v.play();
      if (p && p.catch) p.catch(function (e) { S.log("[Video] play() blocked: " + e); });
    },

    detachVideo: function () {
      var S = WebGLRemote;
      var had = S.hasVideo;
      S.hasVideo = false; S.frameDirty = false; S.videoW = 0; S.videoH = 0;
      if (S.stream) { try { S.stream.getTracks().forEach(function (t) { t.stop(); }); } catch (e) {} S.stream = null; }
      if (S.video) { try { S.video.pause(); S.video.srcObject = null; } catch (e) {} }
      if (had) S.emit("video-stopped", "");
    },

    applyOverlayStyle: function () {
      var S = WebGLRemote, v = S.video;
      if (!v) return;
      if (S.overlay) {
        v.style.width = "40%"; v.style.height = "auto";
        v.style.right = "8px"; v.style.left = "auto"; v.style.top = "8px";
        v.style.opacity = "1"; v.style.zIndex = "1000";
      } else {
        v.style.width = "1px"; v.style.height = "1px";
        v.style.left = "0"; v.style.right = "auto"; v.style.top = "0";
        v.style.opacity = "0"; v.style.zIndex = "";
      }
    },

    updateTexture: function (texId) {
      var S = WebGLRemote, v = S.video;
      if (!v || !S.hasVideo || !S.frameDirty || v.readyState < 2) return 0;
      var tex = GL.textures[texId];
      if (!tex) return 0;
      var gl = GLctx;
      var prev = gl.getParameter(gl.TEXTURE_BINDING_2D);
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, true);
      try {
        gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, v);
      } catch (e) {
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
        gl.bindTexture(gl.TEXTURE_2D, prev);
        return 0;
      }
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, false);
      gl.bindTexture(gl.TEXTURE_2D, prev);
      S.frameDirty = false;
      return 1;
    }
  },

  // ───────── exports (C# DllImport) ─────────

  WebGLRemote_Init__deps: ['$WebGLRemote'],
  WebGLRemote_Init: function (cb) {
    WebGLRemote.cb = cb;
    if (!WebGLRemote._unloadHooked) {
      WebGLRemote._unloadHooked = true;
      window.addEventListener("beforeunload", function () {
        try {
          if (WebGLRemote.pc && WebGLRemote.targetDeviceId)
            WebGLRemote.send({ type: "disconnect", targetId: WebGLRemote.targetDeviceId, reason: "page-unload" });
          if (WebGLRemote.ws) WebGLRemote.ws.close();
        } catch (e) {}
      });
    }
  },

  WebGLRemote_ConnectSignaling__deps: ['$WebGLRemote'],
  WebGLRemote_ConnectSignaling: function (urlPtr) {
    WebGLRemote.connectSignaling(UTF8ToString(urlPtr));
  },

  WebGLRemote_DisconnectSignaling__deps: ['$WebGLRemote'],
  WebGLRemote_DisconnectSignaling: function () {
    WebGLRemote.disconnectSignaling();
  },

  WebGLRemote_IsSignalingOpen__deps: ['$WebGLRemote'],
  WebGLRemote_IsSignalingOpen: function () {
    return (WebGLRemote.ws && WebGLRemote.ws.readyState === 1) ? 1 : 0;
  },

  WebGLRemote_RequestDeviceList__deps: ['$WebGLRemote'],
  WebGLRemote_RequestDeviceList: function () {
    return WebGLRemote.send({ type: "list-devices" }) ? 1 : 0;
  },

  WebGLRemote_Connect__deps: ['$WebGLRemote'],
  WebGLRemote_Connect: function (deviceIdPtr, iceServersJsonPtr) {
    WebGLRemote.connectPeer(UTF8ToString(deviceIdPtr), UTF8ToString(iceServersJsonPtr));
  },

  WebGLRemote_Disconnect__deps: ['$WebGLRemote'],
  WebGLRemote_Disconnect: function () {
    WebGLRemote.disconnectPeer();
  },

  WebGLRemote_IsConnected__deps: ['$WebGLRemote'],
  WebGLRemote_IsConnected: function () {
    return WebGLRemote.connected ? 1 : 0;
  },

  WebGLRemote_SendData__deps: ['$WebGLRemote'],
  WebGLRemote_SendData: function (msgPtr) {
    var dc = WebGLRemote.dc;
    if (!dc || dc.readyState !== "open") return 0;
    try { dc.send(UTF8ToString(msgPtr)); return 1; } catch (e) { return 0; }
  },

  WebGLRemote_HasVideo__deps: ['$WebGLRemote'],
  WebGLRemote_HasVideo: function () {
    return WebGLRemote.hasVideo ? 1 : 0;
  },

  WebGLRemote_GetVideoWidth__deps: ['$WebGLRemote'],
  WebGLRemote_GetVideoWidth: function () { return WebGLRemote.videoW | 0; },

  WebGLRemote_GetVideoHeight__deps: ['$WebGLRemote'],
  WebGLRemote_GetVideoHeight: function () { return WebGLRemote.videoH | 0; },

  WebGLRemote_UpdateTexture__deps: ['$WebGLRemote'],
  WebGLRemote_UpdateTexture: function (texId) {
    return WebGLRemote.updateTexture(texId);
  },

  WebGLRemote_SetOverlay__deps: ['$WebGLRemote'],
  WebGLRemote_SetOverlay: function (enabled) {
    WebGLRemote.overlay = !!enabled;
    WebGLRemote.applyOverlayStyle();
  }
};

autoAddDeps(WebGLRemoteBridgeLib, '$WebGLRemote');
mergeInto(LibraryManager.library, WebGLRemoteBridgeLib);
