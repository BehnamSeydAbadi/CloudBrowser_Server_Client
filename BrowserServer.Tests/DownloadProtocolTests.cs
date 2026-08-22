using System;
using BrowserServer;
using BrowserServer.Tests.Helpers;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BrowserServer.Tests
{
    public class DownloadProtocolTests
    {
        [Fact]
        public void DownloadEventPayload_JsonRoundTrips()
        {
            var payload = new DownloadEventPayload
            {
                id = "dl-1",
                fileName = "report.pdf",
                mimeType = "application/pdf",
                totalBytes = 4096,
                receivedBytes = 1024,
                percent = 25
            };

            var json = JsonConvert.SerializeObject(payload);
            var restored = JsonConvert.DeserializeObject<DownloadEventPayload>(json);

            restored.Should().BeEquivalentTo(payload);
        }

        [Fact]
        public void DownloadAckPayload_JsonRoundTrips()
        {
            var payload = new DownloadAckPayload { id = "dl-1", seq = 42 };

            var json = JsonConvert.SerializeObject(payload);
            var restored = JsonConvert.DeserializeObject<DownloadAckPayload>(json);

            restored.Should().BeEquivalentTo(payload);
        }

        [Fact]
        public void StreamingDownloadHandler_HandleClientAck_DoesNotThrowWhenIdle()
        {
            DownloadHandlerReflection.HandleClientAck("idle-id", 0);
        }

        [Fact]
        public void StreamingDownloadHandler_Magic_MatchesBinaryWebSocketFrameFileKind()
        {
            var buffer = new byte[] { (byte)'F', (byte)'I', (byte)'L', (byte)'E' };

            BinaryWebSocketFrame.Classify(buffer, buffer.Length)
                .Should().Be(BinaryFrameKind.File);
        }
    }
}
