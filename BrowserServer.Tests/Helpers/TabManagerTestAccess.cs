using System;
using System.Collections.Generic;
using System.Reflection;
using BrowserServer;

namespace BrowserServer.Tests.Helpers
{
    public static class TabManagerTestAccess
    {
        public static void InjectTabs(
            TabManager manager,
            IEnumerable<TabEntry> entries,
            string activeTabId)
        {
            if (manager == null)
                throw new ArgumentNullException("manager");

            var type = typeof(TabManager);
            var tabsField = type.GetField("tabs", BindingFlags.Instance | BindingFlags.NonPublic);
            var orderField = type.GetField("tabOrder", BindingFlags.Instance | BindingFlags.NonPublic);
            var activeProp = type.GetProperty("ActiveTabId", BindingFlags.Instance | BindingFlags.Public);

            var tabs = (Dictionary<string, TabSession>)tabsField.GetValue(manager);
            var order = (List<string>)orderField.GetValue(manager);
            tabs.Clear();
            order.Clear();

            foreach (var entry in entries)
            {
                tabs[entry.Id] = new TabSession
                {
                    Id = entry.Id,
                    Url = entry.Url,
                    Title = entry.Title,
                    PwaEntryUrl = entry.PwaEntryUrl
                };
                order.Add(entry.Id);
            }

            activeProp.SetValue(manager, activeTabId, null);
        }

        public sealed class TabEntry
        {
            public string Id;
            public string Url;
            public string Title;
            public string PwaEntryUrl;
        }
    }
}
