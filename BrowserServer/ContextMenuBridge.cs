using System;
using System.Threading.Tasks;
using CefSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrowserServer
{
    /// <summary>
    /// Long-press context menu: hit-test page at touch coords and send an offer to the phone.
    /// </summary>
    public static class ContextMenuBridge
    {
        public static async Task HandleQueryAsync(ClientSession session, PointerPacket pointer)
        {
            if (session == null)
                return;

            var browser = session.Tabs.ActiveBrowser;
            if (browser == null || !browser.IsBrowserInitialized)
                return;

            var cssX = (float)pointer.px * session.Tabs.CssWidth;
            var cssY = (float)pointer.py * session.Tabs.CssHeight;
            var script = BuildHitTestScript(cssX, cssY);

            try
            {
                var response = await browser.EvaluateScriptAsync(script).ConfigureAwait(false);
                var offer = ParseHitTestResult(response);
                SendOffer(session, offer);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Context menu hit-test error: " + ex.Message);
                SendOffer(session, new ContextMenuOfferPayload());
            }
        }

        public static void HandleAction(ClientSession session, ContextMenuActionPayload action)
        {
            if (session == null || action == null || string.IsNullOrWhiteSpace(action.action))
                return;

            var tabs = session.Tabs;
            var url = (action.url ?? "").Trim();
            switch (action.action)
            {
                case "openNewTab":
                    if (string.IsNullOrEmpty(url))
                        return;
                    var tab = tabs.CreateTab(url);
                    if (tab != null)
                        tabs.ScheduleNavigate(tab, url);
                    else
                        tabs.NavigateActive(url);
                    break;

                case "saveImage":
                    if (string.IsNullOrEmpty(url))
                        return;
                    StreamingDownloadHandler.StreamUrlToClient(session, url, GuessImageFileName(url));
                    break;
            }
        }

        public static ContextMenuOfferPayload ParseHitTestResult(JavascriptResponse response)
        {
            if (response == null || !response.Success || response.Result == null)
                return new ContextMenuOfferPayload();

            return ParseHitTestJson(response.Result as string);
        }

        public static ContextMenuOfferPayload ParseHitTestJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new ContextMenuOfferPayload();

            try
            {
                var obj = JObject.Parse(json);
                return new ContextMenuOfferPayload
                {
                    linkUrl = NullIfEmpty(obj.Value<string>("linkUrl")),
                    imageUrl = NullIfEmpty(obj.Value<string>("imageUrl")),
                    text = NullIfEmpty(obj.Value<string>("text"))
                };
            }
            catch
            {
                return new ContextMenuOfferPayload();
            }
        }

        public static string BuildHitTestScript(float cssX, float cssY)
        {
            var x = cssX.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var y = cssY.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return @"
(function () {
  var x = " + x + @";
  var y = " + y + @";
  var el = document.elementFromPoint(x, y);
  if (!el) return JSON.stringify({});
  var linkUrl = null, imageUrl = null, text = '';
  try {
    var sel = window.getSelection ? window.getSelection().toString() : '';
    if (sel) text = sel.trim();
  } catch (e) {}
  var node = el;
  while (node && node !== document.documentElement) {
    if (node.tagName === 'A' && node.href) {
      linkUrl = node.href;
      if (!text) text = (node.innerText || node.textContent || '').trim();
      break;
    }
    if (node.tagName === 'IMG') {
      imageUrl = node.currentSrc || node.src || null;
      if (!text) text = (node.alt || '').trim();
    }
    node = node.parentElement;
  }
  if (!imageUrl && el.tagName === 'IMG')
    imageUrl = el.currentSrc || el.src || null;
  if (linkUrl && !imageUrl) {
    var img = el.tagName === 'IMG' ? el : (el.querySelector ? el.querySelector('img') : null);
    if (img) imageUrl = img.currentSrc || img.src || null;
  }
  if (!linkUrl && imageUrl)
    linkUrl = imageUrl;
  return JSON.stringify({
    linkUrl: linkUrl || null,
    imageUrl: imageUrl || null,
    text: text || null
  });
})();";
        }

        static void SendOffer(ClientSession session, ContextMenuOfferPayload offer)
        {
            try
            {
                session.SendText(TextPacketType.ContextMenu, JsonConvert.SerializeObject(offer ?? new ContextMenuOfferPayload()));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Context menu send error: " + ex.Message);
            }
        }

        static string GuessImageFileName(string url)
        {
            try
            {
                var uri = new Uri(url);
                var name = System.IO.Path.GetFileName(uri.LocalPath);
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
            catch
            {
            }
            return "image.jpg";
        }

        static string NullIfEmpty(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
