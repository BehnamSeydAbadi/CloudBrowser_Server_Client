using BrowserServer;
using FluentAssertions;
using Xunit;

namespace BrowserServer.Tests
{
    public class VideoPlaybackBridgeTests
    {
        [Fact]
        public void VideoPlaybackBridge_IsStreamingTab_ReturnsFalseForUnknownTab()
        {
            VideoPlaybackBridge.IsStreamingTab("unknown-tab").Should().BeFalse();
            VideoPlaybackBridge.IsStreamingTab(null).Should().BeFalse();
        }

        [Fact]
        public void VideoPlaybackBridge_IsStreaming_InitiallyFalse()
        {
            VideoPlaybackBridge.IsStreaming.Should().BeFalse();
        }
    }
}
