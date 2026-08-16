using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.WebSockets;
using System.Threading;
using Newtonsoft.Json;
using System.Runtime.InteropServices;
using Windows.UI.Input;
using Windows.Foundation;
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

        public async void StartRecive(string addr)
        {
            var localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

            // Create a simple setting.
            localSettings.Values["LastServerUrl"] = addr ;

            sock = new ClientWebSocket();

            await sock.ConnectAsync(new Uri(addr), CancellationToken.None);

            //2mb should be enough
            ArraySegment<byte> readbuffer = new ArraySegment<byte>(new byte[2000000]);
            
            while (sock.State == WebSocketState.Open)
            {
                Array.Clear(readbuffer.Array, 0, readbuffer.Array.Length);

                var res = await sock.ReceiveAsync(readbuffer, CancellationToken.None);
                var messageType = res.MessageType;
                // Reassemble continuation frames if any.
                int total = res.Count;
                while (!res.EndOfMessage && sock.State == WebSocketState.Open)
                {
                    if (total >= readbuffer.Array.Length)
                        break;
                    res = await sock.ReceiveAsync(new ArraySegment<byte>(readbuffer.Array, total, readbuffer.Array.Length - total), CancellationToken.None);
                    total += res.Count;
                }

                // AUDI magic is always binary — protects against wrong MessageType after fragmentation.
                if (total >= 4 &&
                    readbuffer.Array[0] == (byte)'A' &&
                    readbuffer.Array[1] == (byte)'U' &&
                    readbuffer.Array[2] == (byte)'D' &&
                    readbuffer.Array[3] == (byte)'I')
                {
                    messageType = WebSocketMessageType.Binary;
                }

                switch (messageType)
                {
                    case WebSocketMessageType.Binary:
                        if (total >= 4 &&
                            readbuffer.Array[0] == (byte)'A' &&
                            readbuffer.Array[1] == (byte)'U' &&
                            readbuffer.Array[2] == (byte)'D' &&
                            readbuffer.Array[3] == (byte)'I')
                        {
                            // Must copy — the receive buffer is cleared/reused on the next loop iteration.
                            var audio = new byte[total];
                            System.Buffer.BlockCopy(readbuffer.Array, 0, audio, 0, total);
                            audioPlayer.SubmitPacket(audio, total);
                            AudioPacketRecived?.Invoke(this, new ArraySegment<byte>(audio, 0, total));
                        }
                        else
                        {
                            var jpeg = new byte[total];
                            System.Buffer.BlockCopy(readbuffer.Array, 0, jpeg, 0, total);
                            // BitmapImage must be created on the UI sync context (WM10/UWP).
                            // Stay on the context captured by StartRecive — do not ConfigureAwait(false).
                            try
                            {
                                var bitmap = await ConvertToBitmapImage(jpeg);
                                FrameRecived?.Invoke(this, bitmap);
                            }
                            catch
                            {
                            }
                        }
                        break;
                    case WebSocketMessageType.Close:

                        break;
                    case WebSocketMessageType.Text:
                        //text packet
                        try
                        {
                            var json = System.Text.Encoding.UTF8.GetString(readbuffer.Array, 0, total);
                            var packet = JsonConvert.DeserializeObject<TextPacket>(json);
                            if (packet.PType == TextPacketType.AudioStop)
                                audioPlayer.Stop();
                            TextPacketRecived?.Invoke(this, packet);
                        }
                        catch (Exception)
                        {

                           // throw;
                        }
                       
                        break;
                    default:
                        break;
                }
            }

            audioPlayer.Stop();
        }

        public async void Navigate(string s)
        {
            var cp = new CommPacket();
            cp.PType = PacketType.Navigation;
            cp.JSONData = s;
            string PacketJSON = JsonConvert.SerializeObject(cp);
            var encoded = Encoding.UTF8.GetBytes(PacketJSON);
            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
        }
        public async void NavigateBack()
        {
            var encoded = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new CommPacket
            {
                PType = PacketType.NavigateBack
            }));

            var buffer = new ArraySegment<byte>(encoded, 0, encoded.Length);
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);


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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);


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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
            
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
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
            await sock.SendAsync(buffer, WebSocketMessageType.Text, true, CancellationToken.None);
        }

        public async Task<BitmapImage> ConvertToBitmapImage(byte[] image)
        {
            BitmapImage bitmapimage = null;
            using (InMemoryRandomAccessStream ms = new InMemoryRandomAccessStream())
            {
                using (DataWriter writer = new DataWriter(ms.GetOutputStreamAt(0)))
                {
                    writer.WriteBytes(image);
                    await writer.StoreAsync();
                }
                ms.Seek(0);
                bitmapimage = new BitmapImage();
                bitmapimage.SetSource(ms);
            }
            return bitmapimage;
        }
    }
}
