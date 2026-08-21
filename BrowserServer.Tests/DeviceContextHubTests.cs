using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using Xunit;

namespace BrowserServer.Tests
{
    public class DeviceContextHubTests
    {
        [Fact]
        public void IsValidDeviceId_AcceptsGuidNFormat()
        {
            var id = Guid.NewGuid().ToString("N");
            DeviceContextHub.IsValidDeviceId(id).Should().BeTrue();
            DeviceContextHub.IsValidDeviceId("").Should().BeFalse();
            DeviceContextHub.IsValidDeviceId("not-a-guid").Should().BeFalse();
        }

        [Fact]
        public void Attach_FirstConnection_BindsDevice()
        {
            DeviceContextHub.ResetForTests();
            var session = new ClientSession("ws-1");
            var deviceId = Guid.NewGuid().ToString("N");

            DeviceContextHub.Attach(deviceId, session).Should().Be(DeviceAttachResult.Success);
            session.Device.Should().NotBeNull();
            session.Device.DeviceId.Should().Be(deviceId);
            session.Device.ActiveWebSocketSessionId.Should().Be("ws-1");
        }

        [Fact]
        public void Attach_ReconnectSameDevice_AllowsNewWebSocket()
        {
            DeviceContextHub.ResetForTests();
            var deviceId = Guid.NewGuid().ToString("N");
            var first = new ClientSession("ws-a");
            DeviceContextHub.Attach(deviceId, first).Should().Be(DeviceAttachResult.Success);
            DeviceContextHub.Detach(first);

            var second = new ClientSession("ws-b");
            DeviceContextHub.Attach(deviceId, second).Should().Be(DeviceAttachResult.Success);
            second.Device.DeviceId.Should().Be(deviceId);
            second.Device.ActiveWebSocketSessionId.Should().Be("ws-b");
        }

        [Fact]
        public void Attach_SecondLiveConnectionSameDevice_TakeoverSucceeds()
        {
            DeviceContextHub.ResetForTests();
            SessionMessaging.TestIsSessionConnected = id =>
                string.Equals(id, "ws-a", StringComparison.Ordinal)
                || string.Equals(id, "ws-b", StringComparison.Ordinal);

            var deviceId = Guid.NewGuid().ToString("N");
            var first = ClientSessionHub.Create("ws-a");
            var second = ClientSessionHub.Create("ws-b");

            DeviceContextHub.Attach(deviceId, first).Should().Be(DeviceAttachResult.Success);
            DeviceContextHub.Attach(deviceId, second).Should().Be(DeviceAttachResult.Success);
            second.Device.Should().NotBeNull();
            second.Device.ActiveWebSocketSessionId.Should().Be("ws-b");
            ClientSessionHub.Get("ws-a").Should().BeNull();
        }

        [Fact]
        public void Attach_ReclaimsStaleBindingWhenOldSocketClosed()
        {
            DeviceContextHub.ResetForTests();
            var deviceId = Guid.NewGuid().ToString("N");
            var first = new ClientSession("ws-a");
            DeviceContextHub.Attach(deviceId, first).Should().Be(DeviceAttachResult.Success);

            var device = DeviceContextHub.GetByDeviceId(deviceId);
            device.Should().NotBeNull();
            device.ActiveWebSocketSessionId = "ws-dead";
            first.Device = null;

            SessionMessaging.IsSessionConnected("ws-dead").Should().BeFalse();

            var second = new ClientSession("ws-b");
            DeviceContextHub.Attach(deviceId, second).Should().Be(DeviceAttachResult.Success);
            second.Device.Should().NotBeNull();
            second.Device.ActiveWebSocketSessionId.Should().Be("ws-b");
        }

        [Fact]
        public void CefPaths_DeviceFolders_AreUniquePerDeviceId()
        {
            CefPaths.Root = Path.Combine(Path.GetTempPath(), "CloudBrowserTestCefDevices");
            var a = CefPaths.GetDeviceProfilePath(Guid.NewGuid().ToString("N"));
            var b = CefPaths.GetDeviceProfilePath(Guid.NewGuid().ToString("N"));
            a.Should().NotBe(b);
            CefPaths.IsDirectChildOfRoot(a).Should().BeTrue();
        }
    }
}
