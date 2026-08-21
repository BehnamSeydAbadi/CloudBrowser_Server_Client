using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace BrowserServer
{
    /// <summary>Long-lived browser profile for one client device.</summary>
    public sealed class DeviceContext
    {
        private readonly object sync = new object();
        private DeviceBrowserContext browserContext;
        private Timer snapshotTimer;
        private TabManager pendingSnapshotTabs;

        public DeviceContext(string deviceId)
        {
            DeviceId = deviceId;
            FolderPath = CefPaths.GetDeviceProfilePath(deviceId);
            MigrateLegacyProfileFolder(deviceId);
            PwaInstalledOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            NotificationOrigins = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        public string DeviceId { get; private set; }
        public string FolderPath { get; private set; }
        public string ActiveWebSocketSessionId { get; set; }
        public int RefCount { get; internal set; }

        public HashSet<string> PwaInstalledOrigins { get; private set; }
        public Dictionary<string, bool> NotificationOrigins { get; private set; }
        private readonly HashSet<ClientSession> attachedSessions = new HashSet<ClientSession>();

        public string SnapshotPath
        {
            get { return Path.Combine(FolderPath, "tabs.json"); }
        }

        public DeviceBrowserContext EnsureBrowserContext()
        {
            lock (sync)
            {
                if (browserContext == null)
                    browserContext = new DeviceBrowserContext(FolderPath);
                browserContext.EnsureInitialized();
                return browserContext;
            }
        }

        public void ReleaseBrowserContext()
        {
            lock (sync)
            {
                if (browserContext != null)
                {
                    browserContext.ReleaseMemory();
                    browserContext = null;
                }
            }
        }

        public DeviceTabSnapshot LoadTabSnapshot()
        {
            return DeviceTabSnapshot.Load(SnapshotPath);
        }

        public void SaveTabSnapshot(TabManager tabs)
        {
            if (tabs == null)
                return;

            try
            {
                var snapshot = DeviceTabSnapshot.FromTabs(tabs);
                DeviceTabSnapshot.Save(SnapshotPath, snapshot);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Tab snapshot save error: " + ex.Message);
            }
        }

        public void ScheduleSaveTabSnapshot(TabManager tabs)
        {
            lock (sync)
            {
                pendingSnapshotTabs = tabs;
                if (snapshotTimer == null)
                    snapshotTimer = new Timer(_ => FlushSnapshot(), null, 500, Timeout.Infinite);
                else
                    snapshotTimer.Change(500, Timeout.Infinite);
            }
        }

        private void FlushSnapshot()
        {
            TabManager tabs;
            lock (sync)
            {
                tabs = pendingSnapshotTabs;
                pendingSnapshotTabs = null;
            }

            if (tabs != null)
                SaveTabSnapshot(tabs);
        }

        public void TrackSession(ClientSession session)
        {
            if (session != null)
                attachedSessions.Add(session);
        }

        public void UntrackSession(ClientSession session)
        {
            if (session != null)
                attachedSessions.Remove(session);
        }

        internal IEnumerable<ClientSession> AttachedSessionsSnapshot()
        {
            lock (sync)
            {
                return attachedSessions.ToList();
            }
        }

        public void QuarantineDiskProfile()
        {
            ReleaseBrowserContext();
            try
            {
                if (!Directory.Exists(FolderPath))
                    return;

                var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var quarantined = Path.Combine(
                    CefPaths.Root ?? Path.GetDirectoryName(FolderPath),
                    CefPaths.SanitizeDeviceFolderName(DeviceId) + ".conflict." + stamp);
                if (Directory.Exists(quarantined))
                    Directory.Delete(quarantined, true);
                Directory.Move(FolderPath, quarantined);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Device profile quarantine error: " + ex.Message);
            }
        }

        static void MigrateLegacyProfileFolder(string deviceId)
        {
            var legacyPath = Path.Combine(
                CefPaths.DevicesRoot,
                CefPaths.SanitizeDeviceFolderName(deviceId));
            var profilePath = CefPaths.GetDeviceProfilePath(deviceId);
            if (!Directory.Exists(legacyPath) || Directory.Exists(profilePath))
                return;

            try
            {
                Directory.Move(legacyPath, profilePath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Legacy device profile migration error: " + ex.Message);
            }
        }
    }
}
