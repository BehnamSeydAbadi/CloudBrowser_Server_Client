using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using Windows.UI.Input;
using Windows.Foundation;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace BrowserClient
{
    public class WebBrowserDataSource
    {
        ClientWebSocket sock;
        public event EventHandler<string> JSONRecived;
        public event EventHandler<BitmapImage> FrameRecived;
        public event EventHandler<TextPacket> TextPacketRecived;
        public event EventHandler<ArraySegment<byte>> AudioPacketRecived;
        private readonly StreamAudioPlayer audioPlayer = new StreamAudioPlayer();
        public readonly DownloadStore Downloads = new DownloadStore();
        public readonly ClientMediaCapture MediaCapture = new ClientMediaCapture();
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private byte[] pendingFrame;
        private int framePumpRunning;

        /// <summary>Raised on UI thread expectation — MainPage shows Allow/Deny.</summary>
        public event EventHandler<MediaPermissionPayload> MediaPermissionRequested;
        /// <summary>Raised when a site calls Notification.requestPermission().</summary>
        public event EventHandler<NotificationPermissionPayload> NotificationPermissionRequested;
        /// <summary>Pinned Start-tile URLs to treat as installed PWAs (sent right after connect).</summary>
        public Func<Task<List<string>>> ProvidePwaUrls;

        public async void StartRecive(string addr)
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

            // Create a simple setting.
            localSettings.Values["LastServerUrl"] = addr ;

            sock = new ClientWebSocket();
            Downloads.OnChunkWritten = (id, seq) =>
            {
                var ignored = SendDownloadAckAsync(id, seq);
            };
            MediaCapture.SendBinaryAsync = SendBinaryAsync;

            try
            {
                await sock.ConnectAsync(new Uri(addr), CancellationToken.None);
                await Downloads.EnsureLoadedAsync();
                await SendPwaInstalledAsync(false);

                // Typical messages are << 1MB; grow on demand for rare large frames.
                var readbuffer = new byte[512 * 1024];

                while (sock.State == WebSocketState.Open)
                {
                    var res = await sock.ReceiveAsync(new ArraySegment<byte>(readbuffer), CancellationToken.None);
                    var messageType = res.MessageType;
                    int total = res.Count;

                    while (!res.EndOfMessage && sock.State == WebSocketState.Open)
                    {
                        if (total >= readbuffer.Length)
                        {
                            var grown = new byte[readbuffer.Length * 2];
                            System.Buffer.BlockCopy(readbuffer, 0, grown, 0, total);
                            readbuffer = grown;
                        }

                        res = await sock.ReceiveAsync(
                            new ArraySegment<byte>(readbuffer, total, readbuffer.Length - total),
                            CancellationToken.None);
                        total += res.Count;
                    }

                    if (sock.State != WebSocketState.Open && messageType == WebSocketMessageType.Close)
                        break;

                    // Magic prefixes are always binary — protects against wrong MessageType after fragmentation.
                    if (total >= 4 &&
                        ((readbuffer[0] == (byte)'A' && readbuffer[1] == (byte)'U' && readbuffer[2] == (byte)'D' && readbuffer[3] == (byte)'I')
                         || (readbuffer[0] == (byte)'F' && readbuffer[1] == (byte)'I' && readbuffer[2] == (byte)'L' && readbuffer[3] == (byte)'E')))
                    {
                        messageType = WebSocketMessageType.Binary;
                    }

                    switch (messageType)
                    {
                        case WebSocketMessageType.Binary:
                            if (total >= 4 &&
                                readbuffer[0] == (byte)'A' &&
                                readbuffer[1] == (byte)'U' &&
                                readbuffer[2] == (byte)'D' &&
                                readbuffer[3] == (byte)'I')
                            {
                                var audio = new byte[total];
                                System.Buffer.BlockCopy(readbuffer, 0, audio, 0, total);
                                audioPlayer.SubmitPacket(audio, total);
                                AudioPacketRecived?.Invoke(this, new ArraySegment<byte>(audio, 0, total));
                            }
                            else if (total >= 4 &&
                                readbuffer[0] == (byte)'F' &&
                                readbuffer[1] == (byte)'I' &&
                                readbuffer[2] == (byte)'L' &&
                                readbuffer[3] == (byte)'E')
                            {
                                // Copy + queue — never await disk I/O on the receive loop (aborts WS under load).
                                var filePacket = new byte[total];
                                System.Buffer.BlockCopy(readbuffer, 0, filePacket, 0, total);
                                Downloads.EnqueueFilePacket(filePacket);
                            }
                            else
                            {
                                // Latest-frame-wins: never decode on the receive loop.
                                // Queued JPEGs make scrolling feel a second behind and freeze WM10.
                                var jpeg = new byte[total];
                                System.Buffer.BlockCopy(readbuffer, 0, jpeg, 0, total);
                                Interlocked.Exchange(ref pendingFrame, jpeg);
                                if (Interlocked.CompareExchange(ref framePumpRunning, 1, 0) == 0)
                                {
                                    var ignored = PumpPendingFramesAsync();
                                }
                            }
                            break;
                        case WebSocketMessageType.Close:
                            break;
                        case WebSocketMessageType.Text:
                            try
                            {
                                var json = System.Text.Encoding.UTF8.GetString(readbuffer, 0, total);
                                var packet = JsonConvert.DeserializeObject<TextPacket>(json);
                                if (packet.PType == TextPacketType.AudioStop)
                                    audioPlayer.Stop();
                                HandleDownloadTextPacket(packet);
                                HandleMediaTextPacket(packet);
                                HandleNotificationTextPacket(packet);
                                TextPacketRecived?.Invoke(this, packet);
                            }
                            catch
                            {
                            }
                            break;
                        default:
                            break;
                    }
                }
            }
            catch (WebSocketException)
            {
                // Common when the peer resets during a heavy transfer; connection will be recreated on next Connect().
            }
            catch (ObjectDisposedException)
            {
            }
            catch (Exception)
            {
            }
            finally
            {
                audioPlayer.Stop();
                try { MediaCapture.Dispose(); } catch { }
                try
                {
                    if (sock != null &&
                        (sock.State == WebSocketState.Open || sock.State == WebSocketState.CloseReceived))
                    {
                        await sock.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    }
                }
                catch
                {
                }
            }
        }

        private void HandleNotificationTextPacket(TextPacket packet)
        {
            if (packet.text == null)
                return;

            try
            {
                if (packet.PType == TextPacketType.NotificationPermissionRequest)
                {
                    var payload = JsonConvert.DeserializeObject<NotificationPermissionPayload>(packet.text);
                    if (payload != null)
                        NotificationPermissionRequested?.Invoke(this, payload);
                }
            }
            catch
            {
            }
        }

        public async Task RespondNotificationPermissionAsync(NotificationPermissionPayload request, bool allowed)
        {
            if (request == null || string.IsNullOrEmpty(request.requestId))
                return;

            try
            {
                var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
                {
                    PType = PacketType.NotificationPermissionResponse,
                    JSONData = JsonConvert.SerializeObject(new NotificationPermissionPayload
                    {
                        requestId = request.requestId,
                        origin = request.origin,
                        allowed = allowed
                    })
                }));
                await SendTextAsync(new ArraySegment<byte>(encoded));
            }
            catch
            {
            }
        }

        private void HandleMediaTextPacket(TextPacket packet)
        {
            if (packet.text == null)
                return;

            try
            {
                if (packet.PType == TextPacketType.MediaPermissionRequest)
                {
                    var payload = JsonConvert.DeserializeObject<MediaPermissionPayload>(packet.text);
                    if (payload != null)
                        MediaPermissionRequested?.Invoke(this, payload);
                }
                else if (packet.PType == TextPacketType.MediaCaptureUpgrade)
                {
                    var payload = JsonConvert.DeserializeObject<MediaPermissionPayload>(packet.text);
                    if (payload != null)
                    {
                        var ignored = RespondMediaUpgradeAsync(payload);
                    }
                }
                else if (packet.PType == TextPacketType.MediaCaptureStop)
                {
                    var payload = JsonConvert.DeserializeObject<MediaPermissionPayload>(packet.text);
                    MediaCapture.StopIfRequest(payload?.requestId);
                }
            }
            catch
            {
            }
        }

        public async Task RespondMediaUpgradeAsync(MediaPermissionPayload request)
        {
            if (request == null || string.IsNullOrEmpty(request.requestId))
                return;

            var ok = await MediaCapture.EnsureAsync(request.requestId, request.audio, request.video);
            try
            {
                var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
                {
                    PType = PacketType.MediaPermissionResponse,
                    JSONData = JsonConvert.SerializeObject(new MediaPermissionPayload
                    {
                        requestId = request.requestId,
                        allowed = ok,
                        audio = request.audio,
                        video = request.video,
                        origin = request.origin
                    })
                }));
                await SendTextAsync(new ArraySegment<byte>(encoded));
            }
            catch
            {
            }
        }

        public async Task RespondMediaPermissionAsync(MediaPermissionPayload request, bool allowed)
        {
            if (request == null || string.IsNullOrEmpty(request.requestId))
                return;

            var started = false;
            if (allowed)
            {
                started = await MediaCapture.StartAsync(request.requestId, request.audio, request.video);
                if (!started)
                    allowed = false;
            }

            try
            {
                var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
                {
                    PType = PacketType.MediaPermissionResponse,
                    JSONData = JsonConvert.SerializeObject(new MediaPermissionPayload
                    {
                        requestId = request.requestId,
                        allowed = allowed,
                        audio = request.audio,
                        video = request.video,
                        origin = request.origin
                    })
                }));
                await SendTextAsync(new ArraySegment<byte>(encoded));
            }
            catch
            {
            }

            if (!allowed)
                await MediaCapture.StopAsync();
        }

        private void HandleDownloadTextPacket(TextPacket packet)
        {
            if (packet.text == null)
                return;

            try
            {
                switch (packet.PType)
                {
                    case TextPacketType.DownloadStarted:
                        Downloads.OnStarted(JsonConvert.DeserializeObject<DownloadEventPayload>(packet.text));
                        break;
                    case TextPacketType.DownloadProgress:
                        Downloads.OnProgress(JsonConvert.DeserializeObject<DownloadEventPayload>(packet.text));
                        break;
                    case TextPacketType.DownloadCompleted:
                        Downloads.OnCompletedMeta(JsonConvert.DeserializeObject<DownloadEventPayload>(packet.text));
                        break;
                }
            }
            catch
            {
            }
        }

        private async Task SendDownloadAckAsync(string id, int seq)
        {
            if (string.IsNullOrEmpty(id))
                return;

            try
            {
                var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
                {
                    PType = PacketType.DownloadAck,
                    JSONData = JsonConvert.SerializeObject(new DownloadAckPayload { id = id, seq = seq })
                }));
                await SendTextAsync(new ArraySegment<byte>(encoded));
            }
            catch
            {
            }
        }

        /// <summary>All sends go through here — ClientWebSocket forbids concurrent SendAsync.</summary>
        private async Task SendTextAsync(ArraySegment<byte> payload)
        {
            await sendGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (sock == null || sock.State != WebSocketState.Open)
                    return;
                await sock.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }

        private async Task SendBinaryAsync(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return;

            // CAM/MIC must never block behind a backlog — drop the frame if the socket is busy.
            bool isMediaUplink = payload.Length >= 4 &&
                ((payload[0] == (byte)'C' && payload[1] == (byte)'A' && payload[2] == (byte)'M' && payload[3] == (byte)' ') ||
                 (payload[0] == (byte)'M' && payload[1] == (byte)'I' && payload[2] == (byte)'C' && payload[3] == (byte)' '));

            if (isMediaUplink)
            {
                if (!await sendGate.WaitAsync(0).ConfigureAwait(false))
                    return;
            }
            else
            {
                await sendGate.WaitAsync().ConfigureAwait(false);
            }

            try
            {
                if (sock == null || sock.State != WebSocketState.Open)
                    return;
                await sock.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Binary, true, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                sendGate.Release();
            }
        }

        public async Task SendPwaInstalledAsync(bool reload)
        {
            List<string> urls = null;
            try
            {
                if (ProvidePwaUrls != null)
                    urls = await ProvidePwaUrls();
            }
            catch
            {
            }

            await SendPwaInstalledAsync(urls, reload);
        }

        public async Task SendPwaInstalledAsync(IEnumerable<string> urls, bool reload)
        {
            try
            {
                var list = new List<string>();
                if (urls != null)
                {
                    foreach (var url in urls)
                    {
                        if (!string.IsNullOrWhiteSpace(url))
                            list.Add(url.Trim());
                    }
                }

                var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
                {
                    PType = PacketType.PwaInstalled,
                    JSONData = JsonConvert.SerializeObject(new PwaInstallPayload
                    {
                        urls = list,
                        reload = reload
                    })
                }));
                await SendTextAsync(new ArraySegment<byte>(encoded));
            }
            catch
            {
            }
        }

        public async void Navigate(string s)
        {
            var cp = new CommPacket();
            cp.PType = PacketType.Navigation;
            cp.JSONData = s;
            string PacketJSON = JsonConvert.SerializeObject(cp);
            var encoded = Encoding.UTF8.GetBytes(PacketJSON);
            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        public async void NavigateForward()
        {
            if (sock.State != WebSocketState.Open)
                return;

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
            {
                PType = PacketType.NavigateForward
            }));

            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }
        public async void NavigateBack(bool stopBeforeBlank = false)
        {
            if (sock == null || sock.State != WebSocketState.Open)
                return;

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
            {
                PType = PacketType.NavigateBack,
                JSONData = stopBeforeBlank ? "stopBeforeBlank" : null
            }));

            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        public async void SendKey(Windows.UI.Xaml.Input.KeyRoutedEventArgs key)
        {
            if (sock == null || sock.State != WebSocketState.Open)
                return;

            await SendKeyCode((int)key.Key, "char");
        }

        public async Task SendInsertText(string text)
        {
            if (sock == null || sock.State != WebSocketState.Open || string.IsNullOrEmpty(text))
                return;

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
            {
                PType = PacketType.SendKey,
                JSONData = JsonConvert.SerializeObject(new { type = "insert", text })
            }));

            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        public async Task SendBackspace()
        {
            if (sock == null || sock.State != WebSocketState.Open)
                return;

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
            {
                PType = PacketType.SendKey,
                JSONData = JsonConvert.SerializeObject(new { type = "backspace" })
            }));

            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        public async Task SendEnterKey()
        {
            if (sock == null || sock.State != WebSocketState.Open)
                return;

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
            {
                PType = PacketType.SendKey,
                JSONData = JsonConvert.SerializeObject(new { type = "enter" })
            }));

            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        public async Task SendKeyCode(int code, string type)
        {
            if (sock == null || sock.State != WebSocketState.Open)
                return;

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
            {
                PType = PacketType.SendKey,
                JSONData = JsonConvert.SerializeObject(new { code, type })
            }));

            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        public async void SizeChange(Windows.Foundation.Size newSize, double scale = 0)
        {
            if (sock.State != WebSocketState.Open)
                return;

            if (scale <= 0)
                scale = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;

            var cp = new CommPacket();
            cp.PType = PacketType.SizeChange;
            cp.JSONData = JsonConvert.SerializeObject(new
            {
                Width = newSize.Width,
                Height = newSize.Height,
                Scale = scale
            });

            string PacketJSON = JsonConvert.SerializeObject(cp);
            var encoded = Encoding.UTF8.GetBytes(PacketJSON);
            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        public async void TouchDown(Point p, uint pointerId)
        {

            if (sock.State != WebSocketState.Open)
                return;


            var cp = new CommPacket
            {
                PType = PacketType.TouchDown,
                JSONData = JsonConvert.SerializeObject(new PointerPacket
                {
                    px = p.X,
                    py = p.Y,
                    id = pointerId
                })
            };

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(cp));
            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);


        }
        public async void TouchUp(Point p, uint pointerId)
        {

            if (sock.State != WebSocketState.Open)
                return;

            var cp = new CommPacket
            {
                PType = PacketType.TouchUp,
                JSONData = JsonConvert.SerializeObject(new PointerPacket
                {
                    px = p.X,
                    py = p.Y,
                    id = pointerId
                })
            };

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(cp));
            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);


        }


        public async void SendText(string text)
        {
            if (sock.State != WebSocketState.Open)
                return;

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket {
                PType = PacketType.TextInputSend,
                JSONData = text
            }));

            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }
        public async void TouchMove(Point p, uint pointerId)
        {

            if (sock.State != WebSocketState.Open)
                return;


            var cp = new CommPacket
            {
                PType = PacketType.TouchMoved,
                JSONData = JsonConvert.SerializeObject(new PointerPacket
                {
                    px = p.X,
                    py = p.Y,
                    id = pointerId
                })
            };

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(cp));
            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
            
        }

        public async void ACKRender()
        {
            if (sock.State != WebSocketState.Open)
                return;


            var cp = new CommPacket
            {
                PType = PacketType.ACK
            };

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(cp));
            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        public async void CreateTab()
        {
            if (sock == null || sock.State != WebSocketState.Open)
                return;

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
            {
                PType = PacketType.CreateTab
            }));
            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        public async void CloseTab(string tabId)
        {
            if (sock == null || sock.State != WebSocketState.Open)
                return;

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
            {
                PType = PacketType.CloseTab,
                JSONData = tabId
            }));
            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        public async void SwitchTab(string tabId)
        {
            if (sock == null || sock.State != WebSocketState.Open)
                return;

            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
            {
                PType = PacketType.SwitchTab,
                JSONData = tabId
            }));
            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await SendTextAsync(buffer);
        }

        /// <summary>Decode only the newest JPEG; drop anything that arrived while we were busy.</summary>
        private async Task PumpPendingFramesAsync()
        {
            try
            {
                while (true)
                {
                    var jpeg = Interlocked.Exchange(ref pendingFrame, null);
                    if (jpeg == null || jpeg.Length == 0)
                        break;

                    try
                    {
                        var bitmap = await ConvertToBitmapImage(jpeg).ConfigureAwait(true);
                        if (bitmap != null)
                            FrameRecived?.Invoke(this, bitmap);
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref framePumpRunning, 0);
                if (Volatile.Read(ref pendingFrame) != null &&
                    Interlocked.CompareExchange(ref framePumpRunning, 1, 0) == 0)
                {
                    var ignored = PumpPendingFramesAsync();
                }
            }
        }

        public async Task<BitmapImage> ConvertToBitmapImage(byte[] image)
        {
            if (image == null || image.Length == 0)
                return null;

            using (InMemoryRandomAccessStream ms = new InMemoryRandomAccessStream())
            {
                using (DataWriter writer = new DataWriter(ms.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(image);
                    await writer.StoreAsync();
                }
                ms.Seek(0);

                var dispatcher = GetUiDispatcher();
                if (dispatcher == null || dispatcher.HasThreadAccess)
                {
                    var bitmapimage = new BitmapImage();
                    await bitmapimage.SetSourceAsync(ms);
                    return bitmapimage;
                }

                BitmapImage created = null;
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    created = new BitmapImage();
                    created.SetSource(ms);
                });
                return created;
            }
        }

        private static CoreDispatcher GetUiDispatcher()
        {
            try
            {
                var view = CoreApplication.MainView;
                if (view != null && view.CoreWindow != null)
                    return view.CoreWindow.Dispatcher;
            }
            catch
            {
            }

            try
            {
                var window = CoreWindow.GetForCurrentThread();
                if (window != null)
                    return window.Dispatcher;
            }
            catch
            {
            }

            return null;
        }
    }
}
