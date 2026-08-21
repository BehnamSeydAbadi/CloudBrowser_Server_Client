using System;
using Windows.Storage;

namespace BrowserClient
{
    public static class DeviceIdentity
    {
        const string Key = "DeviceId";

        public static string EnsureDeviceId()
        {
            var local = ApplicationData.Current.LocalSettings;
            if (local.Values.ContainsKey(Key))
            {
                var existing = local.Values[Key] as string;
                if (IsValidGuid(existing))
                    return existing.Trim();
            }

            var id = Guid.NewGuid().ToString("N");
            local.Values[Key] = id;
            return id;
        }

        public static string RotateDeviceId()
        {
            var id = Guid.NewGuid().ToString("N");
            ApplicationData.Current.LocalSettings.Values[Key] = id;
            return id;
        }

        public static bool IsValidGuid(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var trimmed = value.Trim();
            Guid parsed;
            return Guid.TryParseExact(trimmed, "N", out parsed) || Guid.TryParse(trimmed, out parsed);
        }
    }
}
