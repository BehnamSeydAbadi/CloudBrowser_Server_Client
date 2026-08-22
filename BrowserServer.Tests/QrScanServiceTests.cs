using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BrowserServer;
using BrowserServer.Tests.Fixtures;
using BrowserServer.Tests.Helpers;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BrowserServer.Tests
{
    public class QrScanServiceTests : IDisposable
    {
        const string TabId = "tab-qr";
        const string WsId = "ws-qr";
        const string QrText = "scan-payload-12345";

        public QrScanServiceTests()
        {
            TestSessionFactory.ResetAll();
        }

        public void Dispose()
        {
            MediaBridge.Release(null);
            TestSessionFactory.ResetAll();
        }

        [Fact]
        public void QrDetected_TextPacket_RoundTrips()
        {
            var json = WebSocketJsonProtocol.EncodeTextPacket(TextPacketType.QrDetected, QrText);
            var packet = WebSocketJsonProtocol.DecodeTextPacket(json);

            packet.PType.Should().Be(TextPacketType.QrDetected);
            packet.text.Should().Be(QrText);
        }

        [Fact]
        public void QrScanService_DecodesHttpUrlFromCamFixture()
        {
            var session = TestSessionFactory.CreateWithDevice(WsId, TabId);
            string mediaRequestId = null;
            string qrDetected = null;
            SessionMessaging.TestSendHook = (sessionId, packet) =>
            {
                if (sessionId != WsId)
                    return;

                if (packet.PType == TextPacketType.MediaPermissionRequest)
                {
                    var payload = JsonConvert.DeserializeObject<MediaPermissionPayload>(packet.text);
                    mediaRequestId = payload.requestId;
                }
                else if (packet.PType == TextPacketType.QrDetected)
                {
                    qrDetected = packet.text;
                }
            };

            var permissionTask = MediaBridge.RequestAccessAsync(TabId, false, true, "https://example.com");
            var waitStart = Environment.TickCount;
            while (mediaRequestId == null && Environment.TickCount - waitStart < 2000)
                Thread.Sleep(10);

            mediaRequestId.Should().NotBeNullOrEmpty();

            MediaBridge.HandlePermissionResponse(new MediaPermissionPayload
            {
                requestId = mediaRequestId,
                allowed = true,
                video = true,
                audio = false
            });

            permissionTask.Wait(2000);
            MediaBridge.IsCaptureActive.Should().BeTrue();

            var jpeg = QrFixtures.CreateQrJpeg(QrText, pixels: 400);
            QrScanService.TryDecodeAsync(jpeg);

            var deadline = Environment.TickCount + 5000;
            while (qrDetected == null && Environment.TickCount < deadline)
                Thread.Sleep(50);

            qrDetected.Should().Be(QrText);
        }
    }
}
