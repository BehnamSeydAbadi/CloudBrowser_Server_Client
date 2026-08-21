using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using FluentAssertions;
using WebSocketSharp;
using WebSocketSharp.Server;
using Xunit;

namespace BrowserServer.Tests
{
    public class ClientSessionTests
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
        public void Hub_CreateAndRemove_TracksSessions()
        {
            ClientSessionHub.Create("a").Should().NotBeNull();
            ClientSessionHub.Count.Should().Be(1);
            ClientSessionHub.Get("a").Should().NotBeNull();

            ClientSessionHub.Remove("a");
            ClientSessionHub.Count.Should().Be(0);
            ClientSessionHub.Get("a").Should().BeNull();
        }

        [Fact]
        public void Hub_EnforcesMaxSessions()
        {
            for (int i = 0; i < ClientSessionHub.MaxSessions; i++)
                ClientSessionHub.Create("s" + i).Should().NotBeNull();

            ClientSessionHub.Create("overflow").Should().BeNull();

            for (int i = 0; i < ClientSessionHub.MaxSessions; i++)
                ClientSessionHub.Remove("s" + i);
        }

        [Fact]
        public void SessionMessaging_SendText_ReachesOnlyTargetClient()
        {
            var port = GetFreePort();
            var server = new WebSocketServer("ws://127.0.0.1:" + port);
            server.AddWebSocketService<EchoBehavior>("/");
            server.Start();
            SessionMessaging.Server = server;

            try
            {
                var receivedA = new List<string>();
                var receivedB = new List<string>();
                using (var wsA = new WebSocket("ws://127.0.0.1:" + port + "/"))
                using (var wsB = new WebSocket("ws://127.0.0.1:" + port + "/"))
                {
                    wsA.OnMessage += (s, e) => receivedA.Add(e.Data);
                    wsB.OnMessage += (s, e) => receivedB.Add(e.Data);

                    wsA.Connect();
                    wsB.Connect();
                    Thread.Sleep(300);

                    var host = server.WebSocketServices["/"];
                    var ids = host.Sessions.IDs.ToList();
                    ids.Count.Should().BeGreaterThanOrEqualTo(2);

                    SessionMessaging.SendText(ids[0], new TextPacket
                    {
                        PType = TextPacketType.NavigatedUrl,
                        text = "https://client-a-only.test"
                    });

                    Thread.Sleep(200);

                    var targetReceived = receivedA.Count > 0 ? receivedA : receivedB;
                    var otherReceived = receivedA.Count > 0 ? receivedB : receivedA;
                    targetReceived.Should().ContainSingle(m => m.Contains("client-a-only.test"));
                    otherReceived.Should().NotContain(m => m.Contains("client-a-only.test"));
                }
            }
            finally
            {
                server.Stop();
            }
        }

        [Fact]
        public void ClientSessions_AreIsolatedByWebSocketId()
        {
            var sessionA = new ClientSession("ws-a");
            var sessionB = new ClientSession("ws-b");

            sessionA.WebSocketSessionId.Should().Be("ws-a");
            sessionB.WebSocketSessionId.Should().Be("ws-b");
            sessionA.Tabs.Should().NotBeSameAs(sessionB.Tabs);
            sessionA.FrameSession.Should().NotBeSameAs(sessionB.FrameSession);

            CefPaths.Root = Path.Combine(Path.GetTempPath(), "CloudBrowserTestCef");
            var pathA = Path.Combine(CefPaths.SessionsRoot, CefPaths.SanitizeSessionFolderName("ws-a"));
            var pathB = Path.Combine(CefPaths.SessionsRoot, CefPaths.SanitizeSessionFolderName("ws-b"));
            pathA.Should().NotBe(pathB);
        }

        [Fact]
        public void CefPaths_SessionFolders_AreUniquePerClient()
        {
            CefPaths.Root = Path.Combine(Path.GetTempPath(), "CloudBrowserTestCef");
            var a = Path.Combine(CefPaths.SessionsRoot, CefPaths.SanitizeSessionFolderName("client-a"));
            var b = Path.Combine(CefPaths.SessionsRoot, CefPaths.SanitizeSessionFolderName("client-b"));
            a.Should().NotBe(b);
        }

        sealed class EchoBehavior : WebSocketBehavior
        {
            protected override void OnOpen()
            {
            }
        }
    }
}
