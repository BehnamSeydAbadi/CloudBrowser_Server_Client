using System;
using Newtonsoft.Json;
using WebSocketSharp.Server;

namespace BrowserServer
{
    /// <summary>WebSocket unicast helpers — replaces Broadcast for per-client traffic.</summary>
    public static class SessionMessaging
    {
        public const string ServicePath = "/";

        public static WebSocketServer Server { get; set; }

        /// <summary>Test-only hook to observe outbound text packets.</summary>
        public static Action<string, TextPacket> TestSendHook { get; set; }

        /// <summary>Test-only override for live WebSocket session checks.</summary>
        public static Func<string, bool> TestIsSessionConnected { get; set; }

        public static bool SendText(string sessionId, TextPacket packet)
        {
            TestSendHook?.Invoke(sessionId, packet);

            if (string.IsNullOrEmpty(sessionId) || Server == null)
                return false;

            return SendRaw(sessionId, JsonConvert.SerializeObject(packet));
        }

        public static bool SendRaw(string sessionId, string json)
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(json) || Server == null)
                return false;

            try
            {
                var host = Server.WebSocketServices[ServicePath];
                if (host == null)
                    return false;
                host.Sessions.SendTo(json, sessionId);
                return true;
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine("SendText failed session={0}: {1}", sessionId, ex.Message);
                return false;
            }
        }

        public static bool SendBinary(string sessionId, byte[] packet)
        {
            if (string.IsNullOrEmpty(sessionId) || packet == null || packet.Length == 0 || Server == null)
                return false;

            try
            {
                var host = Server.WebSocketServices[ServicePath];
                if (host == null)
                    return false;
                host.Sessions.SendTo(packet, sessionId);
                return true;
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine("SendBinary failed session={0}: {1}", sessionId, ex.Message);
                return false;
            }
        }

        public static void CloseSession(string sessionId, string reason = "replaced")
        {
            if (string.IsNullOrEmpty(sessionId) || Server == null)
                return;

            try
            {
                var host = Server.WebSocketServices[ServicePath];
                if (host == null)
                    return;
                host.Sessions.CloseSession(sessionId, WebSocketSharp.CloseStatusCode.Normal, reason);
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine("CloseSession failed session={0}: {1}", sessionId, ex.Message);
            }
        }

        public static bool IsSessionConnected(string sessionId)
        {
            if (string.IsNullOrEmpty(sessionId))
                return false;

            if (TestIsSessionConnected != null)
                return TestIsSessionConnected(sessionId);

            if (Server == null)
                return false;

            try
            {
                var host = Server.WebSocketServices[ServicePath];
                if (host == null)
                    return false;

                foreach (var id in host.Sessions.IDs)
                {
                    if (string.Equals(id, sessionId, StringComparison.Ordinal))
                        return true;
                }
            }
            catch
            {
            }

            return false;
        }
    }
}
