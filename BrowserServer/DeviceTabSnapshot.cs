using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace BrowserServer
{
    public class DeviceTabEntry
    {
        public string url;
        public string title;
        public string pwaEntryUrl;
    }

    public class DeviceTabSnapshot
    {
        public int activeIndex;
        public List<DeviceTabEntry> tabs = new List<DeviceTabEntry>();

        public static DeviceTabSnapshot Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return null;

            try
            {
                var json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json))
                    return null;
                return Newtonsoft.Json.JsonConvert.DeserializeObject<DeviceTabSnapshot>(json);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Tab snapshot load error: " + ex.Message);
                return null;
            }
        }

        public static void Save(string path, DeviceTabSnapshot snapshot)
        {
            if (string.IsNullOrWhiteSpace(path) || snapshot == null)
                return;

            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = Newtonsoft.Json.JsonConvert.SerializeObject(snapshot);
                File.WriteAllText(path, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Tab snapshot save error: " + ex.Message);
            }
        }

        public static DeviceTabSnapshot FromTabs(TabManager tabs)
        {
            if (tabs == null)
                return null;
            return tabs.BuildSnapshot();
        }
    }
}
