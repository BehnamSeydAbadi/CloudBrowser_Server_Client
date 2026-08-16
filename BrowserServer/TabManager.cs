using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using WebSocketSharp.Server;

namespace BrowserServer
{
    public class TabSession
    {
        public string Id { get; set; }
        public ChromiumWebBrowser Browser { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
    }

    public static class TabManager
    {
        public const int MaxTabs = 8;
        public const string DefaultUrl = "https://www.google.com/";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, TabSession> Tabs = new Dictionary<string, TabSession>();
        private static readonly List<string> TabOrder = new List<string>();

        public static string ActiveTabId { get; private set; }
        /// <summary>CSS viewport = BrowserClient ScaleRect (page displayer), excluding navbars.</summary>
        public static int CssWidth { get; private set; } = 360;
        public static int CssHeight { get; private set; } = 640;
        public static float DeviceScaleFactor { get; private set; } = 2f;
        /// <summary>Equals Css size — CEF GetViewRect uses Size as the CSS viewport.</summary>
        public static Size BrowserSize { get; private set; } = new Size(360, 640);
        public static WebSocketServer Server { get; set; }
        public static Func<ChromiumWebBrowser, string, DefaultRenderHandler> CreateRenderHandler { get; set; }

        public static TabSession Active
        {
            get
            {
                lock (Sync)
                {
                    if (ActiveTabId != null && Tabs.TryGetValue(ActiveTabId, out var session))
                        return session;
                    return null;
                }
            }
        }

        public static ChromiumWebBrowser ActiveBrowser
        {
            get
            {
                var active = Active;
                return active?.Browser;
            }
        }

        public static TabSession EnsureInitialTab()
        {
            lock (Sync)
            {
                if (Tabs.Count == 0)
                    return CreateTabUnlocked(DefaultUrl, setActive: true);
                return Tabs[ActiveTabId];
            }
        }

        public static TabSession CreateTab(string url = null)
        {
            lock (Sync)
            {
                if (Tabs.Count >= MaxTabs)
                    return null;
                return CreateTabUnlocked(url ?? DefaultUrl, setActive: true);
            }
        }

        private static TabSession CreateTabUnlocked(string url, bool setActive)
        {
            var id = Guid.NewGuid().ToString("N");
            // Create browser manually so JS media bridge can register before CEF spins up.
            var browser = new ChromiumWebBrowser(
                address: "",
                browserSettings: null,
                requestContext: null,
                automaticallyCreateBrowser: false);
            browser.DeviceScaleFactor = DeviceScaleFactor;
            browser.Size = BrowserSize;
            browser.LifeSpanHandler = new SameTabLifeSpanHandler();
            browser.RequestHandler = new PermissiveRequestHandler();
            browser.AudioHandler = new StreamingAudioHandler(id);
            browser.DownloadHandler = new StreamingDownloadHandler(id);
            MediaBridge.AttachToBrowser(browser, id);
            MobileChromeIdentity.Apply(browser);
            if (CreateRenderHandler != null)
                browser.RenderHandler = CreateRenderHandler(browser, id);
            browser.CreateBrowser();
            browser.Load(url ?? DefaultUrl);

            var session = new TabSession
            {
                Id = id,
                Browser = browser,
                Title = "New Tab",
                Url = url
            };

            browser.LoadingStateChanged += (s, e) => OnLoadingStateChanged(session, e);
            browser.TitleChanged += (s, e) => OnTitleChanged(session, e);
            browser.AddressChanged += (s, e) => OnAddressChanged(session, e);
            browser.LoadError += (s, e) =>
            {
                if (e.ErrorCode == CefErrorCode.Aborted)
                    return;
                Console.WriteLine("LoadError: " + e.ErrorCode + " " + e.FailedUrl + " — " + e.ErrorText);
            };
            browser.FrameLoadEnd += (s, e) =>
            {
                if (e.Frame == null || !e.Frame.IsValid)
                    return;
                if (e.Frame.IsMain)
                    Console.WriteLine("Loaded: " + e.Url);
                // Inject into iframes too — many camera testers host getUserMedia off-main-frame.
                MediaBridge.InjectShim(e.Frame);
            };

            browser.FrameLoadStart += (s, e) =>
            {
                if (e.Frame != null && e.Frame.IsValid)
                    MediaBridge.InjectShim(e.Frame);
            };

            Tabs[id] = session;
            TabOrder.Add(id);
            if (setActive)
                ActiveTabId = id;

            BroadcastTabList();
            return session;
        }

        public static bool SwitchTab(string tabId)
        {
            lock (Sync)
            {
                if (!Tabs.ContainsKey(tabId))
                    return false;

                ActiveTabId = tabId;
                var session = Tabs[tabId];
                session.Browser.DeviceScaleFactor = DeviceScaleFactor;
                session.Browser.Size = BrowserSize;
                BroadcastTabList();
                BroadcastNavigatedUrl(session.Url);
                return true;
            }
        }

        public static bool CloseTab(string tabId)
        {
            TabSession removed = null;
            lock (Sync)
            {
                if (!Tabs.TryGetValue(tabId, out removed))
                    return false;

                var index = TabOrder.IndexOf(tabId);
                Tabs.Remove(tabId);
                TabOrder.Remove(tabId);

                if (Tabs.Count == 0)
                {
                    CreateTabUnlocked(DefaultUrl, setActive: true);
                }
                else if (ActiveTabId == tabId)
                {
                    var nextIndex = Math.Min(Math.Max(index, 0), TabOrder.Count - 1);
                    ActiveTabId = TabOrder[nextIndex];
                    Tabs[ActiveTabId].Browser.DeviceScaleFactor = DeviceScaleFactor;
                    Tabs[ActiveTabId].Browser.Size = BrowserSize;
                    BroadcastNavigatedUrl(Tabs[ActiveTabId].Url);
                }

                BroadcastTabList();
            }

            DisposeBrowser(removed);
            return true;
        }

        private static int pendingCssW;
        private static int pendingCssH;
        private static float pendingScale;
        private static Timer viewportDebounceTimer;

        public static void SetViewport(int cssWidth, int cssHeight, float deviceScaleFactor)
        {
            if (cssWidth < 1 || cssHeight < 1)
                return;

            pendingCssW = cssWidth;
            pendingCssH = cssHeight;
            pendingScale = deviceScaleFactor;

            // Keyboard show/hide fires many SizeChange packets; applying all freezes CEF.
            if (viewportDebounceTimer == null)
            {
                viewportDebounceTimer = new Timer(_ => ApplyPendingViewport(), null, 120, Timeout.Infinite);
            }
            else
            {
                viewportDebounceTimer.Change(120, Timeout.Infinite);
            }
        }

        private static void ApplyPendingViewport()
        {
            ChromiumWebBrowser active;
            int cssWidth;
            int cssHeight;
            float deviceScaleFactor;

            lock (Sync)
            {
                cssWidth = pendingCssW;
                cssHeight = pendingCssH;
                deviceScaleFactor = pendingScale;
                if (cssWidth < 1 || cssHeight < 1)
                    return;

                CssWidth = cssWidth;
                CssHeight = cssHeight;
                // Cap DPR: phone @4x screenshots every frame easily OOM/crash CEF OffScreen.
                var capped = deviceScaleFactor <= 0 ? 1f : deviceScaleFactor;
                if (capped > 2f)
                    capped = 2f;
                DeviceScaleFactor = capped;
                BrowserSize = new Size(cssWidth, cssHeight);

                active = ActiveBrowser;
                if (active != null)
                {
                    active.DeviceScaleFactor = DeviceScaleFactor;
                    active.Size = BrowserSize;
                }
            }

            Console.WriteLine("Viewport CSS {0}x{1} @ {2}x", cssWidth, cssHeight, DeviceScaleFactor);
        }

        /// <summary>
        /// After client reconnect, poke the active browser so painting resumes if it stalled.
        /// </summary>
        public static void EnsureActiveBrowserHealthy()
        {
            var browser = ActiveBrowser;
            if (browser == null)
            {
                EnsureInitialTab();
                return;
            }

            try
            {
                if (!browser.IsBrowserInitialized)
                {
                    Console.WriteLine("Active browser not initialized — recreating tab");
                    var id = ActiveTabId;
                    if (id != null)
                        CloseTab(id);
                    EnsureInitialTab();
                    return;
                }

                // Force a resize/paint cycle after reconnect.
                browser.Size = BrowserSize;
                browser.DeviceScaleFactor = DeviceScaleFactor;
                browser.GetBrowserHost()?.Invalidate(PaintElementType.View);
                browser.GetBrowserHost()?.WasResized();
            }
            catch (Exception ex)
            {
                Console.WriteLine("EnsureActiveBrowserHealthy: " + ex.Message);
            }
        }

        public static void BroadcastTabList()
        {
            if (Server == null)
                return;

            TabListPayload payload;
            lock (Sync)
            {
                payload = new TabListPayload
                {
                    activeId = ActiveTabId,
                    tabs = TabOrder.Select(id =>
                    {
                        var t = Tabs[id];
                        return new TabInfo
                        {
                            id = t.Id,
                            title = string.IsNullOrWhiteSpace(t.Title) ? "New Tab" : t.Title,
                            url = t.Url ?? ""
                        };
                    }).ToList()
                };
            }

            Server.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new TextPacket
            {
                PType = TextPacketType.TabList,
                text = JsonConvert.SerializeObject(payload)
            }));
        }

        public static void BroadcastNavigatedUrl(string url)
        {
            if (Server == null || url == null)
                return;

            Server.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new TextPacket
            {
                PType = TextPacketType.NavigatedUrl,
                text = url
            }));
        }

        private static void OnLoadingStateChanged(TabSession session, LoadingStateChangedEventArgs e)
        {
            session.Url = session.Browser.Address ?? session.Url;
            bool isActive;
            lock (Sync)
            {
                isActive = session.Id == ActiveTabId;
            }

            BroadcastTabList();
            if (isActive)
                BroadcastNavigatedUrl(session.Url);

            if (!e.IsLoading)
                Console.WriteLine("Navigation finished: " + session.Url);
        }

        private static void OnAddressChanged(TabSession session, AddressChangedEventArgs e)
        {
            session.Url = e.Address ?? session.Url;
            bool isActive;
            lock (Sync)
            {
                isActive = session.Id == ActiveTabId;
            }

            BroadcastTabList();
            if (isActive)
                BroadcastNavigatedUrl(session.Url);
        }

        private static void OnTitleChanged(TabSession session, TitleChangedEventArgs e)
        {
            session.Title = e.Title;
            BroadcastTabList();
        }

        private static void DisposeBrowser(TabSession session)
        {
            if (session?.Browser == null)
                return;

            try
            {
                MediaBridge.Release(session.Id);
                session.Browser.Dispose();
            }
            catch
            {
            }
        }
    }
}
