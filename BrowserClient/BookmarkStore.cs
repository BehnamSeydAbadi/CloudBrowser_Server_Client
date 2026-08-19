using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Windows.Storage;

namespace BrowserClient
{
    public class BookmarkItem
    {
        public string url;
        public string title;
        public string addedUtc;
    }

    /// <summary>
    /// Local bookmarks for the star button and Bookmarks overlay.
    /// </summary>
    public sealed class BookmarkStore
    {
        private const string IndexFileName = "bookmarks_index.json";

        private readonly object sync = new object();
        private readonly Dictionary<string, BookmarkItem> items =
            new Dictionary<string, BookmarkItem>(StringComparer.OrdinalIgnoreCase);
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
                    var list = JsonConvert.DeserializeObject<List<BookmarkItem>>(json) ?? new List<BookmarkItem>();
                    lock (sync)
                    {
                        items.Clear();
                        foreach (var item in list)
                        {
                            var key = HistoryStore.NormalizeUrl(item != null ? item.url : null);
                            if (string.IsNullOrEmpty(key))
                                continue;
                            item.url = key;
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

        public bool Contains(string url)
        {
            var key = HistoryStore.NormalizeUrl(url);
            if (string.IsNullOrEmpty(key))
                return false;
            lock (sync)
            {
                return items.ContainsKey(key);
            }
        }

        public bool Add(string url, string title)
        {
            var key = HistoryStore.NormalizeUrl(url);
            if (string.IsNullOrEmpty(key))
                return false;

            var cleanTitle = string.IsNullOrWhiteSpace(title) ? HistoryStore.HostLabel(key) : title.Trim();
            if (string.Equals(cleanTitle, "New Tab", StringComparison.OrdinalIgnoreCase))
                cleanTitle = HistoryStore.HostLabel(key);

            lock (sync)
            {
                BookmarkItem existing;
                if (items.TryGetValue(key, out existing))
                {
                    existing.title = cleanTitle;
                }
                else
                {
                    items[key] = new BookmarkItem
                    {
                        url = key,
                        title = cleanTitle,
                        addedUtc = DateTime.UtcNow.ToString("o")
                    };
                }
            }

            QueueSave();
            return true;
        }

        public bool Remove(string url)
        {
            var key = HistoryStore.NormalizeUrl(url);
            if (string.IsNullOrEmpty(key))
                key = (url ?? "").Trim();
            if (string.IsNullOrEmpty(key))
                return false;

            bool removed;
            lock (sync)
            {
                removed = items.Remove(key);
            }

            if (removed)
                QueueSave();
            return removed;
        }

        public bool Toggle(string url, string title)
        {
            if (Contains(url))
            {
                Remove(url);
                return false;
            }

            Add(url, title);
            return true;
        }

        public List<BookmarkItem> GetSnapshot()
        {
            lock (sync)
            {
                return items.Values
                    .OrderByDescending(i => i.addedUtc ?? "")
                    .Select(Clone)
                    .ToList();
            }
        }

        private static BookmarkItem Clone(BookmarkItem item)
        {
            if (item == null)
                return null;
            return new BookmarkItem
            {
                url = item.url,
                title = item.title,
                addedUtc = item.addedUtc
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

                List<BookmarkItem> snapshot;
                lock (sync)
                {
                    snapshot = items.Values
                        .OrderByDescending(i => i.addedUtc ?? "")
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
