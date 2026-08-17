using CefSharp;
using CefSharp.OffScreen;
using System;
using System.Drawing;
using System.IO;
using WebSocketSharp;
using WebSocketSharp.Server;
using CefSharp.Structs;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using CefSharp.Enums;

namespace BrowserServer
{
    class Program
    {
        public class test : WebSocketBehavior
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
                    MediaBridge.HandleClientBinary(e.RawData);
                    return;
                }

                var packet = JsonConvert.DeserializeObject<CommPacket>(e.Data);
                var browser = TabManager.ActiveBrowser;
                if (browser == null && packet.PType != PacketType.CreateTab)
                    return;

                switch (packet.PType)
                {
                    case PacketType.CreateTab:
                        TabManager.CreateTab();
                        break;

                    case PacketType.CloseTab:
                        if (!string.IsNullOrEmpty(packet.JSONData))
                            TabManager.CloseTab(packet.JSONData);
                        break;

                    case PacketType.SwitchTab:
                        if (!string.IsNullOrEmpty(packet.JSONData))
                            TabManager.SwitchTab(packet.JSONData);
                        break;

                    case PacketType.MediaPermissionResponse:
                        try
                        {
                            var media = JsonConvert.DeserializeObject<MediaPermissionPayload>(packet.JSONData ?? "");
                            MediaBridge.HandlePermissionResponse(media);
                        }
                        catch
                        {
                        }
                        break;

                    case PacketType.TextInputSend:
                        Console.WriteLine(packet.JSONData);
                        var textscript = @"(function (){document.activeElement.value='" + packet.JSONData + "'})();";

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
                        break;

                    case PacketType.ACK:
                        Console.WriteLine("ACK");
                        break;

                    case PacketType.DownloadAck:
                        try
                        {
                            var ack = JsonConvert.DeserializeObject<DownloadAckPayload>(packet.JSONData ?? "");
                            if (ack != null)
                                StreamingDownloadHandler.HandleClientAck(ack.id, ack.seq);
                        }
                        catch
                        {
                        }
                        break;

                    case PacketType.SendKey:
                        {
                            var host = browser.GetBrowserHost();
                            host.SetFocus(true);

                            var raw = packet.JSONData ?? "";
                            if (raw.TrimStart().StartsWith("{"))
                            {
                                var keyObj = JObject.Parse(raw);
                                var type = (keyObj.Value<string>("type") ?? "char").ToLowerInvariant();

                                if (type == "insert")
                                {
                                    var text = keyObj.Value<string>("text") ?? "";
                                    if (text.Length > 0)
                                    {
                                        var script = JavascriptFunctions.InsertText(JsonConvert.SerializeObject(text));
                                        browser.EvaluateScriptAsync(script);
                                    }
                                    break;
                                }

                                if (type == "backspace")
                                {
                                    browser.EvaluateScriptAsync(JavascriptFunctions.Backspace);
                                    break;
                                }

                                if (type == "enter")
                                {
                                    // Prefer key events for form submit / search.
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
                                }

                                var code = keyObj.Value<int>("code");
                                KeyEventType eventType = KeyEventType.Char;
                                switch (type)
                                {
                                    case "down":
                                        eventType = KeyEventType.RawKeyDown;
                                        break;
                                    case "up":
                                        eventType = KeyEventType.KeyUp;
                                        break;
                                }

                                host.SendKeyEvent(new KeyEvent
                                {
                                    WindowsKeyCode = code,
                                    NativeKeyCode = code,
                                    FocusOnEditableField = true,
                                    IsSystemKey = false,
                                    Type = eventType
                                });
                            }
                            else
                            {
                                var code = int.Parse(raw.Trim('"'));
                                host.SendKeyEvent(new KeyEvent
                                {
                                    WindowsKeyCode = code,
                                    NativeKeyCode = code,
                                    FocusOnEditableField = true,
                                    IsSystemKey = false,
                                    Type = KeyEventType.Char
                                });
                            }
                        }
                        break;

                    case PacketType.Navigation:
                        {
                            var input = (packet.JSONData ?? "").Trim();
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
                        break;

                    case PacketType.NavigateBack:
                        {
                            var stopBeforeBlank = string.Equals(packet.JSONData, "stopBeforeBlank", StringComparison.Ordinal);
                            var ignored = HandleNavigateBackAsync(browser, stopBeforeBlank);
                        }
                        break;
                    case PacketType.NavigateForward:
                        if (browser.CanGoForward) browser.Forward();
                        break;

                    case PacketType.SizeChange:
                        var jsonObject = JObject.Parse(packet.JSONData);
                        // Match BrowserClient ScaleRect exactly (already excludes bottom chrome/navbars).
                        var clientW = Math.Max(1, (int)Math.Round(jsonObject.Value<double>("Width")));
                        var clientH = Math.Max(1, (int)Math.Round(jsonObject.Value<double>("Height")));
                        var scaleToken = jsonObject["Scale"];
                        var clientScale = scaleToken != null && scaleToken.Type != JTokenType.Null
                            ? (float)scaleToken.Value<double>()
                            : ScalingFactor;
                        if (clientScale < 1f)
                            clientScale = 1f;

                        TabManager.SetViewport(clientW, clientH, clientScale);
                        break;

                    case PacketType.TouchDown:
                        var t_down = JsonConvert.DeserializeObject<PointerPacket>(packet.JSONData);
                        var press = new TouchEvent()
                        {
                            Id = (int)t_down.id,
                            X = (float)t_down.px * browser.Size.Width,
                            Y = (float)t_down.py * browser.Size.Height,
                            PointerType = CefSharp.Enums.PointerType.Touch,
                            Pressure = 0,
                            Type = CefSharp.Enums.TouchEventType.Pressed,
                        };
                        browser.GetBrowser().GetHost().SendTouchEvent(press);
                        break;

                    case PacketType.TouchUp:
                        var t_up = JsonConvert.DeserializeObject<PointerPacket>(packet.JSONData);
                        var up = new TouchEvent()
                        {
                            Id = (int)t_up.id,
                            X = (float)t_up.px * browser.Size.Width,
                            Y = (float)t_up.py * browser.Size.Height,
                            PointerType = CefSharp.Enums.PointerType.Touch,
                            Pressure = 0,
                            Type = CefSharp.Enums.TouchEventType.Released,
                        };
                        browser.GetBrowser().GetHost().SendTouchEvent(up);
                        break;

                    case PacketType.TouchMoved:
                        var t_move = JsonConvert.DeserializeObject<PointerPacket>(packet.JSONData);
                        var move = new TouchEvent()
                        {
                            Id = (int)t_move.id,
                            X = (float)t_move.px * browser.Size.Width,
                            Y = (float)t_move.py * browser.Size.Height,
                            PointerType = CefSharp.Enums.PointerType.Touch,
                            Pressure = 0,
                            Type = CefSharp.Enums.TouchEventType.Moved,
                        };
                        browser.GetBrowser().GetHost().SendTouchEvent(move);
                        break;

                    default:
                        break;
                }
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

            const string testUrl = "https://www.google.com/";
            var settings = new CefSettings()
            {
                CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CefSharp\\Cache"),
                // Real Android Google Chrome UA (Chrome still includes AppleWebKit tokens by design).
                UserAgent = MobileChromeIdentity.UserAgent,
                AcceptLanguageList = "en-US,en",
            };

            settings.CefCommandLineArgs["touch-events"] = "enabled";
            // Disable GPU in offscreen — reduces crashes on heavy sites (maps, modern SPAs).
            settings.CefCommandLineArgs["disable-gpu"] = "1";
            settings.CefCommandLineArgs["disable-gpu-compositing"] = "1";
            // Allow media autoplay so remote audio can start without a desktop gesture.
            settings.CefCommandLineArgs["autoplay-policy"] = "no-user-gesture-required";
            // OffScreen CefSettings adds "mute-audio" by default; without this, AudioHandler never fires.
            settings.EnableAudio();
            if (settings.CefCommandLineArgs.ContainsKey("mute-audio"))
                settings.CefCommandLineArgs.Remove("mute-audio");

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
            Console.WriteLine("Phone camera/mic: ENABLED (sites calling getUserMedia prompt on the client)");
            Console.WriteLine("QR decode: ENABLED (while camera is on, HTTP(S) codes open automatically)");
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

        static int captureInFlight = 0;
        static int captureStartedTick = 0;
        static int lastMediaAwareFrameTick = 0;
        static int lastFrameHash;
        static bool hasLastHash;
        static int lastSendTick;
        static int lastDirtyTick;
        static bool sentCrispFrame;
        static ImageCodecInfo jpegCodec;
        static MemoryStream encodeStream;
        const int CaptureStuckMs = 3000;
        const int KeepaliveMs = 750;
        const int CrispAfterStillMs = 160;

        public static void ResetCaptureState()
        {
            Interlocked.Exchange(ref captureInFlight, 0);
            captureStartedTick = 0;
            lastFrameHash = 0;
            hasLastHash = false;
            sentCrispFrame = false;
        }

        static void Callback(object state)
        {
            try
            {
                var browser = TabManager.ActiveBrowser;
                if (browser == null || !browser.IsBrowserInitialized)
                    return;

                // While a file is streaming to the phone, skip JPEG frames so FILE chunks
                // are not delayed/lost on the shared WebSocket (avoids transfer interrupts).
                if (StreamingDownloadHandler.IsStreamingToClients)
                    return;

                bool mediaOn = MediaBridge.IsCaptureActive;
                // Keep a slow page stream while camera is on (so QR UI still updates), but
                // leave most of the WS for CAM uplink — full 20fps + CAM freezes WM10.
                var now = Environment.TickCount;
                if (mediaOn)
                {
                    if (now - lastMediaAwareFrameTick < 400)
                        return;
                    lastMediaAwareFrameTick = now;
                }

                // Recover if a previous capture never finished (used to cause permanent black screen).
                if (Interlocked.CompareExchange(ref captureInFlight, 1, 0) != 0)
                {
                    var started = Volatile.Read(ref captureStartedTick);
                    if (started != 0 && (now - started) > CaptureStuckMs)
                    {
                        Console.WriteLine("Frame capture stuck — resetting");
                        Interlocked.Exchange(ref captureInFlight, 0);
                    }
                    return;
                }

                captureStartedTick = now;

                try
                {
                    // Use paint-buffer snapshot (no DevTools). CaptureScreenshotAsync hangs on heavy SPAs
                    // and then leaves the stream black until the server is restarted.
                    using (var bitmap = browser.ScreenshotOrNull())
                    {
                        if (bitmap == null || server == null)
                            return;

                        int hash = HashBitmap(bitmap);
                        bool dirty = !hasLastHash || hash != lastFrameHash;
                        long quality;

                        if (mediaOn)
                        {
                            quality = 50L;
                        }
                        else if (dirty)
                        {
                            lastFrameHash = hash;
                            hasLastHash = true;
                            lastDirtyTick = now;
                            sentCrispFrame = false;
                            // Fast motion: slightly higher than the old 70, small enough for 30fps LAN.
                            quality = 80L;
                        }
                        else if (!sentCrispFrame && (now - lastDirtyTick) >= CrispAfterStillMs)
                        {
                            // Page settled — one sharp frame so text/icons look clean.
                            sentCrispFrame = true;
                            quality = 90L;
                        }
                        else if ((now - lastSendTick) < KeepaliveMs)
                        {
                            return;
                        }
                        else
                        {
                            quality = 82L;
                        }

                        if (jpegCodec == null)
                            jpegCodec = GetEncoder(ImageFormat.Jpeg);
                        if (encodeStream == null)
                            encodeStream = new MemoryStream(160 * 1024);
                        encodeStream.SetLength(0);

                        var encoderParameters = new EncoderParameters(1);
                        encoderParameters.Param[0] = new EncoderParameter(
                            System.Drawing.Imaging.Encoder.Quality, quality);
                        bitmap.Save(encodeStream, jpegCodec ?? GetEncoder(ImageFormat.Jpeg), encoderParameters);
                        server.WebSocketServices.Broadcast(encodeStream.ToArray());
                        lastSendTick = now;
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref captureInFlight, 0);
                }
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref captureInFlight, 0);
                Console.WriteLine("Frame capture error: " + ex.Message);
            }
        }

        /// <summary>Cheap content hash so static pages are not re-encoded every tick.</summary>
        static int HashBitmap(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            BitmapData data = null;
            try
            {
                data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                int stride = Math.Abs(data.Stride);
                int w = bitmap.Width;
                int h = bitmap.Height;
                int hash = w * 73856093 ^ h * 19349663;
                IntPtr scan0 = data.Scan0;
                for (int y = 0; y < h; y += 4)
                {
                    int row = y * stride;
                    for (int x = 0; x < w; x += 4)
                        hash = (hash * 16777619) ^ Marshal.ReadInt32(scan0, row + (x << 2));
                }
                return hash;
            }
            catch
            {
                return Environment.TickCount;
            }
            finally
            {
                if (data != null)
                {
                    try { bitmap.UnlockBits(data); } catch { }
                }
            }
        }

        static int frameNum = 0;
        private static void CefPaint(object sender, OnPaintEventArgs e)
        {
            frameNum++;
            var browserImage = new Bitmap(e.Width, e.Height, 4 * e.Width, System.Drawing.Imaging.PixelFormat.Format32bppRgb, e.BufferHandle);
            byte[] bufferBytes;
            var encoderParameters = new EncoderParameters(1);
            encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 75L);
            using (MemoryStream stream = new MemoryStream())
            {
                browserImage.Save(stream, GetEncoder(ImageFormat.Jpeg), encoderParameters);
                bufferBytes = stream.ToArray();
            }
            server.WebSocketServices.Broadcast(bufferBytes);
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

                    var entries = await CefSharp.AsyncExtensions.GetNavigationEntriesAsync(host, currentOnly: false);
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

        private static ImageCodecInfo GetEncoder(ImageFormat format)
        {
            var codecs = ImageCodecInfo.GetImageDecoders();
            foreach (var codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
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
