using System;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Display;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Media.Render;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;

namespace BrowserClient
{
    /// <summary>
    /// Captures phone camera (JPEG) and/or microphone (PCM) for uplink to BrowserServer.
    /// Camera requires a CaptureElement preview sink on WM10.
    /// </summary>
    public sealed class ClientMediaCapture : IDisposable
    {
        private readonly object sync = new object();
        private MediaCapture mediaCapture;
        private AudioGraph audioGraph;
        private AudioDeviceInputNode micNode;
        private AudioFrameOutputNode frameOut;
        private CancellationTokenSource pumpCts;
        private string activeRequestId;
        private bool wantAudio;
        private bool wantVideo;
        private bool disposed;
        private int videoFramesSent;
        private int videoFailures;
        private int micPacketsSent;

        /// <summary>Cached on UI thread — phone is usually portrait; sensor frames are landscape.</summary>
        private BitmapRotation encodeRotation = BitmapRotation.Clockwise270Degrees;
        private VideoRotation previewRotation = VideoRotation.Clockwise270Degrees;
        private int outWidth = 480;
        private int outHeight = 640;

        public Func<byte[], Task> SendBinaryAsync { get; set; }
        public CaptureElement PreviewElement { get; set; }

        public bool IsActive
        {
            get { lock (sync) return activeRequestId != null; }
        }

        public bool HasAudio
        {
            get { lock (sync) return wantAudio && audioGraph != null; }
        }

        public bool HasVideo
        {
            get { lock (sync) return wantVideo && mediaCapture != null; }
        }

        /// <summary>Start or upgrade an existing session (e.g. add mic after video-only).</summary>
        public async Task<bool> EnsureAsync(string requestId, bool audio, bool video)
        {
            if (string.IsNullOrEmpty(requestId) || (!audio && !video))
                return false;

            bool already;
            lock (sync)
            {
                already = activeRequestId != null;
            }

            if (!already)
                return await StartAsync(requestId, audio, video).ConfigureAwait(true);

            try
            {
                if (video && !HasVideo)
                {
                    // Rare — restart full session with both.
                    return await StartAsync(requestId, audio || HasAudio, true).ConfigureAwait(true);
                }

                if (audio && !HasAudio)
                {
                    await StartMicGraphAsync().ConfigureAwait(true);
                    CancellationToken token;
                    lock (sync)
                    {
                        wantAudio = true;
                        activeRequestId = requestId;
                        token = pumpCts != null ? pumpCts.Token : CancellationToken.None;
                    }
                    var ignored = Task.Run(() => MicPumpLoopAsync(token));
                    Debug.WriteLine("Mic upgraded onto existing capture session");
                }

                lock (sync) activeRequestId = requestId;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MediaCapture.EnsureAsync failed: " + ex);
                return false;
            }
        }

        public async Task<bool> StartAsync(string requestId, bool audio, bool video)
        {
            if (string.IsNullOrEmpty(requestId) || (!audio && !video))
                return false;

            await StopInternalAsync().ConfigureAwait(true);

            try
            {
                MediaCapture capture = null;
                if (video)
                {
                    if (PreviewElement == null)
                    {
                        Debug.WriteLine("Camera start failed: PreviewElement is null");
                        return false;
                    }

                    var settings = new MediaCaptureInitializationSettings
                    {
                        StreamingCaptureMode = StreamingCaptureMode.Video,
                        PhotoCaptureSource = PhotoCaptureSource.VideoPreview
                    };

                    capture = new MediaCapture();
                    await capture.InitializeAsync(settings);

                    RefreshCaptureRotation();

                    PreviewElement.Source = capture;
                    // Make preview slightly visible so WM keeps the pipeline hot.
                    PreviewElement.Opacity = 0.01;
                    PreviewElement.Width = 32;
                    PreviewElement.Height = 24;
                    await capture.StartPreviewAsync();
                    try
                    {
                        capture.SetPreviewRotation(previewRotation);
                    }
                    catch (Exception rotEx)
                    {
                        Debug.WriteLine("SetPreviewRotation failed: " + rotEx.Message);
                    }
                    Debug.WriteLine("Camera preview started rotation=" + encodeRotation + " out=" + outWidth + "x" + outHeight);
                }

                lock (sync)
                {
                    mediaCapture = capture;
                    activeRequestId = requestId;
                    wantAudio = audio;
                    wantVideo = video;
                    pumpCts = new CancellationTokenSource();
                    videoFramesSent = 0;
                    videoFailures = 0;
                    micPacketsSent = 0;
                }

                if (audio)
                {
                    try
                    {
                        await StartMicGraphAsync().ConfigureAwait(true);
                    }
                    catch (Exception micEx)
                    {
                        Debug.WriteLine("Mic start failed (continuing with video): " + micEx.Message);
                        lock (sync) wantAudio = false;
                    }
                }

                var token = pumpCts.Token;
                if (video)
                {
                    var ignoredVideo = Task.Run(() => VideoPumpLoopAsync(token));
                }
                if (wantAudio)
                {
                    var ignoredAudio = Task.Run(() => MicPumpLoopAsync(token));
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("MediaCapture.StartAsync failed: " + ex);
                await StopInternalAsync().ConfigureAwait(true);
                return false;
            }
        }

        public Task StopAsync()
        {
            return StopInternalAsync();
        }

        public void StopIfRequest(string requestId)
        {
            lock (sync)
            {
                if (activeRequestId == null)
                    return;
                if (!string.IsNullOrEmpty(requestId) &&
                    !string.Equals(activeRequestId, requestId, StringComparison.Ordinal))
                    return;
            }
            var ignored = StopInternalAsync();
        }

        private async Task StopInternalAsync()
        {
            CancellationTokenSource cts;
            MediaCapture capture;
            lock (sync)
            {
                cts = pumpCts;
                pumpCts = null;
                capture = mediaCapture;
                mediaCapture = null;
                activeRequestId = null;
                wantAudio = false;
                wantVideo = false;
            }

            try { cts?.Cancel(); } catch { }

            try
            {
                if (PreviewElement != null)
                    PreviewElement.Source = null;
            }
            catch { }

            try
            {
                if (capture != null)
                {
                    try { await capture.StopPreviewAsync(); } catch { }
                    capture.Dispose();
                }
            }
            catch { }

            DisposeAudioGraph();
        }

        private async Task StartMicGraphAsync()
        {
            var settings = new AudioGraphSettings(AudioRenderCategory.Media)
            {
                QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault
            };

            var create = await AudioGraph.CreateAsync(settings);
            if (create.Status != AudioGraphCreationStatus.Success)
                throw new InvalidOperationException("AudioGraph create failed: " + create.Status);

            var graph = create.Graph;
            AudioDeviceNodeCreationStatus status;
            CreateAudioDeviceInputNodeResult micResult = null;

            // Try a few categories — WM devices vary.
            var categories = new[]
            {
                Windows.Media.Capture.MediaCategory.Communications,
                Windows.Media.Capture.MediaCategory.Speech,
                Windows.Media.Capture.MediaCategory.Other
            };
            foreach (var cat in categories)
            {
                micResult = await graph.CreateDeviceInputNodeAsync(cat);
                status = micResult.Status;
                if (status == AudioDeviceNodeCreationStatus.Success)
                    break;
                Debug.WriteLine("Mic node category " + cat + " failed: " + status);
            }

            if (micResult == null || micResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                graph.Dispose();
                throw new InvalidOperationException("Mic node failed: " + (micResult != null ? micResult.Status.ToString() : "null"));
            }

            var outNode = graph.CreateFrameOutputNode();
            micResult.DeviceInputNode.AddOutgoingConnection(outNode);
            graph.Start();

            lock (sync)
            {
                audioGraph = graph;
                micNode = micResult.DeviceInputNode;
                frameOut = outNode;
            }

            Debug.WriteLine("Mic graph started rate=" + graph.EncodingProperties.SampleRate);
        }

        private void DisposeAudioGraph()
        {
            AudioGraph graph;
            AudioDeviceInputNode mic;
            AudioFrameOutputNode fout;
            lock (sync)
            {
                graph = audioGraph;
                mic = micNode;
                fout = frameOut;
                audioGraph = null;
                micNode = null;
                frameOut = null;
            }

            try { fout?.Stop(); } catch { }
            try { mic?.Dispose(); } catch { }
            try { graph?.Stop(); graph?.Dispose(); } catch { }
        }

        private async Task VideoPumpLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    MediaCapture capture;
                    lock (sync) capture = mediaCapture;
                    if (capture == null)
                        break;

                    byte[] jpeg = await CaptureJpegAsync(capture).ConfigureAwait(false);
                    if (jpeg != null && jpeg.Length > 0 && SendBinaryAsync != null)
                    {
                        int w, h;
                        lock (sync) { w = outWidth; h = outHeight; }
                        var packet = BuildCamPacket(jpeg, w, h);
                        await SendBinaryAsync(packet).ConfigureAwait(false);
                        var n = Interlocked.Increment(ref videoFramesSent);
                        if (n == 1 || n % 25 == 0)
                            Debug.WriteLine("CAM uplink frames=" + n + " bytes=" + jpeg.Length + " " + w + "x" + h);
                    }
                    else
                    {
                        var fails = Interlocked.Increment(ref videoFailures);
                        if (fails <= 3 || fails % 25 == 0)
                            Debug.WriteLine("CAM frame capture returned empty (failures=" + fails + ")");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("CAM pump error: " + ex.Message);
                }

                try { await Task.Delay(250, token).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
        }

        private async Task MicPumpLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    AudioFrameOutputNode fout;
                    AudioGraph graph;
                    lock (sync)
                    {
                        fout = frameOut;
                        graph = audioGraph;
                    }
                    if (fout == null || graph == null)
                        break;

                    using (var frame = fout.GetFrame())
                    {
                        if (frame != null)
                        {
                            var packet = FrameToMicPacket(frame, (int)graph.EncodingProperties.SampleRate);
                            if (packet != null && SendBinaryAsync != null)
                            {
                                await SendBinaryAsync(packet).ConfigureAwait(false);
                                var n = Interlocked.Increment(ref micPacketsSent);
                                if (n == 1 || n % 100 == 0)
                                    Debug.WriteLine("MIC uplink packets=" + n);
                            }
                        }
                    }
                }
                catch
                {
                }

                try { await Task.Delay(20, token).ConfigureAwait(false); }
                catch (TaskCanceledException) { break; }
            }
        }

        /// <summary>
        /// Phone cameras deliver landscape sensor buffers. When the device is held in portrait
        /// (normal for WM), rotate so websites see an upright vertical frame.
        /// </summary>
        private void RefreshCaptureRotation()
        {
            DisplayOrientations orientation;
            try
            {
                orientation = DisplayInformation.GetForCurrentView().CurrentOrientation;
            }
            catch
            {
                orientation = DisplayOrientations.Portrait;
            }

            // Back-camera style mapping. User needs counter-clockwise 90° in portrait
            // (= Clockwise270Degrees) so the upright phone feed is not landscape.
            switch (orientation)
            {
                case DisplayOrientations.PortraitFlipped:
                    previewRotation = VideoRotation.Clockwise90Degrees;
                    encodeRotation = BitmapRotation.Clockwise90Degrees;
                    outWidth = 480;
                    outHeight = 640;
                    break;
                case DisplayOrientations.LandscapeFlipped:
                    previewRotation = VideoRotation.Clockwise180Degrees;
                    encodeRotation = BitmapRotation.Clockwise180Degrees;
                    outWidth = 640;
                    outHeight = 480;
                    break;
                case DisplayOrientations.Landscape:
                    previewRotation = VideoRotation.None;
                    encodeRotation = BitmapRotation.None;
                    outWidth = 640;
                    outHeight = 480;
                    break;
                case DisplayOrientations.Portrait:
                default:
                    previewRotation = VideoRotation.Clockwise270Degrees;
                    encodeRotation = BitmapRotation.Clockwise270Degrees;
                    outWidth = 480;
                    outHeight = 640;
                    break;
            }
        }

        private async Task<byte[]> CaptureJpegAsync(MediaCapture capture)
        {
            try
            {
                const int scaleW = 640;
                const int scaleH = 480;

                using (var videoFrame = new VideoFrame(BitmapPixelFormat.Bgra8, scaleW, scaleH))
                {
                    var preview = await capture.GetPreviewFrameAsync(videoFrame);
                    if (preview?.SoftwareBitmap == null)
                        return null;

                    // BitmapEncoder.BitmapTransform.Rotation is ignored with SetSoftwareBitmap on WM —
                    // rotate the pixels ourselves (counter-clockwise 90° = Clockwise270).
                    SoftwareBitmap toEncode;
                    int jpegW, jpegH;
                    BitmapRotation rotation;
                    lock (sync) rotation = encodeRotation;

                    using (var bgra = SoftwareBitmap.Convert(
                        preview.SoftwareBitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore))
                    {
                        if (rotation == BitmapRotation.None)
                        {
                            toEncode = SoftwareBitmap.Copy(bgra);
                            jpegW = bgra.PixelWidth;
                            jpegH = bgra.PixelHeight;
                        }
                        else
                        {
                            toEncode = RotateBgra(bgra, rotation);
                            jpegW = toEncode.PixelWidth;
                            jpegH = toEncode.PixelHeight;
                        }
                    }

                    lock (sync)
                    {
                        outWidth = jpegW;
                        outHeight = jpegH;
                    }

                    try
                    {
                        using (var stream = new InMemoryRandomAccessStream())
                        {
                            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
                            encoder.SetSoftwareBitmap(toEncode);
                            var props = new BitmapPropertySet();
                            props["ImageQuality"] = new BitmapTypedValue(0.55f, Windows.Foundation.PropertyType.Single);
                            try { await encoder.BitmapProperties.SetPropertiesAsync(props); } catch { }
                            await encoder.FlushAsync();
                            stream.Seek(0);
                            var bytes = new byte[stream.Size];
                            await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
                            return bytes;
                        }
                    }
                    finally
                    {
                        toEncode.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GetPreviewFrameAsync failed: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Rotate BGRA SoftwareBitmap. Clockwise270Degrees = 90° counter-clockwise (what we need in portrait).
        /// </summary>
        private static SoftwareBitmap RotateBgra(SoftwareBitmap source, BitmapRotation rotation)
        {
            int w = source.PixelWidth;
            int h = source.PixelHeight;
            var srcBuf = new byte[4 * w * h];
            source.CopyToBuffer(srcBuf.AsBuffer());

            int dw, dh;
            byte[] dstBuf;

            if (rotation == BitmapRotation.Clockwise90Degrees)
            {
                // (x,y) → (h-1-y, x)  dest size h×w
                dw = h;
                dh = w;
                dstBuf = new byte[4 * dw * dh];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int s = (y * w + x) * 4;
                        int dx = h - 1 - y;
                        int dy = x;
                        int d = (dy * dw + dx) * 4;
                        dstBuf[d] = srcBuf[s];
                        dstBuf[d + 1] = srcBuf[s + 1];
                        dstBuf[d + 2] = srcBuf[s + 2];
                        dstBuf[d + 3] = srcBuf[s + 3];
                    }
                }
            }
            else if (rotation == BitmapRotation.Clockwise270Degrees)
            {
                // 90° counter-clockwise: (x,y) → (y, w-1-x)  dest size h×w
                dw = h;
                dh = w;
                dstBuf = new byte[4 * dw * dh];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int s = (y * w + x) * 4;
                        int dx = y;
                        int dy = w - 1 - x;
                        int d = (dy * dw + dx) * 4;
                        dstBuf[d] = srcBuf[s];
                        dstBuf[d + 1] = srcBuf[s + 1];
                        dstBuf[d + 2] = srcBuf[s + 2];
                        dstBuf[d + 3] = srcBuf[s + 3];
                    }
                }
            }
            else if (rotation == BitmapRotation.Clockwise180Degrees)
            {
                dw = w;
                dh = h;
                dstBuf = new byte[4 * dw * dh];
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int s = (y * w + x) * 4;
                        int dx = w - 1 - x;
                        int dy = h - 1 - y;
                        int d = (dy * dw + dx) * 4;
                        dstBuf[d] = srcBuf[s];
                        dstBuf[d + 1] = srcBuf[s + 1];
                        dstBuf[d + 2] = srcBuf[s + 2];
                        dstBuf[d + 3] = srcBuf[s + 3];
                    }
                }
            }
            else
            {
                return SoftwareBitmap.Copy(source);
            }

            var dest = new SoftwareBitmap(BitmapPixelFormat.Bgra8, dw, dh, BitmapAlphaMode.Ignore);
            dest.CopyFromBuffer(dstBuf.AsBuffer());
            return dest;
        }

        private static byte[] BuildCamPacket(byte[] jpeg, int width, int height)
        {
            var packet = new byte[12 + jpeg.Length];
            packet[0] = (byte)'C';
            packet[1] = (byte)'A';
            packet[2] = (byte)'M';
            packet[3] = (byte)' ';
            packet[4] = (byte)(width & 0xFF);
            packet[5] = (byte)((width >> 8) & 0xFF);
            packet[6] = (byte)(height & 0xFF);
            packet[7] = (byte)((height >> 8) & 0xFF);
            WriteInt32(packet, 8, jpeg.Length);
            System.Buffer.BlockCopy(jpeg, 0, packet, 12, jpeg.Length);
            return packet;
        }

        private static unsafe byte[] FrameToMicPacket(AudioFrame frame, int sampleRate)
        {
            using (var buffer = frame.LockBuffer(AudioBufferAccessMode.Read))
            using (var reference = buffer.CreateReference())
            {
                byte* data;
                uint capacity;
                ((IMemoryBufferByteAccess)reference).GetBuffer(out data, out capacity);
                if (capacity < 4)
                    return null;

                int floatCount = (int)(capacity / 4);
                if (floatCount <= 0)
                    return null;

                int frames = floatCount;
                var packet = new byte[16 + frames * 2];
                packet[0] = (byte)'M';
                packet[1] = (byte)'I';
                packet[2] = (byte)'C';
                packet[3] = (byte)' ';
                WriteInt32(packet, 4, sampleRate);
                WriteInt32(packet, 8, 1);
                WriteInt32(packet, 12, frames);

                int o = 16;
                for (int i = 0; i < frames; i++)
                {
                    float f = ((float*)data)[i];
                    if (f > 1f) f = 1f;
                    if (f < -1f) f = -1f;
                    short s = (short)(f * 32767f);
                    packet[o++] = (byte)(s & 0xFF);
                    packet[o++] = (byte)((s >> 8) & 0xFF);
                }
                return packet;
            }
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            try { StopInternalAsync().Wait(1000); } catch { }
        }

        [System.Runtime.InteropServices.ComImport]
        [System.Runtime.InteropServices.Guid("5B0D3235-4DBA-4D44-865E-8F1C81F3ED61")]
        [System.Runtime.InteropServices.InterfaceType(System.Runtime.InteropServices.ComInterfaceType.InterfaceIsIUnknown)]
        private unsafe interface IMemoryBufferByteAccess
        {
            void GetBuffer(out byte* buffer, out uint capacity);
        }
    }
}
