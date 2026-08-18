using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using CefSharp;
using CefSharp.Structs;
using Newtonsoft.Json.Linq;

namespace BrowserServer
{
    /// <summary>
    /// While a page &lt;video&gt; is playing, stream Chromium's OnPaint buffer to the phone
    /// the same way StreamingAudioHandler streams PCM — native frames, no overlay.
    /// </summary>
    public static class VideoPlaybackBridge
    {
        private static readonly object Sync = new object();
        private static int sendInFlight;
        private static int lastPaintSendTick;
        private static int lastPollTick;
        private static int framesSent;
        private static bool playing;
        private static bool loggedCodecs;
        private static ImageCodecInfo jpegCodec;
        private static MemoryStream encodeStream;

        public static bool IsStreaming
        {
            get { lock (Sync) return playing; }
        }

        public static void Poll(IWebBrowser browser)
        {
            if (browser == null || !browser.IsBrowserInitialized)
                return;

            var now = Environment.TickCount;
            if (now - lastPollTick < 400)
                return;
            lastPollTick = now;

            try
            {
                var cef = browser.GetBrowser();
                if (cef == null)
                    return;

                if (!loggedCodecs)
                    ProbeCodecs(cef.MainFrame);

                var frames = cef.GetAllFrames();
                if (frames == null || frames.Count == 0)
                {
                    SetPlaying(false, null);
                    return;
                }

                int remaining = frames.Count;
                bool anyPlaying = false;
                string detail = null;
                foreach (var frame in frames)
                {
                    if (frame == null || !frame.IsValid)
                    {
                        if (Interlocked.Decrement(ref remaining) == 0 && !anyPlaying)
                            SetPlaying(false, null);
                        continue;
                    }

                    frame.EvaluateScriptAsync(PlayingVideoScript, null, 1, TimeSpan.FromSeconds(2), false)
                        .ContinueWith(t =>
                        {
                            try
                            {
                                bool isPlaying;
                                string size;
                                if (TryReadPlaying(t.Result, out isPlaying, out size) && isPlaying)
                                {
                                    anyPlaying = true;
                                    detail = size;
                                }
                            }
                            catch
                            {
                            }

                            if (Interlocked.Decrement(ref remaining) == 0)
                                SetPlaying(anyPlaying, detail);
                        });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Video poll error: " + ex.Message);
            }
        }

        public static void HandlePaint(PaintElementType type, IntPtr buffer, int width, int height)
        {
            if (type != PaintElementType.View || buffer == IntPtr.Zero || width < 2 || height < 2)
                return;
            if (!IsStreaming)
                return;
            if (StreamingDownloadHandler.IsStreamingToClients)
                return;
            if (StreamingAudioHandler.PendingCount > 12)
                return;

            var now = Environment.TickCount;
            if (now - lastPaintSendTick < 40)
                return;
            if (Interlocked.CompareExchange(ref sendInFlight, 1, 0) != 0)
                return;

            lastPaintSendTick = now;
            try
            {
                var server = TabManager.Server;
                if (server == null)
                    return;

                using (var bitmap = new Bitmap(width, height, 4 * width, PixelFormat.Format32bppArgb, buffer))
                {
                    if (jpegCodec == null)
                        jpegCodec = GetJpegCodec();
                    if (encodeStream == null)
                        encodeStream = new MemoryStream(200 * 1024);
                    encodeStream.SetLength(0);

                    var encoderParameters = new EncoderParameters(1);
                    encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
                    bitmap.Save(encodeStream, jpegCodec ?? GetJpegCodec(), encoderParameters);
                    server.WebSocketServices.Broadcast(encodeStream.ToArray());
                }

                if (Interlocked.Increment(ref framesSent) == 1)
                    Console.WriteLine("Video paint stream ON {0}x{1}", width, height);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Video paint error: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref sendInFlight, 0);
            }
        }

        private static void SetPlaying(bool value, string detail)
        {
            lock (Sync)
            {
                if (playing == value)
                    return;
                playing = value;
                if (value)
                {
                    framesSent = 0;
                    Console.WriteLine("HTML video playing" + (string.IsNullOrEmpty(detail) ? "" : " " + detail));
                }
                else
                {
                    Console.WriteLine("HTML video stopped");
                }
            }
        }

        private static void ProbeCodecs(IFrame frame)
        {
            if (frame == null || !frame.IsValid)
                return;
            loggedCodecs = true;
            frame.EvaluateScriptAsync(CodecProbeScript, null, 1, TimeSpan.FromSeconds(2), false)
                .ContinueWith(t =>
                {
                    try
                    {
                        string h264, aac, vp9;
                        if (!TryReadCodecs(t.Result, out h264, out aac, out vp9))
                            return;
                        Console.WriteLine("Video codecs h264={0} aac={1} vp9={2}", h264, aac, vp9);
                    }
                    catch
                    {
                    }
                });
        }

        private static bool TryReadPlaying(JavascriptResponse response, out bool isPlaying, out string size)
        {
            isPlaying = false;
            size = null;
            var obj = AsObject(response);
            if (obj == null)
                return false;
            isPlaying = ToBool(obj["playing"]);
            if (isPlaying)
                size = string.Format("{0}x{1}", ToInt(obj["w"]), ToInt(obj["h"]));
            return true;
        }

        private static bool TryReadCodecs(JavascriptResponse response, out string h264, out string aac, out string vp9)
        {
            h264 = aac = vp9 = "";
            var obj = AsObject(response);
            if (obj == null)
                return false;
            h264 = obj.Value<string>("h264") ?? "";
            aac = obj.Value<string>("aac") ?? "";
            vp9 = obj.Value<string>("vp9") ?? "";
            return true;
        }

        private static JObject AsObject(JavascriptResponse response)
        {
            if (response == null || !response.Success || response.Result == null)
                return null;
            var obj = response.Result as JObject;
            if (obj != null)
                return obj;
            if (response.Result is string s && s.TrimStart().StartsWith("{"))
                return JObject.Parse(s);
            return JObject.FromObject(response.Result);
        }

        private static bool ToBool(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return false;
            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>();
            if (token.Type == JTokenType.Integer)
                return token.Value<int>() != 0;
            bool parsed;
            return bool.TryParse(token.ToString(), out parsed) && parsed;
        }

        private static int ToInt(JToken token)
        {
            if (token == null || token.Type == JTokenType.Null)
                return 0;
            int parsed;
            return int.TryParse(token.ToString(), out parsed) ? parsed : 0;
        }

        private static ImageCodecInfo GetJpegCodec()
        {
            foreach (var codec in ImageCodecInfo.GetImageDecoders())
            {
                if (codec.FormatID == ImageFormat.Jpeg.Guid)
                    return codec;
            }
            return null;
        }

        private const string PlayingVideoScript = @"
(function () {
  function collect(root, out) {
    try {
      var vs = root.querySelectorAll('video');
      for (var i = 0; i < vs.length; i++) out.push(vs[i]);
      var els = root.querySelectorAll('*');
      for (var i = 0; i < els.length; i++) {
        if (els[i].shadowRoot) collect(els[i].shadowRoot, out);
      }
    } catch (e) {}
  }
  var list = [];
  collect(document, list);
  for (var i = 0; i < list.length; i++) {
    var v = list[i];
    if (v && !v.paused && !v.ended && v.readyState >= 2 && (v.videoWidth || 0) > 0)
      return { playing: true, w: v.videoWidth || 0, h: v.videoHeight || 0 };
  }
  return { playing: false };
})();";

        private const string CodecProbeScript = @"
(function () {
  var v = document.createElement('video');
  return {
    h264: v.canPlayType('video/mp4; codecs=""avc1.42E01E""') || '',
    aac: v.canPlayType('audio/mp4; codecs=""mp4a.40.2""') || '',
    vp9: v.canPlayType('video/webm; codecs=""vp9""') || ''
  };
})();";
    }
}
