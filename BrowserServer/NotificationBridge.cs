using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using CefSharp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrowserServer
{
    /// <summary>
    /// Forwards the page Notification API to BrowserClient as native phone toasts.
    /// Permission prompts are shown on the phone (same pattern as MediaBridge).
    /// </summary>
    public static class NotificationBridge
    {
        private static readonly object Sync = new object();
        private static readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> PendingPermission =
            new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        private static readonly ConcurrentDictionary<string, string> PendingPermissionTabId =
            new ConcurrentDictionary<string, string>();

        public static void AttachToBrowser(IWebBrowser browser, string tabId)
        {
            if (browser == null)
                return;

            try
            {
                browser.JavascriptMessageReceived += (sender, e) =>
                {
                    try
                    {
                        HandleJavascriptMessage(tabId, e.Message);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("Notification PostMessage error: " + ex.Message);
                    }
                };

                browser.JavascriptObjectRepository.ResolveObject += (sender, e) =>
                {
                    if (!string.Equals(e.ObjectName, "cbNotify", StringComparison.Ordinal))
                        return;
                    try
                    {
                        e.ObjectRepository.Register(
                            "cbNotify",
                            new NotificationJsBridge(tabId),
                            isAsync: true,
                            options: BindingOptions.DefaultBinder);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("cbNotify register error: " + ex.Message);
                    }
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine("Notification bridge attach error: " + ex.Message);
            }
        }

        public static void InjectShim(string tabId, IFrame frame)
        {
            if (frame == null || !frame.IsValid)
                return;

            try
            {
                var origin = TryGetOrigin(frame.Url);
                var perm = GetPermissionState(tabId, origin);
                var script = NotificationShimScript.Replace("{{PERMISSION}}", perm);
                frame.ExecuteJavaScriptAsync(script);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Notification shim inject error: " + ex.Message);
            }
        }

        public static string GetPermissionState(string tabId, string origin)
        {
            if (string.IsNullOrEmpty(origin))
                return "default";

            var session = ClientSessionHub.GetByTabId(tabId);
            if (session == null)
                return "default";

            if (session?.Device == null)
                return "default";

            lock (session.Device.NotificationOrigins)
            {
                bool allowed;
                if (!session.Device.NotificationOrigins.TryGetValue(origin, out allowed))
                    return "default";
                return allowed ? "granted" : "denied";
            }
        }

        public static Task<string> RequestPermissionAsync(string tabId, string origin)
        {
            origin = origin ?? "";

            lock (Sync)
            {
                var session = ClientSessionHub.GetByTabId(tabId);
                if (session != null && session.Device != null)
                {
                    lock (session.Device.NotificationOrigins)
                    {
                        bool allowed;
                        if (session.Device.NotificationOrigins.TryGetValue(origin, out allowed))
                        {
                            Console.WriteLine("Notification permission cached origin={0} → {1}", origin, allowed ? "granted" : "denied");
                            return Task.FromResult(allowed ? "granted" : "denied");
                        }
                    }
                }
            }

            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingPermission[requestId] = tcs;
            PendingPermissionTabId[requestId] = tabId;

            Console.WriteLine("Notification permission → phone id={0} origin={1}", requestId, origin);

            try
            {
                var session = ClientSessionHub.GetByTabId(tabId);
                if (session == null)
                    throw new InvalidOperationException("no session for tab");

                session.SendText(new TextPacket
                {
                    PType = TextPacketType.NotificationPermissionRequest,
                    text = JsonConvert.SerializeObject(new NotificationPermissionPayload
                    {
                        requestId = requestId,
                        origin = origin
                    })
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("Notification permission broadcast error: " + ex.Message);
                PendingPermission.TryRemove(requestId, out _);
                return Task.FromResult("denied");
            }

            Task.Delay(120000).ContinueWith(t =>
            {
                TaskCompletionSource<bool> pending;
                if (PendingPermission.TryRemove(requestId, out pending))
                {
                    Console.WriteLine("Notification permission timeout id=" + requestId);
                    pending.TrySetResult(false);
                }
            });

            return tcs.Task.ContinueWith(t =>
            {
                var allowed = t.Status == TaskStatus.RanToCompletion && t.Result;
                return allowed ? "granted" : "denied";
            });
        }

        public static void HandlePermissionResponse(NotificationPermissionPayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.requestId))
                return;

            TaskCompletionSource<bool> tcs;
            if (!PendingPermission.TryRemove(payload.requestId, out tcs))
            {
                Console.WriteLine("Notification permission response for unknown id=" + payload.requestId);
                return;
            }

            var origin = payload.origin ?? "";
            string tabId;
            PendingPermissionTabId.TryRemove(payload.requestId, out tabId);
            var session = !string.IsNullOrEmpty(tabId) ? ClientSessionHub.GetByTabId(tabId) : null;
            if (session != null && session.Device != null && !string.IsNullOrEmpty(origin))
            {
                lock (session.Device.NotificationOrigins)
                {
                    session.Device.NotificationOrigins[origin] = payload.allowed;
                }
            }

            Console.WriteLine(
                "Notification permission response id={0} origin={1} allowed={2}",
                payload.requestId, origin, payload.allowed);
            tcs.TrySetResult(payload.allowed);
        }

        public static void Show(string tabId, string title, string body, string tag, string origin, string icon, string url)
        {
            title = title ?? "";
            body = body ?? "";
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(body))
                return;

            // Block show if this origin was explicitly denied.
            if (!string.IsNullOrEmpty(origin))
            {
                var session = ClientSessionHub.GetByTabId(tabId);
                if (session != null && session.Device != null)
                {
                    lock (session.Device.NotificationOrigins)
                    {
                        bool allowed;
                        if (session.Device.NotificationOrigins.TryGetValue(origin, out allowed) && !allowed)
                        {
                            Console.WriteLine("Notification blocked (denied) origin=" + origin);
                            return;
                        }
                    }
                }
            }

            Console.WriteLine(
                "Notification → phone tab={0} origin={1} title={2}",
                tabId,
                origin,
                title.Length > 60 ? title.Substring(0, 57) + "…" : title);

            try
            {
                var session = ClientSessionHub.GetByTabId(tabId);
                if (session == null)
                    return;

                session.SendText(new TextPacket
                {
                    PType = TextPacketType.Notification,
                    text = JsonConvert.SerializeObject(new NotificationPayload
                    {
                        title = title,
                        body = body,
                        tag = tag ?? "",
                        origin = origin ?? "",
                        icon = icon ?? "",
                        url = url ?? ""
                    })
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine("Notification broadcast error: " + ex.Message);
            }
        }

        private static string TryGetOrigin(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return "";
            try
            {
                var uri = new Uri(url);
                if (uri.IsAbsoluteUri && (uri.Scheme == "http" || uri.Scheme == "https"))
                    return uri.GetLeftPart(UriPartial.Authority);
            }
            catch
            {
            }
            return "";
        }

        private static void HandleJavascriptMessage(string tabId, object message)
        {
            if (message == null)
                return;

            JObject obj = null;
            var asString = message as string;
            if (!string.IsNullOrEmpty(asString))
            {
                asString = asString.Trim();
                if (asString.Length == 0 || asString[0] != '{')
                    return;
                obj = JObject.Parse(asString);
            }
            else
            {
                var dict = message as IDictionary<string, object>;
                if (dict != null)
                    obj = JObject.FromObject(dict);
                else
                {
                    try { obj = JObject.FromObject(message); }
                    catch { return; }
                }
            }

            if (obj == null)
                return;

            var type = (string)obj["type"] ?? (string)obj["Type"];
            if (!string.Equals(type, "notification", StringComparison.OrdinalIgnoreCase))
                return;

            Show(
                tabId,
                (string)obj["title"] ?? (string)obj["Title"] ?? "",
                (string)obj["body"] ?? (string)obj["Body"] ?? "",
                (string)obj["tag"] ?? (string)obj["Tag"] ?? "",
                (string)obj["origin"] ?? (string)obj["Origin"] ?? "",
                (string)obj["icon"] ?? (string)obj["Icon"] ?? "",
                (string)obj["url"] ?? (string)obj["Url"] ?? "");
        }

        private const string NotificationShimScript = @"
(function () {
  if (window.__cbNotifyShim === 3) return;
  window.__cbNotifyShim = 3;

  var permission = '{{PERMISSION}}';

  function post(payload) {
    try {
      if (window.CefSharp && CefSharp.PostMessage) {
        CefSharp.PostMessage(payload);
        return true;
      }
    } catch (e) {}
    return false;
  }

  function ensureBound() {
    if (window.cbNotify) return Promise.resolve(true);
    if (window.CefSharp && CefSharp.BindObjectAsync)
      return CefSharp.BindObjectAsync('cbNotify').then(function () { return !!window.cbNotify; }).catch(function () { return false; });
    return Promise.resolve(false);
  }

  function forward(title, options) {
    if (permission !== 'granted') {
      try { console.log('[cbNotify] blocked show — permission=' + permission); } catch (e) {}
      return;
    }
    options = options || {};
    var body = options.body != null ? String(options.body) : '';
    var tag = options.tag != null ? String(options.tag) : '';
    var icon = options.icon != null ? String(options.icon) : '';
    var url = '';
    try {
      if (options.data) {
        if (typeof options.data === 'string') url = options.data;
        else if (options.data.url) url = String(options.data.url);
      }
    } catch (e) {}

    var payload = {
      type: 'notification',
      title: String(title || ''),
      body: body,
      tag: tag,
      origin: (location && location.origin) ? location.origin : '',
      icon: icon,
      url: url
    };

    if (post(payload)) {
      try { console.log('[cbNotify] posted', payload.title); } catch (e) {}
      return;
    }

    ensureBound().then(function (bound) {
      try {
        if (bound && window.cbNotify && cbNotify.show)
          cbNotify.show(payload.title, payload.body, payload.tag, payload.origin, payload.icon, payload.url);
      } catch (e) {
        try { console.log('[cbNotify] bind show failed', e); } catch (e2) {}
      }
    });
  }

  function NotificationCtor(title, options) {
    if (permission !== 'granted') {
      var err = new TypeError(""Failed to construct 'Notification': Permission denied."");
      err.name = 'TypeError';
      throw err;
    }
    options = options || {};
    this.title = title != null ? String(title) : '';
    this.body = options.body != null ? String(options.body) : '';
    this.tag = options.tag != null ? String(options.tag) : '';
    this.icon = options.icon != null ? String(options.icon) : '';
    this.dir = options.dir || 'auto';
    this.lang = options.lang || '';
    this.data = options.data;
    this.silent = !!options.silent;
    this.onclick = null;
    this.onclose = null;
    this.onerror = null;
    this.onshow = null;
    this.close = function () {};
    forward(this.title, options);
    var self = this;
    setTimeout(function () {
      try { if (typeof self.onshow === 'function') self.onshow(); } catch (e) {}
    }, 0);
  }

  try {
    Object.defineProperty(NotificationCtor, 'permission', {
      get: function () { return permission; },
      configurable: true
    });
  } catch (e) {
    NotificationCtor.permission = permission;
  }

  NotificationCtor.requestPermission = function (callback) {
    var result = ensureBound().then(function (bound) {
      if (!bound || !(window.cbNotify && cbNotify.requestPermission))
        return 'denied';
      return Promise.resolve(cbNotify.requestPermission(location.origin || '')).then(function (p) {
        permission = (p === 'granted' || p === 'denied') ? p : 'denied';
        return permission;
      });
    }).catch(function () { return 'denied'; });

    if (typeof callback === 'function')
      result.then(function (p) { try { callback(p); } catch (e) {} });
    return result;
  };

  NotificationCtor.maxActions = 0;

  try {
    window.Notification = NotificationCtor;
  } catch (e) {
    try {
      Object.defineProperty(window, 'Notification', {
        configurable: true,
        enumerable: true,
        writable: true,
        value: NotificationCtor
      });
    } catch (e2) {}
  }

  try {
    if (window.ServiceWorkerRegistration && ServiceWorkerRegistration.prototype) {
      ServiceWorkerRegistration.prototype.showNotification = function (title, options) {
        if (permission !== 'granted')
          return Promise.reject(new TypeError('Notification permission denied'));
        forward(title, options || {});
        return Promise.resolve();
      };
    }
  } catch (e) {}

  // Sync with server in case inject ran before a prior decision was known.
  ensureBound().then(function (bound) {
    if (!(bound && window.cbNotify && cbNotify.getPermission)) return;
    return Promise.resolve(cbNotify.getPermission(location.origin || '')).then(function (p) {
      if (p === 'granted' || p === 'denied') permission = p;
    });
  });

  try { console.log('[cbNotify] shim ready permission=' + permission); } catch (e) {}
})();";
    }

    public sealed class NotificationJsBridge
    {
        private readonly string tabId;

        public NotificationJsBridge(string tabId)
        {
            this.tabId = tabId;
        }

        public Task Show(string title, string body, string tag, string origin, string icon, string url)
        {
            NotificationBridge.Show(tabId, title, body, tag, origin, icon, url);
            return Task.CompletedTask;
        }

        public Task<string> RequestPermission(string origin)
        {
            return NotificationBridge.RequestPermissionAsync(tabId, origin);
        }

        public Task<string> GetPermission(string origin)
        {
            return Task.FromResult(NotificationBridge.GetPermissionState(tabId, origin));
        }
    }
}
