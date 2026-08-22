using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using ZXing;
using ZXing.Common;

namespace BrowserServer.Tests.Fixtures
{
    public static class QrFixtures
    {
        public static byte[] CreateQrJpeg(string content, int pixels = 256)
        {
            var writer = new BarcodeWriter
            {
                Format = BarcodeFormat.QR_CODE,
                Options = new EncodingOptions
                {
                    Width = pixels,
                    Height = pixels,
                    Margin = 2
                }
            };

            using (var bitmap = writer.Write(content))
            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Jpeg);
                return stream.ToArray();
            }
        }
    }
}
