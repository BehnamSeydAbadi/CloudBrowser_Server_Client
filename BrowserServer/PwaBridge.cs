using System;
using System.Collections.Generic;
using CefSharp;

namespace BrowserServer
{
    /// <summary>
    /// Makes pinned "Add to Home" sites look like an installed PWA to page JavaScript
    /// (display-mode: standalone, getInstalledRelatedApps, no install prompt).
    /// </summary>
    public static class PwaBridge
    {
        public static void SetInstalledUrls(ClientSession session, IEnumerable<string> urls, bool reloadMatchingTab)
        {
            if (session == null)
                return;

            if (session?.Device == null)
                return;

            lock (session.Device.PwaInstalledOrigins)
            {
                session.Device.PwaInstalledOrigins.Clear();
                if (urls != null)
                {
                    foreach (var url in urls)
                    {
                        var origin = TryGetOrigin(url);
                        if (!string.IsNullOrEmpty(origin))
                            session.Device.PwaInstalledOrigins.Add(origin);
                    }
                }
            }

            Console.WriteLine("PWA installed origins: " + GetOriginSummary(session));

            if (reloadMatchingTab)
                ReloadActiveIfPinned(session);
            else
                InjectIntoActive(session);
        }

        public static bool IsInstalled(ClientSession session, string url)
        {
            if (session == null)
                return false;

            var origin = TryGetOrigin(url);
            if (string.IsNullOrEmpty(origin))
                return false;

            if (session?.Device == null)
                return false;

            lock (session.Device.PwaInstalledOrigins)
            {
                return session.Device.PwaInstalledOrigins.Contains(origin);
            }
        }

        public static void InjectShim(ClientSession session, IFrame frame, TabSession activeTab)
        {
            if (session == null || frame == null || !frame.IsValid)
                return;

            if (!IsInstalled(session, frame.Url)
                && !(frame.IsMain && activeTab != null && IsInstalled(session, activeTab.Url)))
                return;

            try
            {
                frame.ExecuteJavaScriptAsync(PwaShimScript);
            }
            catch (Exception ex)
            {
                Console.WriteLine("PWA shim inject error: " + ex.Message);
            }
        }

        private static void InjectIntoActive(ClientSession session)
        {
            try
            {
                var main = session.Tabs.ActiveBrowser?.GetMainFrame();
                if (main != null && main.IsValid)
                    InjectShim(session, main, session.Tabs.Active);
            }
            catch
            {
            }
        }

        private static void ReloadActiveIfPinned(ClientSession session)
        {
            try
            {
                var browser = session.Tabs.ActiveBrowser;
                if (browser == null)
                    return;
                if (!IsInstalled(session, browser.Address))
                    return;
                Console.WriteLine("PWA reload (standalone) " + browser.Address);
                browser.Reload();
            }
            catch (Exception ex)
            {
                Console.WriteLine("PWA reload error: " + ex.Message);
            }
        }

        public static string TryGetOrigin(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;
            try
            {
                var uri = new Uri(url.Trim(), UriKind.Absolute);
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    return null;
                return uri.GetLeftPart(UriPartial.Authority);
            }
            catch
            {
                return null;
            }
        }

        private static string GetOriginSummary(ClientSession session)
        {
            if (session?.Device == null)
                return "(none)";

            lock (session.Device.PwaInstalledOrigins)
            {
                if (session.Device.PwaInstalledOrigins.Count == 0)
                    return "(none)";
                return string.Join(", ", session.Device.PwaInstalledOrigins);
            }
        }

        private const string PwaShimScript = @"
(function () {
  if (window.__cbPwaShim === 1) return;
  window.__cbPwaShim = 1;

  function fakeMql(query, matches) {
    var listeners = [];
    var mql = {
      matches: !!matches,
      media: String(query || ''),
      onchange: null,
      addListener: function (fn) { if (typeof fn === 'function') listeners.push(fn); },
      removeListener: function (fn) { listeners = listeners.filter(function (x) { return x !== fn; }); },
      addEventListener: function (type, fn) { if (type === 'change' && typeof fn === 'function') listeners.push(fn); },
      removeEventListener: function (type, fn) { listeners = listeners.filter(function (x) { return x !== fn; }); },
      dispatchEvent: function () { return true; }
    };
    return mql;
  }

  function standaloneQuery(query) {
    var q = String(query || '').toLowerCase().replace(/\s+/g, ' ').trim();
    if (q.indexOf('display-mode') === -1) return null;
    var not = /\bnot\b/.test(q);
    var standalone = q.indexOf('standalone') !== -1;
    var fullscreen = q.indexOf('fullscreen') !== -1;
    var minimal = q.indexOf('minimal-ui') !== -1;
    var browser = q.indexOf('browser') !== -1 && !standalone && !fullscreen && !minimal;
    var installed = standalone || fullscreen || minimal;
    if (installed) return not ? false : true;
    if (browser) return not ? true : false;
    return null;
  }

  try {
    var origMatchMedia = window.matchMedia ? window.matchMedia.bind(window) : null;
    window.matchMedia = function (query) {
      var spoof = standaloneQuery(query);
      if (spoof !== null) return fakeMql(query, spoof);
      return origMatchMedia ? origMatchMedia(query) : fakeMql(query, false);
    };
  } catch (e) {}

  try {
    Object.defineProperty(navigator, 'standalone', {
      configurable: true,
      get: function () { return true; }
    });
  } catch (e) {
    try { navigator.standalone = true; } catch (e2) {}
  }

  try {
    navigator.getInstalledRelatedApps = function () {
      var url = (location.origin || '') + '/';
      return Promise.resolve([{ platform: 'webapp', url: url, id: '' }]);
    };
  } catch (e) {}

  try {
    window.addEventListener('beforeinstallprompt', function (e) {
      try { e.preventDefault(); } catch (err) {}
      try { e.stopImmediatePropagation(); } catch (err2) {}
    }, true);
  } catch (e) {}

  try {
    if (window.CSS && CSS.supports) {
      var origSupports = CSS.supports.bind(CSS);
      CSS.supports = function (prop, value) {
        var a = String(prop || '').toLowerCase();
        var b = value != null ? String(value).toLowerCase() : '';
        if (a.indexOf('display-mode') !== -1 && (a.indexOf('standalone') !== -1 || b.indexOf('standalone') !== -1))
          return true;
        return origSupports.apply(CSS, arguments);
      };
    }
  } catch (e) {}

  try { console.log('[cbPwa] standalone shim ready'); } catch (e) {}
})();";
    }
}
