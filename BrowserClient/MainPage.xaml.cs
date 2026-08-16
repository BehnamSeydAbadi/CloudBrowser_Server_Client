using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Display;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.StartScreen;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Newtonsoft.Json;

namespace BrowserClient
{
    public sealed partial class MainPage : Page
    {
        WebBrowserDataSource ds;
        public UdpClient sendingClient;
        public UdpClient recivingClient;
        private readonly HashSet<uint> activePointers = new HashSet<uint>();
        private string activeTabId;
        private string activeTabTitle = "New Tab";
        private string pendingNavigateUrl;
        private bool pendingNavigateSent;
        private bool pageTypingActive;
        private bool suppressKeyboardCapture;
        private bool immersivePinnedMode;
        private string lastTabListJson;
        private UISettings themeSettings;

        public string broadcastAddress = "255.255.255.255";
        Timer UdpDiscoveryTimer;
        public MainPage()
        {
            this.InitializeComponent();
            if (IsMobile && Windows.Foundation.Metadata.ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                var statusBar = StatusBar.GetForCurrentView();
                var ignored = statusBar.HideAsync();
            }
            ApplicationDataContainer localSettings = Windows.Storage.ApplicationData.Current.LocalSettings;

            if (localSettings.Values.ContainsKey("LastServerUrl"))
            {
                Debug.WriteLine("Has key");
                Debug.WriteLine(localSettings.Values["LastServerUrl"] as string);
                serverAddress.Text = localSettings.Values["LastServerUrl"] as string;
            }
            else
            {
                Debug.WriteLine("No known server");
            }

            themeSettings = new UISettings();
            themeSettings.ColorValuesChanged += ThemeSettings_ColorValuesChanged;
        }

        private async void ThemeSettings_ColorValuesChanged(UISettings sender, object args)
        {
            await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                if (!string.IsNullOrEmpty(lastTabListJson))
                    ApplyTabList(lastTabListJson);
            });
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var launchUrl = e.Parameter as string;
            if (!string.IsNullOrWhiteSpace(launchUrl))
                OpenPinnedUrl(launchUrl);
        }

        /// <summary>
        /// Opens a URL from a Start-screen pin. Auto-connects to the last server when needed.
        /// </summary>
        public void OpenPinnedUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            pendingNavigateUrl = url.Trim();
            pendingNavigateSent = false;
            urlField.Text = pendingNavigateUrl;
            SetImmersivePinnedMode(true);

            if (ds != null)
            {
                TryNavigatePendingUrl();
                return;
            }

            var localSettings = ApplicationData.Current.LocalSettings;
            var lastServer = localSettings.Values.ContainsKey("LastServerUrl")
                ? localSettings.Values["LastServerUrl"] as string
                : null;

            if (!string.IsNullOrWhiteSpace(lastServer))
            {
                Connect(lastServer.Replace("tcp://", "ws://"));
                ConnectPage.Visibility = Visibility.Collapsed;
                DiscoveryPage.Visibility = Visibility.Collapsed;
                ApplyChromeVisibility();
                NotifyDisplaySize();
            }
            else
            {
                ConnectPage.Visibility = Visibility.Visible;
            }
        }

        private void SetImmersivePinnedMode(bool enabled)
        {
            immersivePinnedMode = enabled;
            ApplyChromeVisibility();
            NotifyDisplaySize();
        }

        private void ApplyChromeVisibility()
        {
            ChromeGrid.Visibility = immersivePinnedMode ? Visibility.Collapsed : Visibility.Visible;
            if (immersivePinnedMode)
                TabsOverlay.Visibility = Visibility.Collapsed;
        }

        private void NotifyDisplaySize()
        {
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Low, () =>
            {
                browser.UpdateLayout();
                if (ds == null || ScaleRect.ActualWidth < 1 || ScaleRect.ActualHeight < 1)
                    return;

                ds.SizeChange(
                    new Size(ScaleRect.ActualWidth, ScaleRect.ActualHeight),
                    DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel);
            });
        }

        private void TryNavigatePendingUrl()
        {
            if (pendingNavigateSent || ds == null || string.IsNullOrWhiteSpace(pendingNavigateUrl))
                return;

            pendingNavigateSent = true;
            ds.Navigate(pendingNavigateUrl);
        }

        private void LoseFocus(object sender)
        {
            var control = sender as Control;
            var isTabStop = control.IsTabStop;
            control.IsTabStop = false;
            control.IsEnabled = false;
            control.IsEnabled = true;
            control.IsTabStop = isTabStop;
        }
        private void TextBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                var url = urlField.Text;
                ds.Navigate(url);
                e.Handled = true; LoseFocus(sender);
            }
        }
        public static bool IsMobile
        {
            get
            {
                var qualifiers = Windows.ApplicationModel.Resources.Core.ResourceContext.GetForCurrentView().QualifierValues;
                return (qualifiers.ContainsKey("DeviceFamily") && qualifiers["DeviceFamily"] == "Mobile");
            }
        }

        private void Test_SizeChanged(object sender, Windows.UI.Xaml.SizeChangedEventArgs e)
        {
            Debug.WriteLine("sizechange");
        }

        private void Page_SizeChanged(object sender, Windows.UI.Xaml.SizeChangedEventArgs e)
        {
           
        }

        private Point GetNormalizedPointerPosition(Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Must be relative to the page image, not the window — top chrome would otherwise
            // shift every touch downward.
            var pos = e.GetCurrentPoint(test).Position;
            var w = test.ActualWidth;
            var h = test.ActualHeight;
            if (w < 1 || h < 1)
                return new Point(0, 0);

            var x = pos.X / w;
            var y = pos.Y / h;
            if (x < 0) x = 0;
            else if (x > 1) x = 1;
            if (y < 0) y = 0;
            else if (y > 1) y = 1;
            return new Point(x, y);
        }

        private void EndPointerContact(Windows.UI.Xaml.Input.PointerRoutedEventArgs e, bool releaseCapture)
        {
            if (ds == null || !activePointers.Remove(e.Pointer.PointerId))
                return;

            ds.TouchUp(GetNormalizedPointerPosition(e), e.Pointer.PointerId);
            if (releaseCapture)
                test.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void Test_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (ds == null)
                return;

            activePointers.Add(e.Pointer.PointerId);
            test.CapturePointer(e.Pointer);
            ds.TouchDown(GetNormalizedPointerPosition(e), e.Pointer.PointerId);
            e.Handled = true;
        }

        private void Test_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            EndPointerContact(e, releaseCapture: true);
        }

        private void Test_PointerMoved(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (ds == null || !activePointers.Contains(e.Pointer.PointerId))
                return;

            var point = e.GetCurrentPoint(null);
            if (!point.IsInContact)
                return;

            ds.TouchMove(GetNormalizedPointerPosition(e), e.Pointer.PointerId);
            e.Handled = true;
        }

        private void Test_PointerCanceled(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            EndPointerContact(e, releaseCapture: true);
        }

        private void Test_PointerCaptureLost(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Release already sent TouchUp; only end contact if capture was lost unexpectedly.
            EndPointerContact(e, releaseCapture: false);
        }
        private void Button_Click(object sender, RoutedEventArgs e)
        {
            SetImmersivePinnedMode(false);
            Connect(serverAddress.Text.Replace("tcp://", "ws://"));
            ConnectPage.Visibility = Visibility.Collapsed;
        }

        public void Connect(string endpoint)
        {
            ds = new WebBrowserDataSource();
            ds.FrameRecived += (s, o) =>
            {
                test.Source = o;
            };
            ds.StartRecive(endpoint);
            ds.TextPacketRecived += (s, o) =>
            {
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    switch (o.PType)
                    {
                        case TextPacketType.NavigatedUrl:
                            urlField.Text = o.text;
                            //hack to get proper size on first launch — ScaleRect excludes navbars/tabs
                            ds.SizeChange(
                                new Size { Width = ScaleRect.ActualWidth, Height = ScaleRect.ActualHeight },
                                DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel);
                            TryNavigatePendingUrl();
                            break;

                        case TextPacketType.TextInputContent:
                            // Keep chrome policy; show OS keyboard and type into the remote page field.
                            TextInput.Visibility = Visibility.Collapsed;
                            ApplyChromeVisibility();
                            BeginPageTyping();
                            break;

                        case TextPacketType.TextInputSend:
                            break;

                        case TextPacketType.TextInputCancel:
                            EndPageTyping();
                            TextInput.Visibility = Visibility.Collapsed;
                            ApplyChromeVisibility();
                            break;

                        case TextPacketType.TabList:
                            ApplyTabList(o.text);
                            TryNavigatePendingUrl();
                            break;
                    }
                });
            };
        }

        private async void About_Click(object sender, RoutedEventArgs e)
        {
            var v = Windows.ApplicationModel.Package.Current.Id.Version;
            var versionText = string.Format("{0}.{1}.{2}.{3}", v.Major, v.Minor, v.Build, v.Revision);

            var dialog = new ContentDialog
            {
                Title = "About",
                Content = "BrowserClient\nVersion " + versionText +
                          "\n\nRemote browser for Windows Mobile.\nStreams pages from BrowserServer on your PC.",
                PrimaryButtonText = "OK"
            };

            try
            {
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("About dialog failed: " + ex.Message);
            }
        }

        private async void AddToHome_Click(object sender, RoutedEventArgs e)
        {
            var url = (urlField.Text ?? "").Trim();
            if (string.IsNullOrEmpty(url) || url.StartsWith("ws://", StringComparison.OrdinalIgnoreCase))
                return;

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            var displayName = string.IsNullOrWhiteSpace(activeTabTitle) ? "Pinned site" : activeTabTitle;
            if (displayName.Length > 50)
                displayName = displayName.Substring(0, 50);

            var tileId = "pin_" + StableHash(url);
            var fallbackLogo = new Uri("ms-appx:///Assets/Square150x150Logo.png");
            var logo = await TryDownloadFaviconLogoAsync(url, tileId) ?? fallbackLogo;

            var tile = new SecondaryTile(
                tileId,
                displayName,
                url,
                logo,
                TileSize.Square150x150);

            tile.VisualElements.ShowNameOnSquare150x150Logo = true;
            tile.VisualElements.ForegroundText = ForegroundText.Light;
            tile.VisualElements.Square150x150Logo = logo;
            tile.VisualElements.Square44x44Logo = logo;

            try
            {
                if (SecondaryTile.Exists(tileId))
                    await tile.UpdateAsync();
                else
                    await tile.RequestCreateAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Add to Home failed: " + ex.Message);
            }
        }

        private static async Task<Uri> TryDownloadFaviconLogoAsync(string pageUrl, string tileId)
        {
            try
            {
                if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
                    return null;

                var candidates = new List<string>
                {
                    "https://www.google.com/s2/favicons?sz=128&domain=" + Uri.EscapeDataString(pageUri.Host),
                    "https://icons.duckduckgo.com/ip3/" + pageUri.Host + ".ico",
                    new Uri(pageUri, "/favicon.ico").AbsoluteUri
                };

                using (var client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(8);
                    foreach (var candidate in candidates)
                    {
                        try
                        {
                            var bytes = await client.GetByteArrayAsync(candidate);
                            if (bytes == null || bytes.Length < 64)
                                continue;

                            var ext = GuessImageExtension(bytes);
                            if (ext == null)
                                continue;

                            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                                "PinnedIcons",
                                CreationCollisionOption.OpenIfExists);
                            var fileName = tileId + ext;
                            var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                            await FileIO.WriteBytesAsync(file, bytes);
                            return new Uri("ms-appdata:///local/PinnedIcons/" + fileName);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine("Favicon candidate failed (" + candidate + "): " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Favicon download failed: " + ex.Message);
            }

            return null;
        }

        private static string GuessImageExtension(byte[] bytes)
        {
            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return ".png";

            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return ".jpg";

            if (bytes.Length >= 6 &&
                bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46)
                return ".gif";

            // ICO / other — Start tiles are unreliable with .ico; skip.
            if (bytes.Length >= 4 && bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0x01 && bytes[3] == 0x00)
                return null;

            return null;
        }

        private static string StableHash(string value)
        {
            uint hash = 2166136261;
            foreach (var c in value)
            {
                hash ^= c;
                hash *= 16777619;
            }
            return hash.ToString("X8");
        }

        private void ApplyTabList(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            lastTabListJson = json;

            TabListPayload list;
            try
            {
                list = JsonConvert.DeserializeObject<TabListPayload>(json);
            }
            catch
            {
                return;
            }

            if (list?.tabs == null)
                return;

            var previousActive = activeTabId;
            activeTabId = list.activeId;
            if (!string.IsNullOrEmpty(previousActive) && previousActive != activeTabId)
                activePointers.Clear();

            var tabActiveBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabActiveBrush"];
            var tabInactiveBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabInactiveBrush"];
            var tabTitleBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabTitleBrush"];
            var tabSubtitleBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabSubtitleBrush"];

            TabStripPanel.Children.Clear();
            TabCountText.Text = Math.Min(list.tabs.Count, 99).ToString();

            foreach (var tab in list.tabs)
            {
                var isActive = tab.id == list.activeId;
                var item = new Grid
                {
                    Tag = tab.id,
                    MinHeight = 56,
                    Margin = new Thickness(0, 0, 0, 8),
                    Background = isActive ? tabActiveBrush : tabInactiveBrush
                };
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var title = string.IsNullOrWhiteSpace(tab.title) ? "New Tab" : tab.title;
                if (title.Length > 40)
                    title = title.Substring(0, 40) + "…";

                var textStack = new StackPanel
                {
                    Margin = new Thickness(14, 10, 8, 10),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                textStack.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = tabTitleBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                var urlLabel = string.IsNullOrWhiteSpace(tab.url) ? "" : tab.url;
                if (urlLabel.Length > 48)
                    urlLabel = urlLabel.Substring(0, 48) + "…";
                textStack.Children.Add(new TextBlock
                {
                    Text = urlLabel,
                    FontSize = 12,
                    Margin = new Thickness(0, 2, 0, 0),
                    Foreground = tabSubtitleBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(textStack, 0);
                item.Children.Add(textStack);

                var closeButton = new Button
                {
                    Content = "\u00D7",
                    Tag = tab.id,
                    Width = 40,
                    Height = 40,
                    Padding = new Thickness(0),
                    FontSize = 18,
                    BorderThickness = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                    Foreground = tabSubtitleBrush
                };
                Grid.SetColumn(closeButton, 1);
                closeButton.Click += CloseTab_Click;
                item.Children.Add(closeButton);

                item.Tapped += SwitchTab_Tapped;
                TabStripPanel.Children.Add(item);
            }

            var active = list.tabs.Find(t => t.id == list.activeId);
            if (active != null)
            {
                if (!string.IsNullOrEmpty(active.url))
                    urlField.Text = active.url;
                activeTabTitle = string.IsNullOrWhiteSpace(active.title) ? "New Tab" : active.title;
            }
        }

        private void TabsButton_Click(object sender, RoutedEventArgs e)
        {
            TabsOverlay.Visibility = Visibility.Visible;
        }

        private void CloseTabsButton_Click(object sender, RoutedEventArgs e)
        {
            TabsOverlay.Visibility = Visibility.Collapsed;
        }

        private void NewTab_Click(object sender, RoutedEventArgs e)
        {
            ds?.CreateTab();
            TabsOverlay.Visibility = Visibility.Collapsed;
        }

        private void SwitchTab_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var tabId = element?.Tag as string;
            if (string.IsNullOrEmpty(tabId) || ds == null)
                return;

            e.Handled = true;
            activePointers.Clear();
            TabsOverlay.Visibility = Visibility.Collapsed;
            ds.SwitchTab(tabId);
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var tabId = button?.Tag as string;
            if (string.IsNullOrEmpty(tabId) || ds == null)
                return;

            activePointers.Clear();
            ds.CloseTab(tabId);
        }

        private void BeginPageTyping()
        {
            pageTypingActive = true;
            // Delay so CEF can finish focusing the page field before we steal UWP focus for the SIP.
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
            {
                await System.Threading.Tasks.Task.Delay(50);
                if (!pageTypingActive)
                    return;

                suppressKeyboardCapture = true;
                KeyboardCaptureBox.Text = "";
                suppressKeyboardCapture = false;
                KeyboardCaptureBox.Focus(FocusState.Programmatic);
                try
                {
                    InputPane.GetForCurrentView().TryShow();
                }
                catch
                {
                }
            });
        }

        private void EndPageTyping()
        {
            pageTypingActive = false;
            suppressKeyboardCapture = true;
            KeyboardCaptureBox.Text = "";
            suppressKeyboardCapture = false;
            LoseFocus(KeyboardCaptureBox);
            try
            {
                InputPane.GetForCurrentView().TryHide();
            }
            catch
            {
            }
        }

        private void KeyboardCaptureBox_TextChanging(TextBox sender, TextBoxTextChangingEventArgs args)
        {
            if (suppressKeyboardCapture || !pageTypingActive || ds == null)
                return;

            var text = sender.Text;
            if (string.IsNullOrEmpty(text))
                return;

            suppressKeyboardCapture = true;
            sender.Text = "";
            suppressKeyboardCapture = false;

            // Insert into the remote page field via JS (works with React/Google inputs).
            var ignored = ds.SendInsertText(text);
        }

        private void KeyboardCaptureBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            if (!pageTypingActive || ds == null)
                return;

            switch (e.Key)
            {
                case Windows.System.VirtualKey.Back:
                    var ignoredBack = ds.SendBackspace();
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Enter:
                    var ignoredEnter = ds.SendEnterKey();
                    e.Handled = true;
                    break;
                case Windows.System.VirtualKey.Escape:
                    EndPageTyping();
                    e.Handled = true;
                    break;
            }
        }

        private void KeyboardCaptureBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // Do not force-refocus: user may be typing in the URL bar / chrome.
        }

        private void WebsiteTextBox_KeyDown(object sender, Windows.UI.Xaml.Input.KeyRoutedEventArgs e)
        {
            // Legacy overlay path kept collapsed; typing goes through KeyboardCaptureBox.
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                e.Handled = true;
                LoseFocus(sender);
            }
        }
        private void SendText_Click(object sender, RoutedEventArgs e)
        {
            TextInput.Visibility = Visibility.Collapsed;
            ApplyChromeVisibility();
        }

        private void MainGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {

        }

        private void Browser_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var scaleFactor = DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
            Debug.WriteLine("AD" + ScaleRect.ActualWidth + " " + ScaleRect.ActualHeight + " scale=" + scaleFactor);

            if (ds != null)
            {
                // ScaleRect is the page displayer only (navbars/tab strip are outside it).
                var s = new Size(ScaleRect.ActualWidth, ScaleRect.ActualHeight);
                ds.SizeChange(s, scaleFactor);
            }
        }
        public bool discovering = false;

        DatagramSocket serverDatagramSocket;

        private void DiscoverBtn_Click(object sender, RoutedEventArgs e)
        {
            
            //TODO:
            //1336 & 1337 for UDP ports, 5454X is out of specon UWP?
            int udpPort = 54545;
            int udpRecPort = 54546;


            ConnectPage.Visibility = Visibility.Collapsed;
            DiscoveryPage.Visibility = Visibility.Visible;

            sendingClient = new UdpClient(udpPort);
            sendingClient.EnableBroadcast = true;


            recivingClient = new UdpClient(udpRecPort);
            


            //3 seconds
            UdpDiscoveryTimer = new Timer(state =>
            {
                try
                {
                    //datagram discovery, we broadcast that we WANT an adress
                    var packet = new DiscoveryPacket
                    {
                        PType = DiscoveryPacketType.AddressRequest,
                    };
                    var rawPacket = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(packet));
                    sendingClient.SendAsync(rawPacket, rawPacket.Length, new System.Net.IPEndPoint(IPAddress.Parse("255.255.255.255"), udpPort));
                }
                catch (Exception) { }
            }, null, 0, 3000);

            discovering = true;

            serverDatagramSocket = new Windows.Networking.Sockets.DatagramSocket();

            // The ConnectionReceived event is raised when connections are received.
            serverDatagramSocket.MessageReceived += ServerDatagramSocket_MessageReceived;
        
            // Start listening for incoming TCP connections on the specified port. You can specify any port that's not currently in use.
             serverDatagramSocket.BindServiceNameAsync("1337");

        }

        private void ServerDatagramSocket_MessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            string request;
            using (DataReader dataReader = args.GetDataReader())
            {
                request = dataReader.ReadString(dataReader.UnconsumedBufferLength).Trim();
            }
            Debug.WriteLine(request);

            var packet = JsonConvert.DeserializeObject<DiscoveryPacket>(request);

            switch (packet.PType)
            {
                case DiscoveryPacketType.AddressRequest:
                    break;
                case DiscoveryPacketType.ACK:
                    Debug.WriteLine("ws://" + packet.ServerAddress + ":8081");

                    Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () => {
                        //replace with a connect function
                        UdpDiscoveryTimer.Dispose();
                        serverDatagramSocket.Dispose();


                        Connect("ws://" + packet.ServerAddress + ":8081");
                        /*
                        ds = new WebBrowserDataSource();
                        ds.DataRecived += (s, o) =>
                        {
                            test.Source = ConvertToBitmapImage(o).Result;
                            // ds.ACKRender();
                        };
                        */
                        ConnectPage.Visibility = Visibility.Collapsed;
                        DiscoveryPage.Visibility = Visibility.Collapsed;
                        ApplyChromeVisibility();
                        NotifyDisplaySize();
                       // ds.StartRecive("ws://" + packet.ServerAddress + ":8081");
                        
                    });
                    break;
                default:
                    break;
            }
        }

        private void NavigateBack_Click(object sender, RoutedEventArgs e)
        {
            ds.NavigateBack();
        }

        private void NavigateForward_Click(object sender, RoutedEventArgs e)
        {
            ds.NavigateForward();
        }
    }
}
