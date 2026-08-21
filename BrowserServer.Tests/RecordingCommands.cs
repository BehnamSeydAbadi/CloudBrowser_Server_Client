using System.Collections.Generic;

namespace BrowserServer.Tests
{
    sealed class RecordingCommands : IBrowserClientCommands
    {
        public readonly List<string> Log = new List<string>();
        public string LastTabId;
        public MediaPermissionPayload LastMedia;
        public NotificationPermissionPayload LastNotify;
        public PwaInstallPayload LastPwa;
        public string LastText;
        public DownloadAckPayload LastDownloadAck;
        public SendKeyCommand LastKey;
        public string LastNavigate;
        public bool? LastStopBeforeBlank;
        public int LastWidth;
        public int LastHeight;
        public float LastScale;
        public ClientEnvironmentPayload LastEnvironment;
        public PointerPacket LastPointer;
        public ContextMenuActionPayload LastContextAction;
        public PwaSessionStartPayload LastPwaSession;
        public TouchKind? LastTouchKind;
        public byte[] LastBinary;

        public void CreateTab()
        {
            Log.Add("CreateTab");
        }

        public void CloseTab(string tabId)
        {
            LastTabId = tabId;
            Log.Add("CloseTab");
        }

        public void SwitchTab(string tabId)
        {
            LastTabId = tabId;
            Log.Add("SwitchTab");
        }

        public void MediaPermissionResponse(MediaPermissionPayload payload)
        {
            LastMedia = payload;
            Log.Add("MediaPermissionResponse");
        }

        public void NotificationPermissionResponse(NotificationPermissionPayload payload)
        {
            LastNotify = payload;
            Log.Add("NotificationPermissionResponse");
        }

        public void PwaInstalled(PwaInstallPayload payload)
        {
            LastPwa = payload;
            Log.Add("PwaInstalled");
        }

        public void TextInputSend(string text)
        {
            LastText = text;
            Log.Add("TextInputSend");
        }

        public void Ack()
        {
            Log.Add("Ack");
        }

        public void DownloadAck(DownloadAckPayload ack)
        {
            LastDownloadAck = ack;
            Log.Add("DownloadAck");
        }

        public void SendKey(SendKeyCommand key)
        {
            LastKey = key;
            Log.Add("SendKey");
        }

        public void Navigate(string input)
        {
            LastNavigate = input;
            Log.Add("Navigate");
        }

        public void NavigateBack(bool stopBeforeBlank)
        {
            LastStopBeforeBlank = stopBeforeBlank;
            Log.Add("NavigateBack");
        }

        public void NavigateForward()
        {
            Log.Add("NavigateForward");
        }

        public void SizeChange(int width, int height, float scale)
        {
            LastWidth = width;
            LastHeight = height;
            LastScale = scale;
            Log.Add("SizeChange");
        }

        public void ClientEnvironment(ClientEnvironmentPayload payload)
        {
            LastEnvironment = payload;
            if (payload != null)
            {
                LastWidth = payload.cssWidth;
                LastHeight = payload.cssHeight;
                LastScale = (float)payload.devicePixelRatio;
            }
            Log.Add("ClientEnvironment");
        }

        public void ContextMenuQuery(PointerPacket pointer)
        {
            LastPointer = pointer;
            Log.Add("ContextMenuQuery");
        }

        public void ContextMenuAction(ContextMenuActionPayload action)
        {
            LastContextAction = action;
            Log.Add("ContextMenuAction");
        }

        public void PwaSessionStart(PwaSessionStartPayload payload)
        {
            LastPwaSession = payload;
            Log.Add("PwaSessionStart");
        }

        public void Touch(TouchKind kind, PointerPacket pointer)
        {
            LastTouchKind = kind;
            LastPointer = pointer;
            Log.Add("Touch");
        }

        public void ClientBinary(byte[] data)
        {
            LastBinary = data;
            Log.Add("ClientBinary");
        }
    }
}
