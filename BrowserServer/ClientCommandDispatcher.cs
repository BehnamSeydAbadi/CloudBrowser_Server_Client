using System;
using Newtonsoft.Json;

namespace BrowserServer
{
    /// <summary>
    /// Side effects for an inbound client WebSocket message.
    /// Browser/CefSharp work stays in the host; this only routes parsed JSON.
    /// </summary>
    public interface IBrowserClientCommands
    {
        void CreateTab();
        void CloseTab(string tabId);
        void SwitchTab(string tabId);
        void MediaPermissionResponse(MediaPermissionPayload payload);
        void NotificationPermissionResponse(NotificationPermissionPayload payload);
        void PwaInstalled(PwaInstallPayload payload);
        void TextInputSend(string text);
        void Ack();
        void DownloadAck(DownloadAckPayload ack);
        void SendKey(SendKeyCommand key);
        void Navigate(string input);
        void NavigateBack(bool stopBeforeBlank);
        void NavigateForward();
        void SizeChange(int width, int height, float scale);
        void ClientEnvironment(ClientEnvironmentPayload payload);
        void Touch(TouchKind kind, PointerPacket pointer);
        void ClientBinary(byte[] data);
    }

    public enum DispatchResult
    {
        Handled,
        IgnoredNoBrowser,
        IgnoredUnknown,
        IgnoredMalformed
    }

    /// <summary>
    /// Parses inbound WebSocket JSON/binary and invokes <see cref="IBrowserClientCommands"/>.
    /// </summary>
    public static class ClientCommandDispatcher
    {
        public static DispatchResult DispatchBinary(byte[] data, IBrowserClientCommands commands)
        {
            if (commands == null)
                throw new ArgumentNullException("commands");
            commands.ClientBinary(data);
            return DispatchResult.Handled;
        }

        public static DispatchResult DispatchText(string json, bool hasActiveBrowser, float defaultScale, IBrowserClientCommands commands)
        {
            if (commands == null)
                throw new ArgumentNullException("commands");

            CommPacket packet;
            if (!WebSocketJsonProtocol.TryDecodeCommPacket(json, out packet))
                return DispatchResult.IgnoredMalformed;

            if (!hasActiveBrowser
                && packet.PType != PacketType.CreateTab
                && packet.PType != PacketType.ClientEnvironment)
                return DispatchResult.IgnoredNoBrowser;

            switch (packet.PType)
            {
                case PacketType.CreateTab:
                    commands.CreateTab();
                    return DispatchResult.Handled;

                case PacketType.CloseTab:
                    if (!string.IsNullOrEmpty(packet.JSONData))
                        commands.CloseTab(packet.JSONData);
                    return DispatchResult.Handled;

                case PacketType.SwitchTab:
                    if (!string.IsNullOrEmpty(packet.JSONData))
                        commands.SwitchTab(packet.JSONData);
                    return DispatchResult.Handled;

                case PacketType.MediaPermissionResponse:
                    try
                    {
                        commands.MediaPermissionResponse(
                            WebSocketJsonProtocol.DeserializeNested<MediaPermissionPayload>(packet.JSONData));
                    }
                    catch
                    {
                    }
                    return DispatchResult.Handled;

                case PacketType.NotificationPermissionResponse:
                    try
                    {
                        commands.NotificationPermissionResponse(
                            WebSocketJsonProtocol.DeserializeNested<NotificationPermissionPayload>(packet.JSONData));
                    }
                    catch
                    {
                    }
                    return DispatchResult.Handled;

                case PacketType.PwaInstalled:
                    try
                    {
                        var pwa = WebSocketJsonProtocol.DeserializeNested<PwaInstallPayload>(packet.JSONData);
                        commands.PwaInstalled(pwa);
                    }
                    catch
                    {
                    }
                    return DispatchResult.Handled;

                case PacketType.TextInputSend:
                    commands.TextInputSend(packet.JSONData);
                    return DispatchResult.Handled;

                case PacketType.ACK:
                    commands.Ack();
                    return DispatchResult.Handled;

                case PacketType.DownloadAck:
                    try
                    {
                        var ack = WebSocketJsonProtocol.DeserializeNested<DownloadAckPayload>(packet.JSONData);
                        if (ack != null)
                            commands.DownloadAck(ack);
                    }
                    catch
                    {
                    }
                    return DispatchResult.Handled;

                case PacketType.SendKey:
                    SendKeyCommand key;
                    if (WebSocketJsonProtocol.TryParseSendKey(packet.JSONData, out key))
                        commands.SendKey(key);
                    return DispatchResult.Handled;

                case PacketType.Navigation:
                    commands.Navigate((packet.JSONData ?? "").Trim());
                    return DispatchResult.Handled;

                case PacketType.NavigateBack:
                    commands.NavigateBack(string.Equals(packet.JSONData, "stopBeforeBlank", StringComparison.Ordinal));
                    return DispatchResult.Handled;

                case PacketType.NavigateForward:
                    commands.NavigateForward();
                    return DispatchResult.Handled;

                case PacketType.SizeChange:
                    int width, height;
                    float scale;
                    if (WebSocketJsonProtocol.TryParseSizeChange(packet.JSONData, defaultScale, out width, out height, out scale))
                        commands.SizeChange(width, height, scale);
                    else
                        return DispatchResult.IgnoredMalformed;
                    return DispatchResult.Handled;

                case PacketType.ClientEnvironment:
                    try
                    {
                        var env = WebSocketJsonProtocol.DeserializeNested<ClientEnvironmentPayload>(packet.JSONData);
                        commands.ClientEnvironment(env);
                    }
                    catch
                    {
                        return DispatchResult.IgnoredMalformed;
                    }
                    return DispatchResult.Handled;

                case PacketType.TouchDown:
                    commands.Touch(TouchKind.Down, JsonConvert.DeserializeObject<PointerPacket>(packet.JSONData ?? "{}"));
                    return DispatchResult.Handled;

                case PacketType.TouchUp:
                    commands.Touch(TouchKind.Up, JsonConvert.DeserializeObject<PointerPacket>(packet.JSONData ?? "{}"));
                    return DispatchResult.Handled;

                case PacketType.TouchMoved:
                    commands.Touch(TouchKind.Moved, JsonConvert.DeserializeObject<PointerPacket>(packet.JSONData ?? "{}"));
                    return DispatchResult.Handled;

                default:
                    return DispatchResult.IgnoredUnknown;
            }
        }
    }
}
