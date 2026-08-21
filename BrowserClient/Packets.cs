using System;
using System.Collections.Generic;

namespace BrowserClient
{
    public struct PointerPacket
    {
        public double px;
        public double py;
        public uint id;
    }

    public struct TextPacket
    {
        public TextPacketType PType;
        public string text;
    }

    public struct CommPacket
    {
        public PacketType PType;
        public string JSONData;
    }

    public enum TextPacketType
    {
        NavigatedUrl,
        TextInputContent,
        TextInputSend,
        TextInputCancel,
        TabList,
        AudioStop,
        /// <summary>NavigateBack was blocked because the previous entry is about:blank / history root.</summary>
        AtHistoryRoot,
        DownloadStarted,
        DownloadProgress,
        DownloadCompleted,
        /// <summary>Site wants camera/mic — JSON MediaPermissionPayload.</summary>
        MediaPermissionRequest,
        /// <summary>Page released media tracks — stop phone capture.</summary>
        MediaCaptureStop,
        /// <summary>Already capturing — add mic/camera without a new prompt.</summary>
        MediaCaptureUpgrade,
        /// <summary>QR payload decoded from the phone camera (plain text).</summary>
        QrDetected,
        /// <summary>Site Notification API — JSON NotificationPayload.</summary>
        Notification,
        /// <summary>Site wants notification permission — JSON NotificationPermissionPayload.</summary>
        NotificationPermissionRequest,
        /// <summary>Context menu offer after long-press — JSON ContextMenuOfferPayload.</summary>
        ContextMenu
    }

    public enum PacketType
    {
        Navigation,
        SizeChange,
        TouchDown,
        TouchUp,
        TouchMoved,
        ACK,
        Frame,
        TextInputSend,
        NavigateForward,
        NavigateBack,
        SendKey,
        CreateTab,
        CloseTab,
        SwitchTab,
        /// <summary>Client ACK for a received FILE chunk (JSON: id, seq).</summary>
        DownloadAck,
        /// <summary>Phone Allow/Deny for MediaPermissionRequest.</summary>
        MediaPermissionResponse,
        /// <summary>Phone Allow/Deny for NotificationPermissionRequest.</summary>
        NotificationPermissionResponse,
        /// <summary>Origins pinned via Add to Home — treat as installed PWAs.</summary>
        PwaInstalled,
        /// <summary>Phone display/locale environment sent on connect.</summary>
        ClientEnvironment,
        /// <summary>Long-press hit-test at normalized coords — JSON PointerPacket.</summary>
        ContextMenuQuery,
        /// <summary>Open tab or save image — JSON ContextMenuActionPayload.</summary>
        ContextMenuAction
    }

    public class ContextMenuOfferPayload
    {
        public string linkUrl;
        public string imageUrl;
        public string text;
    }

    public class ContextMenuActionPayload
    {
        public string action;
        public string url;
    }

    public class ClientEnvironmentPayload
    {
        public int cssWidth;
        public int cssHeight;
        public double devicePixelRatio;
        public bool isMobile;
        public string acceptLanguage;
        public int screenWidth;
        public int screenHeight;
        public string colorScheme;
        public string timeZone;
        public int utcOffsetMinutes;
        public string orientation;
    }

    public class MediaPermissionPayload
    {
        public string requestId;
        public string origin;
        public bool audio;
        public bool video;
        public bool allowed;
    }

    public class TabInfo
    {
        public string id;
        public string title;
        public string url;
    }

    public class TabListPayload
    {
        public string activeId;
        public List<TabInfo> tabs;
    }

    public class DownloadEventPayload
    {
        public string id;
        public string fileName;
        public long totalBytes;
        public long receivedBytes;
        public int percent;
        public string mimeType;
        public bool success;
        public string error;
    }

    public class DownloadAckPayload
    {
        public string id;
        public int seq;
    }

    public class NotificationPayload
    {
        public string title;
        public string body;
        public string tag;
        public string origin;
        public string icon;
        public string url;
    }

    public class NotificationPermissionPayload
    {
        public string requestId;
        public string origin;
        public bool allowed;
    }

    public class PwaInstallPayload
    {
        public List<string> urls;
        public bool reload;
    }

    public class DownloadInfo
    {
        public string id;
        public string fileName;
        public long size;
        public string mimeType;
        public string status; // downloading | transferring | completed | failed
        public int percent;
        public string accessToken;
        public string completedUtc;
        public string error;
    }

    public struct DiscoveryPacket
    {
        public DiscoveryPacketType PType;
        public string ServerAddress;
    }
    public enum DiscoveryPacketType
    {
        AddressRequest,
        ACK
    }
}
