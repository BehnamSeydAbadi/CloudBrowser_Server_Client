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
            public int ScalingFactor = 2;
            ClientSession session;

            public ClientSession Session
            {
                get { return session; }
            }

            protected override void OnOpen()
            {
                session = ClientSessionHub.Create(ID);
                if (session == null)
                {
                    Console.WriteLine("Max sessions reached — rejecting " + ID);
                    Context.WebSocket.Close(CloseStatusCode.PolicyViolation, "Max sessions");
                    return;
                }

                session.ResetCaptureState();
            }

            protected override void OnClose(CloseEventArgs e)
            {
                ClientSessionHub.Remove(ID);
                session = null;
            }

            protected override void OnMessage(MessageEventArgs e)
            {
                if (session == null)
                    return;

                if (e.IsBinary)
                {
                    ClientCommandDispatcher.DispatchBinary(e.RawData, this);
                    return;
                }

                ClientCommandDispatcher.DispatchText(
                    e.Data,
                    session.Tabs.ActiveBrowser != null,
                    ScalingFactor,
                    this);
            }

            public void CreateTab()
            {
                session.Tabs.CreateTab();
            }

            public void CloseTab(string tabId)
            {
                session.Tabs.CloseTab(tabId);
            }

            public void SwitchTab(string tabId)
            {
                session.Tabs.SwitchTab(tabId);
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
                PwaBridge.SetInstalledUrls(session, payload != null ? payload.urls : null, payload != null && payload.reload);
            }

            public void TextInputSend(string text)
            {
                var browser = session.Tabs.ActiveBrowser;
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
                var browser = session.Tabs.ActiveBrowser;
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
                var browser = session.Tabs.ActiveBrowser;
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
                var browser = session.Tabs.ActiveBrowser;
                if (browser == null)
                    return;
                var ignored = HandleNavigateBackAsync(session, browser, stopBeforeBlank);
            }

            public void NavigateForward()
            {
                var browser = session.Tabs.ActiveBrowser;
                if (browser != null && browser.CanGoForward)
                    browser.Forward();
            }

            public void SizeChange(int width, int height, float scale)
            {
                session.Tabs.SetViewport(width, height, scale);
            }

            public void ClientEnvironment(ClientEnvironmentPayload payload)
            {
                ClientEnvironmentBridge.Apply(session, payload);
            }

            public void ContextMenuQuery(PointerPacket pointer)
            {
                var ignored = ContextMenuBridge.HandleQueryAsync(session, pointer);
            }

            public void ContextMenuAction(ContextMenuActionPayload action)
            {
                ContextMenuBridge.HandleAction(session, action);
            }

            public void PwaSessionStart(PwaSessionStartPayload payload)
            {
                if (payload == null)
                    return;
                PwaSessionBridge.ActivateSession(session, payload.entryUrl);
            }

            public void Touch(TouchKind kind, PointerPacket pointer)
            {
                var browser = session.Tabs.ActiveBrowser;
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
                MediaBridge.HandleClientBinary(session, data);
            }
        }

        static WebSocketServer server;
        static readonly JpegRenderEncoder jpegEncoder = new JpegRenderEncoder();

        static void Main(string[] margs)
        {
            server = new WebSocketServer("ws://0.0.0.0:8081");
            server.AllowForwardedRequest = true;
            server.AddWebSocketService<test>("/");
            server.Start();

            SessionMessaging.Server = server;
            ClientSessionHub.CreateRenderHandler = (browser, tabId) => new TestRHI(browser, tabId);

            var cefRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CefSharp133");
            CefPaths.Root = cefRoot;
            var settings = new CefSettings()
            {
                RootCachePath = cefRoot,
                CachePath = Path.Combine(cefRoot, "Cache"),
                BrowserSubprocessPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CefSharp.BrowserSubprocess.exe"),
                UserAgent = MobileChromeIdentity.UserAgent,
                AcceptLanguageList = "en-US,en",
            };

            settings.CefCommandLineArgs["touch-events"] = "enabled";
            settings.CefCommandLineArgs["disable-gpu"] = "1";
            settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";
            settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
            settings.EnableAudio();
            if (settings.CefCommandLineArgs.ContainsKey("mute-audio"))
                settings.CefCommandLineArgs.Remove("mute-audio");
            CefSharpSettings.ConcurrentTaskExecution = true;

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

            Console.Clear();
            Console.WriteLine("Browser server is now running, you can connect to it via ws://" + NetworkManager.GetLocalIPAddress() + ":8081");
            Console.WriteLine("Per-client sessions: up to " + ClientSessionHub.MaxSessions + " isolated browser instances");
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
            var timer = new Timer(CaptureAllSessions, null, 0, 33);
            var audioTimer = new Timer(_ => StreamingAudioHandler.FlushOutbound(), null, 0, 10);
            var downloadTimer = new Timer(_ => StreamingDownloadHandler.FlushOutbound(), null, 0, 25);

            Console.ReadKey();
            downloadTimer.Dispose();
            audioTimer.Dispose();
            StreamingDownloadHandler.PurgeTempFolder();
            Cef.Shutdown();
            timer.Dispose();
        }

        static void CaptureAllSessions(object state)
        {
            foreach (var session in ClientSessionHub.AllActive())
                CaptureSession(session);
        }

        static void CaptureSession(ClientSession session)
        {
            if (session == null)
                return;

            try
            {
                var browser = session.Tabs.ActiveBrowser;
                if (browser == null || !browser.IsBrowserInitialized)
                    return;

                var now = Environment.TickCount;
                if (session.FrameSession.GetSharedSocketSkip(
                        StreamingDownloadHandler.IsStreamingForSession(session.WebSocketSessionId),
                        StreamingAudioHandler.PendingCountForSession(session.WebSocketSessionId)) != RenderFrameSkipReason.None)
                    return;

                VideoPlaybackBridge.Poll(session.Tabs.ActiveTabId, browser);

                if (session.FrameSession.GetMediaThrottleSkip(
                        MediaBridge.IsCaptureActiveForSession(session),
                        VideoPlaybackBridge.IsStreamingTab(session.Tabs.ActiveTabId),
                        now) != RenderFrameSkipReason.None)
                    return;

                if (!session.FrameSession.TryBeginCapture(now))
                    return;

                try
                {
                    using (var bitmap = browser.ScreenshotOrNull())
                    {
                        if (bitmap == null)
                            return;

                        int hash = BitmapContentHash.Compute(bitmap);
                        long quality;
                        if (!session.FrameSession.TrySelectQuality(hash, MediaBridge.IsCaptureActiveForSession(session), now, out quality))
                            return;

                        var jpeg = jpegEncoder.Encode(bitmap, quality);
                        session.SendBinary(jpeg);
                        session.FrameSession.MarkSent(now);
                    }
                }
                finally
                {
                    session.FrameSession.EndCapture();
                }
            }
            catch (Exception ex)
            {
                session.FrameSession.EndCapture();
                Console.WriteLine("Frame capture error session={0}: {1}", session.WebSocketSessionId, ex.Message);
            }
        }

        private static async System.Threading.Tasks.Task HandleNavigateBackAsync(ClientSession session, ChromiumWebBrowser browser, bool stopBeforeBlank)
        {
            if (browser == null || session == null)
                return;

            try
            {
                if (!browser.CanGoBack)
                {
                    if (stopBeforeBlank)
                        SendAtHistoryRoot(session);
                    return;
                }

                if (stopBeforeBlank)
                {
                    var host = browser.GetBrowser()?.GetHost();
                    if (host == null)
                    {
                        SendAtHistoryRoot(session);
                        return;
                    }

                    var entries = await host.GetNavigationEntriesAsync(false);
                    if (entries == null || entries.Count == 0)
                    {
                        SendAtHistoryRoot(session);
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
                        SendAtHistoryRoot(session);
                        return;
                    }

                    var previous = entries[currentIndex - 1];
                    if (IsBlankNavigationUrl(previous?.Url) || IsBlankNavigationUrl(previous?.DisplayUrl))
                    {
                        SendAtHistoryRoot(session);
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
                    SendAtHistoryRoot(session);
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

        private static void SendAtHistoryRoot(ClientSession session)
        {
            try
            {
                session?.SendText(TextPacketType.AtHistoryRoot, "");
            }
            catch
            {
            }
        }

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

                var session = ClientSessionHub.GetByTabId(tabId);
                if (session == null || session.Tabs.ActiveTabId != tabId)
                    return;

                VideoPlaybackBridge.HandlePaint(tabId, type, buffer, width, height);
            }

            public override void OnVirtualKeyboardRequested(IBrowser browser, TextInputMode inputMode)
            {
                base.OnVirtualKeyboardRequested(browser, inputMode);

                var session = ClientSessionHub.GetByTabId(tabId);
                if (session == null || session.Tabs.ActiveTabId != tabId)
                    return;

                Console.WriteLine("Virtual Keyboard Requested for " + inputMode);
                if (inputMode == TextInputMode.None)
                {
                    session.SendText(TextPacketType.TextInputCancel);
                }
                else
                {
                    session.SendText(TextPacketType.TextInputContent, "");
                }
            }
        }
    }
}
