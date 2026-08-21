using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;

namespace BrowserServer
{
    public class TabSession
    {
        public string Id { get; set; }
        public ChromiumWebBrowser Browser { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        /// <summary>Tile entry URL when this tab belongs to a pinned PWA.</summary>
        public string PwaEntryUrl { get; set; }
    }

    public class TabManager
    {
        public const int MaxTabs = 8;
        public const string DefaultUrl = "about:blank";

        private readonly ClientSession owner;
        private readonly object sync = new object();
        private readonly Dictionary<string, TabSession> tabs = new Dictionary<string, TabSession>();
        private readonly List<string> tabOrder = new List<string>();

        public string ActiveTabId { get; private set; }
        /// <summary>CSS viewport = BrowserClient ScaleRect (page displayer), excluding navbars.</summary>
        public int CssWidth { get; private set; } = 360;
        public int CssHeight { get; private set; } = 640;
        public float DeviceScaleFactor { get; private set; } = 2f;
        /// <summary>Equals Css size — CEF GetViewRect uses Size as the CSS viewport.</summary>
        public Size BrowserSize { get; private set; } = new Size(360, 640);

        public TabManager(ClientSession owner)
        {
            if (owner == null)
                throw new ArgumentNullException("owner");
            this.owner = owner;
        }

        public TabSession Active
        {
            get
            {
                lock (sync)
                {
                    if (ActiveTabId != null && tabs.TryGetValue(ActiveTabId, out var session))
                        return session;
                    return null;
                }
            }
        }

        public ChromiumWebBrowser ActiveBrowser
        {
            get
            {
                var active = Active;
                return active?.Browser;
            }
        }

        public TabSession EnsureInitialTab()
        {
            lock (sync)
            {
                if (tabs.Count == 0)
                    return CreateTabUnlocked(DefaultUrl, setActive: true);
                return tabs[ActiveTabId];
            }
        }

        public TabSession CreateTab(string url = null)
        {
            lock (sync)
            {
                if (tabs.Count >= MaxTabs)
                    return null;
                return CreateTabUnlocked(url ?? DefaultUrl, setActive: true);
            }
        }

        public IEnumerable<TabSession> AllSessions()
        {
            lock (sync)
            {
                return tabOrder.Select(id => tabs[id]).ToList();
            }
        }

        public IEnumerable<string> AllTabIds()
        {
            lock (sync)
            {
                return tabOrder.ToList();
            }
        }

        public void DisposeAll()
        {
            List<TabSession> toDispose;
            lock (sync)
            {
                toDispose = tabOrder.Select(id => tabs[id]).ToList();
                tabs.Clear();
                tabOrder.Clear();
                ActiveTabId = null;
            }

            foreach (var session in toDispose)
                DisposeBrowser(session);
        }

        private TabSession CreateTabUnlocked(string url, bool setActive)
        {
            var id = Guid.NewGuid().ToString("N");
            var loadUrl = string.IsNullOrWhiteSpace(url) ? DefaultUrl : url.Trim();
            // Create browser manually so JS media bridge can register before CEF spins up.
            var requestContext = owner.Device.EnsureBrowserContext().Context;
            var browser = new ChromiumWebBrowser(
                address: loadUrl,
                browserSettings: null,
                requestContext: requestContext,
                automaticallyCreateBrowser: false);
            browser.DeviceScaleFactor = DeviceScaleFactor;
            browser.Size = BrowserSize;
            browser.LifeSpanHandler = new SameTabLifeSpanHandler();
            browser.RequestHandler = new PermissiveRequestHandler(owner);
            browser.AudioHandler = new StreamingAudioHandler(id);
            browser.DownloadHandler = new StreamingDownloadHandler(id);
            MediaBridge.AttachToBrowser(browser, id);
            NotificationBridge.AttachToBrowser(browser, id);
            MobileChromeIdentity.Apply(browser);
            if (ClientSessionHub.CreateRenderHandler != null)
                browser.RenderHandler = ClientSessionHub.CreateRenderHandler(browser, id);
            browser.CreateBrowser();

            var session = new TabSession
            {
                Id = id,
                Browser = browser,
                Title = "New Tab",
                Url = loadUrl
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
                {
                    Console.WriteLine("Loaded: " + e.Url);
                    MediaBridge.InjectShim(e.Frame);
                    NotificationBridge.InjectShim(session.Id, e.Frame);
                    PwaBridge.InjectShim(owner, e.Frame, session);
                    ClientEnvironmentBridge.InjectShim(owner, e.Frame);
                    VideoPlaybackBridge.Poll(session.Id, browser);
                }
                else
                {
                    // Camera/mic testers often host getUserMedia in iframes.
                    MediaBridge.InjectShim(e.Frame);
                }
            };

            browser.FrameLoadStart += (s, e) =>
            {
                if (e.Frame == null || !e.Frame.IsValid)
                    return;

                if (e.Frame.IsMain)
                {
                    MediaBridge.InjectShim(e.Frame);
                    NotificationBridge.InjectShim(session.Id, e.Frame);
                    PwaBridge.InjectShim(owner, e.Frame, session);
                    ClientEnvironmentBridge.InjectShim(owner, e.Frame);
                }
                else
                {
                    MediaBridge.InjectShim(e.Frame);
                }
            };

            tabs[id] = session;
            tabOrder.Add(id);
            ClientSessionHub.RegisterTab(id, owner);

            if (setActive)
            {
                ActiveTabId = id;
                NotifyActiveTabChanged();
                RequestRepaint(session);
            }

            ScheduleNavigate(session, loadUrl);
            SendTabList();
            return session;
        }

        /// <summary>Load a URL once CEF is ready (Load before init is silently ignored).</summary>
        public void ScheduleNavigate(TabSession session, string url)
        {
            if (session?.Browser == null || string.IsNullOrWhiteSpace(url))
                return;

            var target = url.Trim();
            var browser = session.Browser;

            Action navigate = () =>
            {
                try
                {
                    session.Url = target;
                    if (browser.IsBrowserInitialized)
                        browser.LoadUrl(target);
                    else
                        browser.Load(target);
                    RequestRepaint(session);
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ScheduleNavigate error: " + ex.Message);
                }
            };

            if (browser.IsBrowserInitialized)
            {
                navigate();
                return;
            }

            var attempts = 0;
            Timer timer = null;
            timer = new Timer(_ =>
            {
                if (browser.IsBrowserInitialized || ++attempts >= 60)
                {
                    timer.Dispose();
                    navigate();
                }
            }, null, 50, 50);
        }

        void NotifyActiveTabChanged()
        {
            try
            {
                owner.ResetCaptureState();
            }
            catch
            {
            }
        }

        void RequestRepaint(TabSession session)
        {
            try
            {
                var browser = session?.Browser;
                if (browser == null)
                    return;
                browser.GetBrowserHost()?.Invalidate(PaintElementType.View);
                browser.GetBrowserHost()?.WasResized();
            }
            catch
            {
            }
        }

        public bool SwitchTab(string tabId)
        {
            lock (sync)
            {
                if (!tabs.ContainsKey(tabId))
                    return false;

                ActiveTabId = tabId;
                var session = tabs[tabId];
                session.Browser.DeviceScaleFactor = DeviceScaleFactor;
                session.Browser.Size = BrowserSize;
                NotifyActiveTabChanged();
                RequestRepaint(session);
                SendTabList();
                SendNavigatedUrl(session.Url);
                return true;
            }
        }

        public bool CloseTab(string tabId)
        {
            TabSession removed = null;
            lock (sync)
            {
                if (!tabs.TryGetValue(tabId, out removed))
                    return false;

                var index = tabOrder.IndexOf(tabId);
                tabs.Remove(tabId);
                tabOrder.Remove(tabId);

                if (tabs.Count == 0)
                {
                    CreateTabUnlocked(DefaultUrl, setActive: true);
                }
                else if (ActiveTabId == tabId)
                {
                    var nextIndex = Math.Min(Math.Max(index, 0), tabOrder.Count - 1);
                    ActiveTabId = tabOrder[nextIndex];
                    tabs[ActiveTabId].Browser.DeviceScaleFactor = DeviceScaleFactor;
                    tabs[ActiveTabId].Browser.Size = BrowserSize;
                    SendNavigatedUrl(tabs[ActiveTabId].Url);
                }

                SendTabList();
            }

            DisposeBrowser(removed);
            return true;
        }

        private int pendingCssW;
        private int pendingCssH;
        private float pendingScale;
        private Timer viewportDebounceTimer;

        public void NavigateActive(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            var browser = ActiveBrowser;
            if (browser == null)
                return;

            try
            {
                var target = url.Trim();
                // Load() works before IsBrowserInitialized; LoadUrl does not.
                if (browser.IsBrowserInitialized)
                    browser.LoadUrl(target);
                else
                    browser.Load(target);
            }
            catch (Exception ex)
            {
                Console.WriteLine("NavigateActive error: " + ex.Message);
            }
        }

        public void SetViewport(int cssWidth, int cssHeight, float deviceScaleFactor)
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

        private void ApplyPendingViewport()
        {
            ChromiumWebBrowser active;
            int cssWidth;
            int cssHeight;
            float deviceScaleFactor;

            lock (sync)
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
                if (capped > 3f)
                    capped = 3f;
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
        public void EnsureActiveBrowserHealthy()
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

        public void SendTabList()
        {
            TabListPayload payload;
            lock (sync)
            {
                payload = new TabListPayload
                {
                    activeId = ActiveTabId,
                    tabs = tabOrder.Select(id =>
                    {
                        var t = tabs[id];
                        return new TabInfo
                        {
                            id = t.Id,
                            title = string.IsNullOrWhiteSpace(t.Title) ? "New Tab" : t.Title,
                            url = t.Url ?? ""
                        };
                    }).ToList()
                };
            }

            owner.SendText(TextPacketType.TabList, JsonConvert.SerializeObject(payload));
            owner.Device?.ScheduleSaveTabSnapshot(this);
        }

        public DeviceTabSnapshot BuildSnapshot()
        {
            lock (sync)
            {
                var activeIndex = 0;
                if (!string.IsNullOrEmpty(ActiveTabId))
                {
                    var idx = tabOrder.IndexOf(ActiveTabId);
                    if (idx >= 0)
                        activeIndex = idx;
                }

                return new DeviceTabSnapshot
                {
                    activeIndex = activeIndex,
                    tabs = tabOrder.Select(id =>
                    {
                        var t = tabs[id];
                        return new DeviceTabEntry
                        {
                            url = t.Url ?? "",
                            title = string.IsNullOrWhiteSpace(t.Title) ? "New Tab" : t.Title,
                            pwaEntryUrl = t.PwaEntryUrl
                        };
                    }).ToList()
                };
            }
        }

        public void RestoreFromSnapshot(DeviceTabSnapshot snapshot)
        {
            lock (sync)
            {
                if (snapshot?.tabs == null || snapshot.tabs.Count == 0)
                {
                    EnsureInitialTab();
                    return;
                }

                var count = Math.Min(snapshot.tabs.Count, MaxTabs);
                for (var i = 0; i < count; i++)
                {
                    var entry = snapshot.tabs[i];
                    var tab = CreateTabUnlocked(entry?.url, setActive: false);
                    if (tab == null)
                        break;
                    if (!string.IsNullOrWhiteSpace(entry?.title))
                        tab.Title = entry.title.Trim();
                    if (!string.IsNullOrWhiteSpace(entry?.pwaEntryUrl))
                        tab.PwaEntryUrl = entry.pwaEntryUrl.Trim();
                }

                if (tabOrder.Count == 0)
                {
                    EnsureInitialTab();
                    return;
                }

                var activeIndex = snapshot.activeIndex;
                if (activeIndex < 0 || activeIndex >= tabOrder.Count)
                    activeIndex = 0;
                ActiveTabId = tabOrder[activeIndex];
                NotifyActiveTabChanged();
                RequestRepaint(Active);
            }
        }

        public void SendNavigatedUrl(string url)
        {
            if (url == null)
                return;

            owner.SendText(TextPacketType.NavigatedUrl, url);
        }

        private void OnLoadingStateChanged(TabSession session, LoadingStateChangedEventArgs e)
        {
            session.Url = session.Browser.Address ?? session.Url;
            bool isActive;
            lock (sync)
            {
                isActive = session.Id == ActiveTabId;
            }

            SendTabList();
            if (isActive)
                SendNavigatedUrl(session.Url);

            if (!e.IsLoading)
            {
                Console.WriteLine("Navigation finished: " + session.Url);
                try
                {
                    var main = session.Browser?.GetMainFrame();
                    if (main != null && main.IsValid)
                    {
                        MediaBridge.InjectShim(main);
                        NotificationBridge.InjectShim(session.Id, main);
                        PwaBridge.InjectShim(owner, main, session);
                        ClientEnvironmentBridge.InjectShim(owner, main);
                    }
                }
                catch
                {
                }
            }
        }

        private void OnAddressChanged(TabSession session, AddressChangedEventArgs e)
        {
            session.Url = e.Address ?? session.Url;
            bool isActive;
            lock (sync)
            {
                isActive = session.Id == ActiveTabId;
            }

            SendTabList();
            if (isActive)
            {
                SendNavigatedUrl(session.Url);
                MediaBridge.OnNavigated(session.Id, session.Url);
            }
        }

        private void OnTitleChanged(TabSession session, TitleChangedEventArgs e)
        {
            session.Title = e.Title;
            SendTabList();
        }

        private void DisposeBrowser(TabSession session)
        {
            if (session?.Browser == null)
                return;

            try
            {
                ClientSessionHub.UnregisterTab(session.Id);
                MediaBridge.Release(session.Id);
                session.Browser.Dispose();
            }
            catch
            {
            }
        }
    }
}
