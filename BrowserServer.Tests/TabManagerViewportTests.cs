using System;
using System.Threading;
using BrowserServer;
using BrowserServer.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace BrowserServer.Tests
{
    public class TabManagerViewportTests : IDisposable
    {
        public TabManagerViewportTests()
        {
            TestSessionFactory.ResetAll();
        }

        public void Dispose()
        {
            TestSessionFactory.ResetAll();
        }

        [Fact]
        public void TabManager_MaxTabs_IsEight()
        {
            TabManager.MaxTabs.Should().Be(8);
        }

        [Fact]
        public void TabManager_SetViewport_CapsDeviceScaleFactorAt3()
        {
            var session = new ClientSession("ws-viewport-cap");
            var tabs = session.Tabs;

            tabs.SetViewport(360, 640, 4f);
            Thread.Sleep(200);

            tabs.DeviceScaleFactor.Should().Be(3f);
            tabs.CssWidth.Should().Be(360);
            tabs.CssHeight.Should().Be(640);
        }

        [Fact]
        public void TabManager_SetViewport_IgnoresZeroDimensions()
        {
            var session = new ClientSession("ws-viewport-zero");
            var tabs = session.Tabs;

            tabs.DeviceScaleFactor.Should().Be(2f);
            tabs.CssWidth.Should().Be(360);
            tabs.CssHeight.Should().Be(640);

            tabs.SetViewport(0, 640, 2f);
            Thread.Sleep(200);

            tabs.DeviceScaleFactor.Should().Be(2f);
            tabs.CssWidth.Should().Be(360);
            tabs.CssHeight.Should().Be(640);
        }

        [Fact]
        public void TabManager_BuildSnapshot_UsesNewTabTitleFallback()
        {
            var session = new ClientSession("ws-snapshot-title");
            TabManagerTestAccess.InjectTabs(
                session.Tabs,
                new[]
                {
                    new TabManagerTestAccess.TabEntry
                    {
                        Id = "tab-1",
                        Url = "https://example.com/",
                        Title = ""
                    }
                },
                "tab-1");

            var snapshot = session.Tabs.BuildSnapshot();

            snapshot.tabs.Should().ContainSingle();
            snapshot.tabs[0].title.Should().Be("New Tab");
        }

        [Fact]
        public void TabManager_BuildSnapshot_PreservesActiveIndex()
        {
            var session = new ClientSession("ws-snapshot-active");
            TabManagerTestAccess.InjectTabs(
                session.Tabs,
                new[]
                {
                    new TabManagerTestAccess.TabEntry { Id = "tab-a", Url = "https://a.example/", Title = "A" },
                    new TabManagerTestAccess.TabEntry { Id = "tab-b", Url = "https://b.example/", Title = "B" }
                },
                "tab-b");

            var snapshot = session.Tabs.BuildSnapshot();

            snapshot.activeIndex.Should().Be(1);
            snapshot.tabs[1].url.Should().Be("https://b.example/");
        }
    }
}
