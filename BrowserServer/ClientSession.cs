using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace BrowserServer
{
    /// <summary>Isolated browser state for one WebSocket client.</summary>
    public sealed class ClientSession : IDisposable
    {
        public string WebSocketSessionId { get; private set; }
        public TabManager Tabs { get; private set; }
        public RenderFrameSession FrameSession { get; private set; }
        public ClientEnvironmentPayload Environment { get; set; }
        public string AcceptLanguage { get; set; } = "en-US,en";
        public HashSet<string> PwaInstalledOrigins { get; private set; }
        public Dictionary<string, bool> NotificationOrigins { get; private set; }
        private SessionRequestContext browserContext;

        public ClientSession(string webSocketSessionId)
        {
            WebSocketSessionId = webSocketSessionId;
            FrameSession = new RenderFrameSession();
            PwaInstalledOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            NotificationOrigins = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            Tabs = new TabManager(this);
        }

        internal SessionRequestContext EnsureBrowserContext()
        {
            if (browserContext == null)
                browserContext = new SessionRequestContext(WebSocketSessionId);
            browserContext.EnsureInitialized();
            return browserContext;
        }

        public void SendText(TextPacketType type, string text = null)
        {
            SendText(new TextPacket { PType = type, text = text ?? "" });
        }

        public void SendText(TextPacket packet)
        {
            SessionMessaging.SendText(WebSocketSessionId, packet);
        }

        public void SendBinary(byte[] data)
        {
            SessionMessaging.SendBinary(WebSocketSessionId, data);
        }

        public void SendTabList()
        {
            Tabs.SendTabList();
        }

        public void SendNavigatedUrl(string url)
        {
            Tabs.SendNavigatedUrl(url);
        }

        public void ResetCaptureState()
        {
            FrameSession.Reset();
        }

        public void Dispose()
        {
            try
            {
                Tabs.DisposeAll();
            }
            catch
            {
            }

            try
            {
                MediaBridge.ReleaseSession(this);
            }
            catch
            {
            }

            try
            {
                StreamingDownloadHandler.ReleaseSession(this);
            }
            catch
            {
            }

            try
            {
                if (browserContext != null)
                {
                    browserContext.Dispose();
                    browserContext = null;
                }
            }
            catch
            {
            }
        }
    }
}
