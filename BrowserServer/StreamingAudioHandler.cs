using CefSharp;
using CefSharp.Enums;
using CefSharp.Handler;
using CefSharp.Structs;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace BrowserServer
{
    /// <summary>
    /// Captures CEF PCM audio and queues it for WebSocket unicast on a safe sender thread.
    /// Binary packet layout:
    ///   magic[4]="AUDI" | sampleRate:int32 | channels:int32 | frames:int32 | pcmS16le interleaved
    /// </summary>
    public class StreamingAudioHandler : AudioHandler
    {
        public static readonly byte[] Magic = { (byte)'A', (byte)'U', (byte)'D', (byte)'I' };

        private sealed class OutboundPacket
        {
            public string SessionId;
            public byte[] Data;
        }

        private static readonly ConcurrentQueue<OutboundPacket> Outbound = new ConcurrentQueue<OutboundPacket>();
        private static int outboundCount;
        private static int loggedPackets;
        private const int MaxOutboundPackets = 24; // ~240ms at 10ms CEF quanta

        private readonly string tabId;
        private int channels = 2;
        private int sampleRate = 48000;

        public StreamingAudioHandler(string tabId)
        {
            this.tabId = tabId;
        }

        /// <summary>AUDI packets waiting to go out — page JPEG should yield when this is high.</summary>
        public static int PendingCount
        {
            get { return Volatile.Read(ref outboundCount); }
        }

        public static int PendingCountForSession(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return 0;

            var count = 0;
            foreach (var packet in Outbound)
            {
                if (packet != null && packet.SessionId == sessionId)
                    count++;
            }
            return count;
        }

        /// <summary>Drain queued AUDI packets onto the WebSocket (call from timer / UI thread).</summary>
        public static void FlushOutbound()
        {
            OutboundPacket packet;
            int sent = 0;
            while (sent < 16 && Outbound.TryDequeue(out packet))
            {
                Interlocked.Decrement(ref outboundCount);
                if (packet == null || string.IsNullOrEmpty(packet.SessionId) || packet.Data == null)
                    continue;

                try
                {
                    if (!SessionMessaging.SendBinary(packet.SessionId, packet.Data))
                        break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Audio send error: " + ex.Message);
                    break;
                }
                sent++;
            }
        }

        public static void EnqueueStop(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return;

            try
            {
                SessionMessaging.SendText(sessionId, new TextPacket
                {
                    PType = TextPacketType.AudioStop,
                    text = ""
                });
            }
            catch
            {
            }
        }

        protected override bool GetAudioParameters(IWebBrowser chromiumWebBrowser, IBrowser browser, ref AudioParameters parameters)
        {
            parameters = new AudioParameters(ChannelLayout.LayoutStereo, 48000, 480);
            return true;
        }

        protected override void OnAudioStreamStarted(IWebBrowser chromiumWebBrowser, IBrowser browser, AudioParameters parameters, int channels)
        {
            this.channels = Math.Max(1, channels);
            this.sampleRate = parameters.SampleRate > 0 ? parameters.SampleRate : 48000;
            Interlocked.Exchange(ref loggedPackets, 0);
            Console.WriteLine("Audio start tab={0} rate={1} ch={2} layout={3}", tabId, sampleRate, this.channels, parameters.ChannelLayout);
        }

        protected override void OnAudioStreamPacket(IWebBrowser chromiumWebBrowser, IBrowser browser, IntPtr data, int noOfFrames, long pts)
        {
            if (noOfFrames <= 0 || data == IntPtr.Zero)
                return;

            var owner = ClientSessionHub.GetByTabId(tabId);
            if (owner == null || owner.Tabs.ActiveTabId != tabId)
                return;

            try
            {
                int srcChannels = Math.Max(1, channels);
                int outChannels;
                var pcm = InterleaveFloatPlanarToS16(data, srcChannels, noOfFrames, out outChannels);
                if (pcm == null || pcm.Length == 0)
                    return;

                var packet = new byte[4 + 12 + pcm.Length];
                Buffer.BlockCopy(Magic, 0, packet, 0, 4);
                WriteInt32(packet, 4, sampleRate);
                WriteInt32(packet, 8, outChannels);
                WriteInt32(packet, 12, noOfFrames);
                Buffer.BlockCopy(pcm, 0, packet, 16, pcm.Length);

                while (Volatile.Read(ref outboundCount) > MaxOutboundPackets)
                {
                    OutboundPacket dropped;
                    if (Outbound.TryDequeue(out dropped))
                        Interlocked.Decrement(ref outboundCount);
                    else
                        break;
                }

                Outbound.Enqueue(new OutboundPacket
                {
                    SessionId = owner.WebSocketSessionId,
                    Data = packet
                });
                Interlocked.Increment(ref outboundCount);

                if (Interlocked.Increment(ref loggedPackets) <= 3)
                    Console.WriteLine("Audio packet queued frames={0} bytes={1}", noOfFrames, packet.Length);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Audio packet error: " + ex.Message);
            }
        }

        protected override void OnAudioStreamStopped(IWebBrowser chromiumWebBrowser, IBrowser browser)
        {
            Console.WriteLine("Audio stop tab={0}", tabId);
            var owner = ClientSessionHub.GetByTabId(tabId);
            if (owner != null && owner.Tabs.ActiveTabId == tabId)
                EnqueueStop(owner.WebSocketSessionId);
        }

        protected override void OnAudioStreamError(IWebBrowser chromiumWebBrowser, IBrowser browser, string errorMessage)
        {
            Console.WriteLine("Audio error tab={0}: {1}", tabId, errorMessage);
        }

        private static byte[] InterleaveFloatPlanarToS16(IntPtr data, int channelCount, int frames, out int outChannels)
        {
            outChannels = channelCount >= 2 ? 2 : 1;
            var pcm = new byte[frames * outChannels * 2];
            int outIndex = 0;

            for (int i = 0; i < frames; i++)
            {
                if (outChannels == 1)
                {
                    float sample = 0f;
                    for (int c = 0; c < channelCount; c++)
                    {
                        var channelPtr = Marshal.ReadIntPtr(data, c * IntPtr.Size);
                        sample += ReadFloat(channelPtr, i);
                    }
                    sample /= channelCount;
                    WriteSample(pcm, ref outIndex, sample);
                }
                else
                {
                    var leftPtr = Marshal.ReadIntPtr(data, 0);
                    var rightPtr = channelCount > 1
                        ? Marshal.ReadIntPtr(data, IntPtr.Size)
                        : leftPtr;
                    WriteSample(pcm, ref outIndex, ReadFloat(leftPtr, i));
                    WriteSample(pcm, ref outIndex, ReadFloat(rightPtr, i));
                }
            }

            return pcm;
        }

        private static void WriteSample(byte[] pcm, ref int outIndex, float sample)
        {
            if (sample > 1f) sample = 1f;
            else if (sample < -1f) sample = -1f;
            short s = (short)(sample * 32767f);
            pcm[outIndex++] = (byte)(s & 0xFF);
            pcm[outIndex++] = (byte)((s >> 8) & 0xFF);
        }

        private static float ReadFloat(IntPtr channelPtr, int index)
        {
            var bytes = new byte[4];
            Marshal.Copy(IntPtr.Add(channelPtr, index * 4), bytes, 0, 4);
            return BitConverter.ToSingle(bytes, 0);
        }

        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
