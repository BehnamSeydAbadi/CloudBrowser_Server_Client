using System;
using System.Linq;
using System.Threading.Tasks;
using BrowserServer;
using BrowserServer.Tests.Helpers;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BrowserServer.Tests
{
    public class NotificationBridgeTests : IDisposable
    {
        const string TabId = "tab-notify";
        const string WsId = "ws-notify";
        const string Origin = "https://notify.example";

        public NotificationBridgeTests()
        {
            TestSessionFactory.ResetAll();
        }

        public void Dispose()
        {
            TestSessionFactory.ResetAll();
        }

        [Fact]
        public void NotificationBridge_GetPermissionState_ReturnsDefaultWhenNoSession()
        {
            NotificationBridge.GetPermissionState(TabId, Origin).Should().Be("default");
            NotificationBridge.GetPermissionState(TabId, "").Should().Be("default");
        }

        [Fact]
        public void NotificationBridge_GetPermissionState_ReturnsGrantedWhenOriginAllowed()
        {
            var session = TestSessionFactory.CreateWithDevice(WsId, TabId);
            lock (session.Device.NotificationOrigins)
            {
                session.Device.NotificationOrigins[Origin] = true;
            }

            NotificationBridge.GetPermissionState(TabId, Origin).Should().Be("granted");
        }

        [Fact]
        public void NotificationBridge_GetPermissionState_ReturnsDeniedWhenOriginDenied()
        {
            var session = TestSessionFactory.CreateWithDevice(WsId, TabId);
            lock (session.Device.NotificationOrigins)
            {
                session.Device.NotificationOrigins[Origin] = false;
            }

            NotificationBridge.GetPermissionState(TabId, Origin).Should().Be("denied");
        }

        [Fact]
        public async Task NotificationBridge_HandlePermissionResponse_StoresOriginOnDevice()
        {
            TestSessionFactory.CreateWithDevice(WsId, TabId);
            string requestId = null;
            SessionMessaging.TestSendHook = (_, packet) =>
            {
                if (packet.PType == TextPacketType.NotificationPermissionRequest)
                {
                    var payload = JsonConvert.DeserializeObject<NotificationPermissionPayload>(packet.text);
                    requestId = payload.requestId;
                }
            };

            var permissionTask = NotificationBridge.RequestPermissionAsync(TabId, Origin);
            while (requestId == null)
                await Task.Delay(10);

            NotificationBridge.HandlePermissionResponse(new NotificationPermissionPayload
            {
                requestId = requestId,
                origin = Origin,
                allowed = true
            });

            await permissionTask;

            NotificationBridge.GetPermissionState(TabId, Origin).Should().Be("granted");
        }

        [Fact]
        public void NotificationBridge_Show_SendsNotificationPayloadViaTestSendHook()
        {
            var session = TestSessionFactory.CreateWithDevice(WsId, TabId);
            TextPacket captured = default(TextPacket);
            SessionMessaging.TestSendHook = (sessionId, packet) =>
            {
                if (sessionId == WsId && packet.PType == TextPacketType.Notification)
                    captured = packet;
            };

            NotificationBridge.Show(TabId, "Hello", "World", "tag-1", Origin, "", "");

            captured.PType.Should().Be(TextPacketType.Notification, "expected Show to emit a Notification text packet");
            var payload = JsonConvert.DeserializeObject<NotificationPayload>(captured.text);
            payload.title.Should().Be("Hello");
            payload.body.Should().Be("World");
            payload.origin.Should().Be(Origin);
        }

        [Fact]
        public void NotificationBridge_Show_SkipsWhenOriginDenied()
        {
            var session = TestSessionFactory.CreateWithDevice(WsId, TabId);
            lock (session.Device.NotificationOrigins)
            {
                session.Device.NotificationOrigins[Origin] = false;
            }

            var sent = false;
            SessionMessaging.TestSendHook = (_, packet) =>
            {
                if (packet.PType == TextPacketType.Notification)
                    sent = true;
            };

            NotificationBridge.Show(TabId, "Hello", "World", "tag-1", Origin, "", "");

            sent.Should().BeFalse();
        }

        [Fact]
        public void NotificationBridge_Show_SkipsEmptyTitleAndBody()
        {
            TestSessionFactory.CreateWithDevice(WsId, TabId);
            var sent = false;
            SessionMessaging.TestSendHook = (_, packet) =>
            {
                if (packet.PType == TextPacketType.Notification)
                    sent = true;
            };

            NotificationBridge.Show(TabId, "", "   ", "tag-1", Origin, "", "");

            sent.Should().BeFalse();
        }

        [Fact]
        public void NotificationPayload_JsonRoundTrips()
        {
            var payload = new NotificationPayload
            {
                title = "Title",
                body = "Body",
                tag = "t1",
                origin = Origin,
                icon = "https://icon.example/i.png",
                url = "https://notify.example/page"
            };

            var json = JsonConvert.SerializeObject(payload);
            var restored = JsonConvert.DeserializeObject<NotificationPayload>(json);

            restored.Should().BeEquivalentTo(payload);
        }

        [Fact]
        public void NotificationPermissionPayload_JsonRoundTrips()
        {
            var payload = new NotificationPermissionPayload
            {
                requestId = "req-1",
                origin = Origin,
                allowed = true
            };

            var json = JsonConvert.SerializeObject(payload);
            var restored = JsonConvert.DeserializeObject<NotificationPermissionPayload>(json);

            restored.Should().BeEquivalentTo(payload);
        }
    }
}
