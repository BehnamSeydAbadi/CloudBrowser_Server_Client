using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FluentAssertions;
using WebSocketSharp;
using WebSocketSharp.Server;
using Xunit;

namespace BrowserServer.Tests
{
    /// <summary>
    /// In-process websocket-sharp loopback: client JSON in, server JSON out.
    /// </summary>
    public class ProtocolLoopbackBehavior : WebSocketBehavior
    {
        protected override void OnMessage(MessageEventArgs e)
        {
            var sink = new LoopbackCommands(this);
            if (e.IsBinary)
                ClientCommandDispatcher.DispatchBinary(e.RawData, sink);
            else
                ClientCommandDispatcher.DispatchText(e.Data, true, 2f, sink);
        }

        internal void SendJson(string json)
        {
            Send(json);
        }

        sealed class LoopbackCommands : IBrowserClientCommands
        {
            readonly ProtocolLoopbackBehavior _owner;

            public ClientSession Session { get { return null; } }

            public LoopbackCommands(ProtocolLoopbackBehavior owner)
            {
                _owner = owner;
            }

            public void CreateTab()
            {
                _owner.SendJson(WebSocketJsonProtocol.EncodeTextPacket(TextPacketType.TabList, new TabListPayload
                {
                    activeId = "t1",
                    tabs = new List<TabInfo>
                    {
                        new TabInfo { id = "t1", title = "New Tab", url = "about:blank" }
                    }
                }));
            }

            public void Navigate(string input)
            {
                _owner.SendJson(WebSocketJsonProtocol.EncodeTextPacket(TextPacketType.NavigatedUrl, input));
            }

            public void CloseTab(string tabId) { }
            public void SwitchTab(string tabId) { }
            public void MediaPermissionResponse(MediaPermissionPayload payload) { }
            public void NotificationPermissionResponse(NotificationPermissionPayload payload) { }
            public void PwaInstalled(PwaInstallPayload payload) { }
            public void TextInputSend(string text) { }
            public void Ack() { }
            public void DownloadAck(DownloadAckPayload ack) { }
            public void SendKey(SendKeyCommand key) { }
            public void NavigateBack(bool stopBeforeBlank) { }
            public void NavigateForward() { }
            public void SizeChange(int width, int height, float scale) { }
            public void ClientEnvironment(ClientEnvironmentPayload payload) { }
            public void ContextMenuQuery(PointerPacket pointer) { }
            public void ContextMenuAction(ContextMenuActionPayload action) { }
            public void PwaSessionStart(PwaSessionStartPayload payload) { }
            public void Touch(TouchKind kind, PointerPacket pointer) { }
            public void ClientBinary(byte[] data)
            {
                _owner.SendJson(WebSocketJsonProtocol.EncodeTextPacket(TextPacketType.QrDetected, "binary:" + data.Length));
            }
        }
    }

    public class WebSocketLoopbackTests
    {
        static int GetFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        [Fact]
        public void CreateTabJson_ReceivesTabListJson()
        {
            var received = Exchange(WebSocketJsonProtocol.EncodeCommPacket(PacketType.CreateTab));
            var packet = WebSocketJsonProtocol.DecodeTextPacket(received);

            packet.PType.Should().Be(TextPacketType.TabList);
            packet.text.Should().Contain("t1").And.Contain("about:blank");
        }

        [Fact]
        public void NavigationJson_ReceivesNavigatedUrlJson()
        {
            var received = Exchange(WebSocketJsonProtocol.EncodeCommPacket(PacketType.Navigation, "https://example.com/x"));
            var packet = WebSocketJsonProtocol.DecodeTextPacket(received);

            packet.PType.Should().Be(TextPacketType.NavigatedUrl);
            packet.text.Should().Be("https://example.com/x");
        }

        static string Exchange(string inboundJson)
        {
            var port = GetFreePort();
            var server = new WebSocketServer("ws://127.0.0.1:" + port);
            server.AddWebSocketService<ProtocolLoopbackBehavior>("/");
            server.Start();
            try
            {
                string received = null;
                using (var done = new ManualResetEventSlim(false))
                using (var ws = new WebSocket("ws://127.0.0.1:" + port + "/"))
                {
                    ws.OnMessage += (s, e) =>
                    {
                        received = e.Data;
                        done.Set();
                    };
                    ws.Connect();
                    ws.IsAlive.Should().BeTrue("WebSocket should connect to the loopback server");
                    ws.Send(inboundJson);
                    done.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue("server should reply with JSON");
                }
                return received;
            }
            finally
            {
                server.Stop();
            }
        }
    }
}
