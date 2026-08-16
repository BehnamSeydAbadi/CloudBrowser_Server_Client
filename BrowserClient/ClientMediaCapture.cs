using System;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
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
        private bool disposed;
        private int videoFramesSent;
        private int videoFailures;

        public Func<byte[], Task> SendBinaryAsync { get; set; }
        public CaptureElement PreviewElement { get; set; }

        public bool IsActive
        {
            get { lock (sync) return activeRequestId != null; }
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

                    // Required on Windows Mobile — preview will not run without a CaptureElement.
                    PreviewElement.Source = capture;
                    await capture.StartPreviewAsync();
                    Debug.WriteLine("Camera preview started");
                }

                lock (sync)
                {
                    mediaCapture = capture;
                    activeRequestId = requestId;
                    pumpCts = new CancellationTokenSource();
                    videoFramesSent = 0;
                    videoFailures = 0;
                }

                if (audio)
                    await StartMicGraphAsync().ConfigureAwait(true);

                var token = pumpCts.Token;
                if (video)
                {
                    var ignoredVideo = Task.Run(() => VideoPumpLoopAsync(token));
                }
                if (audio)
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
            var micResult = await graph.CreateDeviceInputNodeAsync(Windows.Media.Capture.MediaCategory.Other);
            if (micResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                graph.Dispose();
                throw new InvalidOperationException("Mic node failed: " + micResult.Status);
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
                        var packet = BuildCamPacket(jpeg, 640, 480);
                        await SendBinaryAsync(packet).ConfigureAwait(false);
                        var n = Interlocked.Increment(ref videoFramesSent);
                        if (n == 1 || n % 25 == 0)
                            Debug.WriteLine("CAM uplink frames=" + n + " bytes=" + jpeg.Length);
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

                try { await Task.Delay(150, token).ConfigureAwait(false); }
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
                                await SendBinaryAsync(packet).ConfigureAwait(false);
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

        private static async Task<byte[]> CaptureJpegAsync(MediaCapture capture)
        {
            // Prefer preview frames — CapturePhotoToStreamAsync is flaky without photo mode / shutter.
            try
            {
                using (var videoFrame = new VideoFrame(BitmapPixelFormat.Bgra8, 640, 480))
                {
                    var preview = await capture.GetPreviewFrameAsync(videoFrame);
                    if (preview?.SoftwareBitmap == null)
                        return null;

                    using (var stream = new InMemoryRandomAccessStream())
                    {
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
                        encoder.SetSoftwareBitmap(SoftwareBitmap.Convert(
                            preview.SoftwareBitmap,
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Ignore));
                        encoder.BitmapTransform.ScaledWidth = 640;
                        encoder.BitmapTransform.ScaledHeight = 480;
                        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Linear;
                        // Quality ~0.7 via JPEG property — keep Wi‑Fi usable.
                        var props = new Windows.Graphics.Imaging.BitmapPropertySet();
                        props["ImageQuality"] = new Windows.Graphics.Imaging.BitmapTypedValue(0.7, Windows.Foundation.PropertyType.Single);
                        try { await encoder.BitmapProperties.SetPropertiesAsync(props); } catch { }
                        await encoder.FlushAsync();
                        stream.Seek(0);
                        var bytes = new byte[stream.Size];
                        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
                        return bytes;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("GetPreviewFrameAsync failed: " + ex.Message);
                return null;
            }
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
