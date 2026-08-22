using System;
using BrowserServer;

namespace BrowserServer.Tests.Helpers
{
    /// <summary>Builds CAM/MIC wire packets matching BrowserClient.ClientMediaCapture layout.</summary>
    public static class BinaryPacketBuilder
    {
        public static byte[] BuildCamPacket(byte[] jpeg, int width = 640, int height = 480)
        {
            if (jpeg == null)
                throw new ArgumentNullException("jpeg");

            var packet = new byte[12 + jpeg.Length];
            Buffer.BlockCopy(MediaBridge.MagicCam, 0, packet, 0, 4);
            packet[4] = (byte)(width & 0xFF);
            packet[5] = (byte)((width >> 8) & 0xFF);
            packet[6] = (byte)(height & 0xFF);
            packet[7] = (byte)((height >> 8) & 0xFF);
            WriteInt32(packet, 8, jpeg.Length);
            Buffer.BlockCopy(jpeg, 0, packet, 12, jpeg.Length);
            return packet;
        }

        public static byte[] BuildCamPacketWithBadJpegLength(byte[] jpeg, int declaredLength)
        {
            var packet = BuildCamPacket(jpeg);
            WriteInt32(packet, 8, declaredLength);
            return packet;
        }

        public static byte[] BuildMicPacket(int sampleRate = 48000, int channels = 1, int frameCount = 64)
        {
            var pcmBytes = frameCount * channels * 2;
            var packet = new byte[16 + pcmBytes];
            Buffer.BlockCopy(MediaBridge.MagicMic, 0, packet, 0, 4);
            WriteInt32(packet, 4, sampleRate);
            WriteInt32(packet, 8, channels);
            WriteInt32(packet, 12, frameCount);
            return packet;
        }

        static void WriteInt32(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value & 0xFF);
            buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
            buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
            buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
        }
    }
}
