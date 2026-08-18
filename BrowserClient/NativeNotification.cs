using System;
using System.Diagnostics;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace BrowserClient
{
    /// <summary>
    /// Shows OS Action Center / toast notifications on the phone.
    /// </summary>
    public static class NativeNotification
    {
        private const string ToastGroup = "CloudBrowser";

        public static void Show(NotificationPayload payload)
        {
            if (payload == null)
                return;

            var title = (payload.title ?? "").Trim();
            var body = (payload.body ?? "").Trim();
            if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(body))
                return;

            if (string.IsNullOrEmpty(title))
            {
                title = string.IsNullOrEmpty(payload.origin) ? "CloudBrowser" : payload.origin;
            }

            try
            {
                var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
                var texts = xml.GetElementsByTagName("text");
                if (texts.Count > 0)
                    texts[0].AppendChild(xml.CreateTextNode(Truncate(title, 120)));
                if (texts.Count > 1)
                    texts[1].AppendChild(xml.CreateTextNode(Truncate(body, 200)));

                if (!string.IsNullOrEmpty(payload.url))
                {
                    var toastNode = xml.DocumentElement;
                    if (toastNode != null)
                        toastNode.SetAttribute("launch", payload.url);
                }

                var toast = new ToastNotification(xml);
                var tag = SanitizeTag(payload.tag);
                if (!string.IsNullOrEmpty(tag))
                    toast.Tag = tag;
                toast.Group = ToastGroup;

                ToastNotificationManager.CreateToastNotifier().Show(toast);
                Debug.WriteLine("Native notification shown: " + title);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Native notification failed: " + ex.Message);
                // Fallback template if ToastText02 / Tag / Group is unsupported on some builds.
                try
                {
                    var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText01);
                    var texts = xml.GetElementsByTagName("text");
                    if (texts.Count > 0)
                    {
                        var line = string.IsNullOrEmpty(body) ? title : (title + " — " + body);
                        texts[0].AppendChild(xml.CreateTextNode(Truncate(line, 200)));
                    }
                    ToastNotificationManager.CreateToastNotifier().Show(new ToastNotification(xml));
                }
                catch (Exception ex2)
                {
                    Debug.WriteLine("Native notification fallback failed: " + ex2.Message);
                }
            }
        }

        private static string Truncate(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max)
                return value ?? "";
            return value.Substring(0, max - 1) + "…";
        }

        private static string SanitizeTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
                return null;
            tag = tag.Trim();
            // Toast Tag max length is 64 on UWP.
            if (tag.Length > 64)
                tag = tag.Substring(0, 64);
            return tag;
        }
    }
}
