using System;
using System.Collections.Concurrent;
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
            if (rate < 8000 || ch < 1 || frames <= 0 || 16 + pcmBytes > count)
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
                DisposeGraph_NoLock();
                startTask = null;
            }
        }

        public void Dispose()
        {
            disposed = true;
            Stop();
        }

        private void EnsureGraphStarted(int rate, int ch)
        {
            lock (sync)
            {
                if (disposed)
                    return;

                if (graph != null && sampleRate == rate && channels == ch && frameInput != null)
                    return;

                if (startTask != null && !startTask.IsCompleted)
                    return;

                // Format change or first start.
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
                var settings = new AudioGraphSettings(AudioRenderCategory.Media);
                settings.QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency;
                // Match the remote stream so we do not need resampling.
                settings.EncodingProperties = AudioEncodingProperties.CreatePcm((uint)rate, (uint)ch, 32);

                var createResult = await AudioGraph.CreateAsync(settings);
                if (createResult.Status != AudioGraphCreationStatus.Success || disposed)
                    return;

                var newGraph = createResult.Graph;
                var outputResult = await newGraph.CreateDeviceOutputNodeAsync();
                if (outputResult.Status != AudioDeviceNodeCreationStatus.Success || disposed)
                {
                    newGraph.Dispose();
                    return;
                }

                var newInput = newGraph.CreateFrameInputNode(newGraph.EncodingProperties);
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
                }

                newGraph.Start();
                newInput.Start();
            }
            catch
            {
                lock (sync)
                {
                    DisposeGraph_NoLock();
                }
            }
        }

        private void FrameInput_QuantumStarted(AudioFrameInputNode sender, FrameInputNodeQuantumStartedEventArgs args)
        {
            // RequiredSamples is frames (per channel); AudioFrame is interleaved floats.
            uint framesNeeded = (uint)args.RequiredSamples;
            if (framesNeeded == 0)
                return;

            int ch;
            lock (sync)
            {
                ch = channels > 0 ? channels : 1;
            }

            uint floatCount = framesNeeded * (uint)ch;
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
                    for (uint i = 0; i < floatCount; i++)
                    {
                        float sample;
                        if (sampleQueue.TryDequeue(out sample))
                        {
                            System.Threading.Interlocked.Decrement(ref queuedSamples);
                            floats[i] = sample;
                        }
                        else
                        {
                            floats[i] = 0f;
                        }
                    }
                }
            }

            try
            {
                sender.AddFrame(frame);
            }
            catch
            {
            }
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
