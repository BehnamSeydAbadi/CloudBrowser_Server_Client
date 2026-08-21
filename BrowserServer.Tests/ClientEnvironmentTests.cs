using FluentAssertions;
using Newtonsoft.Json;
using System;
using Xunit;

namespace BrowserServer.Tests
{
    public class ClientEnvironmentTests
    {
        [Fact]
        public void Payload_RoundTripsAllFields()
        {
            var payload = new ClientEnvironmentPayload
            {
                cssWidth = 360,
                cssHeight = 640,
                devicePixelRatio = 2.5,
                isMobile = true,
                acceptLanguage = "fa-IR,en-US",
                screenWidth = 400,
                screenHeight = 800,
                colorScheme = "dark",
                timeZone = "Iran Standard Time",
                utcOffsetMinutes = 210,
                orientation = "portrait"
            };

            var json = JsonConvert.SerializeObject(payload);
            var restored = JsonConvert.DeserializeObject<ClientEnvironmentPayload>(json);

            restored.Should().BeEquivalentTo(payload);
        }

        [Fact]
        public void CommPacket_EncodesClientEnvironmentType()
        {
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.ClientEnvironment, new ClientEnvironmentPayload
            {
                cssWidth = 320,
                cssHeight = 568,
                devicePixelRatio = 2,
                isMobile = true,
                acceptLanguage = "en-US",
                screenWidth = 320,
                screenHeight = 568,
                colorScheme = "light",
                timeZone = "UTC",
                utcOffsetMinutes = 0,
                orientation = "portrait"
            });

            var packet = WebSocketJsonProtocol.DecodeCommPacket(json);
            packet.PType.Should().Be(PacketType.ClientEnvironment);
            var nested = WebSocketJsonProtocol.DeserializeNested<ClientEnvironmentPayload>(packet.JSONData);
            nested.cssWidth.Should().Be(320);
            nested.acceptLanguage.Should().Be("en-US");
        }

        [Fact]
        public void Dispatcher_AllowsClientEnvironmentWithoutActiveBrowser()
        {
            var sink = new RecordingCommands();
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.ClientEnvironment, new ClientEnvironmentPayload
            {
                cssWidth = 400,
                cssHeight = 700,
                devicePixelRatio = 3,
                isMobile = true,
                acceptLanguage = "de-DE",
                screenWidth = 400,
                screenHeight = 800,
                colorScheme = "light",
                timeZone = "W. Europe Standard Time",
                utcOffsetMinutes = 60,
                orientation = "portrait"
            });

            var result = ClientCommandDispatcher.DispatchText(json, false, 2f, sink);

            result.Should().Be(DispatchResult.Handled);
            sink.LastEnvironment.Should().NotBeNull();
            sink.LastEnvironment.acceptLanguage.Should().Be("de-DE");
            sink.LastWidth.Should().Be(400);
            sink.LastHeight.Should().Be(700);
            sink.LastScale.Should().Be(3f);
        }

        [Fact]
        public void Apply_StoresAcceptLanguageAndViewport()
        {
            DeviceContextHub.ResetForTests();
            var deviceId = Guid.NewGuid().ToString("N");
            var session = new ClientSession("test-ws");
            ClientEnvironmentBridge.Apply(session, new ClientEnvironmentPayload
            {
                cssWidth = 412,
                cssHeight = 732,
                devicePixelRatio = 2.625,
                isMobile = true,
                acceptLanguage = "fr-FR,en",
                screenWidth = 412,
                screenHeight = 869,
                colorScheme = "dark",
                timeZone = "Romance Standard Time",
                utcOffsetMinutes = 60,
                orientation = "portrait",
                deviceId = deviceId
            });

            session.AcceptLanguage.Should().Be("fr-FR,en");
            session.Device.Should().NotBeNull();
            session.Device.DeviceId.Should().Be(deviceId);
            session.Environment.Should().BeEquivalentTo(new ClientEnvironmentPayload
            {
                cssWidth = 412,
                cssHeight = 732,
                devicePixelRatio = 2.625,
                isMobile = true,
                acceptLanguage = "fr-FR,en",
                screenWidth = 412,
                screenHeight = 869,
                colorScheme = "dark",
                timeZone = "Romance Standard Time",
                utcOffsetMinutes = 60,
                orientation = "portrait",
                deviceId = deviceId
            });
        }

        [Fact]
        public void BuildShimScript_ContainsOrientationAndColorScheme()
        {
            var json = JsonConvert.SerializeObject(new ClientEnvironmentPayload
            {
                orientation = "landscape",
                colorScheme = "dark",
                isMobile = true,
                screenWidth = 800,
                screenHeight = 400
            });

            var script = ClientEnvironmentBridge.BuildShimScript(json);
            script.Should().Contain("landscape");
            script.Should().Contain("prefers-color-scheme");
            script.Should().Contain("maxTouchPoints");
        }
    }
}
