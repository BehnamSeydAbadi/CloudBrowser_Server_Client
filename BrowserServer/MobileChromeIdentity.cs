using CefSharp.OffScreen;

namespace BrowserServer
{
    /// <summary>
    /// Mobile Chrome identity for CefSharp.
    /// Do NOT use DevTools Emulation here — CaptureScreenshotAsync already uses DevTools,
    /// and a second client causes "MessageId doesn't match" / black screen.
    /// UA comes from CefSettings; CSS viewport comes from Size + DeviceScaleFactor.
    /// </summary>
    public static class MobileChromeIdentity
    {
        public const string UserAgent =
            "Mozilla/5.0 (Linux; Android 13; Pixel 7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/113.0.5672.63 Mobile Safari/537.36";

        public static void Apply(ChromiumWebBrowser browser)
        {
            if (browser == null)
                return;

            browser.DeviceScaleFactor = TabManager.DeviceScaleFactor;
            browser.Size = TabManager.BrowserSize;
        }
    }
}
