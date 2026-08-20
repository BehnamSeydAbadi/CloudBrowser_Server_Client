using System.Collections.Generic;
using FluentAssertions;
using Xunit;

namespace BrowserServer.Tests
{
    public class ClientCommandDispatcherTests
    {
        [Fact]
        public void CreateTab_IsAllowedWithoutActiveBrowser()
        {
            var sink = new RecordingCommands();
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.CreateTab);

            var result = ClientCommandDispatcher.DispatchText(json, false, 2f, sink);

            result.Should().Be(DispatchResult.Handled);
            sink.Log.Should().Equal("CreateTab");
        }

        [Fact]
        public void Navigation_IsIgnoredWithoutActiveBrowser()
        {
            var sink = new RecordingCommands();
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.Navigation, "https://example.com/");

            var result = ClientCommandDispatcher.DispatchText(json, false, 2f, sink);

            result.Should().Be(DispatchResult.IgnoredNoBrowser);
            sink.Log.Should().BeEmpty();
        }

        [Fact]
        public void CloseAndSwitchTab_DispatchTabIds()
        {
            var sink = new RecordingCommands();

            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.CloseTab, "tab-1"), true, 2f, sink);
            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.SwitchTab, "tab-2"), true, 2f, sink);

            sink.Log.Should().Equal("CloseTab", "SwitchTab");
            sink.LastTabId.Should().Be("tab-2");
        }

        [Fact]
        public void CloseTab_EmptyIdDoesNotCallSink()
        {
            var sink = new RecordingCommands();
            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.CloseTab, ""), true, 2f, sink);

            sink.Log.Should().BeEmpty();
        }

        [Fact]
        public void NestedPermissionAndPwaPayloads_AreDeserialized()
        {
            var sink = new RecordingCommands();

            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.MediaPermissionResponse, new MediaPermissionPayload
                {
                    requestId = "m1",
                    allowed = true,
                    audio = true,
                    video = true
                }), true, 2f, sink);

            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.NotificationPermissionResponse, new NotificationPermissionPayload
                {
                    requestId = "n1",
                    origin = "https://app.example",
                    allowed = false
                }), true, 2f, sink);

            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.PwaInstalled, new PwaInstallPayload
                {
                    urls = new List<string> { "https://pwa.example/" },
                    reload = true
                }), true, 2f, sink);

            sink.LastMedia.Should().BeEquivalentTo(new MediaPermissionPayload
            {
                requestId = "m1",
                allowed = true,
                audio = true,
                video = true
            });
            sink.LastNotify.Should().BeEquivalentTo(new NotificationPermissionPayload
            {
                requestId = "n1",
                origin = "https://app.example",
                allowed = false
            });
            sink.LastPwa.Should().BeEquivalentTo(new PwaInstallPayload
            {
                urls = new List<string> { "https://pwa.example/" },
                reload = true
            });
        }

        [Fact]
        public void DownloadAck_AndAck_Dispatch()
        {
            var sink = new RecordingCommands();
            ClientCommandDispatcher.DispatchText(WebSocketJsonProtocol.EncodeCommPacket(PacketType.ACK), true, 2f, sink);
            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.DownloadAck, new DownloadAckPayload { id = "dl", seq = 7 }),
                true, 2f, sink);

            sink.Log.Should().Equal("Ack", "DownloadAck");
            sink.LastDownloadAck.Should().BeEquivalentTo(new DownloadAckPayload { id = "dl", seq = 7 });
        }

        [Fact]
        public void SendKey_Insert_DispatchesParsedCommand()
        {
            var sink = new RecordingCommands();
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.SendKey, "{\"type\":\"insert\",\"text\":\"abc\"}");

            ClientCommandDispatcher.DispatchText(json, true, 2f, sink);

            sink.LastKey.Should().BeEquivalentTo(new SendKeyCommand
            {
                Kind = SendKeyKind.Insert,
                Text = "abc"
            });
        }

        [Fact]
        public void NavigateBack_StopBeforeBlankFlag()
        {
            var sink = new RecordingCommands();
            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.NavigateBack, "stopBeforeBlank"), true, 2f, sink);
            sink.LastStopBeforeBlank.Should().BeTrue();

            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.NavigateBack), true, 2f, sink);
            sink.LastStopBeforeBlank.Should().BeFalse();
        }

        [Fact]
        public void SizeChange_AndTouch_DispatchGeometry()
        {
            var sink = new RecordingCommands();
            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.SizeChange, "{\"Width\":360,\"Height\":640,\"Scale\":3}"),
                true, 2f, sink);

            sink.LastWidth.Should().Be(360);
            sink.LastHeight.Should().Be(640);
            sink.LastScale.Should().Be(3f);

            ClientCommandDispatcher.DispatchText(
                WebSocketJsonProtocol.EncodeCommPacket(PacketType.TouchMoved, new PointerPacket { px = 0.5, py = 0.25, id = 9 }),
                true, 2f, sink);

            sink.LastTouchKind.Should().Be(TouchKind.Moved);
            sink.LastPointer.px.Should().BeApproximately(0.5, 0.0001);
            sink.LastPointer.id.Should().Be(9u);
        }

        [Fact]
        public void MalformedJson_IsIgnored()
        {
            var sink = new RecordingCommands();
            var result = ClientCommandDispatcher.DispatchText("not-json", true, 2f, sink);

            result.Should().Be(DispatchResult.IgnoredMalformed);
            sink.Log.Should().BeEmpty();
        }

        [Fact]
        public void Binary_IsAlwaysForwarded()
        {
            var sink = new RecordingCommands();
            var bytes = new byte[] { 0x46, 0x49, 0x4C, 0x45, 1, 2, 3 };
            var result = ClientCommandDispatcher.DispatchBinary(bytes, sink);

            result.Should().Be(DispatchResult.Handled);
            sink.LastBinary.Should().Equal(bytes);
        }

        [Fact]
        public void UnknownPacketType_IsIgnored()
        {
            var sink = new RecordingCommands();
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.Frame);
            var result = ClientCommandDispatcher.DispatchText(json, true, 2f, sink);

            result.Should().Be(DispatchResult.IgnoredUnknown);
        }
    }
}
