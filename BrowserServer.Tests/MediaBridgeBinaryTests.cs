using System;
using BrowserServer;
using BrowserServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace BrowserServer.Tests
{
    public class MediaBridgeBinaryTests : IDisposable
    {
        public MediaBridgeBinaryTests()
        {
            TestSessionFactory.ResetAll();
        }

        public void Dispose()
        {
            MediaBridge.Release(null);
            TestSessionFactory.ResetAll();
        }

        [Fact]
        public void MediaBridge_HandleClientBinary_RejectsTruncatedCamPacket()
        {
            MediaBridge.HandleClientBinary(new byte[] { (byte)'C', (byte)'A', (byte)'M', (byte)' ' });

            MediaBridge.IsCaptureActive.Should().BeFalse();
        }

        [Fact]
        public void MediaBridge_HandleClientBinary_RejectsInvalidCamJpegLength()
        {
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
            var packet = BinaryPacketBuilder.BuildCamPacketWithBadJpegLength(jpeg, 9999);

            MediaBridge.HandleClientBinary(packet);

            MediaBridge.GetLatestJpegCopy().Should().BeNull();
        }

        [Fact]
        public void MediaBridge_HandleClientBinary_AcceptsMicPacket_EnqueuesPcm()
        {
            MediaBridge.DequeueAudioChunk().Should().BeNull();

            var packet = BinaryPacketBuilder.BuildMicPacket();
            MediaBridge.HandleClientBinary(packet);

            var chunk = MediaBridge.DequeueAudioChunk();
            chunk.Should().NotBeNull();
            chunk.Length.Should().BeGreaterThan(8);
        }

        [Fact]
        public void MediaBridge_MagicCamMic_MatchWirePrefix()
        {
            MediaBridge.MagicCam.Should().Equal(new byte[] { (byte)'C', (byte)'A', (byte)'M', (byte)' ' });
            MediaBridge.MagicMic.Should().Equal(new byte[] { (byte)'M', (byte)'I', (byte)'C', (byte)' ' });
        }
    }
}
