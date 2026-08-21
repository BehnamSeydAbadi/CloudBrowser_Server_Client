using FluentAssertions;
using Xunit;

namespace BrowserServer.Tests
{
    public class PwaSessionTests
    {
        [Theory]
        [InlineData("https://a.example/", "https://a.example/")]
        [InlineData("  https://b.example/path  ", "https://b.example/path")]
        public void NormalizeEntryUrl_ReturnsAbsoluteHttpUrl(string input, string expected)
        {
            PwaSessionBridge.NormalizeEntryUrl(input).Should().Be(expected);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-url")]
        [InlineData("file:///C:/x")]
        public void NormalizeEntryUrl_RejectsInvalid(string input)
        {
            PwaSessionBridge.NormalizeEntryUrl(input).Should().BeNull();
        }

        [Fact]
        public void NormalizeEntryUrl_ProducesDistinctKeysForDifferentTiles()
        {
            var a = PwaSessionBridge.NormalizeEntryUrl("https://pwa-a.test/home");
            var b = PwaSessionBridge.NormalizeEntryUrl("https://pwa-b.test/home");
            a.Should().NotBe(b);
        }

        [Fact]
        public void FindSession_ReturnsNullWhenNoTabs()
        {
            PwaSessionBridge.FindSession("https://missing.example/").Should().BeNull();
        }

        [Fact]
        public void Dispatcher_RoutesPwaSessionStart()
        {
            var sink = new RecordingCommands();
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.PwaSessionStart,
                new PwaSessionStartPayload { entryUrl = "https://pwa.example/start" });

            ClientCommandDispatcher.DispatchText(json, false, 2f, sink).Should().Be(DispatchResult.Handled);
            sink.LastPwaSession.entryUrl.Should().Be("https://pwa.example/start");
        }

        [Fact]
        public void Dispatcher_AllowsPwaSessionStartWithoutActiveBrowser()
        {
            var sink = new RecordingCommands();
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.PwaSessionStart,
                new PwaSessionStartPayload { entryUrl = "https://pwa.example/" });

            ClientCommandDispatcher.DispatchText(json, hasActiveBrowser: false, 2f, sink)
                .Should().Be(DispatchResult.Handled);
        }
    }
}
