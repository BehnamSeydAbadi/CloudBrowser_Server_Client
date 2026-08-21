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
    }
}
