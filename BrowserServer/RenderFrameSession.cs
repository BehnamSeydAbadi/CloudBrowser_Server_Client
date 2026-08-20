using System;
using System.Threading;

namespace BrowserServer
{
    public enum RenderFrameSkipReason
    {
        None,
        DownloadStreaming,
        AudioBacklog,
        CameraThrottle,
        VideoKeepaliveThrottle,
        CaptureInFlight,
        UnchangedKeepalive
    }

    /// <summary>
    /// Adaptive JPEG page-stream policy used when forwarding the CEF render buffer
    /// to the UWP client over the shared WebSocket.
    /// </summary>
    public sealed class RenderFrameSession
    {
        public const int AudioBacklogLimit = 12;
        public const int CameraMinIntervalMs = 400;
        public const int VideoPageMinIntervalMs = 750;
        public const int CaptureStuckMs = 3000;
        public const int KeepaliveMs = 750;
        public const int CrispAfterStillMs = 160;

        public const long QualityCamera = 50L;
        public const long QualityMotion = 80L;
        public const long QualityCrisp = 90L;
        public const long QualityKeepalive = 82L;

        int captureInFlight;
        int captureStartedTick;
        int lastMediaAwareFrameTick;
        int lastVideoPageTick;
        int lastFrameHash;
        bool hasLastHash;
        int lastSendTick;
        int lastDirtyTick;
        bool sentCrispFrame;

        public void Reset()
        {
            Interlocked.Exchange(ref captureInFlight, 0);
            captureStartedTick = 0;
            lastFrameHash = 0;
            hasLastHash = false;
            sentCrispFrame = false;
        }

        public RenderFrameSkipReason GetSharedSocketSkip(bool downloadStreaming, int audioPending)
        {
            if (downloadStreaming)
                return RenderFrameSkipReason.DownloadStreaming;
            if (audioPending > AudioBacklogLimit)
                return RenderFrameSkipReason.AudioBacklog;
            return RenderFrameSkipReason.None;
        }

        public RenderFrameSkipReason GetMediaThrottleSkip(bool mediaOn, bool videoOn, int now)
        {
            if (mediaOn)
            {
                if (now - lastMediaAwareFrameTick < CameraMinIntervalMs)
                    return RenderFrameSkipReason.CameraThrottle;
                lastMediaAwareFrameTick = now;
            }
            else if (videoOn)
            {
                if (now - lastVideoPageTick < VideoPageMinIntervalMs)
                    return RenderFrameSkipReason.VideoKeepaliveThrottle;
                lastVideoPageTick = now;
            }

            return RenderFrameSkipReason.None;
        }

        public bool TryBeginCapture(int now)
        {
            if (Interlocked.CompareExchange(ref captureInFlight, 1, 0) != 0)
            {
                var started = Volatile.Read(ref captureStartedTick);
                if (started != 0 && (now - started) > CaptureStuckMs)
                    Interlocked.Exchange(ref captureInFlight, 0);
                return false;
            }

            captureStartedTick = now;
            return true;
        }

        public void EndCapture()
        {
            Interlocked.Exchange(ref captureInFlight, 0);
        }

        /// <summary>
        /// Chooses JPEG quality for this buffer. Returns false when an unchanged
        /// page should not be re-sent yet (keepalive window).
        /// </summary>
        public bool TrySelectQuality(int bitmapHash, bool mediaOn, int now, out long quality)
        {
            quality = QualityMotion;
            var dirty = !hasLastHash || bitmapHash != lastFrameHash;

            if (mediaOn)
            {
                quality = QualityCamera;
                return true;
            }

            if (dirty)
            {
                lastFrameHash = bitmapHash;
                hasLastHash = true;
                lastDirtyTick = now;
                sentCrispFrame = false;
                quality = QualityMotion;
                return true;
            }

            if (!sentCrispFrame && (now - lastDirtyTick) >= CrispAfterStillMs)
            {
                sentCrispFrame = true;
                quality = QualityCrisp;
                return true;
            }

            if ((now - lastSendTick) < KeepaliveMs)
            {
                quality = 0;
                return false;
            }

            quality = QualityKeepalive;
            return true;
        }

        public void MarkSent(int now)
        {
            lastSendTick = now;
        }
    }
}
