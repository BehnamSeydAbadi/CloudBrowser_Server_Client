using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FluentAssertions;
using WebSocketSharp;
using WebSocketSharp.Server;
using Xunit;

namespace BrowserServer.Tests
{
    public class RenderFrameSessionTests
    {
        [Fact]
        public void SharedSocket_SkipsJpegWhileDownloadStreams()
        {
            var session = new RenderFrameSession();
            session.GetSharedSocketSkip(true, 0).Should().Be(RenderFrameSkipReason.DownloadStreaming);
        }

        [Theory]
        [InlineData(12, RenderFrameSkipReason.None)]
        [InlineData(13, RenderFrameSkipReason.AudioBacklog)]
        public void SharedSocket_SkipsJpegWhenAudioBacklogExceedsLimit(int pending, RenderFrameSkipReason expected)
        {
            var session = new RenderFrameSession();
            session.GetSharedSocketSkip(false, pending).Should().Be(expected);
        }

        [Fact]
        public void CameraThrottle_AllowsSlowPageStreamThenSkipsUntilInterval()
        {
            var session = new RenderFrameSession();
            const int t0 = 10_000;

            session.GetMediaThrottleSkip(true, false, t0).Should().Be(RenderFrameSkipReason.None);
            session.GetMediaThrottleSkip(true, false, t0 + RenderFrameSession.CameraMinIntervalMs - 1)
                .Should().Be(RenderFrameSkipReason.CameraThrottle);
            session.GetMediaThrottleSkip(true, false, t0 + RenderFrameSession.CameraMinIntervalMs)
                .Should().Be(RenderFrameSkipReason.None);
        }

        [Fact]
        public void VideoThrottle_KeepsSlowUiKeepalive()
        {
            var session = new RenderFrameSession();
            const int t0 = 10_000;

            session.GetMediaThrottleSkip(false, true, t0).Should().Be(RenderFrameSkipReason.None);
            session.GetMediaThrottleSkip(false, true, t0 + RenderFrameSession.VideoPageMinIntervalMs - 1)
                .Should().Be(RenderFrameSkipReason.VideoKeepaliveThrottle);
            session.GetMediaThrottleSkip(false, true, t0 + RenderFrameSession.VideoPageMinIntervalMs)
                .Should().Be(RenderFrameSkipReason.None);
        }

        [Fact]
        public void CaptureGate_BlocksOverlappingCapturesUntilStuckTimeout()
        {
            var session = new RenderFrameSession();

            session.TryBeginCapture(1000).Should().BeTrue();
            session.TryBeginCapture(1100).Should().BeFalse();
            session.TryBeginCapture(1000 + RenderFrameSession.CaptureStuckMs)
                .Should().BeFalse("timeout is exclusive, so this tick is not yet stuck");
            session.TryBeginCapture(1000 + RenderFrameSession.CaptureStuckMs + 1)
                .Should().BeFalse("stuck lock is cleared this tick without acquiring");
            session.TryBeginCapture(1000 + RenderFrameSession.CaptureStuckMs + 2)
                .Should().BeTrue();
            session.EndCapture();
        }

        [Fact]
        public void DirtyBuffer_UsesMotionJpegQuality()
        {
            var session = new RenderFrameSession();
            long quality;
            session.TrySelectQuality(1, mediaOn: false, now: 100, out quality).Should().BeTrue();
            quality.Should().Be(RenderFrameSession.QualityMotion);
            session.MarkSent(100);
        }

        [Fact]
        public void SettledPage_SendsOneCrispFrameThenKeepaliveSkip()
        {
            var session = new RenderFrameSession();
            long quality;

            session.TrySelectQuality(42, false, 100, out quality).Should().BeTrue();
            quality.Should().Be(RenderFrameSession.QualityMotion);
            session.MarkSent(100);

            session.TrySelectQuality(42, false, 100 + RenderFrameSession.CrispAfterStillMs, out quality)
                .Should().BeTrue();
            quality.Should().Be(RenderFrameSession.QualityCrisp);
            session.MarkSent(100 + RenderFrameSession.CrispAfterStillMs);

            session.TrySelectQuality(42, false, 100 + RenderFrameSession.CrispAfterStillMs + 10, out quality)
                .Should().BeFalse();
        }

        [Fact]
        public void UnchangedPage_SendsKeepaliveQualityAfterWindow()
        {
            var session = new RenderFrameSession();
            long quality;
            session.TrySelectQuality(7, false, 0, out quality);
            session.MarkSent(0);
            session.TrySelectQuality(7, false, RenderFrameSession.CrispAfterStillMs, out quality);
            session.MarkSent(RenderFrameSession.CrispAfterStillMs);

            session.TrySelectQuality(7, false, RenderFrameSession.CrispAfterStillMs + RenderFrameSession.KeepaliveMs, out quality)
                .Should().BeTrue();
            quality.Should().Be(RenderFrameSession.QualityKeepalive);
        }

        [Fact]
        public void CameraOn_UsesLowQualityEvenWhenUnchanged()
        {
            var session = new RenderFrameSession();
            long quality;
            session.TrySelectQuality(1, mediaOn: true, now: 0, out quality).Should().BeTrue();
            quality.Should().Be(RenderFrameSession.QualityCamera);
            session.MarkSent(0);

            session.TrySelectQuality(1, mediaOn: true, now: 50, out quality).Should().BeTrue();
            quality.Should().Be(RenderFrameSession.QualityCamera);
        }

        [Fact]
        public void Reset_AllowsFirstFrameToBeTreatedAsDirtyAgain()
        {
            var session = new RenderFrameSession();
            long quality;
            session.TrySelectQuality(9, false, 0, out quality);
            session.Reset();
            session.TrySelectQuality(9, false, 1, out quality).Should().BeTrue();
            quality.Should().Be(RenderFrameSession.QualityMotion);
        }
    }

    public class JpegRenderEncoderTests
    {
        [Fact]
        public void Encode_ProducesJpegSoiAndIsNotAudiOrFile()
        {
            using (var bitmap = CreateSolidBitmap(32, 24, Color.SteelBlue))
            {
                var jpeg = new JpegRenderEncoder().Encode(bitmap, RenderFrameSession.QualityMotion);

                jpeg.Length.Should().BeGreaterThan(2);
                BinaryWebSocketFrame.Classify(jpeg).Should().Be(BinaryFrameKind.Jpeg);
                jpeg[0].Should().Be(0xFF);
                jpeg[1].Should().Be(0xD8);
            }
        }

        [Fact]
        public void Hash_IsStableForSamePixelsAndChangesWhenPixelsChange()
        {
            using (var a = CreateSolidBitmap(16, 16, Color.Red))
            using (var b = CreateSolidBitmap(16, 16, Color.Red))
            using (var c = CreateSolidBitmap(16, 16, Color.Blue))
            {
                var ha = BitmapContentHash.Compute(a);
                BitmapContentHash.Compute(b).Should().Be(ha);
                BitmapContentHash.Compute(c).Should().NotBe(ha);
            }
        }

        internal static Bitmap CreateSolidBitmap(int width, int height, Color color)
        {
            var bitmap = new Bitmap(width, height);
            using (var g = Graphics.FromImage(bitmap))
                g.Clear(color);
            return bitmap;
        }
    }

    public class BinaryWebSocketFrameTests
    {
        [Fact]
        public void Classify_TreatsNonPrefixedJpegAsRenderBuffer()
        {
            BinaryWebSocketFrame.Classify(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }).Should().Be(BinaryFrameKind.Jpeg);
        }

        [Fact]
        public void Classify_AudioAndFilePrefixesAreNotJpeg()
        {
            BinaryWebSocketFrame.Classify(new byte[] { (byte)'A', (byte)'U', (byte)'D', (byte)'I', 1 })
                .Should().Be(BinaryFrameKind.Audio);
            BinaryWebSocketFrame.Classify(new byte[] { (byte)'F', (byte)'I', (byte)'L', (byte)'E', 1 })
                .Should().Be(BinaryFrameKind.File);
        }

        [Fact]
        public void LatestFrameSlot_DropsStaleJpegsSoUwpShowsNewest()
        {
            var slot = new LatestFrameSlot();
            slot.Offer(new byte[] { 1 });
            slot.Offer(new byte[] { 2 });
            slot.TakeLatest().Should().Equal(2);
            slot.TakeLatest().Should().BeNull();
        }
    }

    public class JpegForwardingLoopbackBehavior : WebSocketBehavior
    {
        protected override void OnOpen()
        {
            using (var bitmap = JpegRenderEncoderTests.CreateSolidBitmap(48, 32, Color.DarkGreen))
            {
                var jpeg = new JpegRenderEncoder().Encode(bitmap, RenderFrameSession.QualityMotion);
                Send(jpeg);
            }
        }
    }

    public class RenderBufferLoopbackTests
    {
        [Fact]
        public void WebSocket_ForwardsJpegBinaryToClient()
        {
            var port = GetFreePort();
            var server = new WebSocketServer("ws://127.0.0.1:" + port);
            server.AddWebSocketService<JpegForwardingLoopbackBehavior>("/");
            server.Start();
            try
            {
                byte[] received = null;
                var wasBinary = false;
                using (var done = new ManualResetEventSlim(false))
                using (var ws = new WebSocket("ws://127.0.0.1:" + port + "/"))
                {
                    ws.OnMessage += (s, e) =>
                    {
                        wasBinary = e.IsBinary;
                        received = e.RawData;
                        done.Set();
                    };
                    ws.Connect();
                    ws.IsAlive.Should().BeTrue();
                    done.Wait(System.TimeSpan.FromSeconds(5)).Should().BeTrue("client should receive a JPEG frame");
                }

                wasBinary.Should().BeTrue("UWP treats page frames as binary JPEG");
                BinaryWebSocketFrame.Classify(received).Should().Be(BinaryFrameKind.Jpeg);
            }
            finally
            {
                server.Stop();
            }
        }

        static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
