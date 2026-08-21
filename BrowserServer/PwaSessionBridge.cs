using System;

namespace BrowserServer
{
    /// <summary>
    /// One CEF tab per pinned Start-tile URL; switch on tile launch to isolate back/forward history.
    /// </summary>
    public static class PwaSessionBridge
    {
        public static string NormalizeEntryUrl(string entryUrl)
        {
            if (string.IsNullOrWhiteSpace(entryUrl))
                return null;

            try
            {
                var uri = new Uri(entryUrl.Trim(), UriKind.Absolute);
                if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                    return null;
                return uri.AbsoluteUri;
            }
            catch
            {
                return null;
            }
        }

        public static TabSession FindSession(string normalizedEntryUrl)
        {
            if (string.IsNullOrEmpty(normalizedEntryUrl))
                return null;

            foreach (var session in TabManager.AllSessions())
            {
                if (session != null
                    && !string.IsNullOrEmpty(session.PwaEntryUrl)
                    && string.Equals(session.PwaEntryUrl, normalizedEntryUrl, StringComparison.OrdinalIgnoreCase))
                {
                    return session;
                }
            }

            return null;
        }

        public static void ActivateSession(string entryUrl)
        {
            var key = NormalizeEntryUrl(entryUrl);
            if (string.IsNullOrEmpty(key))
            {
                Console.WriteLine("PwaSessionStart ignored — invalid entry URL");
                return;
            }

            var session = FindSession(key);
            if (session == null)
            {
                session = TabManager.CreateTab(key);
                if (session == null)
                {
                    Console.WriteLine("PwaSessionStart failed — tab limit reached for " + key);
                    return;
                }

                session.PwaEntryUrl = key;
                Console.WriteLine("PWA tab created entry={0} id={1}", key, session.Id);
                return;
            }

            if (!TabManager.SwitchTab(session.Id))
            {
                Console.WriteLine("PwaSessionStart switch failed id={0}", session.Id);
                return;
            }

            Console.WriteLine("PWA tab switched entry={0} id={1} url={2}", key, session.Id, session.Url);
        }
    }
}
