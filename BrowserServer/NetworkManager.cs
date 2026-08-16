using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BrowserServer
{
    public static class NetworkManager
    {
        //UDP discovery
        static UdpClient receivingClient;
        static UdpClient sendingClient;
        static Thread udpReciving;
        const int udpDiscoveryPort = 54545;
        const int udpSendDiscoveryPort = 54546;
        const string broadcastAddress = "255.255.255.255";

        delegate void AddMessage(string message);

        public static void StartUdpDiscoveryServer()
        {
            receivingClient = new UdpClient(udpDiscoveryPort);
            ThreadStart start = new ThreadStart(UdpDiscoveryReciver);
            udpReciving = new Thread(start);
            udpReciving.IsBackground = true;
            udpReciving.Start();


            sendingClient = new UdpClient(broadcastAddress, 1337);
            sendingClient.EnableBroadcast = true;

        }
        private static void UdpDiscoveryReciver()
        {
            IPEndPoint endPoint = new IPEndPoint(IPAddress.Any, udpDiscoveryPort);
            AddMessage messageDelegate = UdpMessageRecived;
            while (true)
            {
                byte[] data = receivingClient.Receive(ref endPoint);
                string message = Encoding.ASCII.GetString(data);
                UdpMessageRecived(message);
            }
        }
        private static void UdpMessageRecived(string packetJSON)
        {
            try
            {
                var udpPacket = JsonConvert.DeserializeObject<DiscoveryPacket>(packetJSON);
                switch (udpPacket.PType)
                {
                    case DiscoveryPacketType.AddressRequest:
                         Console.WriteLine("request addr");
                        // byte[] data = Encoding.ASCII.GetBytes("hallo");
                        // sendingClient.Send(data, data.Length);


                        var packet = new DiscoveryPacket
                        {
                            PType = DiscoveryPacketType.ACK,
                            ServerAddress = GetLocalIPAddress()
                        };
                        var rawPacket = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(packet));
                        sendingClient.Send(rawPacket, rawPacket.Length);

                        break;

                    case DiscoveryPacketType.ACK:
                        break;
                    default:
                        break;
                }
            }
            catch (Exception){}
            
        }
        //UDP discovery

        //helpers
        public static string GetLocalIPAddress()
        {
            var host = Dns.GetHostEntry(Dns.GetHostName());
            foreach (var ip in host.AddressList)
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip.ToString();
                }
            }
            throw new Exception("No network adapters with an IPv4 address in the system!");
        }

        /// <summary>
        /// Returns true when <paramref name="input"/> should be loaded as a page URL.
        /// Outputs an absolute http(s) URL (adds https:// when the scheme is missing).
        /// </summary>
        public static bool TryGetNavigableUrl(string input, out string url)
        {
            url = null;
            if (string.IsNullOrWhiteSpace(input))
                return false;

            input = input.Trim();

            if (Uri.TryCreate(input, UriKind.Absolute, out var absolute)
                && IsHttpScheme(absolute))
            {
                url = absolute.AbsoluteUri;
                return true;
            }

            // "example.com", "www.example.com/path", "192.168.0.1", "localhost:8081"
            if (!LooksLikeHostOrUrl(input))
                return false;

            if (Uri.TryCreate("https://" + input, UriKind.Absolute, out var withHttps)
                && IsHttpScheme(withHttps)
                && !string.IsNullOrEmpty(withHttps.Host))
            {
                url = withHttps.AbsoluteUri;
                return true;
            }

            return false;
        }

        [Obsolete("Use TryGetNavigableUrl")]
        public static bool IsUrl(string s)
        {
            return TryGetNavigableUrl(s, out _);
        }

        private static bool IsHttpScheme(Uri uri)
        {
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }

        private static bool LooksLikeHostOrUrl(string s)
        {
            if (s.IndexOf(' ') >= 0)
                return false;

            var hostPart = s;
            var slash = s.IndexOf('/');
            if (slash >= 0)
                hostPart = s.Substring(0, slash);

            var colon = hostPart.LastIndexOf(':');
            if (colon > 0 && colon < hostPart.Length - 1)
            {
                var portText = hostPart.Substring(colon + 1);
                if (int.TryParse(portText, out _))
                    hostPart = hostPart.Substring(0, colon);
            }

            if (hostPart.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            if (IPAddress.TryParse(hostPart, out _))
                return true;

            // Require at least one dot (example.com) and a plausible TLD-ish label.
            return Regex.IsMatch(
                hostPart,
                @"^(?i)([a-z0-9]([a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,}$");
        }
        //helpers
    }
}
