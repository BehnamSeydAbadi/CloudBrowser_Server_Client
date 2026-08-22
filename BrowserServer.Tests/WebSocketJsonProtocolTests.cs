using System.Collections.Generic;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BrowserServer.Tests
{
    public class WebSocketJsonProtocolTests
    {
        [Fact]
        public void CommPacket_RoundTripsNavigationStringPayload()
        {
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.Navigation, "https://example.com/path");
            var packet = WebSocketJsonProtocol.DecodeCommPacket(json);

            packet.PType.Should().Be(PacketType.Navigation);
            packet.JSONData.Should().Be("https://example.com/path");
        }

        [Fact]
        public void CommPacket_RoundTripsNestedMediaPermission()
        {
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.MediaPermissionResponse, new MediaPermissionPayload
            {
                requestId = "r1",
                origin = "https://site.example",
                audio = true,
                video = false,
                allowed = true
            });

            var packet = WebSocketJsonProtocol.DecodeCommPacket(json);
            var nested = WebSocketJsonProtocol.DeserializeNested<MediaPermissionPayload>(packet.JSONData);

            packet.PType.Should().Be(PacketType.MediaPermissionResponse);
            nested.Should().BeEquivalentTo(new MediaPermissionPayload
            {
                requestId = "r1",
                origin = "https://site.example",
                audio = true,
                video = false,
                allowed = true
            });
        }

        [Fact]
        public void TextPacket_RoundTripsTabListNestedJson()
        {
            var payload = new TabListPayload
            {
                activeId = "tab-a",
                tabs = new List<TabInfo>
                {
                    new TabInfo { id = "tab-a", title = "Example", url = "https://example.com/" }
                }
            };

            var json = WebSocketJsonProtocol.EncodeTextPacket(TextPacketType.TabList, payload);
            var packet = WebSocketJsonProtocol.DecodeTextPacket(json);
            var inner = JsonConvert.DeserializeObject<TabListPayload>(packet.text);

            packet.PType.Should().Be(TextPacketType.TabList);
            inner.Should().BeEquivalentTo(payload);
        }

        [Fact]
        public void TextPacket_RoundTripsNavigatedUrlPlainText()
        {
            var json = WebSocketJsonProtocol.EncodeTextPacket(TextPacketType.NavigatedUrl, "https://news.example/");
            var packet = WebSocketJsonProtocol.DecodeTextPacket(json);

            packet.PType.Should().Be(TextPacketType.NavigatedUrl);
            packet.text.Should().Be("https://news.example/");
        }

        [Theory]
        [InlineData("{not json")]
        [InlineData("")]
        [InlineData(null)]
        public void TryDecodeCommPacket_RejectsInvalidJson(string json)
        {
            WebSocketJsonProtocol.TryDecodeCommPacket(json, out _).Should().BeFalse();
        }

        [Fact]
        public void TryParseSizeChange_UsesDefaultScaleWhenOmitted()
        {
            WebSocketJsonProtocol.TryParseSizeChange(
                "{\"Width\":320.4,\"Height\":480.6}", 2f, out var w, out var h, out var scale)
                .Should().BeTrue();

            w.Should().Be(320);
            h.Should().Be(481);
            scale.Should().Be(2f);
        }

        [Fact]
        public void TryParseSizeChange_ClampsScaleBelowOne()
        {
            WebSocketJsonProtocol.TryParseSizeChange(
                "{\"Width\":100,\"Height\":200,\"Scale\":0.5}", 2f, out _, out _, out var scale)
                .Should().BeTrue();

            scale.Should().Be(1f);
        }

        [Fact]
        public void TextInputContent_TextPacket_RoundTrips()
        {
            var json = WebSocketJsonProtocol.EncodeTextPacket(TextPacketType.TextInputContent, "field value");
            var packet = WebSocketJsonProtocol.DecodeTextPacket(json);

            packet.PType.Should().Be(TextPacketType.TextInputContent);
            packet.text.Should().Be("field value");
        }

        [Fact]
        public void TextInputCancel_TextPacket_RoundTrips()
        {
            var json = WebSocketJsonProtocol.EncodeTextPacket(TextPacketType.TextInputCancel);
            var packet = WebSocketJsonProtocol.DecodeTextPacket(json);

            packet.PType.Should().Be(TextPacketType.TextInputCancel);
            packet.text.Should().Be("");
        }

        [Theory]
        [InlineData("{\"type\":\"insert\",\"text\":\"Hi\"}", SendKeyKind.Insert, "Hi", 0, null)]
        [InlineData("{\"type\":\"backspace\"}", SendKeyKind.Backspace, null, 0, null)]
        [InlineData("{\"type\":\"enter\"}", SendKeyKind.Enter, null, 0, null)]
        [InlineData("{\"type\":\"down\",\"code\":8}", SendKeyKind.Coded, null, 8, "down")]
        [InlineData("\"65\"", SendKeyKind.LegacyChar, null, 65, null)]
        public void TryParseSendKey_RecognizesKnownShapes(
            string json, SendKeyKind kind, string text, int code, string eventType)
        {
            WebSocketJsonProtocol.TryParseSendKey(json, out var cmd).Should().BeTrue();
            cmd.Should().BeEquivalentTo(new SendKeyCommand
            {
                Kind = kind,
                Text = text,
                Code = code,
                EventType = eventType
            });
        }
    }
}
