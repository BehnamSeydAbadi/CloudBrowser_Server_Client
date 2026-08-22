using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BrowserServer.Tests
{
    public class HistoryProtocolTests
    {
        [Fact]
        public void NavigateBack_StopBeforeBlank_DispatchesTrue()
        {
            var sink = new RecordingCommands();
            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.NavigateBack, "stopBeforeBlank"),
                true,
                2f,
                sink);

            sink.LastStopBeforeBlank.Should().BeTrue();
            sink.Log.Should().Contain("NavigateBack");
        }

        [Fact]
        public void NavigateBack_WithoutFlag_DispatchesFalse()
        {
            var sink = new RecordingCommands();
            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.NavigateBack),
                true,
                2f,
                sink);

            sink.LastStopBeforeBlank.Should().BeFalse();
        }

        [Fact]
        public void NavigateForward_DispatchesWhenBrowserActive()
        {
            var sink = new RecordingCommands();
            var result = ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.NavigateForward),
                true,
                2f,
                sink);

            result.Should().Be(DispatchResult.Handled);
            sink.Log.Should().Equal("NavigateForward");
        }

        [Fact]
        public void AtHistoryRoot_TextPacket_RoundTrips()
        {
            var json = WebSocketJsonProtocol.EncodeTextPacket(TextPacketType.AtHistoryRoot, "");
            var packet = WebSocketJsonProtocol.DecodeTextPacket(json);

            packet.PType.Should().Be(TextPacketType.AtHistoryRoot);
            packet.text.Should().Be("");
        }
    }
}
