using System;
using System.Collections.Generic;
using System.Linq;

namespace BrowserServer
{
    public enum DeviceAttachResult
    {
        Success,
        DeviceIdConflict,
        InvalidDeviceId
    }

    public static class DeviceContextHub
    {
        public const int MaxDevices = 64;

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, DeviceContext> Devices =
            new Dictionary<string, DeviceContext>(StringComparer.OrdinalIgnoreCase);

        public static bool IsValidDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return false;

            var trimmed = deviceId.Trim();
            Guid parsed;
            return Guid.TryParseExact(trimmed, "N", out parsed) || Guid.TryParse(trimmed, out parsed);
        }

        public static DeviceContext GetOrCreate(string deviceId)
        {
            var key = deviceId.Trim();
            lock (Sync)
            {
                DeviceContext device;
                if (!Devices.TryGetValue(key, out device))
                {
                    if (Devices.Count >= MaxDevices)
                        throw new InvalidOperationException("Max devices reached");
                    device = new DeviceContext(key);
                    Devices[key] = device;
                }
                return device;
            }
        }

        public static DeviceAttachResult Attach(string deviceId, ClientSession session)
        {
            if (session == null || !IsValidDeviceId(deviceId))
                return DeviceAttachResult.InvalidDeviceId;

            var key = deviceId.Trim();
            lock (Sync)
            {
                DeviceContext device;
                if (!Devices.TryGetValue(key, out device))
                {
                    if (Devices.Count >= MaxDevices)
                        return DeviceAttachResult.InvalidDeviceId;
                    device = new DeviceContext(key);
                    Devices[key] = device;
                }

                if (!string.IsNullOrEmpty(device.ActiveWebSocketSessionId)
                    && !string.Equals(device.ActiveWebSocketSessionId, session.WebSocketSessionId, StringComparison.Ordinal))
                {
                    var existingWs = device.ActiveWebSocketSessionId;
                    var existingSession = ClientSessionHub.Get(existingWs);
                    if (existingSession != null && SessionMessaging.IsSessionConnected(existingWs))
                        TakeoverExistingConnectionLocked(device, existingWs);
                    else
                        ReclaimStaleBindingLocked(device, existingWs);
                }

                device.ActiveWebSocketSessionId = session.WebSocketSessionId;
                device.RefCount++;
                device.TrackSession(session);
                session.Device = device;
                return DeviceAttachResult.Success;
            }
        }

        public static void Detach(ClientSession session)
        {
            if (session == null)
                return;

            lock (Sync)
            {
                DetachSessionLocked(session);
            }
        }

        public static DeviceContext GetByDeviceId(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return null;

            lock (Sync)
            {
                DeviceContext device;
                Devices.TryGetValue(deviceId.Trim(), out device);
                return device;
            }
        }

        public static void ResetForTests()
        {
            lock (Sync)
            {
                foreach (var device in Devices.Values)
                    device.ReleaseBrowserContext();
                Devices.Clear();
            }

            ClientSessionHub.ResetForTests();
            SessionMessaging.TestSendHook = null;
            SessionMessaging.TestIsSessionConnected = null;
        }

        /// <summary>
        /// Same device opened a new WebSocket before the old one fully disconnected.
        /// Keep the profile and replace the active connection instead of rotating DeviceId.
        /// </summary>
        private static void TakeoverExistingConnectionLocked(DeviceContext device, string existingWs)
        {
            Console.WriteLine(
                "Device {0}: replacing active connection {1} with reconnect",
                device.DeviceId,
                existingWs);

            SessionMessaging.CloseSession(existingWs, "replaced");
            ClientSessionHub.Remove(existingWs);
        }

        private static void ReclaimStaleBindingLocked(DeviceContext device, string staleWs)
        {
            var stale = ClientSessionHub.Get(staleWs);
            if (stale != null)
                ClientSessionHub.Remove(staleWs);
            else if (string.Equals(device.ActiveWebSocketSessionId, staleWs, StringComparison.Ordinal))
                device.ActiveWebSocketSessionId = null;
        }

        /// <summary>
        /// True concurrency: two live clients intentionally sharing one DeviceId.
        /// </summary>
        internal static void HandleDeviceIdConflictLocked(
            string deviceId,
            DeviceContext device,
            string existingWs,
            string incomingWs)
        {
            if (!string.IsNullOrEmpty(existingWs))
            {
                SessionMessaging.SendText(existingWs, new TextPacket
                {
                    PType = TextPacketType.RotateDeviceId
                });
            }

            if (!string.IsNullOrEmpty(incomingWs))
            {
                SessionMessaging.SendText(incomingWs, new TextPacket
                {
                    PType = TextPacketType.RotateDeviceId
                });
            }

            var existingSession = ClientSessionHub.Get(existingWs);
            var incomingSession = ClientSessionHub.Get(incomingWs);
            foreach (var attached in device.AttachedSessionsSnapshot().ToList())
                DetachSessionLocked(attached);
            DetachSessionLocked(existingSession);
            DetachSessionLocked(incomingSession);

            Devices.Remove(deviceId);
            device.QuarantineDiskProfile();
        }

        private static void DetachSessionLocked(ClientSession session)
        {
            if (session == null || session.Device == null)
                return;

            var device = session.Device;
            device.RefCount = Math.Max(0, device.RefCount - 1);
            device.UntrackSession(session);
            if (string.Equals(device.ActiveWebSocketSessionId, session.WebSocketSessionId, StringComparison.Ordinal))
                device.ActiveWebSocketSessionId = null;
            session.Device = null;

            if (device.RefCount == 0)
                device.ReleaseBrowserContext();
        }
    }
}
