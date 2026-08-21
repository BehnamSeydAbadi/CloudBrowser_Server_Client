using CefSharp;
using CefSharp.Handler;
using System;

namespace BrowserServer
{
    /// <summary>
    /// Off-screen CEF has no real popup windows. Open target=_blank / window.open in the same tab.
    /// Without this, Google result clicks often hang or crash the render process.
    /// </summary>
    public class SameTabLifeSpanHandler : LifeSpanHandler
    {
        protected override bool OnBeforePopup(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            string targetUrl,
            string targetFrameName,
            WindowOpenDisposition targetDisposition,
            bool userGesture,
            IPopupFeatures popupFeatures,
            IWindowInfo windowInfo,
            IBrowserSettings browserSettings,
            ref bool noJavascriptAccess,
            out IWebBrowser newBrowser)
        {
            newBrowser = null;

            if (!string.IsNullOrWhiteSpace(targetUrl))
            {
                Console.WriteLine("Popup → same tab: " + targetUrl);
                try
                {
                    if (browser != null && browser.MainFrame != null && browser.MainFrame.IsValid)
                        browser.MainFrame.LoadUrl(targetUrl);
                    else
                        chromiumWebBrowser.Load(targetUrl);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Popup redirect failed: " + ex.Message);
                }
            }

            return true; // cancel creating a popup browser
        }

        protected override void OnAfterCreated(IWebBrowser chromiumWebBrowser, IBrowser browser)
        {
            // Off-screen popups cancelled in OnBeforePopup can still briefly exist in CEF.
            if (browser != null && browser.IsPopup)
            {
                try
                {
                    browser.GetHost()?.CloseBrowser(true);
                }
                catch
                {
                }
            }
        }
    }

    /// <summary>
    /// Continue past certificate errors so https sites with odd certs still load in this local streaming setup.
    /// Stamps Accept-Language from the connected UWP client when available.
    /// </summary>
    public class PermissiveRequestHandler : RequestHandler
    {
        private readonly ClientSession owner;

        public PermissiveRequestHandler(ClientSession owner)
        {
            this.owner = owner;
        }

        protected override IResourceRequestHandler GetResourceRequestHandler(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            IRequest request,
            bool isNavigation,
            bool isDownload,
            string requestInitiator,
            ref bool disableDefaultHandling)
        {
            return new AcceptLanguageResourceRequestHandler(owner);
        }

        protected override bool OnCertificateError(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            CefErrorCode errorCode,
            string requestUrl,
            ISslInfo sslInfo,
            IRequestCallback callback)
        {
            Console.WriteLine("Certificate error (" + errorCode + ") for " + requestUrl + " — continuing");
            if (!callback.IsDisposed)
            {
                using (callback)
                {
                    callback.Continue(true);
                }
            }
            return true;
        }
    }

    sealed class AcceptLanguageResourceRequestHandler : ResourceRequestHandler
    {
        private readonly ClientSession owner;

        public AcceptLanguageResourceRequestHandler(ClientSession owner)
        {
            this.owner = owner;
        }

        protected override CefReturnValue OnBeforeResourceLoad(
            IWebBrowser chromiumWebBrowser,
            IBrowser browser,
            IFrame frame,
            IRequest request,
            IRequestCallback callback)
        {
            var lang = owner != null ? owner.AcceptLanguage : "en-US,en";
            if (!string.IsNullOrWhiteSpace(lang))
                request.SetHeaderByName("Accept-Language", lang, true);
            return CefReturnValue.Continue;
        }
    }
}
