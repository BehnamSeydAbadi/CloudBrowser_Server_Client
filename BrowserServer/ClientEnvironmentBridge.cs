using System;
using CefSharp;
using Newtonsoft.Json;

namespace BrowserServer
{
    /// <summary>
    /// Applies UWP client environment (viewport, locale, screen, theme, timezone)
    /// without changing User-Agent.
    /// </summary>
    public static class ClientEnvironmentBridge
    {
        public static void Apply(ClientSession session, ClientEnvironmentPayload payload)
        {
            if (session == null || payload == null)
                return;

            session.Environment = payload;
            if (!string.IsNullOrWhiteSpace(payload.acceptLanguage))
                session.AcceptLanguage = payload.acceptLanguage.Trim();

            if (payload.cssWidth >= 1 && payload.cssHeight >= 1)
            {
                session.Tabs.SetViewport(
                    payload.cssWidth,
                    payload.cssHeight,
                    (float)payload.devicePixelRatio);
            }

            Console.WriteLine(
                "Client environment: CSS {0}x{1} @ {2}x, screen {3}x{4}, {5}, lang={6}, tz={7}",
                payload.cssWidth,
                payload.cssHeight,
                payload.devicePixelRatio,
                payload.screenWidth,
                payload.screenHeight,
                payload.orientation,
                session.AcceptLanguage,
                payload.timeZone);

            InjectIntoActive(session);
        }

        public static void InjectShim(ClientSession session, IFrame frame)
        {
            if (session == null || frame == null || !frame.IsValid)
                return;

            var cfg = session.Environment;
            if (cfg == null)
                return;

            try
            {
                var json = JsonConvert.SerializeObject(cfg);
                frame.ExecuteJavaScriptAsync(BuildShimScript(json));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Client environment shim inject error: " + ex.Message);
            }
        }

        private static void InjectIntoActive(ClientSession session)
        {
            try
            {
                var main = session.Tabs.ActiveBrowser?.GetMainFrame();
                if (main != null && main.IsValid)
                    InjectShim(session, main);
            }
            catch
            {
            }
        }

        public static string BuildShimScript(string configJson)
        {
            return @"
(function (cfg) {
  if (window.__cbEnvShim === 1) return;
  window.__cbEnvShim = 1;
  if (!cfg) return;

  function fakeMql(query, matches) {
    var listeners = [];
    return {
      matches: !!matches,
      media: String(query || ''),
      onchange: null,
      addListener: function (fn) { if (typeof fn === 'function') listeners.push(fn); },
      removeListener: function (fn) { listeners = listeners.filter(function (x) { return x !== fn; }); },
      addEventListener: function (type, fn) { if (type === 'change' && typeof fn === 'function') listeners.push(fn); },
      removeEventListener: function (type, fn) { listeners = listeners.filter(function (x) { return x !== fn; }); },
      dispatchEvent: function () { return true; }
    };
  }

  function envQuery(query) {
    var q = String(query || '').toLowerCase().replace(/\s+/g, ' ').trim();
    if (q.indexOf('orientation') !== -1) {
      var portrait = cfg.orientation === 'portrait';
      if (q.indexOf('portrait') !== -1) return portrait;
      if (q.indexOf('landscape') !== -1) return !portrait;
    }
    if (q.indexOf('prefers-color-scheme') !== -1) {
      var dark = cfg.colorScheme === 'dark';
      if (q.indexOf('dark') !== -1) return dark;
      if (q.indexOf('light') !== -1) return !dark;
    }
    if (q.indexOf('pointer') !== -1 && q.indexOf('coarse') !== -1)
      return !!cfg.isMobile;
    return null;
  }

  try {
    var origMatchMedia = window.matchMedia ? window.matchMedia.bind(window) : null;
    window.matchMedia = function (query) {
      var spoof = envQuery(query);
      if (spoof !== null) return fakeMql(query, spoof);
      return origMatchMedia ? origMatchMedia(query) : fakeMql(query, false);
    };
  } catch (e) {}

  try {
    var sw = cfg.screenWidth | 0;
    var sh = cfg.screenHeight | 0;
    if (sw > 0 && sh > 0 && window.screen) {
      try {
        Object.defineProperty(window.screen, 'width', { configurable: true, get: function () { return sw; } });
        Object.defineProperty(window.screen, 'height', { configurable: true, get: function () { return sh; } });
        Object.defineProperty(window.screen, 'availWidth', { configurable: true, get: function () { return sw; } });
        Object.defineProperty(window.screen, 'availHeight', { configurable: true, get: function () { return sh; } });
      } catch (e2) {
        try { window.screen.width = sw; window.screen.height = sh; } catch (e3) {}
      }
    }
  } catch (e) {}

  try {
    var portrait = cfg.orientation === 'portrait';
    Object.defineProperty(window, 'orientation', {
      configurable: true,
      get: function () { return portrait ? 0 : 90; }
    });
  } catch (e) {}

  try {
    if (cfg.isMobile) {
      try {
        Object.defineProperty(navigator, 'maxTouchPoints', { configurable: true, get: function () { return 5; } });
      } catch (e2) {
        try { navigator.maxTouchPoints = 5; } catch (e3) {}
      }
    }
  } catch (e) {}

  try {
    if (cfg.timeZone && window.Intl && Intl.DateTimeFormat) {
      var OrigDTF = Intl.DateTimeFormat;
      Intl.DateTimeFormat = function (locales, options) {
        var dtf = new OrigDTF(locales, options);
        var origResolved = dtf.resolvedOptions.bind(dtf);
        dtf.resolvedOptions = function () {
          var o = origResolved();
          o.timeZone = cfg.timeZone;
          return o;
        };
        return dtf;
      };
      Intl.DateTimeFormat.prototype = OrigDTF.prototype;
    }
  } catch (e) {}

  try { console.log('[cbEnv] shim ready', cfg.orientation, cfg.colorScheme); } catch (e) {}
})(" + configJson + ");";
        }
    }
}
