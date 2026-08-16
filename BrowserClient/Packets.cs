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
        MediaCaptureStop
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
        MediaPermissionResponse
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

    public class DownloadInfo
    {
        public string id;
        public string fileName;
        public long size;
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
