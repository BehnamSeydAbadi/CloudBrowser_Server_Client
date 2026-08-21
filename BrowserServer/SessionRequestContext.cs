using System;
using System.IO;
using CefSharp;

namespace BrowserServer
{
    /// <summary>Per-client CefSharp request context — isolates cookies/storage between WebSocket sessions.</summary>
    public sealed class SessionRequestContext : IDisposable
    {
        private readonly string cachePath;
        private IRequestContext context;
        private bool initialized;

        public SessionRequestContext(string webSocketSessionId)
        {
            cachePath = Path.Combine(
                CefPaths.SessionsRoot,
                CefPaths.SanitizeSessionFolderName(webSocketSessionId));

            Directory.CreateDirectory(cachePath);
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
            if (!CefRuntime.IsReady)
                return;

            context = new RequestContext(new RequestContextSettings
            {
                CachePath = cachePath,
                PersistSessionCookies = true
            });
        }

        public void Dispose()
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
                Console.WriteLine("Session request context dispose error: " + ex.Message);
            }

            try
            {
                if (!string.IsNullOrEmpty(cachePath) && Directory.Exists(cachePath))
                    Directory.Delete(cachePath, recursive: true);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Session cache delete error: " + ex.Message);
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
