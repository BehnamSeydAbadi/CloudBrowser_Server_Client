using System;
using System.IO;

namespace BrowserServer
{
    /// <summary>Shared CefSharp disk paths (set once in Program.Main before Cef.Initialize).</summary>
    public static class CefPaths
    {
        public static string Root { get; set; }

        public static string SessionsRoot
        {
            get { return Path.Combine(Root ?? "", "Sessions"); }
        }

        /// <summary>Legacy nested layout — not valid for CEF RequestContext cache paths.</summary>
        public static string DevicesRoot
        {
            get { return Path.Combine(Root ?? "", "Devices"); }
        }

        public static string SanitizeSessionFolderName(string sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return Guid.NewGuid().ToString("N");

            var safe = sessionId.Trim();
            foreach (var c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');

            if (safe.Length > 64)
                safe = safe.Substring(0, 64);

            return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
        }

        public static string SanitizeDeviceFolderName(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
                return Guid.NewGuid().ToString("N");

            var safe = deviceId.Trim().ToLowerInvariant();
            foreach (var c in Path.GetInvalidFileNameChars())
                safe = safe.Replace(c, '_');

            if (safe.Length > 64)
                safe = safe.Substring(0, 64);

            return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
        }

        /// <summary>
        /// CEF RequestContext cache paths must be a direct child of RootCachePath
        /// (not nested under Devices/ or other subfolders).
        /// </summary>
        public static string GetDeviceProfilePath(string deviceId)
        {
            return Path.Combine(Root ?? "", SanitizeDeviceFolderName(deviceId));
        }

        public static bool IsDirectChildOfRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(Root) || string.IsNullOrWhiteSpace(path))
                return false;

            try
            {
                var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var parent = Path.GetDirectoryName(full);
                return string.Equals(parent, root, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
