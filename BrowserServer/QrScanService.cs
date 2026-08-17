using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ZXing;
using ZXing.Common;

namespace BrowserServer
{
    /// <summary>
    /// Decodes QR codes from phone camera JPEGs (ZXing) while capture is active.
    /// HTTP(S) payloads navigate the active tab; everything else is shown on the phone.
    /// </summary>
    public static class QrScanService
    {
        private static int decodeInFlight;
        private static string lastText;
        private static int lastHitTick;
        private const int CooldownMs = 3500;

        public static void TryDecodeAsync(byte[] jpeg)
        {
            if (jpeg == null || jpeg.Length < 64)
                return;
            if (Interlocked.CompareExchange(ref decodeInFlight, 1, 0) != 0)
                return;

            // Copy — caller may reuse/overwrite the buffer.
            var copy = new byte[jpeg.Length];
            Buffer.BlockCopy(jpeg, 0, copy, 0, jpeg.Length);

            Task.Run(() =>
            {
                try
                {
                    DecodeAndHandle(copy);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("QR decode error: " + ex.Message);
                }
                finally
                {
                    Interlocked.Exchange(ref decodeInFlight, 0);
                }
            });
        }

        public static void Reset()
        {
            lastText = null;
            lastHitTick = 0;
        }

        private static void DecodeAndHandle(byte[] jpeg)
        {
            if (!MediaBridge.IsCaptureActive)
                return;

            var text = DecodeQrText(jpeg);
            if (string.IsNullOrWhiteSpace(text))
                return;

            text = text.Trim();
            var now = Environment.TickCount;
            if (string.Equals(text, lastText, StringComparison.Ordinal) &&
                (now - lastHitTick) < CooldownMs)
                return;

            lastText = text;
            lastHitTick = now;

            Console.WriteLine("QR detected: " + Truncate(text, 120));

            BroadcastDetected(text);

            Uri uri;
            if (TryGetHttpUrl(text, out uri))
            {
                Console.WriteLine("QR navigate → " + uri.AbsoluteUri);
                // Stop camera first so navigation does not fight the uplink.
                MediaBridge.Release(null);
                TabManager.NavigateActive(uri.AbsoluteUri);
            }
        }

        private static string DecodeQrText(byte[] jpeg)
        {
            using (var input = new MemoryStream(jpeg))
            using (var bmp = new Bitmap(input))
            {
                // Slight downscale speeds TryHarder without hurting typical phone QR size.
                Bitmap work = bmp;
                Bitmap scaled = null;
                try
                {
                    if (bmp.Width > 800 || bmp.Height > 800)
                    {
                        var w = bmp.Width > bmp.Height ? 800 : (int)(bmp.Width * (800.0 / bmp.Height));
                        var h = bmp.Height > bmp.Width ? 800 : (int)(bmp.Height * (800.0 / bmp.Width));
                        scaled = new Bitmap(bmp, w, h);
                        work = scaled;
                    }

                    var source = ToLuminanceSource(work);
                    var reader = new BarcodeReaderGeneric
                    {
                        AutoRotate = true,
                        Options = new DecodingOptions
                        {
                            TryHarder = true,
                            PossibleFormats = new List<BarcodeFormat> { BarcodeFormat.QR_CODE },
                            CharacterSet = "UTF-8"
                        }
                    };

                    var result = reader.Decode(source);
                    if (result != null && !string.IsNullOrEmpty(result.Text))
                        return result.Text;

                    // Dark-on-light vs light-on-dark.
                    result = reader.Decode(new InvertedLuminanceSource(source));
                    return result != null ? result.Text : null;
                }
                finally
                {
                    if (scaled != null)
                        scaled.Dispose();
                }
            }
        }

        private static LuminanceSource ToLuminanceSource(Bitmap bmp)
        {
            var w = bmp.Width;
            var h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                var raw = new byte[stride * h];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);

                // Format24bppRgb is BGR in memory; pack tightly if stride has padding.
                byte[] bgr;
                if (stride == w * 3)
                {
                    bgr = raw;
                }
                else
                {
                    bgr = new byte[w * h * 3];
                    for (int y = 0; y < h; y++)
                        Buffer.BlockCopy(raw, y * stride, bgr, y * w * 3, w * 3);
                }

                return new RGBLuminanceSource(bgr, w, h, RGBLuminanceSource.BitmapFormat.BGR24);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static bool TryGetHttpUrl(string text, out Uri uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            var t = text.Trim();
            if (t.StartsWith("URL:", StringComparison.OrdinalIgnoreCase))
                t = t.Substring(4).Trim();

            if (!t.Contains("://") &&
                (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ||
                 t.IndexOf('.') > 0 && t.IndexOf(' ') < 0))
                t = "https://" + t;

            if (!Uri.TryCreate(t, UriKind.Absolute, out uri))
                return false;

            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        private static void BroadcastDetected(string text)
        {
            try
            {
                var server = TabManager.Server;
                if (server == null)
                    return;

                server.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new TextPacket
                {
                    PType = TextPacketType.QrDetected,
                    text = text
                }));
            }
            catch
            {
            }
        }

        private static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
                return s;
            return s.Substring(0, max) + "…";
        }
    }
}
