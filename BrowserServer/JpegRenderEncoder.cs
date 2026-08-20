using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace BrowserServer
{
    /// <summary>Cheap content hash so static pages are not re-encoded every tick.</summary>
    public static class BitmapContentHash
    {
        public static int Compute(Bitmap bitmap)
        {
            if (bitmap == null)
                throw new ArgumentNullException("bitmap");

            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = null;
            try
            {
                data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int stride = Math.Abs(data.Stride);
                int w = bitmap.Width;
                int h = bitmap.Height;
                int hash = w * 73856093 ^ h * 19349663;
                IntPtr scan0 = data.Scan0;
                for (int y = 0; y < h; y += 4)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x += 4)
                        hash = (hash * 16777619) ^ Marshal.ReadInt32(scan0, row + (x << 2));
                }
                return hash;
            }
            catch
            {
                return Environment.TickCount;
            }
            finally
            {
                if (data != null)
                {
                    try { bitmap.UnlockBits(data); } catch { }
                }
            }
        }
    }

    public sealed class JpegRenderEncoder
    {
        readonly MemoryStream stream = new MemoryStream(160 * 1024);
        ImageCodecInfo codec;

        public byte[] Encode(Bitmap bitmap, long quality)
        {
            if (bitmap == null)
                throw new ArgumentNullException("bitmap");

            if (codec == null)
                codec = FindJpegCodec();
            stream.SetLength(0);

            var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(stream, codec ?? FindJpegCodec(), encoderParameters);
            return stream.ToArray();
        }

        static ImageCodecInfo FindJpegCodec()
        {
            var codecs = ImageCodecInfo.GetImageDecoders();
            foreach (var c in codecs)
            {
                if (c.FormatID == ImageFormat.Jpeg.Guid)
                    return c;
            }
            return null;
        }
    }
}
