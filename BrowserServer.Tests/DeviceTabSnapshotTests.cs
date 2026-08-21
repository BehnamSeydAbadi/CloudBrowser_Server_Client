using System;
using System.IO;
using FluentAssertions;
using Newtonsoft.Json;
using Xunit;

namespace BrowserServer.Tests
{
    public class DeviceTabSnapshotTests
    {
        [Fact]
        public void Snapshot_RoundTripsJson()
        {
            var snapshot = new DeviceTabSnapshot
            {
                activeIndex = 1,
                tabs = new System.Collections.Generic.List<DeviceTabEntry>
                {
                    new DeviceTabEntry { url = "https://a.test/", title = "A", pwaEntryUrl = "https://a.test/app" },
                    new DeviceTabEntry { url = "https://b.test/", title = "B" }
                }
            };

            var path = Path.Combine(Path.GetTempPath(), "cb-tabs-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                DeviceTabSnapshot.Save(path, snapshot);
                var restored = DeviceTabSnapshot.Load(path);
                restored.Should().NotBeNull();
                restored.activeIndex.Should().Be(1);
                restored.tabs.Should().HaveCount(2);
                restored.tabs[0].url.Should().Be("https://a.test/");
                restored.tabs[0].pwaEntryUrl.Should().Be("https://a.test/app");
            }
            finally
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        [Fact]
        public void Load_MissingFile_ReturnsNull()
        {
            DeviceTabSnapshot.Load(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".json"))
                .Should().BeNull();
        }

        [Fact]
        public void Payload_IncludesDeviceId()
        {
            var payload = new ClientEnvironmentPayload
            {
                cssWidth = 360,
                cssHeight = 640,
                devicePixelRatio = 2,
                isMobile = true,
                acceptLanguage = "en-US",
                screenWidth = 360,
                screenHeight = 640,
                colorScheme = "light",
                timeZone = "UTC",
                utcOffsetMinutes = 0,
                orientation = "portrait",
                deviceId = Guid.NewGuid().ToString("N")
            };

            var json = JsonConvert.SerializeObject(payload);
            var restored = JsonConvert.DeserializeObject<ClientEnvironmentPayload>(json);
            restored.deviceId.Should().Be(payload.deviceId);
        }
    }
}
