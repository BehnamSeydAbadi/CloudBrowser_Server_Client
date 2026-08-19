using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.Storage;

namespace BrowserClient
{
    public class HistoryItem
    {
        public string url;
        public string title;
        public string visitedUtc;
        public int visitCount;
    }

    /// <summary>
    /// Local visit history for URL-bar suggestions and the History overlay.
    /// </summary>
    public sealed class HistoryStore
    {
        private const string IndexFileName = "history_index.json";
        private const int MaxItems = 500;
        private const int SuggestLimit = 6;

        private readonly object sync = new object();
        private readonly Dictionary<string, HistoryItem> items =
            new Dictionary<string, HistoryItem>(StringComparer.OrdinalIgnoreCase);
        private bool loaded;
        private int saveQueued;

        public async Task EnsureLoadedAsync()
        {
            if (loaded)
                return;

            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(IndexFileName, CreationCollisionOption.OpenIfExists);
                var json = await FileIO.ReadTextAsync(file);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    var list = JsonConvert.DeserializeObject<List<HistoryItem>>(json) ?? new List<HistoryItem>();
                    lock (sync)
                    {
                        items.Clear();
                        foreach (var item in list)
                        {
                            var key = NormalizeUrl(item != null ? item.url : null);
                            if (string.IsNullOrEmpty(key))
                                continue;
                            item.url = key;
                            if (item.visitCount < 1)
                                item.visitCount = 1;
                            items[key] = item;
                        }
                    }
                }
            }
            catch
            {
            }

            loaded = true;
        }

        public void Record(string url, string title, bool countVisit = true)
        {
            var key = NormalizeUrl(url);
            if (string.IsNullOrEmpty(key))
                return;

            var now = DateTime.UtcNow.ToString("o");
            var cleanTitle = string.IsNullOrWhiteSpace(title) ? null : title.Trim();

            lock (sync)
            {
                HistoryItem item;
                if (items.TryGetValue(key, out item))
                {
                    if (countVisit)
                    {
                        item.visitCount = Math.Max(1, item.visitCount) + 1;
                        item.visitedUtc = now;
                    }
                    if (!string.IsNullOrEmpty(cleanTitle) &&
                        !string.Equals(cleanTitle, "New Tab", StringComparison.OrdinalIgnoreCase))
                        item.title = cleanTitle;
                }
                else
                {
                    items[key] = new HistoryItem
                    {
                        url = key,
                        title = cleanTitle ?? HostLabel(key),
                        visitedUtc = now,
                        visitCount = 1
                    };
                }

                TrimUnlocked();
            }

            QueueSave();
        }

        public bool Remove(string url)
        {
            var key = NormalizeUrl(url);
            if (string.IsNullOrEmpty(key))
                key = (url ?? "").Trim();
            if (string.IsNullOrEmpty(key))
                return false;

            bool removed;
            lock (sync)
            {
                removed = items.Remove(key);
                if (!removed)
                {
                    var match = items.Keys.FirstOrDefault(k =>
                        string.Equals(k, url, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        removed = items.Remove(match);
                }
            }

            if (removed)
                QueueSave();
            return removed;
        }

        public void Clear()
        {
            lock (sync)
            {
                items.Clear();
            }
            QueueSave();
        }

        public List<HistoryItem> GetSnapshot()
        {
            lock (sync)
            {
                return items.Values
                    .OrderByDescending(i => i.visitedUtc ?? "")
                    .Select(Clone)
                    .ToList();
            }
        }

        public List<HistoryItem> Suggest(string query)
        {
            lock (sync)
            {
                IEnumerable<HistoryItem> source = items.Values;
                var needle = (query ?? "").Trim();
                if (needle.Length > 0)
                {
                    source = source
                        .Select(i => new { Item = i, Score = Score(i, needle) })
                        .Where(x => x.Score > 0)
                        .OrderByDescending(x => x.Score)
                        .ThenByDescending(x => x.Item.visitCount)
                        .ThenByDescending(x => x.Item.visitedUtc ?? "")
                        .Select(x => x.Item);
                }
                else
                {
                    source = source
                        .OrderByDescending(i => i.visitedUtc ?? "")
                        .ThenByDescending(i => i.visitCount);
                }

                return source.Take(SuggestLimit).Select(Clone).ToList();
            }
        }

        public static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            url = url.Trim();
            if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("chrome:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("wss://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("cbmedia:", StringComparison.OrdinalIgnoreCase))
                return null;

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                if (!Uri.TryCreate("http://" + url, UriKind.Absolute, out uri))
                    return null;
            }

            var scheme = uri.Scheme ?? "";
            if (!string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase))
                return null;

            var host = uri.Host;
            if (string.IsNullOrEmpty(host))
                return null;

            scheme = scheme.ToLowerInvariant();
            var port = uri.IsDefaultPort ? -1 : uri.Port;
            var path = uri.AbsolutePath ?? "/";
            var query = uri.Query ?? "";

            var result = scheme + "://" + host;
            if (port > 0)
                result += ":" + port;
            if (path == "/" && query.Length == 0)
                return result;

            result += path;
            if (query.Length > 0)
                result += query;
            return result;
        }

        private static int Score(HistoryItem item, string query)
        {
            if (item == null)
                return 0;

            var q = query.Trim();
            if (q.Length == 0)
                return 1;

            var url = item.url ?? "";
            var title = item.title ?? "";
            var host = HostLabel(url);
            var qLower = q.ToLowerInvariant();
            var urlLower = url.ToLowerInvariant();
            var titleLower = title.ToLowerInvariant();
            var hostLower = host.ToLowerInvariant();

            if (hostLower.StartsWith(qLower, StringComparison.Ordinal) ||
                hostLower.StartsWith("www." + qLower, StringComparison.Ordinal))
                return 400 + item.visitCount;
            if (titleLower.StartsWith(qLower, StringComparison.Ordinal))
                return 300 + item.visitCount;
            if (urlLower.StartsWith(qLower, StringComparison.Ordinal) ||
                urlLower.StartsWith("https://" + qLower, StringComparison.Ordinal) ||
                urlLower.StartsWith("http://" + qLower, StringComparison.Ordinal))
                return 250 + item.visitCount;
            if (hostLower.IndexOf(qLower, StringComparison.Ordinal) >= 0)
                return 180 + item.visitCount;
            if (titleLower.IndexOf(qLower, StringComparison.Ordinal) >= 0)
                return 140 + item.visitCount;
            if (urlLower.IndexOf(qLower, StringComparison.Ordinal) >= 0)
                return 100 + item.visitCount;
            return 0;
        }

        public static string HostLabel(string url)
        {
            Uri uri;
            if (Uri.TryCreate(url, UriKind.Absolute, out uri) && !string.IsNullOrEmpty(uri.Host))
            {
                var host = uri.Host;
                if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && host.Length > 4)
                    host = host.Substring(4);
                return host;
            }
            return url ?? "";
        }

        private void TrimUnlocked()
        {
            if (items.Count <= MaxItems)
                return;

            var extra = items.Values
                .OrderBy(i => i.visitedUtc ?? "")
                .Take(items.Count - MaxItems)
                .Select(i => i.url)
                .ToList();
            foreach (var key in extra)
                items.Remove(key);
        }

        private static HistoryItem Clone(HistoryItem item)
        {
            if (item == null)
                return null;
            return new HistoryItem
            {
                url = item.url,
                title = item.title,
                visitedUtc = item.visitedUtc,
                visitCount = item.visitCount
            };
        }

        private void QueueSave()
        {
            if (System.Threading.Interlocked.CompareExchange(ref saveQueued, 1, 0) != 0)
                return;
            var ignored = SaveSoonAsync();
        }

        private async Task SaveSoonAsync()
        {
            try
            {
                await Task.Delay(250);
                System.Threading.Interlocked.Exchange(ref saveQueued, 0);

                List<HistoryItem> snapshot;
                lock (sync)
                {
                    snapshot = items.Values
                        .OrderByDescending(i => i.visitedUtc ?? "")
                        .Select(Clone)
                        .ToList();
                }

                var folder = ApplicationData.Current.LocalFolder;
                var file = await folder.CreateFileAsync(IndexFileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, JsonConvert.SerializeObject(snapshot));
            }
            catch
            {
                System.Threading.Interlocked.Exchange(ref saveQueued, 0);
            }
        }
    }
}
