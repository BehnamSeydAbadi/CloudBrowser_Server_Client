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
using Windows.Foundation.Metadata;
using Windows.Graphics.Display;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
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
        private readonly DownloadStore offlineDownloads = new DownloadStore();
        private readonly HistoryStore browsingHistory = new HistoryStore();
        private readonly BookmarkStore bookmarks = new BookmarkStore();
        private readonly HashSet<string> downloadStartToasts = new HashSet<string>();
        private readonly HashSet<string> downloadCompleteToasts = new HashSet<string>();
        private int downloadToastGeneration;
        private bool suppressUrlSuggest;
        private int urlSuggestHideGeneration;

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
            var historyLoad = browsingHistory.EnsureLoadedAsync();
            var bookmarkLoad = bookmarks.EnsureLoadedAsync();
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
            RegisterBackHandlers();
            var launchUrl = e.Parameter as string;
            if (!string.IsNullOrWhiteSpace(launchUrl))
                OpenPinnedUrl(launchUrl);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            UnregisterBackHandlers();
            base.OnNavigatedFrom(e);
        }

        private void RegisterBackHandlers()
        {
            // Windows Mobile nav-bar back button.
            if (ApiInformation.IsTypePresent("Windows.Phone.UI.Input.HardwareButtons"))
                Windows.Phone.UI.Input.HardwareButtons.BackPressed += HardwareButtons_BackPressed;

            SystemNavigationManager.GetForCurrentView().BackRequested += SystemNavigationManager_BackRequested;
        }

        private void UnregisterBackHandlers()
        {
            if (ApiInformation.IsTypePresent("Windows.Phone.UI.Input.HardwareButtons"))
                Windows.Phone.UI.Input.HardwareButtons.BackPressed -= HardwareButtons_BackPressed;

            SystemNavigationManager.GetForCurrentView().BackRequested -= SystemNavigationManager_BackRequested;
        }

        private void HardwareButtons_BackPressed(object sender, Windows.Phone.UI.Input.BackPressedEventArgs e)
        {
            if (TryHandleBack())
                e.Handled = true;
        }

        private void SystemNavigationManager_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (TryHandleBack())
                e.Handled = true;
        }

        /// <summary>
        /// Returns true when back was consumed in-app (do not exit).
        /// </summary>
        private bool TryHandleBack()
        {
            if (TabsOverlay.Visibility == Visibility.Visible)
            {
                TabsOverlay.Visibility = Visibility.Collapsed;
                return true;
            }

            if (BookmarksOverlay.Visibility == Visibility.Visible)
            {
                BookmarksOverlay.Visibility = Visibility.Collapsed;
                return true;
            }

            if (HistoryOverlay.Visibility == Visibility.Visible)
            {
                HistoryOverlay.Visibility = Visibility.Collapsed;
                return true;
            }

            if (DownloadsOverlay.Visibility == Visibility.Visible)
            {
                DownloadsOverlay.Visibility = Visibility.Collapsed;
                return true;
            }

            if (UrlSuggestPanel != null && UrlSuggestPanel.Visibility == Visibility.Visible)
            {
                HideUrlSuggestions();
                return true;
            }

            if (TextInput.Visibility == Visibility.Visible)
            {
                TextInput.Visibility = Visibility.Collapsed;
                ApplyChromeVisibility();
                return true;
            }

            if (pageTypingActive)
            {
                EndPageTyping();
                return true;
            }

            if (DiscoveryPage.Visibility == Visibility.Visible)
            {
                DiscoveryPage.Visibility = Visibility.Collapsed;
                ConnectPage.Visibility = Visibility.Visible;
                try
                {
                    UdpDiscoveryTimer?.Dispose();
                }
                catch
                {
                }
                return true;
            }

            // Connected browsing session: navigate remote CEF history instead of leaving the app.
            if (ds != null && ConnectPage.Visibility != Visibility.Visible)
            {
                // Pinned/home-screen mode: never step back onto about:blank — exit instead.
                ds.NavigateBack(stopBeforeBlank: immersivePinnedMode);
                return true;
            }

            return false;
        }

        private void ExitPinnedSession()
        {
            try
            {
                Application.Current.Exit();
            }
            catch
            {
            }
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
            SetUrlFieldText(pendingNavigateUrl);
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
            {
                TabsOverlay.Visibility = Visibility.Collapsed;
                DownloadsOverlay.Visibility = Visibility.Collapsed;
                HistoryOverlay.Visibility = Visibility.Collapsed;
                BookmarksOverlay.Visibility = Visibility.Collapsed;
                HideUrlSuggestions();
            }
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
                HideUrlSuggestions();
                ds.Navigate(url);
                e.Handled = true;
                LoseFocus(sender);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                HideUrlSuggestions();
                e.Handled = true;
                LoseFocus(sender);
            }
        }

        private void UrlField_GotFocus(object sender, RoutedEventArgs e)
        {
            urlSuggestHideGeneration++;
            RefreshUrlSuggestions();
        }

        private async void UrlField_LostFocus(object sender, RoutedEventArgs e)
        {
            var generation = ++urlSuggestHideGeneration;
            try
            {
                await Task.Delay(180);
            }
            catch
            {
                return;
            }
            if (generation != urlSuggestHideGeneration)
                return;
            if (urlField != null && urlField.FocusState != FocusState.Unfocused)
                return;
            HideUrlSuggestions();
        }

        private void UrlField_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (suppressUrlSuggest)
                return;
            if (urlField == null || urlField.FocusState == FocusState.Unfocused)
            {
                HideUrlSuggestions();
                return;
            }
            RefreshUrlSuggestions();
        }

        private void SetUrlFieldText(string text)
        {
            suppressUrlSuggest = true;
            try
            {
                urlField.Text = text ?? "";
            }
            finally
            {
                suppressUrlSuggest = false;
            }
        }

        private void RefreshUrlSuggestions()
        {
            if (UrlSuggestPanel == null || UrlSuggestStrip == null || immersivePinnedMode)
            {
                HideUrlSuggestions();
                return;
            }

            var query = urlField != null ? urlField.Text : "";
            var matches = browsingHistory.Suggest(query);
            UrlSuggestStrip.Children.Clear();

            if (matches.Count == 0)
            {
                HideUrlSuggestions();
                return;
            }

            var titleBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabTitleBrush"];
            var mutedBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabSubtitleBrush"];

            foreach (var item in matches)
            {
                var title = string.IsNullOrWhiteSpace(item.title) ? HistoryStore.HostLabel(item.url) : item.title;
                if (title.Length > 42)
                    title = title.Substring(0, 39) + "…";
                var subtitle = item.url ?? "";
                if (subtitle.Length > 52)
                    subtitle = subtitle.Substring(0, 49) + "…";

                var row = new Button
                {
                    Tag = item.url,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    BorderThickness = new Thickness(0),
                    Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                    Padding = new Thickness(12, 8, 12, 8),
                    MinHeight = 48
                };
                var texts = new StackPanel();
                texts.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 14,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = titleBrush,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                texts.Children.Add(new TextBlock
                {
                    Text = subtitle,
                    FontSize = 12,
                    Foreground = mutedBrush,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                row.Content = texts;
                row.Click += UrlSuggest_Click;
                UrlSuggestStrip.Children.Add(row);
            }

            UrlSuggestPanel.Visibility = Visibility.Visible;
        }

        private void HideUrlSuggestions()
        {
            urlSuggestHideGeneration++;
            if (UrlSuggestPanel != null)
                UrlSuggestPanel.Visibility = Visibility.Collapsed;
            if (UrlSuggestStrip != null)
                UrlSuggestStrip.Children.Clear();
        }

        private void UrlSuggest_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var url = button?.Tag as string;
            if (string.IsNullOrEmpty(url) || ds == null)
                return;

            urlSuggestHideGeneration++;
            HideUrlSuggestions();
            SetUrlFieldText(url);
            ds.Navigate(url);
            LoseFocus(urlField);
        }

        private void RecordHistory(string url, string title)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;
            browsingHistory.Record(url, string.IsNullOrWhiteSpace(title) ? activeTabTitle : title);
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
            if (ds != null)
            {
                try { ds.Downloads.ListChanged -= Downloads_ListChanged; } catch { }
            }

            ds = new WebBrowserDataSource();
            ds.Downloads.ListChanged += Downloads_ListChanged;
            ds.FrameRecived += (s, o) =>
            {
                if (o == null)
                    return;
                if (Dispatcher.HasThreadAccess)
                {
                    test.Source = o;
                    return;
                }
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    test.Source = o;
                });
            };
            ds.StartRecive(endpoint);
            ds.MediaPermissionRequested += (s, payload) =>
            {
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    var dialogIgnored = ShowMediaPermissionDialogAsync(payload);
                });
            };
            ds.NotificationPermissionRequested += (s, payload) =>
            {
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    var dialogIgnored = ShowNotificationPermissionDialogAsync(payload);
                });
            };
            ds.TextPacketRecived += (s, o) =>
            {
                var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    switch (o.PType)
                    {
                        case TextPacketType.NavigatedUrl:
                            if (urlField.FocusState == FocusState.Unfocused)
                                SetUrlFieldText(o.text);
                            RecordHistory(o.text, activeTabTitle);
                            UpdateBookmarkButton();
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

                        case TextPacketType.AtHistoryRoot:
                            // Only pinned sessions treat history-root as "leave the app".
                            if (immersivePinnedMode)
                                ExitPinnedSession();
                            break;

                        case TextPacketType.DownloadStarted:
                        case TextPacketType.DownloadProgress:
                        case TextPacketType.DownloadCompleted:
                            // DownloadStore already updated inside WebBrowserDataSource;
                            // ListChanged drives toasts + list refresh.
                            break;

                        case TextPacketType.QrDetected:
                            var qrIgnored = HandleQrDetectedAsync(o.text);
                            break;

                        case TextPacketType.Notification:
                            HandleServerNotification(o.text);
                            break;
                    }
                });
            };
        }

        private void HandleServerNotification(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            try
            {
                var payload = JsonConvert.DeserializeObject<NotificationPayload>(json);
                if (payload == null)
                    return;

                // Always show an in-app banner — OS toasts are easy to miss while the app is focused
                // (especially on Windows Mobile, where banners may only land in Action Center).
                var title = (payload.title ?? "").Trim();
                var body = (payload.body ?? "").Trim();
                string banner;
                if (!string.IsNullOrEmpty(title) && !string.IsNullOrEmpty(body))
                    banner = title + ": " + body;
                else
                    banner = !string.IsNullOrEmpty(title) ? title : body;
                if (banner.Length > 64)
                    banner = banner.Substring(0, 61) + "…";
                if (!string.IsNullOrEmpty(banner))
                    ShowDownloadToast(banner);

                NativeNotification.Show(payload);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Notification parse failed: " + ex.Message);
            }
        }

        private async Task HandleQrDetectedAsync(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            text = text.Trim();
            ShowDownloadToast(text.Length > 48 ? ("QR: " + text.Substring(0, 45) + "…") : ("QR: " + text));

            // HTTP(S) links are opened by the server; non-URL payloads get a dialog.
            Uri uri;
            var looksLikeUrl =
                (Uri.TryCreate(text, UriKind.Absolute, out uri) &&
                 (uri.Scheme == "http" || uri.Scheme == "https")) ||
                text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            if (looksLikeUrl)
                return;

            try
            {
                var dialog = new ContentDialog
                {
                    Title = "QR Code",
                    Content = text,
                    PrimaryButtonText = "Search",
                    SecondaryButtonText = "OK"
                };
                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary && ds != null)
                    ds.Navigate(text);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("QR dialog: " + ex.Message);
            }
        }

        private void Downloads_ListChanged(object sender, EventArgs e)
        {
            var ignored = Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
            {
                NotifyDownloadToasts();
                RefreshDownloadsUi();
            });
        }

        private void NotifyDownloadToasts()
        {
            var list = ActiveDownloads.GetSnapshot();
            foreach (var item in list)
            {
                if (item == null || string.IsNullOrEmpty(item.id))
                    continue;

                if ((item.status == "downloading" || item.status == "transferring")
                    && downloadStartToasts.Add(item.id))
                {
                    ShowDownloadToast("Downloading...");
                }
                else if (item.status == "completed" && downloadCompleteToasts.Add(item.id))
                {
                    ShowDownloadToast("Download Complete");
                }
            }
        }

        private async void ShowDownloadToast(string message)
        {
            if (DownloadToast == null || DownloadToastMessage == null)
                return;

            var generation = ++downloadToastGeneration;
            DownloadToastMessage.Text = message;
            DownloadToast.Visibility = Visibility.Visible;
            DownloadToast.Opacity = 0;

            try
            {
                await AnimateOpacityAsync(DownloadToast, 0, 1, 220);
                await Task.Delay(2000);
                if (generation != downloadToastGeneration)
                    return;

                await AnimateOpacityAsync(DownloadToast, 1, 0, 320);
                if (generation == downloadToastGeneration)
                {
                    DownloadToast.Visibility = Visibility.Collapsed;
                    DownloadToast.Opacity = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Download toast failed: " + ex.Message);
                if (generation == downloadToastGeneration)
                {
                    DownloadToast.Visibility = Visibility.Collapsed;
                    DownloadToast.Opacity = 0;
                }
            }
        }

        private static Task AnimateOpacityAsync(UIElement element, double from, double to, int durationMs)
        {
            var tcs = new TaskCompletionSource<bool>();
            var animation = new Windows.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EnableDependentAnimation = true,
                EasingFunction = new Windows.UI.Xaml.Media.Animation.QuadraticEase
                {
                    EasingMode = Windows.UI.Xaml.Media.Animation.EasingMode.EaseInOut
                }
            };

            Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, element);
            Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");

            var storyboard = new Windows.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(animation);
            EventHandler<object> handler = null;
            handler = (s, e) =>
            {
                storyboard.Completed -= handler;
                tcs.TrySetResult(true);
            };
            storyboard.Completed += handler;
            element.Opacity = from;
            storyboard.Begin();
            return tcs.Task;
        }

        private async void DownloadToastDetails_Click(object sender, RoutedEventArgs e)
        {
            downloadToastGeneration++;
            DownloadToast.Visibility = Visibility.Collapsed;
            DownloadToast.Opacity = 0;

            await ActiveDownloads.EnsureLoadedAsync();
            TabsOverlay.Visibility = Visibility.Collapsed;
            HistoryOverlay.Visibility = Visibility.Collapsed;
            BookmarksOverlay.Visibility = Visibility.Collapsed;
            HideUrlSuggestions();
            DownloadsOverlay.Visibility = Visibility.Visible;
            RefreshDownloadsUi();
        }

        private async Task ShowMediaPermissionDialogAsync(MediaPermissionPayload payload)
        {
            if (payload == null || ds == null)
                return;

            var parts = new System.Collections.Generic.List<string>();
            if (payload.video) parts.Add("camera");
            if (payload.audio) parts.Add("microphone");
            var what = parts.Count > 0 ? string.Join(" and ", parts) : "media";
            var origin = string.IsNullOrWhiteSpace(payload.origin) ? "This site" : payload.origin;

            var dialog = new ContentDialog
            {
                Title = "Allow media access?",
                Content = origin + " wants to use your " + what + ".",
                PrimaryButtonText = "Allow",
                SecondaryButtonText = "Deny"
            };

            var allowed = false;
            try
            {
                var result = await dialog.ShowAsync();
                allowed = result == ContentDialogResult.Primary;
            }
            catch
            {
                allowed = false;
            }

            if (allowed && ds != null)
                ds.MediaCapture.PreviewElement = MediaPreviewSink;

            await ds.RespondMediaPermissionAsync(payload, allowed);
        }

        private async Task ShowNotificationPermissionDialogAsync(NotificationPermissionPayload payload)
        {
            if (payload == null || ds == null)
                return;

            var origin = string.IsNullOrWhiteSpace(payload.origin) ? "This site" : payload.origin;

            var dialog = new ContentDialog
            {
                Title = "Allow notifications?",
                Content = origin + " wants to show notifications.",
                PrimaryButtonText = "Allow",
                SecondaryButtonText = "Deny"
            };

            var allowed = false;
            try
            {
                var result = await dialog.ShowAsync();
                allowed = result == ContentDialogResult.Primary;
            }
            catch
            {
                allowed = false;
            }

            await ds.RespondNotificationPermissionAsync(payload, allowed);
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
                if (!string.IsNullOrEmpty(active.url) && urlField.FocusState == FocusState.Unfocused)
                    SetUrlFieldText(active.url);
                activeTabTitle = string.IsNullOrWhiteSpace(active.title) ? "New Tab" : active.title;
                if (!string.IsNullOrEmpty(active.url))
                    browsingHistory.Record(active.url, activeTabTitle, countVisit: false);
                UpdateBookmarkButton();
            }
        }

        private void TabsButton_Click(object sender, RoutedEventArgs e)
        {
            HideUrlSuggestions();
            DownloadsOverlay.Visibility = Visibility.Collapsed;
            HistoryOverlay.Visibility = Visibility.Collapsed;
            BookmarksOverlay.Visibility = Visibility.Collapsed;
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

        private DownloadStore ActiveDownloads => ds != null ? ds.Downloads : offlineDownloads;

        private async void DownloadsMenu_Click(object sender, RoutedEventArgs e)
        {
            MoreButton.Flyout?.Hide();
            await ActiveDownloads.EnsureLoadedAsync();
            TabsOverlay.Visibility = Visibility.Collapsed;
            HistoryOverlay.Visibility = Visibility.Collapsed;
            BookmarksOverlay.Visibility = Visibility.Collapsed;
            HideUrlSuggestions();
            DownloadsOverlay.Visibility = Visibility.Visible;
            RefreshDownloadsUi();
        }

        private async void HistoryMenu_Click(object sender, RoutedEventArgs e)
        {
            MoreButton.Flyout?.Hide();
            await browsingHistory.EnsureLoadedAsync();
            TabsOverlay.Visibility = Visibility.Collapsed;
            DownloadsOverlay.Visibility = Visibility.Collapsed;
            BookmarksOverlay.Visibility = Visibility.Collapsed;
            HideUrlSuggestions();
            HistoryOverlay.Visibility = Visibility.Visible;
            RefreshHistoryUi();
        }

        private async void BookmarksMenu_Click(object sender, RoutedEventArgs e)
        {
            MoreButton.Flyout?.Hide();
            await bookmarks.EnsureLoadedAsync();
            TabsOverlay.Visibility = Visibility.Collapsed;
            DownloadsOverlay.Visibility = Visibility.Collapsed;
            HistoryOverlay.Visibility = Visibility.Collapsed;
            HideUrlSuggestions();
            BookmarksOverlay.Visibility = Visibility.Visible;
            RefreshBookmarksUi();
        }

        private void CloseBookmarksButton_Click(object sender, RoutedEventArgs e)
        {
            BookmarksOverlay.Visibility = Visibility.Collapsed;
        }

        private void BookmarkButton_Click(object sender, RoutedEventArgs e)
        {
            var url = CurrentPageUrl();
            if (string.IsNullOrEmpty(url))
                return;

            bookmarks.Toggle(url, activeTabTitle);
            UpdateBookmarkButton();
            if (BookmarksOverlay.Visibility == Visibility.Visible)
                RefreshBookmarksUi();
        }

        private string CurrentPageUrl()
        {
            var url = urlField != null ? urlField.Text : null;
            return HistoryStore.NormalizeUrl(url) ?? (url ?? "").Trim();
        }

        private void UpdateBookmarkButton()
        {
            if (BookmarkStarIcon == null)
                return;

            var starred = bookmarks.Contains(CurrentPageUrl());
            BookmarkStarIcon.Glyph = starred ? "\uE735" : "\uE734";
            if (BookmarkButton != null)
                ToolTipService.SetToolTip(BookmarkButton, starred ? "Remove bookmark" : "Bookmark");
        }

        private void RefreshBookmarksUi()
        {
            if (BookmarkStripPanel == null)
                return;

            BookmarkStripPanel.Children.Clear();
            var list = bookmarks.GetSnapshot();
            if (BookmarksEmptyText != null)
                BookmarksEmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var titleBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabTitleBrush"];
            var mutedBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabSubtitleBrush"];
            var pillBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabInactiveBrush"];

            foreach (var item in list)
            {
                var row = new Grid
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    MinHeight = 56,
                    Background = pillBrush,
                    Tag = item.url
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var title = string.IsNullOrWhiteSpace(item.title) ? HistoryStore.HostLabel(item.url) : item.title;
                var texts = new StackPanel
                {
                    Margin = new Thickness(14, 10, 8, 10),
                    VerticalAlignment = VerticalAlignment.Center
                };
                texts.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = titleBrush,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                texts.Children.Add(new TextBlock
                {
                    Text = item.url ?? "",
                    FontSize = 12,
                    Foreground = mutedBrush,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(texts, 0);
                row.Children.Add(texts);

                var delete = new Button
                {
                    Tag = item.url,
                    Width = 44,
                    Height = 44,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon
                    {
                        FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                        Glyph = "\uE74D",
                        FontSize = 16,
                        Foreground = titleBrush
                    }
                };
                delete.Click += DeleteBookmark_Click;
                Grid.SetColumn(delete, 1);
                row.Children.Add(delete);

                row.Tapped += BookmarkRow_Tapped;
                BookmarkStripPanel.Children.Add(row);
            }
        }

        private void BookmarkRow_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var url = element?.Tag as string;
            if (string.IsNullOrEmpty(url) || ds == null)
                return;

            if (e.OriginalSource is Windows.UI.Xaml.Controls.Primitives.ButtonBase)
                return;

            e.Handled = true;
            BookmarksOverlay.Visibility = Visibility.Collapsed;
            HideUrlSuggestions();
            SetUrlFieldText(url);
            ds.Navigate(url);
        }

        private void DeleteBookmark_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var url = button?.Tag as string;
            if (string.IsNullOrEmpty(url))
                return;

            bookmarks.Remove(url);
            RefreshBookmarksUi();
            UpdateBookmarkButton();
        }

        private void CloseHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            HistoryOverlay.Visibility = Visibility.Collapsed;
        }

        private void RefreshHistoryUi()
        {
            if (HistoryStripPanel == null)
                return;

            HistoryStripPanel.Children.Clear();
            var list = browsingHistory.GetSnapshot();
            var empty = list.Count == 0;
            if (HistoryEmptyText != null)
                HistoryEmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            if (ClearHistoryButton != null)
                ClearHistoryButton.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

            var titleBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabTitleBrush"];
            var mutedBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabSubtitleBrush"];
            var pillBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabInactiveBrush"];

            foreach (var item in list)
            {
                var row = new Grid
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    MinHeight = 56,
                    Background = pillBrush,
                    Tag = item.url
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var title = string.IsNullOrWhiteSpace(item.title) ? HistoryStore.HostLabel(item.url) : item.title;
                var texts = new StackPanel
                {
                    Margin = new Thickness(14, 10, 8, 10),
                    VerticalAlignment = VerticalAlignment.Center
                };
                texts.Children.Add(new TextBlock
                {
                    Text = title,
                    FontSize = 15,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = titleBrush,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                texts.Children.Add(new TextBlock
                {
                    Text = item.url ?? "",
                    FontSize = 12,
                    Foreground = mutedBrush,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                Grid.SetColumn(texts, 0);
                row.Children.Add(texts);

                var delete = new Button
                {
                    Tag = item.url,
                    Width = 44,
                    Height = 44,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon
                    {
                        FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                        Glyph = "\uE74D",
                        FontSize = 16,
                        Foreground = titleBrush
                    }
                };
                delete.Click += DeleteHistory_Click;
                Grid.SetColumn(delete, 1);
                row.Children.Add(delete);

                row.Tapped += HistoryRow_Tapped;
                HistoryStripPanel.Children.Add(row);
            }
        }

        private void HistoryRow_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var url = element?.Tag as string;
            if (string.IsNullOrEmpty(url) || ds == null)
                return;

            if (e.OriginalSource is Windows.UI.Xaml.Controls.Primitives.ButtonBase)
                return;

            e.Handled = true;
            HistoryOverlay.Visibility = Visibility.Collapsed;
            HideUrlSuggestions();
            SetUrlFieldText(url);
            ds.Navigate(url);
        }

        private void DeleteHistory_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var url = button?.Tag as string;
            if (string.IsNullOrEmpty(url))
                return;

            browsingHistory.Remove(url);
            RefreshHistoryUi();
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            browsingHistory.Clear();
            RefreshHistoryUi();
        }

        private void CloseDownloadsButton_Click(object sender, RoutedEventArgs e)
        {
            DownloadsOverlay.Visibility = Visibility.Collapsed;
        }

        private void RefreshDownloadsUi()
        {
            if (DownloadStripPanel == null)
                return;

            DownloadStripPanel.Children.Clear();
            var list = ActiveDownloads.GetSnapshot();
            DownloadsEmptyText.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var titleBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabTitleBrush"];
            var mutedBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabSubtitleBrush"];
            var pillBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["TabInactiveBrush"];
            var accentBrush = (Windows.UI.Xaml.Media.Brush)Application.Current.Resources["ConnectAccentBrush"];

            foreach (var item in list)
            {
                var showProgress = item.status == "downloading" || item.status == "transferring";
                var percent = Math.Max(0, Math.Min(100, item.percent));
                if (item.status == "transferring" && percent < 1)
                    percent = 100;

                var row = new Grid
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    MinHeight = 56,
                    Background = pillBrush,
                    Tag = item.id
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var texts = new StackPanel
                {
                    Margin = new Thickness(14, 10, 8, 10),
                    VerticalAlignment = VerticalAlignment.Center
                };
                texts.Children.Add(new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(item.fileName) ? "download" : item.fileName,
                    FontSize = 15,
                    FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                    Foreground = titleBrush,
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                texts.Children.Add(new TextBlock
                {
                    Text = FormatDownloadSubtitle(item),
                    FontSize = 12,
                    Foreground = mutedBrush,
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis
                });

                if (showProgress || item.status == "completed")
                {
                    var bar = new ProgressBar
                    {
                        Minimum = 0,
                        Maximum = 100,
                        Value = item.status == "completed" ? 100 : percent,
                        Height = 6,
                        Margin = new Thickness(0, 8, 0, 0),
                        Foreground = accentBrush,
                        Background = mutedBrush,
                        IsIndeterminate = false
                    };
                    texts.Children.Add(bar);

                    texts.Children.Add(new TextBlock
                    {
                        Text = (item.status == "completed" ? 100 : percent) + "%",
                        FontSize = 11,
                        Foreground = mutedBrush,
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                }

                Grid.SetColumn(texts, 0);
                row.Children.Add(texts);

                var delete = new Button
                {
                    Tag = item.id,
                    Width = 44,
                    Height = 44,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0),
                    Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0),
                    Content = new FontIcon
                    {
                        FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                        Glyph = "\uE74D",
                        FontSize = 16,
                        Foreground = titleBrush
                    }
                };
                delete.Click += DeleteDownload_Click;
                Grid.SetColumn(delete, 1);
                row.Children.Add(delete);

                row.Tapped += DownloadRow_Tapped;
                DownloadStripPanel.Children.Add(row);
            }
        }

        private static string FormatDownloadSubtitle(DownloadInfo item)
        {
            if (item == null)
                return "";

            var sizeText = FormatByteSize(item.size);
            switch (item.status)
            {
                case "downloading":
                    return string.Format("Downloading {0}% · {1}", item.percent, sizeText);
                case "transferring":
                    return string.Format("Saving to phone… {0}% · {1}", Math.Max(item.percent, 1), sizeText);
                case "failed":
                    return string.IsNullOrWhiteSpace(item.error) ? "Failed" : ("Failed · " + item.error);
                case "completed":
                default:
                    return sizeText;
            }
        }

        private static string FormatByteSize(long bytes)
        {
            if (bytes < 0) bytes = 0;
            if (bytes < 1024) return bytes + " B";
            double kb = bytes / 1024.0;
            if (kb < 1024) return kb.ToString("0.#") + " KB";
            double mb = kb / 1024.0;
            if (mb < 1024) return mb.ToString("0.#") + " MB";
            return (mb / 1024.0).ToString("0.##") + " GB";
        }

        private async void DownloadRow_Tapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            var element = sender as FrameworkElement;
            var id = element?.Tag as string;
            if (string.IsNullOrEmpty(id))
                return;

            if (e.OriginalSource is Windows.UI.Xaml.Controls.Primitives.ButtonBase
                || ((e.OriginalSource as FrameworkElement)?.Parent is Button))
                return;

            e.Handled = true;
            var file = await ActiveDownloads.TryGetFileAsync(id);
            if (file == null)
                return;

            try
            {
                await Windows.System.Launcher.LaunchFileAsync(file);
            }
            catch
            {
            }
        }

        private async void DeleteDownload_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as FrameworkElement;
            var id = button?.Tag as string;
            if (string.IsNullOrEmpty(id))
                return;

            await ActiveDownloads.DeleteAsync(id);
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
            ds?.NavigateBack(stopBeforeBlank: immersivePinnedMode);
        }

        private void NavigateForward_Click(object sender, RoutedEventArgs e)
        {
            ds?.NavigateForward();
        }
    }
}
