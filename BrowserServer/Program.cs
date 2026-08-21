using CefSharp;
using CefSharp.OffScreen;
using System;
using System.Drawing;
using System.IO;
using WebSocketSharp;
using WebSocketSharp.Server;
using CefSharp.Structs;
using Newtonsoft.Json;
using System.Threading;
using System.Text;
using CefSharp.Enums;

namespace BrowserServer
{
    class Program
    {
        public class test : WebSocketBehavior, IBrowserClientCommands
        {
            //tested on 950XL
            public static int ScalingFactor = 2;
            protected override void OnOpen()
            {
                // Client (re)connected — clear any stuck frame-capture lock from a previous session.
                ResetCaptureState();
                TabManager.EnsureInitialTab();
                TabManager.EnsureActiveBrowserHealthy();
                TabManager.BroadcastTabList();
                var active = TabManager.Active;
                if (active != null)
                    TabManager.BroadcastNavigatedUrl(active.Url);
            }

            protected override void OnMessage(MessageEventArgs e)
            {
                if (e.IsBinary)
                {
                    ClientCommandDispatcher.DispatchBinary(e.RawData, this);
                    return;
                }

                ClientCommandDispatcher.DispatchText(
                    e.Data,
                    TabManager.ActiveBrowser != null,
                    ScalingFactor,
                    this);
            }

            public void CreateTab()
            {
                TabManager.CreateTab();
            }

            public void CloseTab(string tabId)
            {
                TabManager.CloseTab(tabId);
            }

            public void SwitchTab(string tabId)
            {
                TabManager.SwitchTab(tabId);
            }

            public void MediaPermissionResponse(MediaPermissionPayload payload)
            {
                MediaBridge.HandlePermissionResponse(payload);
            }

            public void NotificationPermissionResponse(NotificationPermissionPayload payload)
            {
                NotificationBridge.HandlePermissionResponse(payload);
            }

            public void PwaInstalled(PwaInstallPayload payload)
            {
                PwaBridge.SetInstalledUrls(payload != null ? payload.urls : null, payload != null && payload.reload);
            }

            public void TextInputSend(string text)
            {
                var browser = TabManager.ActiveBrowser;
                if (browser == null)
                    return;

                Console.WriteLine(text);
                var textscript = @"(function (){document.activeElement.value='" + text + "'})();";

                browser.EvaluateScriptAsync(textscript).ContinueWith(t =>
                {
                    browser.GetBrowserHost().SendKeyEvent(new KeyEvent
                    {
                        WindowsKeyCode = 0x0D,
                        FocusOnEditableField = true,
                        IsSystemKey = false,
                        Type = KeyEventType.RawKeyDown
                    });
                });
            }

            public void Ack()
            {
                Console.WriteLine("ACK");
            }

            public void DownloadAck(DownloadAckPayload ack)
            {
                StreamingDownloadHandler.HandleClientAck(ack.id, ack.seq);
            }

            public void SendKey(SendKeyCommand key)
            {
                var browser = TabManager.ActiveBrowser;
                if (browser == null || key == null)
                    return;

                var host = browser.GetBrowserHost();
                host.SetFocus(true);

                switch (key.Kind)
                {
                    case SendKeyKind.Insert:
                        if (!string.IsNullOrEmpty(key.Text))
                        {
                            var script = JavascriptFunctions.InsertText(JsonConvert.SerializeObject(key.Text));
                            browser.EvaluateScriptAsync(script);
                        }
                        break;

                    case SendKeyKind.Backspace:
                        browser.EvaluateScriptAsync(JavascriptFunctions.Backspace);
                        break;

                    case SendKeyKind.Enter:
                        host.SendKeyEvent(new KeyEvent
                        {
                            WindowsKeyCode = 0x0D,
                            NativeKeyCode = 0x0D,
                            FocusOnEditableField = true,
                            IsSystemKey = false,
                            Type = KeyEventType.RawKeyDown
                        });
                        host.SendKeyEvent(new KeyEvent
                        {
                            WindowsKeyCode = 0x0D,
                            NativeKeyCode = 0x0D,
                            FocusOnEditableField = true,
                            IsSystemKey = false,
                            Type = KeyEventType.Char
                        });
                        host.SendKeyEvent(new KeyEvent
                        {
                            WindowsKeyCode = 0x0D,
                            NativeKeyCode = 0x0D,
                            FocusOnEditableField = true,
                            IsSystemKey = false,
                            Type = KeyEventType.KeyUp
                        });
                        break;

                    case SendKeyKind.Coded:
                    case SendKeyKind.LegacyChar:
                        {
                            KeyEventType eventType = KeyEventType.Char;
                            if (key.Kind == SendKeyKind.Coded)
                            {
                                switch (key.EventType)
                                {
                                    case "down":
                                        eventType = KeyEventType.RawKeyDown;
                                        break;
                                    case "up":
                                        eventType = KeyEventType.KeyUp;
                                        break;
                                }
                            }

                            host.SendKeyEvent(new KeyEvent
                            {
                                WindowsKeyCode = key.Code,
                                NativeKeyCode = key.Code,
                                FocusOnEditableField = true,
                                IsSystemKey = false,
                                Type = eventType
                            });
                        }
                        break;
                }
            }

            public void Navigate(string input)
            {
                var browser = TabManager.ActiveBrowser;
                if (browser == null)
                    return;

                if (NetworkManager.TryGetNavigableUrl(input, out var navUrl))
                {
                    Console.WriteLine("Navigate URL: " + navUrl);
                    browser.LoadUrl(navUrl);
                }
                else
                {
                    Console.WriteLine("Search: " + input);
                    browser.LoadUrl("https://www.google.com/search?q=" + Uri.EscapeDataString(input));
                }
            }

            public void NavigateBack(bool stopBeforeBlank)
            {
                var browser = TabManager.ActiveBrowser;
                if (browser == null)
                    return;
                var ignored = HandleNavigateBackAsync(browser, stopBeforeBlank);
            }

            public void NavigateForward()
            {
                var browser = TabManager.ActiveBrowser;
                if (browser != null && browser.CanGoForward)
                    browser.Forward();
            }

            public void SizeChange(int width, int height, float scale)
            {
                TabManager.SetViewport(width, height, scale);
            }

            public void ClientEnvironment(ClientEnvironmentPayload payload)
            {
                ClientEnvironmentBridge.Apply(payload);
            }

            public void ContextMenuQuery(PointerPacket pointer)
            {
                var ignored = ContextMenuBridge.HandleQueryAsync(pointer);
            }

            public void ContextMenuAction(ContextMenuActionPayload action)
            {
                ContextMenuBridge.HandleAction(action);
            }

            public void Touch(TouchKind kind, PointerPacket pointer)
            {
                var browser = TabManager.ActiveBrowser;
                if (browser == null)
                    return;

                CefSharp.Enums.TouchEventType type;
                switch (kind)
                {
                    case TouchKind.Up:
                        type = CefSharp.Enums.TouchEventType.Released;
                        break;
                    case TouchKind.Moved:
                        type = CefSharp.Enums.TouchEventType.Moved;
                        break;
                    default:
                        type = CefSharp.Enums.TouchEventType.Pressed;
                        break;
                }

                var ev = new TouchEvent()
                {
                    Id = (int)pointer.id,
                    X = (float)pointer.px * browser.Size.Width,
                    Y = (float)pointer.py * browser.Size.Height,
                    PointerType = CefSharp.Enums.PointerType.Touch,
                    Pressure = 0,
                    Type = type,
                };
                browser.GetBrowser().GetHost().SendTouchEvent(ev);
            }

            public void ClientBinary(byte[] data)
            {
                MediaBridge.HandleClientBinary(data);
            }
        }

        static WebSocketServer server;

        static void Main(string[] margs)
        {
            server = new WebSocketServer("ws://0.0.0.0:8081");
            //ngrok compatible ngrok.exe tcp 8081 -> 
            server.AllowForwardedRequest = true;
            server.AddWebSocketService<test>("/");
            server.Start();

            TabManager.Server = server;
            TabManager.CreateRenderHandler = (browser, tabId) => new TestRHI(browser, tabId);
            TabManager.ActiveTabChanged = ResetCaptureState;

            const string testUrl = "about:blank";
            var cefRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CefSharp133");
            var settings = new CefSettings()
            {
                RootCachePath = cefRoot,
                CachePath = Path.Combine(cefRoot, "Cache"),
                BrowserSubprocessPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CefSharp.BrowserSubprocess.exe"),
                // Real Android Google Chrome UA (Chrome still includes AppleWebKit tokens by design).
                UserAgent = MobileChromeIdentity.UserAgent,
                AcceptLanguageList = "en-US,en",
            };

            settings.CefCommandLineArgs["touch-events"] = "enabled";
            // Disable GPU in offscreen — reduces crashes on heavy sites (maps, modern SPAs).
            settings.CefCommandLineArgs["disable-gpu"] = "1";
            settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";
            // Allow media autoplay so remote audio/video can start without a desktop gesture.
            settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
            // OffScreen CefSettings adds "mute-audio" by default; without this, AudioHandler never fires.
            settings.EnableAudio();
            if (settings.CefCommandLineArgs.ContainsKey("mute-audio"))
                settings.CefCommandLineArgs.Remove("mute-audio");
            CefSharpSettings.ConcurrentTaskExecution = true;

            // Phone camera/mic bridge — fetchable from https pages (treated as secure).
            settings.RegisterScheme(new CefCustomScheme
            {
                SchemeName = "cbmedia",
                SchemeHandlerFactory = new MediaSchemeHandlerFactory(),
                DomainName = "local",
                IsStandard = true,
                IsSecure = true,
                IsCorsEnabled = true,
                IsFetchEnabled = true,
                IsCSPBypassing = true
            });

            settings.LogSeverity = LogSeverity.Warning;
            settings.LogFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CefSharp\\debug.log");
            settings.MultiThreadedMessageLoop = true;
            Cef.Initialize(settings, performDependencyCheck: true, browserProcessHandler: null);
            StreamingDownloadHandler.PurgeTempFolder();

            TabManager.CreateTab(testUrl);

            Console.Clear();
            Console.WriteLine("Browser server is now running, you can connect to it via ws://" + NetworkManager.GetLocalIPAddress() + ":8081");
            Console.WriteLine("Audio capture: ENABLED (expect 'Audio start' when a page plays sound)");
            Console.WriteLine("Video playback: H.264/AAC ENABLED (CefSharp.H264.x64 133) — page video streams like audio");
            Console.WriteLine("Phone camera/mic: ENABLED (sites calling getUserMedia prompt on the client)");
            Console.WriteLine("QR decode: ENABLED (while camera is on, HTTP(S) codes open automatically)");
            Console.WriteLine("PWA: Add to Home origins are reported as installed (standalone)");
            Console.WriteLine("Notifications: ENABLED (page Notification API → phone toast)");
            Console.WriteLine("Page stream: adaptive (~30fps motion, sharp stills, skip unchanged)");
            Console.WriteLine("Or click the Discovery button in the UWP app to autimatically find the server on your local network");
            Console.WriteLine();
            Console.WriteLine();
            Console.WriteLine("Alternatively you can set up ngrok to acess the server over internet, to do this follow the steps below");
            Console.WriteLine("1. Set up a ngrok account at https://ngrok.com/");
            Console.WriteLine("2. download ngrok (it's just one self-contained .exe file)");
            Console.WriteLine("3. open a command prompt (cmd.exe) in the location where you have the ngrok.exe file");
            Console.WriteLine("4. open https://dashboard.ngrok.com/get-started/setup and run the command with your ngrok auth token under section 2. Connect your account");
            Console.WriteLine("5. you should get back \"Authtoken saved to configuration file\"");
            Console.WriteLine("6. run the following command: ngrok tcp 8081");
            Console.WriteLine("7. you'll need the url starting with tcp://");
            Console.WriteLine("8. enter the url in the UWP application as the server adress and connect.");
            Console.WriteLine("9. congratulations! you just connected over the internet");

            NetworkManager.StartUdpDiscoveryServer();
            // Poll ~30fps; unchanged pages are skipped so motion can use the extra budget.
            var timer = new Timer(Callback, null, 0, 33);
            var audioTimer = new Timer(_ => StreamingAudioHandler.FlushOutbound(), null, 0, 10);
            var downloadTimer = new Timer(_ => StreamingDownloadHandler.FlushOutbound(), null, 0, 25);

            Console.ReadKey();
            downloadTimer.Dispose();
            audioTimer.Dispose();
            StreamingDownloadHandler.PurgeTempFolder();
            Cef.Shutdown();
            timer.Dispose();
        }

        static readonly RenderFrameSession frameSession = new RenderFrameSession();
        static readonly JpegRenderEncoder jpegEncoder = new JpegRenderEncoder();

        public static void ResetCaptureState()
        {
            frameSession.Reset();
        }

        static void Callback(object state)
        {
            try
            {
                var browser = TabManager.ActiveBrowser;
                if (browser == null || !browser.IsBrowserInitialized)
                    return;

                var now = Environment.TickCount;
                if (frameSession.GetSharedSocketSkip(
                        StreamingDownloadHandler.IsStreamingToClients,
                        StreamingAudioHandler.PendingCount) != RenderFrameSkipReason.None)
                    return;

                VideoPlaybackBridge.Poll(browser);

                if (frameSession.GetMediaThrottleSkip(
                        MediaBridge.IsCaptureActive,
                        VideoPlaybackBridge.IsStreaming,
                        now) != RenderFrameSkipReason.None)
                    return;

                if (!frameSession.TryBeginCapture(now))
                    return;

                try
                {
                    // Use paint-buffer snapshot (no DevTools). CaptureScreenshotAsync hangs on heavy SPAs
                    // and then leaves the stream black until the server is restarted.
                    using (var bitmap = browser.ScreenshotOrNull())
                    {
                        if (bitmap == null || server == null)
                            return;

                        int hash = BitmapContentHash.Compute(bitmap);
                        long quality;
                        if (!frameSession.TrySelectQuality(hash, MediaBridge.IsCaptureActive, now, out quality))
                            return;

                        var jpeg = jpegEncoder.Encode(bitmap, quality);
                        server.WebSocketServices.Broadcast(jpeg);
                        frameSession.MarkSent(now);
                    }
                }
                finally
                {
                    frameSession.EndCapture();
                }
            }
            catch (Exception ex)
            {
                frameSession.EndCapture();
                Console.WriteLine("Frame capture error: " + ex.Message);
            }
        }

        static int frameNum = 0;
        private static void CefPaint(object sender, OnPaintEventArgs e)
        {
            frameNum++;
            var browserImage = new Bitmap(e.Width, e.Height, 4 * e.Width, System.Drawing.Imaging.PixelFormat.Format32bppRgb, e.BufferHandle);
            server.WebSocketServices.Broadcast(jpegEncoder.Encode(browserImage, 75L));
        }

        private static async System.Threading.Tasks.Task HandleNavigateBackAsync(ChromiumWebBrowser browser, bool stopBeforeBlank)
        {
            if (browser == null)
                return;

            try
            {
                if (!browser.CanGoBack)
                {
                    if (stopBeforeBlank)
                        BroadcastAtHistoryRoot();
                    return;
                }

                if (stopBeforeBlank)
                {
                    var host = browser.GetBrowser()?.GetHost();
                    if (host == null)
                    {
                        BroadcastAtHistoryRoot();
                        return;
                    }

                    var entries = await host.GetNavigationEntriesAsync(false);
                    if (entries == null || entries.Count == 0)
                    {
                        BroadcastAtHistoryRoot();
                        return;
                    }

                    var currentIndex = -1;
                    for (int i = 0; i < entries.Count; i++)
                    {
                        if (entries[i] != null && entries[i].IsCurrent)
                        {
                            currentIndex = i;
                            break;
                        }
                    }

                    if (currentIndex <= 0)
                    {
                        BroadcastAtHistoryRoot();
                        return;
                    }

                    var previous = entries[currentIndex - 1];
                    if (IsBlankNavigationUrl(previous?.Url) || IsBlankNavigationUrl(previous?.DisplayUrl))
                    {
                        BroadcastAtHistoryRoot();
                        return;
                    }
                }

                if (browser.CanGoBack)
                    browser.Back();
            }
            catch (Exception ex)
            {
                Console.WriteLine("NavigateBack error: " + ex.Message);
                if (stopBeforeBlank)
                    BroadcastAtHistoryRoot();
            }
        }

        private static bool IsBlankNavigationUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return true;

            url = url.Trim();
            return url.Equals("about:blank", StringComparison.OrdinalIgnoreCase)
                || url.Equals("about:blank#blocked", StringComparison.OrdinalIgnoreCase);
        }

        private static void BroadcastAtHistoryRoot()
        {
            try
            {
                server?.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new TextPacket
                {
                    PType = TextPacketType.AtHistoryRoot,
                    text = ""
                }));
            }
            catch
            {
            }
        }

        //TODO: accelerated Draw
        // Forward the renderbuffer from here instead of screenshot?
        public class TestRHI : DefaultRenderHandler
        {
            private ChromiumWebBrowser browser;
            private readonly string tabId;

            public TestRHI(ChromiumWebBrowser browser, string tabId) : base(browser)
            {
                this.browser = browser;
                this.tabId = tabId;
            }

            public override void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
            {
                base.OnPaint(type, dirtyRect, buffer, width, height);
                if (TabManager.ActiveTabId == tabId)
                    VideoPlaybackBridge.HandlePaint(type, buffer, width, height);
            }

            public override void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode)
            {
                base.OnVirtualKeyboardRequested(browser, inputMode);

                if (TabManager.ActiveTabId != tabId)
                    return;

                Console.WriteLine("Virtual Keyboard Requested for " + inputMode);
                if (inputMode == TextInputMode.None)
                {
                    server.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new TextPacket
                    {
                        PType = TextPacketType.TextInputCancel
                    }));
                }
                else
                {
                    // Signal client to show the OS keyboard and forward keys into this focused field.
                    server.WebSocketServices.Broadcast(JsonConvert.SerializeObject(new TextPacket
                    {
                        PType = TextPacketType.TextInputContent,
                        text = ""
                    }));
                }
            }
        }
    }
}
