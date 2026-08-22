using System;
using BrowserServer;
using BrowserServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace BrowserServer.Tests
{
    public class MediaBridgeAudioTests : IDisposable
    {
        public MediaBridgeAudioTests()
        {
            TestSessionFactory.ResetAll();
        }

        public void Dispose()
        {
            MediaBridge.Release(null);
            TestSessionFactory.ResetAll();
        }

        [Fact]
        public void MediaBridge_DequeueAudioChunk_ReturnsNullWhenEmpty()
        {
            MediaBridge.DequeueAudioChunk().Should().BeNull();
        }

        [Fact]
        public void MediaBridge_DequeueAudioChunk_ReturnsPcmAfterMicPacket()
        {
            MediaBridge.HandleClientBinary(BinaryPacketBuilder.BuildMicPacket(frameCount: 32));

            var chunk = MediaBridge.DequeueAudioChunk();
            chunk.Should().NotBeNull();
            chunk.Length.Should().Be(32 * 2 + 8);
        }
    }
}
