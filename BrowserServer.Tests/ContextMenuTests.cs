using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BrowserServer.Tests
{
    public class ContextMenuTests
    {
        [Fact]
        public void OfferPayload_RoundTrips()
        {
            var payload = new ContextMenuOfferPayload
            {
                linkUrl = "https://example.com/page",
                imageUrl = "https://example.com/a.png",
                text = "Hello"
            };

            var json = JsonConvert.SerializeObject(payload);
            JsonConvert.DeserializeObject<ContextMenuOfferPayload>(json)
                .Should().BeEquivalentTo(payload);
        }

        [Fact]
        public void ParseHitTestJson_ReadsLinkImageAndText()
        {
            var offer = ContextMenuBridge.ParseHitTestJson(
                "{\"linkUrl\":\"https://a.test\",\"imageUrl\":\"https://img.test/x.png\",\"text\":\"Hi\"}");
            offer.linkUrl.Should().Be("https://a.test");
            offer.imageUrl.Should().Be("https://img.test/x.png");
            offer.text.Should().Be("Hi");
        }

        [Theory]
        [InlineData("https://l", null, null, true, false, false, true)]
        [InlineData(null, "https://i", null, true, false, true, true)]
        [InlineData(null, null, "word", false, true, false, false)]
        public void Rules_MatchClientMenuItems(
            string link, string image, string text,
            bool openTab, bool copyText, bool saveImage, bool share)
        {
            var offer = new ContextMenuOfferPayload
            {
                linkUrl = link,
                imageUrl = image,
                text = text
            };

            ContextMenuRules.CanOpenNewTab(offer).Should().Be(openTab);
            ContextMenuRules.CanCopyText(offer).Should().Be(copyText);
            ContextMenuRules.CanSavePicture(offer).Should().Be(saveImage);
            ContextMenuRules.CanShare(offer).Should().Be(share);
        }

        [Fact]
        public void Dispatcher_RoutesContextMenuQuery()
        {
            var sink = new RecordingCommands();
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.ContextMenuQuery,
                new PointerPacket { px = 0.5, py = 0.25, id = 1 });

            ClientCommandDispatcher.DispatchText(json, true, 2f, sink).Should().Be(DispatchResult.Handled);
            sink.LastPointer.px.Should().BeApproximately(0.5, 0.0001);
            sink.LastPointer.py.Should().BeApproximately(0.25, 0.0001);
        }

        [Fact]
        public void Dispatcher_RoutesContextMenuAction()
        {
            var sink = new RecordingCommands();
            var json = WebSocketJsonProtocol.EncodeCommPacket(PacketType.ContextMenuAction,
                new ContextMenuActionPayload { action = "saveImage", url = "https://img.test/a.jpg" });

            ClientCommandDispatcher.DispatchText(json, true, 2f, sink).Should().Be(DispatchResult.Handled);
            sink.LastContextAction.action.Should().Be("saveImage");
            sink.LastContextAction.url.Should().Be("https://img.test/a.jpg");
        }

        [Fact]
        public void BuildHitTestScript_ContainsCssCoordinates()
        {
            var script = ContextMenuBridge.BuildHitTestScript(120.5f, 640.25f);
            script.Should().Contain("120.5");
            script.Should().Contain("640.25");
            script.Should().Contain("elementFromPoint");
        }
    }
}
