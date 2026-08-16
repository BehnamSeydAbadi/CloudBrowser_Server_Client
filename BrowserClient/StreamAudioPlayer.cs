using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.MediaProperties;
using Windows.Media.Render;

namespace BrowserClient
{
    /// <summary>
    /// Plays PCM audio packets forwarded from BrowserServer (AUDI binary frames).
    /// Uses a sample queue + QuantumStarted so playback is pull-driven and safe across awaits.
    /// </summary>
    public sealed class StreamAudioPlayer : IDisposable
    {
        private const int MaxQueuedSamples = 48000 * 2 * 2; // ~2s stereo @ 48k

        private readonly ConcurrentQueue<float> sampleQueue = new ConcurrentQueue<float>();
        private readonly object sync = new object();

        private AudioGraph graph;
        private AudioDeviceOutputNode deviceOutput;
        private AudioFrameInputNode frameInput;
        private int sampleRate;
        private int channels;
        private int nodeChannels = 2;
        private Task startTask;
        private int queuedSamples;
        private bool disposed;

        /// <summary>Packet buffer must be owned by the caller (not the shared WebSocket receive buffer).</summary>
        public void SubmitPacket(byte[] buffer, int count)
        {
            if (disposed || buffer == null || count < 16)
                return;
            if (buffer[0] != (byte)'A' || buffer[1] != (byte)'U' || buffer[2] != (byte)'D' || buffer[3] != (byte)'I')
                return;

            int rate = ReadInt32(buffer, 4);
            int ch = ReadInt32(buffer, 8);
            int frames = ReadInt32(buffer, 12);
            int pcmBytes = frames * ch * 2;
            if (rate < 8000 || ch < 1 || ch > 8 || frames <= 0 || 16 + pcmBytes > count)
                return;

            EnsureGraphStarted(rate, ch);

            int offset = 16;
            for (int i = 0; i < frames * ch; i++)
            {
                if (queuedSamples >= MaxQueuedSamples)
                {
                    float discarded;
                    if (sampleQueue.TryDequeue(out discarded))
                        System.Threading.Interlocked.Decrement(ref queuedSamples);
                }

                short s = (short)(buffer[offset] | (buffer[offset + 1] << 8));
                offset += 2;
                sampleQueue.Enqueue(s / 32768f);
                System.Threading.Interlocked.Increment(ref queuedSamples);
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                float discarded;
                while (sampleQueue.TryDequeue(out discarded)) { }
                queuedSamples = 0;
                // Keep the graph warm — sites often restart audio streams (MSE). Full teardown
                // caused long gaps / total silence while CreateAsync ran again.
                try { frameInput?.Stop(); } catch { }
            }
        }

        public void Dispose()
        {
            disposed = true;
            lock (sync)
            {
                float discarded;
                while (sampleQueue.TryDequeue(out discarded)) { }
                queuedSamples = 0;
                DisposeGraph_NoLock();
                startTask = null;
            }
        }

        private void EnsureGraphStarted(int rate, int ch)
        {
            lock (sync)
            {
                if (disposed)
                    return;

                if (graph != null && sampleRate == rate && channels == ch && frameInput != null)
                {
                    try { frameInput.Start(); } catch { }
                    return;
                }

                if (startTask != null && !startTask.IsCompleted)
                    return;

                DisposeGraph_NoLock();
                float discarded;
                while (sampleQueue.TryDequeue(out discarded)) { }
                queuedSamples = 0;
                sampleRate = rate;
                channels = ch;
                startTask = StartGraphAsync(rate, ch);
            }
        }

        private async Task StartGraphAsync(int rate, int ch)
        {
            try
            {
                // Device-native graph. Forcing CEF sample rate / LowestLatency fails on many WM10 phones.
                var settings = new AudioGraphSettings(AudioRenderCategory.Media);
                settings.QuantumSizeSelectionMode = QuantumSizeSelectionMode.SystemDefault;

                var createResult = await AudioGraph.CreateAsync(settings);
                if (createResult.Status != AudioGraphCreationStatus.Success || disposed)
                {
                    Debug.WriteLine("AudioGraph create failed: " + createResult.Status);
                    return;
                }

                var newGraph = createResult.Graph;
                var outputResult = await newGraph.CreateDeviceOutputNodeAsync();
                if (outputResult.Status != AudioDeviceNodeCreationStatus.Success || disposed)
                {
                    Debug.WriteLine("AudioDeviceOutput create failed: " + outputResult.Status);
                    newGraph.Dispose();
                    return;
                }

                AudioFrameInputNode newInput = null;
                int useChannels = ch;

                // Prefer a node matching the remote stream; AudioGraph resamples to the device.
                try
                {
                    var nodeProps = AudioEncodingProperties.CreatePcm((uint)rate, (uint)ch, 32);
                    nodeProps.Subtype = MediaEncodingSubtypes.Float;
                    newInput = newGraph.CreateFrameInputNode(nodeProps);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("FrameInput float format failed: " + ex.Message);
                }

                if (newInput == null)
                {
                    try
                    {
                        // Same rate/channels without overriding subtype.
                        newInput = newGraph.CreateFrameInputNode(
                            AudioEncodingProperties.CreatePcm((uint)rate, (uint)ch, 32));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine("FrameInput pcm format failed: " + ex.Message);
                        outputResult.DeviceOutputNode.Dispose();
                        newGraph.Dispose();
                        return;
                    }
                }

                newInput.AddOutgoingConnection(outputResult.DeviceOutputNode);
                newInput.QuantumStarted += FrameInput_QuantumStarted;

                lock (sync)
                {
                    if (disposed)
                    {
                        newInput.Dispose();
                        outputResult.DeviceOutputNode.Dispose();
                        newGraph.Dispose();
                        return;
                    }

                    graph = newGraph;
                    deviceOutput = outputResult.DeviceOutputNode;
                    frameInput = newInput;
                    sampleRate = rate;
                    channels = ch;
                    nodeChannels = useChannels;
                }

                newGraph.Start();
                newInput.Start();
                Debug.WriteLine("AudioGraph started stream={0}/{1} nodeCh={2} deviceRate={3}",
                    rate, ch, useChannels, newGraph.EncodingProperties.SampleRate);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("AudioGraph start error: " + ex.Message);
                lock (sync)
                {
                    DisposeGraph_NoLock();
                }
            }
        }

        private void FrameInput_QuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
        {
            uint framesNeeded = (uint)args.RequiredSamples;
            if (framesNeeded == 0)
                return;

            int srcCh;
            int dstCh;
            lock (sync)
            {
                srcCh = channels > 0 ? channels : 1;
                dstCh = nodeChannels > 0 ? nodeChannels : srcCh;
            }

            uint floatCount = framesNeeded * (uint)dstCh;
            uint byteCapacity = floatCount * sizeof(float);
            var frame = new AudioFrame(byteCapacity);

            using (var audioBuffer = frame.LockBuffer(AudioBufferAccessMode.Write))
            using (var reference = audioBuffer.CreateReference())
            {
                unsafe
                {
                    byte* dataInBytes;
                    uint capacityInBytes;
                    ((IMemoryBufferByteAccess)reference).GetBuffer(out dataInBytes, out capacityInBytes);

                    float* floats = (float*)dataInBytes;
                    for (uint f = 0; f < framesNeeded; f++)
                    {
                        // Pull one source frame (srcCh samples), write dstCh samples.
                        float left = 0f, right = 0f;
                        for (int c = 0; c < srcCh; c++)
                        {
                            float sample;
                            if (!sampleQueue.TryDequeue(out sample))
                                sample = 0f;
                            else
                                System.Threading.Interlocked.Decrement(ref queuedSamples);

                            if (c == 0) left = sample;
                            else if (c == 1) right = sample;
                            else
                            {
                                // Fold extra channels into L/R lightly.
                                if ((c & 1) == 0) left = Clamp(left + sample * 0.5f);
                                else right = Clamp(right + sample * 0.5f);
                            }
                        }

                        if (srcCh == 1)
                            right = left;

                        uint baseIndex = f * (uint)dstCh;
                        floats[baseIndex] = left;
                        if (dstCh > 1)
                            floats[baseIndex + 1] = right;
                    }
                }
            }

            try
            {
                sender.AddFrame(frame);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Audio AddFrame error: " + ex.Message);
            }
        }

        private static float Clamp(float sample)
        {
            if (sample > 1f) return 1f;
            if (sample < -1f) return -1f;
            return sample;
        }

        private void DisposeGraph_NoLock()
        {
            try
            {
                if (frameInput != null)
                    frameInput.QuantumStarted -= FrameInput_QuantumStarted;
            }
            catch
            {
            }

            try { frameInput?.Stop(); } catch { }
            try { graph?.Stop(); } catch { }
            try { frameInput?.Dispose(); } catch { }
            try { deviceOutput?.Dispose(); } catch { }
            try { graph?.Dispose(); } catch { }
            frameInput = null;
            deviceOutput = null;
            graph = null;
        }

        private static int ReadInt32(byte[] buffer, int offset)
        {
            return buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
        }
    }

    [ComImport]
    [Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    unsafe interface IMemoryBufferByteAccess
    {
        void GetBuffer(out byte* buffer, out uint capacity);
    }
}
