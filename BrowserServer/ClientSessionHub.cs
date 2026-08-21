using System;
using System.Collections.Generic;
using System.Linq;
using CefSharp;
using CefSharp.OffScreen;

namespace BrowserServer
{
    /// <summary>Registry of one ClientSession per WebSocket connection.</summary>
    public static class ClientSessionHub
    {
        public const int MaxSessions = 8;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, ClientSession> ByWebSocketId =
            new Dictionary<string, ClientSession>(StringComparer.Ordinal);
        private static readonly Dictionary<string, ClientSession> ByTabId =
            new Dictionary<string, ClientSession>(StringComparer.Ordinal);

        public static Func<ChromiumWebBrowser, string, DefaultRenderHandler> CreateRenderHandler { get; set; }

        public static ClientSession Create(string webSocketSessionId)
        {
            if (string.IsNullOrEmpty(webSocketSessionId))
                throw new ArgumentException("session id required", "webSocketSessionId");

            lock (Sync)
            {
                if (ByWebSocketId.Count >= MaxSessions && !ByWebSocketId.ContainsKey(webSocketSessionId))
                    return null;

                ClientSession existing;
                if (ByWebSocketId.TryGetValue(webSocketSessionId, out existing))
                    return existing;

                var session = new ClientSession(webSocketSessionId);
                ByWebSocketId[webSocketSessionId] = session;
                return session;
            }
        }

        public static ClientSession Get(string webSocketSessionId)
        {
            if (string.IsNullOrEmpty(webSocketSessionId))
                return null;

            lock (Sync)
            {
                ClientSession session;
                return ByWebSocketId.TryGetValue(webSocketSessionId, out session) ? session : null;
            }
        }

        public static ClientSession GetByTabId(string tabId)
        {
            if (string.IsNullOrEmpty(tabId))
                return null;

            lock (Sync)
            {
                ClientSession session;
                return ByTabId.TryGetValue(tabId, out session) ? session : null;
            }
        }

        public static void RegisterTab(string tabId, ClientSession session)
        {
            if (string.IsNullOrEmpty(tabId) || session == null)
                return;

            lock (Sync)
            {
                ByTabId[tabId] = session;
            }
        }

        public static void UnregisterTab(string tabId)
        {
            if (string.IsNullOrEmpty(tabId))
                return;

            lock (Sync)
            {
                ByTabId.Remove(tabId);
            }
        }

        public static void Remove(string webSocketSessionId)
        {
            if (string.IsNullOrEmpty(webSocketSessionId))
                return;

            ClientSession session;
            lock (Sync)
            {
                if (!ByWebSocketId.TryGetValue(webSocketSessionId, out session))
                    return;

                ByWebSocketId.Remove(webSocketSessionId);
                var tabIds = session.Tabs.AllTabIds().ToList();
                foreach (var tabId in tabIds)
                    ByTabId.Remove(tabId);
            }

            session.Dispose();
        }

        public static IEnumerable<ClientSession> AllActive()
        {
            lock (Sync)
            {
                return ByWebSocketId.Values.ToList();
            }
        }

        public static int Count
        {
            get { lock (Sync) return ByWebSocketId.Count; }
        }

        public static void ResetForTests()
        {
            List<ClientSession> sessions;
            lock (Sync)
            {
                sessions = ByWebSocketId.Values.ToList();
                ByWebSocketId.Clear();
                ByTabId.Clear();
            }

            foreach (var session in sessions)
            {
                try { session.Dispose(); } catch { }
            }
        }

        public static string FindTabIdForBrowser(IBrowser browser)
        {
            if (browser == null)
                return null;

            lock (Sync)
            {
                foreach (var session in ByWebSocketId.Values)
                {
                    foreach (var tab in session.Tabs.AllSessions())
                    {
                        if (tab?.Browser == null)
                            continue;
                        try
                        {
                            var cef = tab.Browser.GetBrowser();
                            if (cef != null && cef.Identifier == browser.Identifier)
                                return tab.Id;
                        }
                        catch
                        {
                        }
                    }
                }
            }

            return null;
        }
    }
}
