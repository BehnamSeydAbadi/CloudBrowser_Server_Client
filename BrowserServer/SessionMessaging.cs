using Newtonsoft.Json;
using WebSocketSharp.Server;

namespace BrowserServer
{
    /// <summary>WebSocket unicast helpers — replaces Broadcast for per-client traffic.</summary>
    public static class SessionMessaging
    {
        public const string ServicePath = "/";

        public static WebSocketServer Server { get; set; }

        public static bool SendText(string sessionId, TextPacket packet)
        {
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
    }
}
