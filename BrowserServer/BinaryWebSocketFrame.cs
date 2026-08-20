using System.Threading;

namespace BrowserServer
{
    public enum BinaryFrameKind
    {
        Empty,
        Jpeg,
        Audio,
        File,
        Unknown
    }

    /// <summary>
    /// UWP client contract: WebSocket binary frames are JPEG render buffers unless
    /// they start with AUDI (PCM) or FILE (download chunk).
    /// </summary>
    public static class BinaryWebSocketFrame
    {
        public static BinaryFrameKind Classify(byte[] buffer, int count)
        {
            if (buffer == null || count <= 0)
                return BinaryFrameKind.Empty;

            if (count >= 4 &&
                buffer[0] == (byte)'A' && buffer[1] == (byte)'U' &&
                buffer[2] == (byte)'D' && buffer[3] == (byte)'I')
                return BinaryFrameKind.Audio;

            if (count >= 4 &&
                buffer[0] == (byte)'F' && buffer[1] == (byte)'I' &&
                buffer[2] == (byte)'L' && buffer[3] == (byte)'E')
                return BinaryFrameKind.File;

            if (count >= 2 && buffer[0] == 0xFF && buffer[1] == 0xD8)
                return BinaryFrameKind.Jpeg;

            return BinaryFrameKind.Unknown;
        }

        public static BinaryFrameKind Classify(byte[] buffer)
        {
            return Classify(buffer, buffer == null ? 0 : buffer.Length);
        }
    }

    /// <summary>
    /// Latest-frame-wins slot matching UWP <c>pendingFrame</c>: queued JPEGs are dropped
    /// so scrolling does not lag a full second behind.
    /// </summary>
    public sealed class LatestFrameSlot
    {
        byte[] pending;

        public void Offer(byte[] jpeg)
        {
            Interlocked.Exchange(ref pending, jpeg);
        }

        public byte[] TakeLatest()
        {
            return Interlocked.Exchange(ref pending, null);
        }
    }
}
