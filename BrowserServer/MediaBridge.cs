using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using Newtonsoft.Json;

namespace BrowserServer
{
    /// <summary>
    /// Routes website getUserMedia through the phone via cbmedia:// scheme + CAM/MIC uplink.
    /// </summary>
    public static class MediaBridge
    {
        public static readonly byte[] MagicMic = { (byte)'M', (byte)'I', (byte)'C', (byte)' ' };
        public static readonly byte[] MagicCam = { (byte)'C', (byte)'A', (byte)'M', (byte)' ' };

        private static readonly object Sync = new object();
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> PendingPermission =
            new ConcurrentDictionary<string, TaskCompletionSource<bool>>();

        private static byte[] latestJpeg;
        private static readonly ConcurrentQueue<byte[]> PcmChunks = new ConcurrentQueue<byte[]>();
        private static int pcmQueuedBytes;
        private const int MaxPcmQueuedBytes = 48000 * 2 * 2;

        private static string activeRequestId;
        private static string activeTabId;
        private static bool captureActive;
        private static int camPacketsReceived;
        private static int micPacketsReceived;

        public static bool IsCaptureActive
        {
            get { lock (Sync) return captureActive; }
        }

        public static void AttachToBrowser(IWebBrowser browser, string tabId)
        {
            if (browser == null)
                return;

            try
            {
                // Modern async binding — page calls CefSharp.BindObjectAsync('cbMedia').
                browser.JavascriptObjectRepository.ResolveObject += (sender, e) =>
                {
                    if (string.Equals(e.ObjectName, "cbMedia", StringComparison.Ordinal))
                    {
                        try
                        {
                            e.ObjectRepository.Register(
                                "cbMedia",
                                new MediaJsBridge(tabId),
                                isAsync: true,
                                options: BindingOptions.DefaultBinder);
                            Console.WriteLine("cbMedia bound for tab " + tabId);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine("cbMedia register error: " + ex.Message);
                        }
                    }
                };

                // Also register eagerly for environments that resolve without the event.
                browser.JavascriptObjectRepository.Register(
                    "cbMedia",
                    new MediaJsBridge(tabId),
                    isAsync: true,
                    options: BindingOptions.DefaultBinder);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Media bridge attach error: " + ex.Message);
            }
        }

        public static string FindTabId(IBrowser browser)
        {
            return TabManager.ActiveTabId;
        }

        public static void InjectShim(IFrame frame)
        {
            if (frame == null || !frame.IsValid)
                return;

            try
            {
                frame.ExecuteJavaScriptAsync(GetUserMediaShimScript);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Media shim inject error: " + ex.Message);
            }
        }

        public static Task<bool> RequestAccessAsync(string tabId, bool audio, bool video, string origin)
        {
            if (!audio && !video)
                return Task.FromResult(false);

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingPermission[requestId] = tcs;

            lock (Sync)
            {
                if (captureActive)
                {
                    TaskCompletionSource<bool> stale;
                    PendingPermission.TryRemove(requestId, out stale);
                    return Task.FromResult(true);
                }

                activeRequestId = requestId;
                activeTabId = tabId;
                camPacketsReceived = 0;
                micPacketsReceived = 0;
            }

            Console.WriteLine("Media permission → phone id={0} audio={1} video={2} origin={3}",
                requestId, audio, video, origin);

            BroadcastText(TextPacketType.MediaPermissionRequest, new MediaPermissionPayload
            {
                requestId = requestId,
                origin = origin ?? "",
                audio = audio,
                video = video
            });

            Task.Delay(120000).ContinueWith(t =>
            {
                TaskCompletionSource<bool> pending;
                if (PendingPermission.TryRemove(requestId, out pending))
                {
                    Console.WriteLine("Media permission timeout id=" + requestId);
                    pending.TrySetResult(false);
                    lock (Sync)
                    {
                        if (activeRequestId == requestId)
                        {
                            activeRequestId = null;
                            captureActive = false;
                        }
                    }
                }
            });

            return tcs.Task;
        }

        public static void HandlePermissionResponse(MediaPermissionPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.requestId))
                return;

            TaskCompletionSource<bool> tcs;
            if (!PendingPermission.TryRemove(payload.requestId, out tcs))
            {
                Console.WriteLine("Media permission response for unknown id=" + payload.requestId);
                return;
            }

            lock (Sync)
            {
                if (payload.allowed && activeRequestId == payload.requestId)
                    captureActive = true;
                else if (activeRequestId == payload.requestId)
                {
                    activeRequestId = null;
                    captureActive = false;
                }
            }

            Console.WriteLine("Media permission response id={0} allowed={1}", payload.requestId, payload.allowed);
            tcs.TrySetResult(payload.allowed);
        }

        public static void Release(string tabId)
        {
            string requestId;
            lock (Sync)
            {
                if (!captureActive && activeRequestId == null)
                    return;
                if (tabId != null && activeTabId != null &&
                    !string.Equals(tabId, activeTabId, StringComparison.Ordinal))
                    return;

                requestId = activeRequestId;
                activeRequestId = null;
                activeTabId = null;
                captureActive = false;
                latestJpeg = null;
            }

            byte[] discarded;
            while (PcmChunks.TryDequeue(out discarded)) { }
            Interlocked.Exchange(ref pcmQueuedBytes, 0);

            if (!string.IsNullOrEmpty(requestId))
            {
                BroadcastText(TextPacketType.MediaCaptureStop, new MediaPermissionPayload
                {
                    requestId = requestId
                });
            }

            Console.WriteLine("Media capture released");
        }

        public static void HandleClientBinary(byte[] data)
        {
            if (data == null || data.Length < 4)
                return;

            if (data[0] == MagicCam[0] && data[1] == MagicCam[1] && data[2] == MagicCam[2] && data[3] == MagicCam[3])
            {
                if (data.Length < 12)
                    return;
                int jpegLen = ReadInt32(data, 8);
                if (jpegLen < 0 || 12 + jpegLen > data.Length)
                    return;
                var jpeg = new byte[jpegLen];
                Buffer.BlockCopy(data, 12, jpeg, 0, jpegLen);
                lock (Sync)
                {
                    latestJpeg = jpeg;
                    camPacketsReceived++;
                    if (camPacketsReceived == 1 || camPacketsReceived % 25 == 0)
                        Console.WriteLine("CAM packets={0} jpeg={1}B", camPacketsReceived, jpegLen);
                }
                return;
            }

            if (data[0] == MagicMic[0] && data[1] == MagicMic[1] && data[2] == MagicMic[2] && data[3] == MagicMic[3])
            {
                if (data.Length < 16)
                    return;
                int frames = ReadInt32(data, 12);
                int channels = ReadInt32(data, 8);
                if (frames <= 0 || channels < 1 || channels > 2)
                    return;
                int pcmBytes = frames * channels * 2;
                if (16 + pcmBytes > data.Length)
                    return;

                var chunk = new byte[pcmBytes + 8];
                WriteInt32(chunk, 0, ReadInt32(data, 4));
                WriteInt32(chunk, 4, channels);
                Buffer.BlockCopy(data, 16, chunk, 8, pcmBytes);

                byte[] drop;
                while (pcmQueuedBytes > MaxPcmQueuedBytes && PcmChunks.TryDequeue(out drop))
                    Interlocked.Add(ref pcmQueuedBytes, -(drop.Length - 8));

                PcmChunks.Enqueue(chunk);
                Interlocked.Add(ref pcmQueuedBytes, pcmBytes);
                var n = Interlocked.Increment(ref micPacketsReceived);
                if (n == 1 || n % 100 == 0)
                    Console.WriteLine("MIC packets={0}", n);
            }
        }

        public static byte[] GetLatestJpegCopy()
        {
            lock (Sync)
            {
                if (latestJpeg == null || latestJpeg.Length == 0)
                    return null;
                var copy = new byte[latestJpeg.Length];
                Buffer.BlockCopy(latestJpeg, 0, copy, 0, latestJpeg.Length);
                return copy;
            }
        }

        public static byte[] DequeueAudioChunk()
        {
            byte[] chunk;
            if (!PcmChunks.TryDequeue(out chunk) || chunk == null || chunk.Length <= 8)
                return null;
            Interlocked.Add(ref pcmQueuedBytes, -(chunk.Length - 8));
            return chunk;
        }

        private static void BroadcastText(TextPacketType type, MediaPermissionPayload payload)
        {
            try
            {
                TabManager.Server?.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new TextPacket
                {
                    PType = type,
                    text = JsonConvert.SerializeObject(payload)
                }));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Media text broadcast error: " + ex.Message);
            }
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        // Prefer CefSharp binding (CSP-safe); fall back to cbmedia:// fetch.
        // Video uses MediaStreamTrackGenerator when available — canvas.captureStream is unreliable in OffScreen CEF.
        private const string GetUserMediaShimScript = @"
(function () {
  if (window.__cbMediaShim) return;
  window.__cbMediaShim = true;
  var BASE = 'cbmedia://local';
  var captureUsers = 0;

  function ensureDevices() {
    if (!navigator.mediaDevices) navigator.mediaDevices = {};
    navigator.mediaDevices.enumerateDevices = function () {
      return Promise.resolve([
        { deviceId: 'cb-audio', kind: 'audioinput', label: 'CloudBrowser Microphone', groupId: 'cb', toJSON: function(){return this;} },
        { deviceId: 'cb-video', kind: 'videoinput', label: 'CloudBrowser Camera', groupId: 'cb', toJSON: function(){return this;} }
      ]);
    };
  }

  function ensureBound() {
    if (window.cbMedia) return Promise.resolve(true);
    if (window.CefSharp && CefSharp.BindObjectAsync) {
      return CefSharp.BindObjectAsync('cbMedia').then(function () { return !!window.cbMedia; }).catch(function () { return false; });
    }
    return Promise.resolve(false);
  }

  function requestAccess(audio, video) {
    return ensureBound().then(function (bound) {
      if (bound && window.cbMedia && cbMedia.requestAccess) {
        return cbMedia.requestAccess(!!audio, !!video, location.origin || '');
      }
      var origin = encodeURIComponent(location.origin || '');
      var url = BASE + '/request?audio=' + (audio ? '1' : '0') +
                '&video=' + (video ? '1' : '0') + '&origin=' + origin;
      return fetch(url, { cache: 'no-store' })
        .then(function (res) { return res.json(); })
        .then(function (body) { return !!(body && body.ok); });
    });
  }

  function pullVideoB64() {
    if (window.cbMedia && cbMedia.pullVideoJpegBase64) {
      return Promise.resolve(cbMedia.pullVideoJpegBase64()).then(function (b64) {
        return b64 && b64.length ? b64 : null;
      });
    }
    return fetch(BASE + '/video?t=' + Date.now(), { cache: 'no-store' })
      .then(function (res) { return res.ok ? res.arrayBuffer() : null; })
      .then(function (buf) {
        if (!buf || !buf.byteLength) return null;
        var bytes = new Uint8Array(buf);
        var bin = '';
        for (var i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
        return btoa(bin);
      });
  }

  function pullAudioBytes() {
    if (window.cbMedia && cbMedia.pullAudioPcmBase64) {
      return Promise.resolve(cbMedia.pullAudioPcmBase64()).then(function (b64) {
        if (!b64) return null;
        var bin = atob(b64);
        var out = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
        return out;
      });
    }
    return fetch(BASE + '/audio?t=' + Date.now(), { cache: 'no-store' })
      .then(function (res) { return res.ok ? res.arrayBuffer() : null; })
      .then(function (buf) { return buf ? new Uint8Array(buf) : null; });
  }

  function releaseMedia() {
    captureUsers = Math.max(0, captureUsers - 1);
    if (captureUsers > 0) return;
    try {
      if (window.cbMedia && cbMedia.release) cbMedia.release();
      else fetch(BASE + '/release', { method: 'POST', cache: 'no-store' });
    } catch (e) {}
  }

  function patchTrack(track, width, height) {
    if (!track) return track;
    try {
      var gs = track.getSettings ? track.getSettings.bind(track) : null;
      track.getSettings = function () {
        var base = gs ? gs() : {};
        return Object.assign({
          width: width || 640,
          height: height || 480,
          frameRate: 10,
          deviceId: 'cb-video',
          facingMode: 'environment',
          aspectRatio: (width || 640) / (height || 480)
        }, base || {});
      };
      if (track.getCapabilities) {
        var gc = track.getCapabilities.bind(track);
        track.getCapabilities = function () {
          return Object.assign({
            width: { min: 160, max: 1280 },
            height: { min: 120, max: 720 },
            frameRate: { min: 1, max: 30 },
            facingMode: ['user', 'environment'],
            deviceId: 'cb-video'
          }, gc() || {});
        };
      }
      if (track.getConstraints) {
        track.getConstraints = function () { return {}; };
      }
    } catch (e) {}
    return track;
  }

  function b64ToBlob(b64, mime) {
    var bin = atob(b64);
    var arr = new Uint8Array(bin.length);
    for (var i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
    return new Blob([arr], { type: mime || 'image/jpeg' });
  }

  async function createVideoTrack() {
    var alive = true;
    var stopFn = function () { alive = false; };

    // Preferred: Insertable Streams — real VideoFrames (works in OffScreen CEF).
    if (typeof MediaStreamTrackGenerator !== 'undefined' && typeof VideoFrame !== 'undefined') {
      var generator = new MediaStreamTrackGenerator({ kind: 'video' });
      var writer = generator.writable.getWriter();
      var ts = 0;
      (async function pump() {
        while (alive) {
          try {
            var b64 = await pullVideoB64();
            if (b64) {
              var bitmap = await createImageBitmap(b64ToBlob(b64, 'image/jpeg'));
              ts += 100000; // 10 fps in microseconds
              var frame = new VideoFrame(bitmap, { timestamp: ts });
              await writer.write(frame);
              frame.close();
              bitmap.close();
            }
          } catch (e) {}
          await new Promise(function (r) { setTimeout(r, 100); });
        }
        try { await writer.close(); } catch (e) {}
      })();
      stopFn = function () { alive = false; try { writer.abort(); } catch (e) {} };
      return { track: patchTrack(generator, 640, 480), stop: stopFn };
    }

    // Fallback: canvas in the DOM + requestFrame when available.
    var canvas = document.createElement('canvas');
    canvas.width = 640;
    canvas.height = 480;
    canvas.style.cssText = 'position:fixed;left:-10000px;top:0;width:640px;height:480px;opacity:0;pointer-events:none;';
    (document.body || document.documentElement).appendChild(canvas);
    var ctx = canvas.getContext('2d', { alpha: false, desynchronized: true });
    ctx.fillStyle = '#222';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    var vstream = canvas.captureStream(0);
    var vtrack = vstream.getVideoTracks()[0];
    patchTrack(vtrack, 640, 480);
    (async function pump() {
      while (alive) {
        try {
          var b64 = await pullVideoB64();
          if (b64) {
            var bitmap = await createImageBitmap(b64ToBlob(b64, 'image/jpeg'));
            canvas.width = bitmap.width;
            canvas.height = bitmap.height;
            ctx.drawImage(bitmap, 0, 0);
            bitmap.close();
            if (vtrack.requestFrame) vtrack.requestFrame();
          }
        } catch (e) {}
        await new Promise(function (r) { setTimeout(r, 100); });
      }
      try { canvas.remove(); } catch (e) {}
    })();
    stopFn = function () { alive = false; try { vtrack.stop(); } catch (e) {} };
    return { track: vtrack, stop: stopFn };
  }

  async function createStream(audio, video) {
    var tracks = [];
    var stopFns = [];
    captureUsers++;

    if (video) {
      var vt = await createVideoTrack();
      tracks.push(vt.track);
      stopFns.push(vt.stop);
    }

    if (audio) {
      var AC = window.AudioContext || window.webkitAudioContext;
      var ac = new AC();
      var dest = ac.createMediaStreamDestination();
      var nextTime = ac.currentTime + 0.05;
      var aliveA = true;
      (async function pumpAudio() {
        while (aliveA) {
          try {
            var bytes = await pullAudioBytes();
            if (bytes && bytes.length > 8) {
              var rate = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
              var ch = bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24);
              if (rate < 8000) rate = 48000;
              if (ch < 1) ch = 1;
              var samples = Math.floor((bytes.length - 8) / 2 / ch);
              if (samples > 0) {
                var abuf = ac.createBuffer(ch, samples, rate);
                for (var c = 0; c < ch; c++) {
                  var data = abuf.getChannelData(c);
                  for (var i = 0; i < samples; i++) {
                    var o = 8 + (i * ch + c) * 2;
                    var s = (bytes[o] | (bytes[o + 1] << 8));
                    if (s >= 0x8000) s -= 0x10000;
                    data[i] = s / 32768;
                  }
                }
                var srcNode = ac.createBufferSource();
                srcNode.buffer = abuf;
                srcNode.connect(dest);
                if (nextTime < ac.currentTime) nextTime = ac.currentTime + 0.02;
                srcNode.start(nextTime);
                nextTime += abuf.duration;
              }
            }
          } catch (e) {}
          await new Promise(function (r) { setTimeout(r, 20); });
        }
      })();
      var atrack = dest.stream.getAudioTracks()[0];
      tracks.push(atrack);
      stopFns.push(function () {
        aliveA = false;
        try { atrack.stop(); } catch (e) {}
        try { ac.close(); } catch (e) {}
      });
    }

    var stream = new MediaStream(tracks);
    var stopped = false;
    function stopAll() {
      if (stopped) return;
      stopped = true;
      stopFns.forEach(function (fn) { try { fn(); } catch (e) {} });
      releaseMedia();
    }
    stream.getTracks().forEach(function (t) {
      var old = t.stop.bind(t);
      t.stop = function () { stopAll(); old(); };
    });
    stream.addEventListener('inactive', stopAll);
    return stream;
  }

  ensureDevices();

  navigator.mediaDevices.getUserMedia = function (constraints) {
    constraints = constraints || {};
    var wantAudio = !!(constraints.audio);
    var wantVideo = !!(constraints.video);
    return requestAccess(wantAudio, wantVideo).then(async function (ok) {
      if (!ok) {
        var err = new Error('Permission denied');
        err.name = 'NotAllowedError';
        throw err;
      }
      // Wait for the first phone frame so <video> does not start on a black track.
      if (wantVideo) {
        for (var i = 0; i < 40; i++) {
          var b64 = await pullVideoB64();
          if (b64) break;
          await new Promise(function (r) { setTimeout(r, 100); });
        }
      }
      return createStream(wantAudio, wantVideo);
    });
  };

  if (navigator.getUserMedia || navigator.webkitGetUserMedia) {
    var legacy = function (c, success, error) {
      navigator.mediaDevices.getUserMedia(c).then(success).catch(error || function(){});
    };
    navigator.getUserMedia = legacy;
    navigator.webkitGetUserMedia = legacy;
  }
})();";
    }

    /// <summary>Bound into each CEF tab as window.cbMedia.</summary>
    public sealed class MediaJsBridge
    {
        private readonly string tabId;

        public MediaJsBridge(string tabId)
        {
            this.tabId = tabId;
        }

        public Task<bool> RequestAccess(bool audio, bool video, string origin)
        {
            Console.WriteLine("getUserMedia via phone tab={0} audio={1} video={2} origin={3}",
                tabId, audio, video, origin);
            return MediaBridge.RequestAccessAsync(tabId, audio, video, origin);
        }

        public Task<string> PullVideoJpegBase64()
        {
            var jpeg = MediaBridge.GetLatestJpegCopy();
            if (jpeg == null || jpeg.Length == 0)
                return Task.FromResult("");
            return Task.FromResult(Convert.ToBase64String(jpeg));
        }

        public Task<string> PullAudioPcmBase64()
        {
            var chunk = MediaBridge.DequeueAudioChunk();
            if (chunk == null || chunk.Length == 0)
                return Task.FromResult("");
            return Task.FromResult(Convert.ToBase64String(chunk));
        }

        public void Release()
        {
            MediaBridge.Release(tabId);
        }
    }
}
