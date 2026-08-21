using System;
using System.IO;
using CefSharp;

namespace BrowserServer
{
    /// <summary>Per-device CefSharp request context — isolates cookies/storage between devices.</summary>
    public sealed class DeviceBrowserContext
    {
        private readonly string cachePath;
        private IRequestContext context;
        private bool initialized;

        public DeviceBrowserContext(string cachePath)
        {
            this.cachePath = cachePath ?? "";
        }

        public string CachePath
        {
            get { return cachePath; }
        }

        public IRequestContext Context
        {
            get
            {
                EnsureInitialized();
                return context;
            }
        }

        public void EnsureInitialized()
        {
            if (initialized)
                return;

            initialized = true;
            if (!CefRuntime.IsReady || string.IsNullOrEmpty(cachePath))
                return;

            if (!CefPaths.IsDirectChildOfRoot(cachePath))
            {
                Console.WriteLine(
                    "Device profile path must be a direct child of Cef RootCachePath: {0}",
                    cachePath);
                return;
            }

            RemoveEmptyPreCreatedProfileDir(cachePath);

            context = new RequestContext(new RequestContextSettings
            {
                CachePath = cachePath,
                PersistSessionCookies = true
            });
        }

        public void ReleaseMemory()
        {
            try
            {
                if (context != null)
                {
                    context.Dispose();
                    context = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Device request context release error: " + ex.Message);
            }

            initialized = false;
        }

        static void RemoveEmptyPreCreatedProfileDir(string path)
        {
            if (!Directory.Exists(path))
                return;

            try
            {
                var entries = Directory.GetFileSystemEntries(path);
                if (entries.Length == 0)
                {
                    Directory.Delete(path, recursive: false);
                    return;
                }

                if (entries.Length == 1 &&
                    string.Equals(Path.GetFileName(entries[0]), "tabs.json", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(entries[0]);
                    Directory.Delete(path, recursive: false);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Device profile cleanup error: " + ex.Message);
            }
        }
    }

    /// <summary>Avoids loading CefSharp native DLLs when running unit tests.</summary>
    static class CefRuntime
    {
        public static bool IsReady
        {
            get
            {
                try
                {
                    return Cef.IsInitialized == true;
                }
                catch (FileNotFoundException)
                {
                    return false;
                }
                catch (DllNotFoundException)
                {
                    return false;
                }
            }
        }
    }
}
