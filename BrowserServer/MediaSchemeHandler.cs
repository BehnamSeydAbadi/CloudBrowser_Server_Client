using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CefSharp;
using CefSharp.Callback;

namespace BrowserServer
{
    public sealed class MediaSchemeHandlerFactory : ISchemeHandlerFactory
    {
        public IResourceHandler Create(IBrowser browser, IFrame frame, string schemeName, IRequest request)
        {
            return new MediaResourceHandler(browser);
        }
    }

    /// <summary>
    /// Serves cbmedia://local/... so pages can reach MediaBridge without CefSharp JS binding.
    /// </summary>
    public sealed class MediaResourceHandler : ResourceHandler
    {
        private readonly IBrowser browser;

        public MediaResourceHandler(IBrowser browser)
        {
            this.browser = browser;
        }

        public override CefReturnValue ProcessRequestAsync(IRequest request, ICallback callback)
        {
            var url = request.Url ?? "";
            Task.Run(async () =>
            {
                try
                {
                    using (callback)
                    {
                        var uri = new Uri(url);
                        var path = (uri.AbsolutePath ?? "/").TrimEnd('/').ToLowerInvariant();
                        if (string.IsNullOrEmpty(path))
                            path = "/";

                        var query = ParseQuery(uri.Query);
                        byte[] body;
                        string mime = "application/json";
                        int status = 200;

                        if (path == "/request" || path.EndsWith("/request"))
                        {
                            bool audio = IsTruthy(GetQuery(query, "audio"));
                            bool video = IsTruthy(GetQuery(query, "video"));
                            string origin = GetQuery(query, "origin") ?? "";
                            string tabId = MediaBridge.FindTabId(browser) ?? "";

                            Console.WriteLine("cbmedia /request audio={0} video={1} origin={2}", audio, video, origin);
                            bool ok = await MediaBridge.RequestAccessAsync(tabId, audio, video, origin).ConfigureAwait(false);
                            body = Encoding.UTF8.GetBytes("{\"ok\":" + (ok ? "true" : "false") + "}");
                        }
                        else if (path == "/video" || path.EndsWith("/video"))
                        {
                            var jpeg = MediaBridge.GetLatestJpegCopy();
                            if (jpeg == null || jpeg.Length == 0)
                            {
                                status = 204;
                                body = new byte[0];
                                mime = "image/jpeg";
                            }
                            else
                            {
                                body = jpeg;
                                mime = "image/jpeg";
                            }
                        }
                        else if (path == "/audio" || path.EndsWith("/audio"))
                        {
                            var pcm = MediaBridge.DequeueAudioChunk();
                            if (pcm == null || pcm.Length == 0)
                            {
                                status = 204;
                                body = new byte[0];
                                mime = "application/octet-stream";
                            }
                            else
                            {
                                body = pcm;
                                mime = "application/octet-stream";
                            }
                        }
                        else if (path == "/release" || path.EndsWith("/release"))
                        {
                            MediaBridge.Release(MediaBridge.FindTabId(browser));
                            body = Encoding.UTF8.GetBytes("{\"ok\":true}");
                        }
                        else
                        {
                            status = 404;
                            body = Encoding.UTF8.GetBytes("{\"error\":\"not found\"}");
                        }

                        Stream = new MemoryStream(body);
                        MimeType = mime;
                        StatusCode = status;
                        Headers["Access-Control-Allow-Origin"] = "*";
                        Headers["Cache-Control"] = "no-store";
                        AutoDisposeStream = true;
                        callback.Continue();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("cbmedia handler error: " + ex.Message);
                    try
                    {
                        using (callback)
                        {
                            Stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"ok\":false}"));
                            MimeType = "application/json";
                            StatusCode = 500;
                            AutoDisposeStream = true;
                            callback.Continue();
                        }
                    }
                    catch
                    {
                    }
                }
            });

            return CefReturnValue.ContinueAsync;
        }

        private static bool IsTruthy(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;
            return value == "1" ||
                   value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                   value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetQuery(Dictionary<string, string> query, string key)
        {
            string value;
            return query != null && query.TryGetValue(key, out value) ? value : null;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query))
                return result;
            if (query[0] == '?')
                query = query.Substring(1);
            foreach (var part in query.Split('&'))
            {
                if (string.IsNullOrEmpty(part))
                    continue;
                var eq = part.IndexOf('=');
                if (eq < 0)
                {
                    result[Uri.UnescapeDataString(part)] = "";
                    continue;
                }
                var key = Uri.UnescapeDataString(part.Substring(0, eq));
                var val = Uri.UnescapeDataString(part.Substring(eq + 1));
                result[key] = val;
            }
            return result;
        }
    }
}
