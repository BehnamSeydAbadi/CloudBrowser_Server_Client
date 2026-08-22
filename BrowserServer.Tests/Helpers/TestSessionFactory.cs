using System;
using System.Collections.Generic;
using BrowserServer;

namespace BrowserServer.Tests.Helpers
{
    public static class TestSessionFactory
    {
        public static ClientSession CreateWithDevice(string webSocketId, string tabId)
        {
            ResetAll();
            var session = ClientSessionHub.Create(webSocketId);
            if (session == null)
                throw new InvalidOperationException("Failed to create session");

            DeviceContextHub.Attach(Guid.NewGuid().ToString("N"), session);
            ClientSessionHub.RegisterTab(tabId, session);
            return session;
        }

        public static List<TextPacket> CaptureOutbound(string webSocketId)
        {
            var captured = new List<TextPacket>();
            SessionMessaging.TestSendHook = (sessionId, packet) =>
            {
                if (string.Equals(sessionId, webSocketId, StringComparison.Ordinal))
                    captured.Add(packet);
            };
            return captured;
        }

        public static void ResetAll()
        {
            DeviceContextHub.ResetForTests();
            QrScanService.Reset();
            SessionMessaging.TestSendHook = null;
            SessionMessaging.TestIsSessionConnected = null;
        }
    }
}
