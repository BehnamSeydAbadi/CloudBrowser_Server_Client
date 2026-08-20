using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BrowserServer
{
    /// <summary>
    /// JSON codec for the client↔server WebSocket text protocol
    /// (inbound <see cref="CommPacket"/>, outbound <see cref="TextPacket"/>).
    /// </summary>
    public static class WebSocketJsonProtocol
    {
        public static string EncodeCommPacket(PacketType type, string jsonData = null)
        {
            return JsonConvert.SerializeObject(new CommPacket
            {
                PType = type,
                JSONData = jsonData
            });
        }

        public static string EncodeCommPacket(PacketType type, object nestedPayload)
        {
            return EncodeCommPacket(type, nestedPayload == null ? null : JsonConvert.SerializeObject(nestedPayload));
        }

        public static CommPacket DecodeCommPacket(string json)
        {
            return JsonConvert.DeserializeObject<CommPacket>(json);
        }

        public static bool TryDecodeCommPacket(string json, out CommPacket packet)
        {
            packet = default(CommPacket);
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                packet = JsonConvert.DeserializeObject<CommPacket>(json);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        public static string EncodeTextPacket(TextPacketType type, string text = "")
        {
            return JsonConvert.SerializeObject(new TextPacket
            {
                PType = type,
                text = text ?? ""
            });
        }

        public static string EncodeTextPacket(TextPacketType type, object nestedPayload)
        {
            return EncodeTextPacket(type, nestedPayload == null ? null : JsonConvert.SerializeObject(nestedPayload));
        }

        public static TextPacket DecodeTextPacket(string json)
        {
            return JsonConvert.DeserializeObject<TextPacket>(json);
        }

        public static bool TryParseSizeChange(string jsonData, float defaultScale, out int width, out int height, out float scale)
        {
            width = 1;
            height = 1;
            scale = defaultScale;
            try
            {
                var jsonObject = JObject.Parse(jsonData ?? "{}");
                width = Math.Max(1, (int)Math.Round(jsonObject.Value<double>("Width")));
                height = Math.Max(1, (int)Math.Round(jsonObject.Value<double>("Height")));
                var scaleToken = jsonObject["Scale"];
                scale = scaleToken != null && scaleToken.Type != JTokenType.Null
                    ? (float)scaleToken.Value<double>()
                    : defaultScale;
                if (scale < 1f)
                    scale = 1f;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool TryParseSendKey(string jsonData, out SendKeyCommand command)
        {
            command = null;
            var raw = jsonData ?? "";
            try
            {
                if (raw.TrimStart().StartsWith("{"))
                {
                    var keyObj = JObject.Parse(raw);
                    var type = (keyObj.Value<string>("type") ?? "char").ToLowerInvariant();

                    if (type == "insert")
                    {
                        command = new SendKeyCommand
                        {
                            Kind = SendKeyKind.Insert,
                            Text = keyObj.Value<string>("text") ?? ""
                        };
                        return true;
                    }

                    if (type == "backspace")
                    {
                        command = new SendKeyCommand { Kind = SendKeyKind.Backspace };
                        return true;
                    }

                    if (type == "enter")
                    {
                        command = new SendKeyCommand { Kind = SendKeyKind.Enter };
                        return true;
                    }

                    command = new SendKeyCommand
                    {
                        Kind = SendKeyKind.Coded,
                        Code = keyObj.Value<int>("code"),
                        EventType = type
                    };
                    return true;
                }

                command = new SendKeyCommand
                {
                    Kind = SendKeyKind.LegacyChar,
                    Code = int.Parse(raw.Trim('"'))
                };
                return true;
            }
            catch
            {
                command = null;
                return false;
            }
        }

        public static T DeserializeNested<T>(string jsonData) where T : class
        {
            return JsonConvert.DeserializeObject<T>(jsonData ?? "");
        }
    }

    public enum SendKeyKind
    {
        Insert,
        Backspace,
        Enter,
        Coded,
        LegacyChar
    }

    public sealed class SendKeyCommand
    {
        public SendKeyKind Kind { get; set; }
        public string Text { get; set; }
        public int Code { get; set; }
        /// <summary>down, up, or char — used with <see cref="SendKeyKind.Coded"/>.</summary>
        public string EventType { get; set; }
    }

    public enum TouchKind
    {
        Down,
        Up,
        Moved
    }
}
