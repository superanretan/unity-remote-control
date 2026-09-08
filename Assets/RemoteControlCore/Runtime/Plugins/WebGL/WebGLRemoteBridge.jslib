// WebGLRemoteBridge.jslib
// Browser side of the WebGL controller: WebSocket signaling + RTCPeerConnection + RTCDataChannel
// + incoming video track rendered into an HTMLVideoElement that is copied into a Unity texture.
//
// All events reach C# through ONE callback: cb(typeUtf8Ptr, payloadUtf8Ptr).
// Event types:
//   signaling-open, signaling-closed(reason), signaling-rejected(reason), device-list(json {devices:[...]}),
//   connecting(deviceId), connected, disconnected(reason),
//   datachannel-open, datachannel-closed, datachannel-message(text),
//   video-started("w,h"), video-size("w,h"), video-stopped, ice-state(state), log(text)
//
// Identity: the controller owns a stable 128-bit clientId (sessionStorage, survives F5 in the same tab;
// Web Locks make a duplicated tab pick a fresh id). It is sent in register-controller so the server can
// keep pairing across the forced socket reconnect (Vercel max duration) and the host's fromId guards keep
// matching. The signaling socket dropping is routine and never tears down an established peer.

var WebGLRemoteBridgeLib = {

  $WebGLRemote: {
    cb: 0,
    ws: null,
    wsUrl: null,
    wantSignaling: false,
    reconnectDelay: 1000,
    reconnectTimer: null,
    clientId: null,
    clientIdReady: null,
    memoryClientId: null,
    rejected: false,
    serverIceServers: [],

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
    uploadKey: "",      // texId+size the upload path below was probed for
    upOk: 0,            // successful frame uploads since the page loaded
    frameCount: 0,      // frames the browser actually presented (requestVideoFrameCallback)
    diagAt: 0,
    diagPost: 0,
    uploadMode: null,   // null = not probed yet, then "texsubimage" | "teximage"
    uploadFails: 0,
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

    // ───────── identity ─────────
    randomId: function () {
      var bytes = new Uint8Array(16);
      if (typeof crypto !== "undefined" && crypto.getRandomValues) {
        crypto.getRandomValues(bytes);
      } else {
        // Only reachable in very old engines; getRandomValues exists in every browser Unity WebGL supports.
        WebGLRemote.log("[Signaling] crypto.getRandomValues unavailable — weak clientId");
        for (var i = 0; i < 16; i++) bytes[i] = (Math.random() * 256) | 0;
      }
      var hex = "";
      for (var j = 0; j < 16; j++) hex += (bytes[j] < 16 ? "0" : "") + bytes[j].toString(16);
      return hex;
    },

    storageGet: function (key) {
      try { return window.sessionStorage.getItem(key); } catch (e) { return WebGLRemote.memoryClientId; }
    },

    storageSet: function (key, value) {
      WebGLRemote.memoryClientId = value;
      try { window.sessionStorage.setItem(key, value); } catch (e) { /* sandboxed iframe / webview — memory fallback */ }
    },

    // Resolves true when this tab now owns `name`; false when another tab (a duplicate sharing our
    // sessionStorage copy) already holds it. The lock is held until the page goes away.
    acquireLock: function (name) {
      return new Promise(function (resolve) {
        if (typeof navigator === "undefined" || !navigator.locks || !navigator.locks.request) { resolve(true); return; }
        try {
          navigator.locks.request(name, { ifAvailable: true }, function (lock) {
            if (!lock) { resolve(false); return; }
            resolve(true);
            return new Promise(function () {});          // never settles → lock held for the page lifetime
          }).catch(function () { resolve(true); });
        } catch (e) { resolve(true); }
      });
    },

    ensureClientId: function () {
      var S = WebGLRemote;
      if (S.clientId) return Promise.resolve(S.clientId);
      if (S.clientIdReady) return S.clientIdReady;
      var KEY = "rc.clientId";
      S.clientIdReady = (function tryId(candidate, attempt) {
        if (!candidate || !/^[A-Za-z0-9_-]{8,64}$/.test(candidate)) candidate = S.randomId();
        return S.acquireLock("rc-client-" + candidate).then(function (owned) {
          if (!owned && attempt < 3) {
            S.log("[Signaling] clientId already in use by another tab — generating a new one");
            return tryId(S.randomId(), attempt + 1);
          }
          S.storageSet(KEY, candidate);
          S.clientId = candidate;
          return candidate;
        });
      })(S.storageGet(KEY), 0);
      return S.clientIdReady;
    },

    // Another tab/device registered with our id and the server evicted us: take a fresh identity.
    rotateClientId: function () {
      var S = WebGLRemote;
      S.clientId = null;
      S.clientIdReady = null;
      S.storageSet("rc.clientId", "");
      S.log("[Signaling] clientId replaced by another connection — rotating identity");
    },

    // ───────── signaling ─────────
    connectSignaling: function (url) {
      var S = WebGLRemote;
      S.wsUrl = url;
      S.wantSignaling = true;
      S.rejected = false;
      S.openSocket();
    },

    openSocket: function () {
      var S = WebGLRemote;
      if (S.ws && (S.ws.readyState === 0 || S.ws.readyState === 1)) return;
      if (S.reconnectTimer) { clearTimeout(S.reconnectTimer); S.reconnectTimer = null; }
      S.ensureClientId().then(function () {
        if (!S.wantSignaling) return;
        if (S.ws && (S.ws.readyState === 0 || S.ws.readyState === 1)) return;
        S.openSocketNow();
      });
    },

    openSocketNow: function () {
      var S = WebGLRemote;
      var ws;
      try {
        ws = new WebSocket(S.wsUrl);
      } catch (e) {
        S.emit("signaling-closed", "invalid-url");
        S.scheduleReconnect();
        return;
      }
      S.ws = ws;
      var opened = false;

      ws.onopen = function () {
        opened = true;
        S.reconnectDelay = 1000;
        S.rejected = false;
        // The server honours our id and evicts any older socket using it (same-tab reconnect).
        S.send({ type: "register-controller", clientId: S.clientId });
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
        var reason = "code=" + ev.code + (ev.reason ? " reason=" + ev.reason : "");
        if (ev.code === 4000) {
          // "replaced": somebody else registered with our clientId while this socket was live.
          S.rotateClientId();
        } else if (ev.code === 4401 || ev.code === 4403) {
          S.rejected = true;
          S.emit("signaling-rejected", ev.reason || (ev.code === 4401 ? "unauthorized" : "origin-not-allowed"));
        } else if (!opened) {
          reason += " (closed before open — check URL, token and origin allowlist)";
        }
        S.emit("signaling-closed", reason);
        // A signaling drop does not kill an established peer connection, but a pending
        // negotiation cannot complete without it.
        if (S.pc && !S.connected) S.teardownPeer("signaling-lost");
        S.scheduleReconnect();
      };
    },

    scheduleReconnect: function () {
      var S = WebGLRemote;
      if (!S.wantSignaling || S.reconnectTimer) return;
      var delay = S.rejected ? 10000 : S.reconnectDelay;     // rejected: keep retrying slowly so a server fix is picked up
      S.log("[Signaling] reconnect in " + (delay / 1000) + "s");
      S.reconnectTimer = setTimeout(function () {
        S.reconnectTimer = null;
        if (S.wantSignaling) S.openSocket();
      }, delay);
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
          if (msg.clientId && msg.clientId !== S.clientId) {
            // Server replaced an invalid id with its own; adopt it so fromId stays consistent on reconnect.
            S.clientId = msg.clientId;
            S.storageSet("rc.clientId", msg.clientId);
          }
          S.serverIceServers = Array.isArray(msg.iceServers) ? msg.iceServers : [];
          if (S.serverIceServers.length) S.log("[WebRTC] Server issued " + S.serverIceServers.length + " ICE server entr" + (S.serverIceServers.length === 1 ? "y" : "ies") + " (TURN)");
          break;

        case "device-list":
          S.emit("device-list", JSON.stringify({ devices: msg.devices || [] }));
          break;

        case "answer": {
          var apc = S.pc;
          if (!apc) return;
          if (msg.sessionId && msg.sessionId !== S.sessionId) { S.log("[WebRTC] stale answer ignored"); return; }
          if (msg.fromId && S.targetDeviceId && msg.fromId !== S.targetDeviceId) return;
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
          // Only the paired host (or the server speaking for it: host-timeout) may end our session.
          if (S.pc && (!msg.fromId || !S.targetDeviceId || msg.fromId === S.targetDeviceId))
            S.teardownPeer(msg.reason || "host-disconnected");
          break;

        case "error":
          if (msg.message === "unauthorized" || msg.message === "origin-not-allowed") {
            S.rejected = true;
            S.emit("signaling-rejected", msg.message);
          } else {
            S.log("[Signaling] error: " + msg.message);
          }
          if (S.pc && !S.connected) S.teardownPeer("signaling-error");
          break;
      }
    },

    // ───────── peer ─────────
    parseIceServers: function (json) {
      // Accepts the 2.x format [{urls:[…], username?, credential?}] and the 1.x flat ["stun:…"] format.
      var out = [];
      var list = [];
      try { list = JSON.parse(json || "[]"); } catch (e) {}
      if (!Array.isArray(list)) return out;
      list.forEach(function (entry) {
        if (typeof entry === "string") { if (entry) out.push({ urls: entry }); return; }
        if (!entry || typeof entry !== "object") return;
        var urls = Array.isArray(entry.urls) ? entry.urls.filter(function (u) { return typeof u === "string" && u; })
                 : (typeof entry.urls === "string" && entry.urls ? [entry.urls] : []);
        if (!urls.length) return;
        var server = { urls: urls };
        if (typeof entry.username === "string" && entry.username) server.username = entry.username;
        if (typeof entry.credential === "string" && entry.credential) server.credential = entry.credential;
        out.push(server);
      });
      return out;
    },

    connectPeer: function (deviceId, iceServersJson) {
      var S = WebGLRemote;
      if (S.pc) S.teardownPeer("reconnect");
      if (!S.ws || S.ws.readyState !== 1) {
        S.emit("disconnected", "signaling-offline");
        return;
      }

      // NetworkConfig entries + ephemeral TURN credentials issued by the signaling server (if configured).
      var servers = S.parseIceServers(iceServersJson).concat(S.parseIceServers(JSON.stringify(S.serverIceServers || [])));
      var cfg = { iceServers: servers };

      S.targetDeviceId = deviceId;
      S.sessionId = (typeof crypto !== "undefined" && crypto.randomUUID) ? crypto.randomUUID() : S.randomId();
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

      var videoTransceiver = pc.addTransceiver("video", { direction: "recvonly" });

      // Ask for H.264 first. The host answers from *this* offer's payload order, so the encoder
      // factory's preferredCodec on the Vision Pro cannot pick H.264 on its own — without this the
      // negotiation lands on VP8 and the headset encodes 720p in software (libvpx), which is a lot
      // of heat for nothing. Best effort: browsers without setCodecPreferences keep their default.
      try {
        if (videoTransceiver && videoTransceiver.setCodecPreferences && window.RTCRtpReceiver &&
            RTCRtpReceiver.getCapabilities) {
          var caps = RTCRtpReceiver.getCapabilities("video");
          if (caps && caps.codecs) {
            var h264 = [], rest = [];
            caps.codecs.forEach(function (c) {
              (/h264/i.test(c.mimeType) ? h264 : rest).push(c);
            });
            if (h264.length) {
              videoTransceiver.setCodecPreferences(h264.concat(rest));
              S.log("[WebRTC] Preferring H.264 (hardware encode on the Vision Pro).");
            }
          }
        }
      } catch (e) {
        S.log("[WebRTC] Could not set codec preferences: " + e);
      }

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

      if (S.targetDeviceId && wasActive) S.send({ type: "disconnect", targetId: S.targetDeviceId, sessionId: S.sessionId, reason: reason });

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

    // Tab closing / navigating away: tell the host right now so it stops capturing (fast path; the
    // server-side pairing lease is the fallback). `pagehide` covers iOS Safari, which often skips
    // beforeunload; a persisted (bfcache) pagehide is NOT a departure.
    onPageLeaving: function () {
      var S = WebGLRemote;
      if (S.unloadSent) return;
      S.unloadSent = true;
      try {
        if (S.pc && S.targetDeviceId)
          S.send({ type: "disconnect", targetId: S.targetDeviceId, sessionId: S.sessionId, reason: "page-unload" });
        if (S.ws) S.ws.close();
      } catch (e) {}
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
        var onFrame = function () { S.frameDirty = true; S.frameCount++; if (S.video === v) v.requestVideoFrameCallback(onFrame); };
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
      S.upOk = 0; S.frameCount = 0; S.diagAt = 0;   // re-arm diagnostics for the next session
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

    // Diagnostics. Silent once a frame has landed; until then it says, at most every two
    // seconds, which gate is blocking and what both sides believe about the video.
    diagNote: function (reason) {
      var S = WebGLRemote, v = S.video, t = Date.now();
      if (S.upOk || t - S.diagAt < 2000) return;
      S.diagAt = t;
      S.log("[Video] jslib skip: " + reason +
            " — video " + (v ? v.videoWidth + "x" + v.videoHeight + " ready=" + v.readyState +
                               " paused=" + v.paused + " t=" + (v.currentTime || 0).toFixed(2)
                             : "(none)") +
            ", bridge " + S.videoW + "x" + S.videoH +
            ", presented=" + S.frameCount + ", dirty=" + S.frameDirty);
    },

    // Reads one pixel back out of the texture GL.textures[texId] resolved to. Run BEFORE the first
    // upload it is an identity test: the pixel must be the magenta Unity filled the Texture2D with,
    // and anything else proves GetNativeTexturePtr did not hand us this texture. Run after the
    // upload it proves the frame actually landed there.
    diagReadback: function (tex, w, h, label) {
      var gl = GLctx, S = WebGLRemote;
      try {
        var prevFb = gl.getParameter(gl.FRAMEBUFFER_BINDING);
        var fb = gl.createFramebuffer();
        gl.bindFramebuffer(gl.FRAMEBUFFER, fb);
        gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, tex, 0);
        if (gl.checkFramebufferStatus(gl.FRAMEBUFFER) === gl.FRAMEBUFFER_COMPLETE) {
          var px = new Uint8Array(4);
          gl.readPixels(w >> 1, h >> 1, 1, 1, gl.RGBA, gl.UNSIGNED_BYTE, px);
          S.log("[Video] Centre pixel " + label + ": rgba(" + px[0] + "," + px[1] + "," + px[2] +
                "," + px[3] + ").");
        } else {
          S.log("[Video] Pixel readback " + label + " unavailable (framebuffer incomplete).");
        }
        gl.bindFramebuffer(gl.FRAMEBUFFER, prevFb);
        gl.deleteFramebuffer(fb);
      } catch (e) {
        S.log("[Video] Pixel readback " + label + " threw: " + e);
      }
    },

    // Uploads the current video frame into the GL texture Unity allocated for the
    // Texture2D. Unity's WebGL2 backend allocates Texture2D storage with texStorage2D,
    // which makes the texture IMMUTABLE: texImage2D on it fails with GL_INVALID_OPERATION
    // ("Texture is immutable") and the frame never lands. texSubImage2D is the only legal
    // upload path there, and it requires the source video to match the texture exactly,
    // hence the (w,h) the caller passes in.
    updateTexture: function (texId, w, h) {
      var S = WebGLRemote, v = S.video;
      if (!v || !S.hasVideo || v.readyState < 2) { S.diagNote("no video / not ready"); return 0; }
      if (!S.frameDirty) { S.diagNote("no new decoded frame"); return 0; }

      var vw = v.videoWidth | 0, vh = v.videoHeight | 0;
      if (vw <= 0 || vh <= 0) { S.diagNote("video has no dimensions"); return 0; }

      // The video resized without the "resize" event having reached Unity yet (or the
      // texture is still the old size). Re-publish the size and skip this frame; the view
      // reallocates the texture and the next frame uploads cleanly.
      if (vw !== S.videoW || vh !== S.videoH) {
        S.videoW = vw; S.videoH = vh;
        S.emit("video-size", vw + "," + vh);
        S.diagNote("video resized to " + vw + "x" + vh);
        return 0;
      }
      if ((w | 0) !== vw || (h | 0) !== vh) { S.diagNote("texture " + w + "x" + h + " does not match video"); return 0; }

      var gl = GLctx;
      if (!gl || (gl.isContextLost && gl.isContextLost())) { S.diagNote("GL context lost"); return 0; }

      var tex = GL.textures[texId];
      if (!tex) { S.diagNote("GL.textures[" + texId + "] is empty"); return 0; }

      // The upload path is decided per texture, not once per session: a texture Unity
      // allocated differently (or a restored context handing back the same slot) must be
      // probed again rather than inheriting a stale decision.
      var key = texId + "x" + w + "x" + h;
      if (S.uploadKey !== key) {
        S.uploadKey = key;
        S.uploadMode = null;
        S.uploadFails = 0;
        S.diagPost = 0;
        // Identity check, before this texture is written to for the first time: Unity filled the
        // Texture2D with magenta, so rgba(255,0,255,255) here means texId really is that texture.
        S.diagReadback(tex, w, h, "before first upload into GL.textures[" + texId + "]");
      }

      var prevTex = gl.getParameter(gl.TEXTURE_BINDING_2D);
      var prevFlip = gl.getParameter(gl.UNPACK_FLIP_Y_WEBGL);
      gl.bindTexture(gl.TEXTURE_2D, tex);
      gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, 1);

      var ok = 0;
      try {
        if (S.uploadMode === "teximage") {
          gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, v);
          ok = 1;
        } else if (S.uploadMode === "texsubimage") {
          gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, v);
          ok = 1;
        } else {
          // First frame into this texture: prefer texSubImage2D and verify, so a texture
          // whose storage was never allocated can still fall back to texImage2D. glGetError
          // is a sync point, so it is paid once per texture, not once per frame. The drain
          // is bounded — a lost context can keep reporting an error forever.
          for (var i = 0; i < 32 && gl.getError() !== gl.NO_ERROR; i++) { }
          gl.texSubImage2D(gl.TEXTURE_2D, 0, 0, 0, gl.RGBA, gl.UNSIGNED_BYTE, v);
          if (gl.getError() === gl.NO_ERROR) {
            S.uploadMode = "texsubimage";
            ok = 1;
          } else {
            gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, gl.RGBA, gl.UNSIGNED_BYTE, v);
            if (gl.getError() === gl.NO_ERROR) {
              S.uploadMode = "teximage";
              ok = 1;
            } else {
              // Neither path worked. Stop paying for the probe after a few frames and keep
              // the spec-correct call so a transient failure can still recover.
              S.uploadFails++;
              if (S.uploadFails === 1) {
                S.log("[Video] Frame upload failed: neither texSubImage2D nor texImage2D accepted the video.");
              }
              if (S.uploadFails >= 10) S.uploadMode = "texsubimage";
            }
          }
        }
      } catch (e) {
        S.log("[Video] Frame upload threw: " + e);
      } finally {
        gl.pixelStorei(gl.UNPACK_FLIP_Y_WEBGL, prevFlip ? 1 : 0);
        gl.bindTexture(gl.TEXTURE_2D, prevTex);
      }

      if (ok) {
        S.frameDirty = false;
        S.upOk++;
        if (!S.diagPost) { S.diagPost = 1; S.diagReadback(tex, w, h, "after first upload"); }
      }
      return ok;
    }
  },

  // ───────── exports (C# DllImport) ─────────

  WebGLRemote_Init__deps: ['$WebGLRemote'],
  WebGLRemote_Init: function (cb) {
    WebGLRemote.cb = cb;
    WebGLRemote.log("[Video] bridge build: diag-1 (texSubImage2D + pixel readback). " +
                    "If you do not see this line, the deployed build is stale.");
    if (!WebGLRemote._unloadHooked) {
      WebGLRemote._unloadHooked = true;
      window.addEventListener("beforeunload", function () { WebGLRemote.onPageLeaving(); });
      window.addEventListener("pagehide", function (ev) {
        if (ev && ev.persisted) return;           // bfcache / tab switch on iOS — page may come back
        WebGLRemote.onPageLeaving();
      });
      window.addEventListener("pageshow", function () { WebGLRemote.unloadSent = false; });
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
  WebGLRemote_UpdateTexture: function (texId, w, h) {
    return WebGLRemote.updateTexture(texId, w, h);
  },

  WebGLRemote_SetOverlay__deps: ['$WebGLRemote'],
  WebGLRemote_SetOverlay: function (enabled) {
    WebGLRemote.overlay = !!enabled;
    WebGLRemote.applyOverlayStyle();
  }
};

autoAddDeps(WebGLRemoteBridgeLib, '$WebGLRemote');
mergeInto(LibraryManager.library, WebGLRemoteBridgeLib);
