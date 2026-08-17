using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CefSharp;
using Newtonsoft.Json;

namespace BrowserServer
{
    /// <summary>
    /// Phone camera/mic for cloud browsing.
    ///
    /// Important: OffScreen CEF does not reliably render synthetic MediaStream video tracks.
    /// So we do NOT depend on &lt;video srcObject&gt; painting. Instead:
    /// - C# pushes each JPEG into page JS (no pull/bind/fetch required)
    /// - JS draws onto a live canvas and covers every &lt;video&gt; with that canvas
    /// - drawImage(video) is redirected to the live canvas (QR scanners)
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
        private static string activeOrigin;
        private static bool captureActive;
        private static bool activeAudio;
        private static bool activeVideo;
        private static int camPacketsReceived;
        private static int micPacketsReceived;
        private static int lastPushTick;
        private static int pushCount;
        private static int pushInFlight;

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
                browser.JavascriptObjectRepository.ResolveObject += (sender, e) =>
                {
                    if (!string.Equals(e.ObjectName, "cbMedia", StringComparison.Ordinal))
                        return;
                    try
                    {
                        e.ObjectRepository.Register(
                            "cbMedia",
                            new MediaJsBridge(tabId),
                            isAsync: true,
                            options: BindingOptions.DefaultBinder);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("cbMedia register error: " + ex.Message);
                    }
                };

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

            bool needPrompt;
            bool needUpgrade;
            string requestId = Guid.NewGuid().ToString("N");

            lock (Sync)
            {
                needUpgrade = captureActive && ((audio && !activeAudio) || (video && !activeVideo));
                needPrompt = !captureActive;

                if (captureActive && !needUpgrade)
                {
                    Console.WriteLine("Media already active audio={0} video={1} — reuse", activeAudio, activeVideo);
                    return Task.FromResult(true);
                }

                activeRequestId = requestId;
                activeTabId = tabId;
                if (!string.IsNullOrEmpty(origin))
                    activeOrigin = origin;
                if (needPrompt)
                {
                    camPacketsReceived = 0;
                    micPacketsReceived = 0;
                    pushCount = 0;
                }
            }

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingPermission[requestId] = tcs;

            Console.WriteLine(
                needUpgrade
                    ? "Media upgrade → phone id={0} audio={1} video={2}"
                    : "Media permission → phone id={0} audio={1} video={2} origin={3}",
                requestId, audio, video, origin);

            BroadcastText(
                needUpgrade ? TextPacketType.MediaCaptureUpgrade : TextPacketType.MediaPermissionRequest,
                new MediaPermissionPayload
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
                if (payload.allowed)
                {
                    captureActive = true;
                    if (payload.audio) activeAudio = true;
                    if (payload.video) activeVideo = true;
                    activeRequestId = payload.requestId;
                }
                else if (activeRequestId == payload.requestId)
                {
                    activeRequestId = null;
                    captureActive = false;
                    activeAudio = false;
                    activeVideo = false;
                }
            }

            Console.WriteLine("Media permission response id={0} allowed={1} audio={2} video={3}",
                payload.requestId, payload.allowed, payload.audio, payload.video);
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
                activeOrigin = null;
                captureActive = false;
                activeAudio = false;
                activeVideo = false;
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

            QrScanService.Reset();
            Console.WriteLine("Media capture released");
        }

        /// <summary>Stop phone uplink when the tab leaves the site that opened the camera.</summary>
        public static void OnNavigated(string tabId, string url)
        {
            string origin;
            lock (Sync)
            {
                if (!captureActive)
                    return;
                if (tabId != null && activeTabId != null &&
                    !string.Equals(tabId, activeTabId, StringComparison.Ordinal))
                    return;
                origin = activeOrigin;
            }

            if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(url))
                return;

            // Same-origin navigations (hash/query) keep the stream; cross-origin stops it.
            if (url.StartsWith(origin, StringComparison.OrdinalIgnoreCase))
                return;

            Console.WriteLine("Media stop — left origin {0} → {1}", origin, url);
            Release(tabId);
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

                // Drop most uplink frames before expensive GDI rotate + JS push.
                var now = Environment.TickCount;
                if (now - lastPushTick < 240 || Volatile.Read(ref pushInFlight) != 0)
                {
                    Interlocked.Increment(ref camPacketsReceived);
                    return;
                }

                var jpeg = new byte[jpegLen];
                Buffer.BlockCopy(data, 12, jpeg, 0, jpegLen);

                // Always upright the phone sensor buffer here (GDI). Client/encoder rotation was a no-op on WM.
                // Rotate270FlipNone = 90° counter-clockwise.
                jpeg = RotateJpegCounterClockwise90(jpeg);

                int n;
                lock (Sync)
                {
                    latestJpeg = jpeg;
                    camPacketsReceived++;
                    n = camPacketsReceived;
                }
                if (n == 1 || n % 25 == 0)
                    Console.WriteLine("CAM packets={0} jpeg={1}B (rotated CCW90 on server)", n, jpeg.Length);

                PushJpegToPages(jpeg);
                QrScanService.TryDecodeAsync(jpeg);
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
                var mn = Interlocked.Increment(ref micPacketsReceived);
                if (mn == 1 || mn % 100 == 0)
                    Console.WriteLine("MIC packets={0}", mn);
            }
        }

        /// <summary>
        /// 90° counter-clockwise via GDI+ (Rotate270FlipNone). Applied to every phone CAM frame.
        /// </summary>
        private static byte[] RotateJpegCounterClockwise90(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length < 24)
                return jpeg;

            try
            {
                using (var input = new MemoryStream(jpeg))
                using (var bmp = new Bitmap(input))
                {
                    var before = bmp.Width + "x" + bmp.Height;
                    // Always CCW90 — phone sensor orientation that matched what the user saw as upright.
                    bmp.RotateFlip(RotateFlipType.Rotate270FlipNone);
                    var after = bmp.Width + "x" + bmp.Height;

                    using (var output = new MemoryStream())
                    {
                        var eps = new EncoderParameters(1);
                        eps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 55L);
                        ImageCodecInfo jpgCodec = null;
                        foreach (var c in ImageCodecInfo.GetImageEncoders())
                        {
                            if (c.FormatID == ImageFormat.Jpeg.Guid)
                            {
                                jpgCodec = c;
                                break;
                            }
                        }
                        if (jpgCodec != null)
                            bmp.Save(output, jpgCodec, eps);
                        else
                            bmp.Save(output, ImageFormat.Jpeg);

                        if (camPacketsReceived < 3)
                            Console.WriteLine("CAM rotate CCW90 {0} → {1}", before, after);

                        return output.ToArray();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("CAM rotate failed: " + ex.Message);
                return jpeg;
            }
        }

        /// <summary>Push JPEG into the main frame only — throttled. Flooding every iframe freezes WM10.</summary>
        private static void PushJpegToPages(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length == 0)
                return;

            // ~4 fps into the page.
            var now = Environment.TickCount;
            if (now - lastPushTick < 240)
                return;
            if (Interlocked.CompareExchange(ref pushInFlight, 1, 0) != 0)
                return;
            lastPushTick = now;

            var browser = TabManager.ActiveBrowser;
            if (browser == null || !browser.IsBrowserInitialized)
            {
                Interlocked.Exchange(ref pushInFlight, 0);
                return;
            }

            string b64;
            try
            {
                b64 = Convert.ToBase64String(jpeg);
            }
            catch
            {
                Interlocked.Exchange(ref pushInFlight, 0);
                return;
            }

            var jsArg = JsonConvert.SerializeObject(b64);
            var script = "window.__cbPush&&window.__cbPush(" + jsArg + ");";

            try
            {
                var cefBrowser = browser.GetBrowser();
                var main = cefBrowser != null ? cefBrowser.MainFrame : null;
                if (main != null && main.IsValid)
                    main.ExecuteJavaScriptAsync(script);

                var p = Interlocked.Increment(ref pushCount);
                if (p == 1 || p % 25 == 0)
                    Console.WriteLine("CAM pushed to page frames={0}", p);
            }
            catch (Exception ex)
            {
                if (pushCount < 3)
                    Console.WriteLine("CAM push error: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref pushInFlight, 0);
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

        public static void ClientLog(string message)
        {
            if (!string.IsNullOrEmpty(message))
                Console.WriteLine("[media-js] " + message);
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

        private const string GetUserMediaShimScript = @"
(function () {
  if (window.__cbMediaShim === 4) return;
  window.__cbMediaShim = 4;

  var live = null, liveCtx = null, lastW = 640, lastH = 480, users = 0, pushCount = 0;
  var HAVE_ENOUGH_DATA = 4;

  function log(m) {
    try { if (window.cbMedia && cbMedia.log) cbMedia.log(String(m)); } catch (e) {}
    try { console.log('[cbMedia]', m); } catch (e) {}
  }

  function ensureLive() {
    if (live) return;
    live = document.createElement('canvas');
    live.width = 640; live.height = 480;
    live.__cbPhoneCam = true;
    liveCtx = live.getContext('2d', { alpha: false });
    liveCtx.fillStyle = '#203040';
    liveCtx.fillRect(0, 0, 640, 480);
    liveCtx.fillStyle = '#fff';
    liveCtx.font = '16px sans-serif';
    liveCtx.fillText('Waiting for phone camera…', 20, 40);
  }

  function activatePhoneVideo(video) {
    if (!video || video.__cbPhoneActivated) return;
    video.__cbPhoneActivated = true;
    video.__cbUsePhoneCam = true;
    video.__cbRvfc = [];

    try {
      Object.defineProperty(video, 'readyState', { get: function () { return HAVE_ENOUGH_DATA; }, configurable: true });
      Object.defineProperty(video, 'networkState', { get: function () { return 1; }, configurable: true });
      Object.defineProperty(video, 'paused', { get: function () { return false; }, configurable: true });
      Object.defineProperty(video, 'ended', { get: function () { return false; }, configurable: true });
      Object.defineProperty(video, 'seeking', { get: function () { return false; }, configurable: true });
      Object.defineProperty(video, 'videoWidth', { get: function () { return lastW || 640; }, configurable: true });
      Object.defineProperty(video, 'videoHeight', { get: function () { return lastH || 480; }, configurable: true });
      Object.defineProperty(video, 'currentTime', {
        get: function () { return (pushCount / 10); },
        set: function () {},
        configurable: true
      });
      Object.defineProperty(video, 'duration', { get: function () { return Infinity; }, configurable: true });
    } catch (e) { log('prop patch fail ' + e); }

    // html5-qrcode / zxing often use this instead of rAF.
    video.requestVideoFrameCallback = function (cb) {
      if (!video.__cbRvfc) video.__cbRvfc = [];
      var id = (video.__cbRvfcSeq = (video.__cbRvfcSeq || 0) + 1);
      video.__cbRvfc.push({ id: id, cb: cb });
      return id;
    };
    video.cancelVideoFrameCallback = function (id) {
      if (!video.__cbRvfc) return;
      video.__cbRvfc = video.__cbRvfc.filter(function (x) { return x.id !== id; });
    };

    var origPlay = video.play ? video.play.bind(video) : null;
    video.play = function () {
      try {
        ['loadstart','loadedmetadata','loadeddata','canplay','canplaythrough','playing','play'].forEach(function (ev) {
          try { video.dispatchEvent(new Event(ev)); } catch (e) {}
        });
      } catch (e) {}
      return Promise.resolve();
    };

    setTimeout(function () {
      try { video.play(); } catch (e) {}
    }, 0);

    log('video activated for QR scanning');
  }

  function fireVideoFrameCallbacks() {
    var list = document.querySelectorAll('video');
    var now = (window.performance && performance.now) ? performance.now() : Date.now();
    for (var i = 0; i < list.length; i++) {
      var v = list[i];
      if (!v.__cbUsePhoneCam) continue;
      if (v.__cbRvfc && v.__cbRvfc.length) {
        var pending = v.__cbRvfc.splice(0, v.__cbRvfc.length);
        for (var j = 0; j < pending.length; j++) {
          try {
            pending[j].cb(now, {
              presentationTime: now,
              expectedDisplayTime: now,
              width: lastW,
              height: lastH,
              mediaTime: pushCount / 10,
              presentedFrames: pushCount,
              processingDuration: 0
            });
          } catch (e) {}
        }
      }
      try { v.dispatchEvent(new Event('timeupdate')); } catch (e) {}
    }
  }

  // C# pushes each JPEG here.
  window.__cbPush = function (b64) {
    if (!b64) return;
    ensureLive();
    var img = new Image();
    img.onload = function () {
      // Server already rotates CCW90; just paint upright frames.
      var iw = img.naturalWidth || 640;
      var ih = img.naturalHeight || 480;
      live.width = iw; live.height = ih;
      liveCtx.drawImage(img, 0, 0);
      lastW = iw; lastH = ih;
      coverVideos();
      fireVideoFrameCallbacks();
      try {
        document.querySelectorAll('video').forEach(function (v) {
          if (v.srcObject && v.srcObject.__cbPhone) {
            var t = v.srcObject.getVideoTracks()[0];
            if (t && t.requestFrame) t.requestFrame();
          }
        });
      } catch (e) {}
      pushCount++;
      if (pushCount === 1) log('first pushed frame ' + lastW + 'x' + lastH);
    };
    img.onerror = function () { if (pushCount < 3) log('jpeg decode failed'); };
    img.src = 'data:image/jpeg;base64,' + b64;
  };

  function coverVideos() {
    ensureLive();
    var list = document.querySelectorAll('video');
    for (var i = 0; i < list.length; i++) {
      var v = list[i];
      if (v.srcObject && v.srcObject.__cbPhone) activatePhoneVideo(v);

      var r = v.getBoundingClientRect();
      if (r.width < 2 || r.height < 2) continue;

      if (!v.__cbCover) {
        var c = document.createElement('canvas');
        c.setAttribute('data-cb-cover', '1');
        v.__cbCover = c;
        if (v.parentNode) v.parentNode.insertBefore(c, v.nextSibling);
        else document.documentElement.appendChild(c);
      }
      var cover = v.__cbCover;
      cover.width = live.width;
      cover.height = live.height;
      try { cover.getContext('2d').drawImage(live, 0, 0); } catch (e) {}
      cover.style.cssText =
        'position:fixed;left:' + r.left + 'px;top:' + r.top + 'px;width:' + r.width +
        'px;height:' + r.height + 'px;z-index:2147483647;pointer-events:none;' +
        'background:#000;';
      try { v.style.opacity = '0'; } catch (e) {}
    }
  }

  setInterval(function () { if (users > 0 || pushCount > 0) coverVideos(); }, 250);

  // Hook srcObject so QR libs see a 'live' video element immediately.
  (function () {
    var proto = HTMLMediaElement.prototype;
    var desc = Object.getOwnPropertyDescriptor(proto, 'srcObject');
    if (!desc || !desc.set) return;
    Object.defineProperty(proto, 'srcObject', {
      configurable: true,
      enumerable: desc.enumerable,
      get: function () {
        if (this.__cbSrcObject !== undefined) return this.__cbSrcObject;
        try { return desc.get.call(this); } catch (e) { return null; }
      },
      set: function (stream) {
        this.__cbSrcObject = stream;
        try { desc.set.call(this, stream); } catch (e) {}
        if (stream && stream.__cbPhone) {
          activatePhoneVideo(this);
          coverVideos();
        }
      }
    });
  })();

  // QR scanners: drawImage(video) → live canvas (always, while phone cam is active).
  (function () {
    var orig = CanvasRenderingContext2D.prototype.drawImage;
    CanvasRenderingContext2D.prototype.drawImage = function (img) {
      try {
        var isVid = img && (img.tagName === 'VIDEO' || img instanceof HTMLVideoElement);
        if (img && (img.__cbPhoneCam || img.__cbUsePhoneCam || (isVid && pushCount > 0))) {
          ensureLive();
          if (isVid) activatePhoneVideo(img);
          var args = [live];
          for (var i = 1; i < arguments.length; i++) args.push(arguments[i]);
          return orig.apply(this, args);
        }
      } catch (e) {}
      return orig.apply(this, arguments);
    };
  })();

  // Some libs use createImageBitmap(video).
  if (typeof createImageBitmap === 'function') {
    var origBmp = createImageBitmap;
    window.createImageBitmap = function (src) {
      try {
        if (src && (src.__cbPhoneCam || src.__cbUsePhoneCam ||
            ((src.tagName === 'VIDEO' || src instanceof HTMLVideoElement) && pushCount > 0))) {
          ensureLive();
          var rest = Array.prototype.slice.call(arguments, 1);
          return origBmp.apply(null, [live].concat(rest));
        }
      } catch (e) {}
      return origBmp.apply(null, arguments);
    };
  }

  // BarcodeDetector.detect(video) → detect(live canvas).
  try {
    if (window.BarcodeDetector && BarcodeDetector.prototype && BarcodeDetector.prototype.detect) {
      var origDetect = BarcodeDetector.prototype.detect;
      BarcodeDetector.prototype.detect = function (source) {
        try {
          if (source && (source.tagName === 'VIDEO' || source instanceof HTMLVideoElement) && pushCount > 0) {
            ensureLive();
            return origDetect.call(this, live);
          }
        } catch (e) {}
        return origDetect.apply(this, arguments);
      };
    }
  } catch (e) {}

  function ensureBound() {
    if (window.cbMedia) return Promise.resolve(true);
    if (window.CefSharp && CefSharp.BindObjectAsync)
      return CefSharp.BindObjectAsync('cbMedia').then(function () { return !!window.cbMedia; }).catch(function () { return false; });
    return Promise.resolve(false);
  }

  function requestAccess(audio, video) {
    return ensureBound().then(function (bound) {
      if (bound && window.cbMedia && cbMedia.requestAccess)
        return cbMedia.requestAccess(!!audio, !!video, location.origin || '');
      return false;
    });
  }

  function pullAudioBytes() {
    if (!(window.cbMedia && cbMedia.pullAudioPcmBase64)) return Promise.resolve(null);
    return Promise.resolve(cbMedia.pullAudioPcmBase64()).then(function (b64) {
      if (!b64) return null;
      var bin = atob(b64), out = new Uint8Array(bin.length);
      for (var i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
      return out;
    });
  }

  function releaseMedia() {
    users = Math.max(0, users - 1);
    if (users > 0) return;
    try { if (window.cbMedia && cbMedia.release) cbMedia.release(); } catch (e) {}
  }

  async function createStream(audio, video) {
    users++;
    ensureLive();
    log('createStream a=' + audio + ' v=' + video);
    var tracks = [];
    var stops = [];

    if (video) {
      var vs = live.captureStream(0);
      var vt = vs.getVideoTracks()[0];
      try {
        vt.getSettings = function () {
          return { width: lastW, height: lastH, frameRate: 10, deviceId: 'cb-video', facingMode: 'environment' };
        };
        vt.getCapabilities = function () {
          return { facingMode: ['environment', 'user'], width: { max: 1280 }, height: { max: 720 } };
        };
      } catch (e) {}
      tracks.push(vt);
      stops.push(function () { try { vt.stop(); } catch (e) {} });
    }

    if (audio) {
      var AC = window.AudioContext || window.webkitAudioContext;
      var ac = new AC();
      var dest = ac.createMediaStreamDestination();
      var next = ac.currentTime + 0.05;
      var alive = true;
      (async function () {
        while (alive) {
          try {
            var bytes = await pullAudioBytes();
            if (bytes && bytes.length > 8) {
              var rate = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16) | (bytes[3] << 24);
              var ch = bytes[4] | (bytes[5] << 8) | (bytes[6] << 16) | (bytes[7] << 24);
              if (rate < 8000) rate = 48000;
              if (ch < 1) ch = 1;
              var samples = Math.floor((bytes.length - 8) / 2 / ch);
              if (samples > 0) {
                var buf = ac.createBuffer(ch, samples, rate);
                for (var c = 0; c < ch; c++) {
                  var data = buf.getChannelData(c);
                  for (var i = 0; i < samples; i++) {
                    var o = 8 + (i * ch + c) * 2;
                    var s = (bytes[o] | (bytes[o + 1] << 8));
                    if (s >= 0x8000) s -= 0x10000;
                    data[i] = s / 32768;
                  }
                }
                var src = ac.createBufferSource();
                src.buffer = buf;
                src.connect(dest);
                if (next < ac.currentTime) next = ac.currentTime + 0.02;
                src.start(next);
                next += buf.duration;
              }
            }
          } catch (e) {}
          await new Promise(function (r) { setTimeout(r, 20); });
        }
      })();
      tracks.push(dest.stream.getAudioTracks()[0]);
      stops.push(function () { alive = false; try { ac.close(); } catch (e) {} });
    }

    var stream = new MediaStream(tracks);
    stream.__cbPhone = true;
    var stopped = false;
    function stopAll() {
      if (stopped) return;
      stopped = true;
      stops.forEach(function (fn) { try { fn(); } catch (e) {} });
      releaseMedia();
    }
    stream.getTracks().forEach(function (t) {
      var old = t.stop.bind(t);
      t.stop = function () { stopAll(); old(); };
    });
    return stream;
  }

  if (!navigator.mediaDevices) navigator.mediaDevices = {};
  navigator.mediaDevices.enumerateDevices = function () {
    return Promise.resolve([
      { deviceId: 'cb-audio', kind: 'audioinput', label: 'CloudBrowser Microphone', groupId: 'cb', toJSON: function(){return this;} },
      { deviceId: 'cb-video', kind: 'videoinput', label: 'CloudBrowser Camera', groupId: 'cb', toJSON: function(){return this;} }
    ]);
  };

  var gumInflight = null;
  var gumCache = null;
  navigator.mediaDevices.getUserMedia = function (constraints) {
    constraints = constraints || {};
    var a = !!constraints.audio, v = !!constraints.video;
    // Sites like scanqr spam getUserMedia — share one grant + one stream.
    if (gumCache && gumCache.a === a && gumCache.v === v && gumCache.stream &&
        (Date.now() - gumCache.at) < 8000) {
      try {
        var live = gumCache.stream.getTracks().some(function (t) { return t.readyState === 'live'; });
        if (live) return Promise.resolve(gumCache.stream);
      } catch (e) {}
    }
    if (gumInflight) return gumInflight;
    log('getUserMedia a=' + a + ' v=' + v);
    gumInflight = requestAccess(a, v).then(function (ok) {
      if (!ok) {
        var err = new Error('Permission denied');
        err.name = 'NotAllowedError';
        throw err;
      }
      return createStream(a, v);
    }).then(function (stream) {
      gumCache = { a: a, v: v, at: Date.now(), stream: stream };
      gumInflight = null;
      return stream;
    }, function (e) {
      gumInflight = null;
      throw e;
    });
    return gumInflight;
  };

  var legacy = function (c, success, error) {
    navigator.mediaDevices.getUserMedia(c).then(success).catch(error || function () {});
  };
  navigator.getUserMedia = legacy;
  navigator.webkitGetUserMedia = legacy;

  log('shim ready (QR mode)');
})();";
    }

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

        public void Log(string message)
        {
            MediaBridge.ClientLog(message);
        }

        public void Release()
        {
            MediaBridge.Release(tabId);
        }
    }
}
