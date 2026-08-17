using System;
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
    /// Pull-driven via QuantumStarted, with a small channel-aligned jitter buffer.
    /// Dropping single samples (old code) desynced L/R after a few seconds and
    /// made playback sound crushed/underwater.
    /// </summary>
    public sealed class StreamAudioPlayer : IDisposable
    {
        private const int TargetMs = 90;
        private const int MaxMs = 220;
        private const int PrerollMs = 70;

        private readonly object sync = new object();
        private float[] ring = new float[48000 * 2];
        private int ringRead;
        private int ringWrite;
        private int ringCount;

        private AudioGraph graph;
        private AudioDeviceOutputNode deviceOutput;
        private AudioFrameInputNode frameInput;
        private int sampleRate = 48000;
        private int channels = 2;
        private int nodeChannels = 2;
        private Task startTask;
        private bool prerolled;
        private int underrunStreak;
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
            int sampleCount = frames * ch;
            lock (sync)
            {
                EnsureRingCapacity(sampleCount + ringCount);
                for (int i = 0; i < sampleCount; i++)
                {
                    short s = (short)(buffer[offset] | (buffer[offset + 1] << 8));
                    offset += 2;
                    ring[ringWrite] = s / 32768f;
                    ringWrite++;
                    if (ringWrite == ring.Length)
                        ringWrite = 0;
                    ringCount++;
                }

                TrimToTarget_NoLock();
            }
        }

        public void Stop()
        {
            lock (sync)
            {
                ringRead = ringWrite = ringCount = 0;
                prerolled = false;
                underrunStreak = 0;
                try { frameInput?.Stop(); } catch { }
            }
        }

        public void Dispose()
        {
            disposed = true;
            lock (sync)
            {
                ringRead = ringWrite = ringCount = 0;
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
                ringRead = ringWrite = ringCount = 0;
                prerolled = false;
                underrunStreak = 0;
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

                    lock (sync)
                    {
                        int prerollSamples = SamplesForMs(PrerollMs);
                        bool writeSilence = false;
                        if (!prerolled)
                        {
                            if (ringCount < prerollSamples)
                                writeSilence = true;
                            else
                            {
                                prerolled = true;
                                underrunStreak = 0;
                            }
                        }

                        if (writeSilence)
                        {
                            ZeroFloats(floats, floatCount);
                        }
                        else
                        {
                            for (uint f = 0; f < framesNeeded; f++)
                            {
                                float left = 0f, right = 0f;

                                // Never dequeue a partial frame — that swapped L/R after underruns.
                                if (ringCount >= srcCh)
                                {
                                    for (int c = 0; c < srcCh; c++)
                                    {
                                        float sample = Dequeue_NoLock();
                                        if (c == 0) left = sample;
                                        else if (c == 1) right = sample;
                                        else if ((c & 1) == 0) left = Clamp(left + sample * 0.5f);
                                        else right = Clamp(right + sample * 0.5f);
                                    }
                                    if (srcCh == 1)
                                        right = left;
                                    underrunStreak = 0;
                                }
                                else
                                {
                                    underrunStreak++;
                                    if (underrunStreak > 8)
                                        prerolled = false;
                                }

                                uint baseIndex = f * (uint)dstCh;
                                floats[baseIndex] = left;
                                if (dstCh > 1)
                                    floats[baseIndex + 1] = right;
                            }
                        }
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

        private void TrimToTarget_NoLock()
        {
            int ch = channels > 0 ? channels : 1;
            int maxSamples = SamplesForMs(MaxMs);
            int targetSamples = SamplesForMs(TargetMs);
            if (ringCount <= maxSamples)
                return;

            int drop = ringCount - targetSamples;
            drop -= drop % ch;
            if (drop < ch)
                return;

            ringRead = (ringRead + drop) % ring.Length;
            ringCount -= drop;
            Debug.WriteLine("Audio catch-up dropped {0} samples (~{1}ms)", drop, sampleRate > 0 ? drop * 1000 / (sampleRate * ch) : 0);
        }

        private float Dequeue_NoLock()
        {
            float sample = ring[ringRead];
            ringRead++;
            if (ringRead == ring.Length)
                ringRead = 0;
            ringCount--;
            return sample;
        }

        private void EnsureRingCapacity(int needed)
        {
            if (needed <= ring.Length)
                return;

            int size = ring.Length;
            while (size < needed)
                size *= 2;
            var next = new float[size];
            for (int i = 0; i < ringCount; i++)
                next[i] = ring[(ringRead + i) % ring.Length];
            ring = next;
            ringRead = 0;
            ringWrite = ringCount;
        }

        private int SamplesForMs(int ms)
        {
            int rate = sampleRate > 0 ? sampleRate : 48000;
            int ch = channels > 0 ? channels : 1;
            return Math.Max(ch, rate * ch * ms / 1000);
        }

        private static unsafe void ZeroFloats(float* dest, uint count)
        {
            for (uint i = 0; i < count; i++)
                dest[i] = 0f;
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
            prerolled = false;
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
