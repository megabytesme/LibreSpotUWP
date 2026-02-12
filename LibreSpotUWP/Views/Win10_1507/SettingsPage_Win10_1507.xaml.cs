using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP.Views.Win10_1507
{
    public sealed partial class SettingsPage_Win10_1507 : Page
    {
        private IMediaService _media;
        private ISpotifyAuthService _auth;

        protected bool _loading = true;
        protected bool _suppressAppearanceChange;

        public SettingsPage_Win10_1507()
        {
            InitializeComponent();
            Loaded += SettingsPage_Loaded;
        }

        private void SettingsPage_Loaded(object sender, RoutedEventArgs e)
        {
            _media = App.Media;
            _auth = App.SpotifyAuth;

            if (_auth != null)
                _auth.AuthStateChanged += (s, state) => RunOnUI(() => UpdateSpotifyApiStatus(state));

            UpdateSpotifyApiStatus(_auth?.Current);
        }

        protected async void AppearanceRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (_suppressAppearanceChange || _loading) return;

            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                var selectedMode = SettingsPage_Win10_1507.TagToMode(tag);

                SetAppearance(selectedMode);
            }
        }

        protected void SetAppearance(AppearanceMode mode)
        {
            AppearanceService.Set(mode);
            ApplyAppearanceWithoutRestart();
        }

        protected void ApplyAppearanceWithoutRestart()
        {
            var window = Window.Current;
            window.Content = null;
            var appResources = Application.Current.Resources;
            appResources.MergedDictionaries.Clear();

            switch (AppearanceService.Current)
            {
                case AppearanceMode.Win11:
                    appResources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Themes/Win11.xaml") });
                    break;
                case AppearanceMode.Win10_1709:
                    appResources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Themes/Win10_1709.xaml") });
                    break;
                default:
                    appResources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("ms-appx:///Themes/Win10_1507.xaml") });
                    break;
            }

            var frame = new Frame();
            window.Content = frame;
            frame.Navigate(NavigationHelper.GetPageType("Shell"), null);
            window.Activate();
        }

        public static AppearanceMode TagToMode(string tag)
        {
            if (tag == "1709") return AppearanceMode.Win10_1709;
            if (tag == "11") return AppearanceMode.Win11;
            return AppearanceMode.Win10_1507;
        }

        public static string ModeToTag(AppearanceMode mode)
        {
            if (mode == AppearanceMode.Win10_1709) return "1709";
            if (mode == AppearanceMode.Win11) return "11";
            return "1507";
        }

        protected async void BtnResetAllSettings_Click(object sender, RoutedEventArgs e)
        {
            var dialog = CreateDialog();
            dialog.Title = "Reset All Settings";
            dialog.Content = "This will delete all LibreSpotUWP configuration and cached data. Continue?";
            dialog.PrimaryButtonText = "Yes";
            dialog.SecondaryButtonText = "No";

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            try
            {
                // todo: reset
#if UWP1507
                await ShowSimpleDialogAsync("Restart Required", "The app will now close. Please restart it to apply the reset.");
                Application.Current.Exit();
#else
                
                await ShowSimpleDialogAsync("Restarting", "The app will now restart to apply the reset.");
                await CoreApplication.RequestRestartAsync("");
#endif
            }
            catch (Exception ex) { await ShowSimpleDialogAsync("Error", ex.Message); }
        }

        protected ContentDialog CreateDialog() => new ContentDialog();

        protected async Task ShowSimpleDialogAsync(string title, string content)
        {
            var dialog = CreateDialog();
            dialog.Title = title;
            dialog.Content = content;
            dialog.PrimaryButtonText = "OK";
            await dialog.ShowAsync();
        }

        protected async void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            var scrollContent = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Inlines =
                    {
                        new Run { Text = "LibreSpotUWP", FontWeight = FontWeights.Bold, FontSize = 18 },
                        new LineBreak(),
                        new Run { Text = $"Version {OSHelper.AppVersion} ({OSHelper.PlatformFamily}) {OSHelper.Architecture}" },
                        new LineBreak(),

                        new Run { Text = $"LibreSpot 0.8.0 (MegaBytesMe fork - FFI)" },
                        new LineBreak(),

                        new Run { Text = "LibreSpot Commit: " },
                        new Hyperlink
                        {
                            NavigateUri = new Uri("https://github.com/megabytesme/librespot/tree/ad7e58284499914ccda9b3c872589f0363263b2a"),
                            Inlines = { new Run { Text = "ad7e58284499914ccda9b3c872589f0363263b2a" } }
                        },
                        new LineBreak(),
                        new LineBreak(),

                        new Run { Text = "Copyright © 2026 MegaBytesMe" },
                        new LineBreak(),
                        new LineBreak(),

                        new Run { Text = "LibreSpotUWP is a Spotify client designed with UWP in mind, powered by LibreSpot." },
                        new LineBreak(),
                        new LineBreak(),

                        new Run { Text = "Source code available on " },
                        new Hyperlink
                        {
                            NavigateUri = new Uri("https://github.com/megabytesme/LibreSpotUWP"),
                            Inlines = { new Run { Text = "GitHub" } }
                        },
                        new LineBreak(),

                        new Run { Text = "Found a bug? Report it here: " },
                        new Hyperlink
                        {
                            NavigateUri = new Uri("https://github.com/megabytesme/LibreSpotUWP/issues"),
                            Inlines = { new Run { Text = "Issue Tracker" } }
                        },
                        new LineBreak(),
                        new LineBreak(),

                        new Run { Text = "Like what you see? Consider supporting me on " },
                        new Hyperlink
                        {
                            NavigateUri = new Uri("https://ko-fi.com/megabytesme"),
                            Inlines = { new Run { Text = "Ko-fi!" } }
                        },
                        new LineBreak(),
                        new LineBreak(),

                        new Hyperlink
                        {
                            NavigateUri = new Uri("https://github.com/megabytesme/LibreSpotUWP/blob/master/LICENSE.md"),
                            Inlines = { new Run { Text = "License:" } }
                        },
                        new LineBreak(),
                        new Run { Text = "• App (Client): CC BY-NC-SA 4.0" },
                        new LineBreak(),
                        new Run { Text = "• LibreSpot (Core): MIT License" }
                    },
                    TextWrapping = TextWrapping.Wrap
                }
            };

            var dialog = CreateDialog();
            dialog.Title = "About";
            dialog.Content = scrollContent;
            dialog.PrimaryButtonText = "OK";
            await dialog.ShowAsync();
        }

        protected async void DisclaimerButton_Click(object sender, RoutedEventArgs e)
        {
            var textBlock = new TextBlock { TextWrapping = TextWrapping.Wrap };

            textBlock.Inlines.Add(new Run
            {
                Text = "This is an unofficial, third-party Spotify client. This project is "
            });
            textBlock.Inlines.Add(new Run
            {
                Text = "not affiliated with, endorsed, or sponsored by Spotify AB.",
                FontWeight = FontWeights.Bold
            });
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add(new Run { Text = "\"Spotify\" is a trademark of Spotify AB." });
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add(new LineBreak());
            textBlock.Inlines.Add(new Run
            {
                Text = "The author (MegaBytesMe) claims no responsibility for any issues that may arise from using this app."
            });

            var dialog = CreateDialog();
            dialog.Title = "Disclaimer";
            dialog.Content = new ScrollViewer { Content = textBlock };
            dialog.PrimaryButtonText = "I Understand";
            await dialog.ShowAsync();
        }

        private void RunOnUI(Action action)
            => _ = Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () => action());

        private void UpdateLibrespotStatus(LibrespotSessionState state)
        {
            if (state == null) LibrespotStatusText.Text = "Not Initialized";
            else LibrespotStatusText.Text = state.IsConnected ? $"Connected as {state.UserName}" : "Disconnected";
        }

        private void UpdateSpotifyApiStatus(AuthState state)
        {
            SpotifyApiStatusText.Text = (state == null || state.IsExpired) ? "Web API: Logged Out" : "Web API: Authenticated";
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (ScannerPage.LastScanResult != null)
            {
                var rawData = ScannerPage.LastScanResult.Text;
                ScannerPage.LastScanResult = null;

                await ProcessQrLoginAsync(rawData);
            }
        }

        private async Task ProcessQrLoginAsync(string json)
        {
            try
            {
                var importedState = Newtonsoft.Json.JsonConvert.DeserializeObject<AuthState>(json);

                if (importedState != null)
                {
                    var stackPanel = new StackPanel();
                    stackPanel.Children.Add(new TextBlock
                    {
                        Text = "A Spotify session was found in the QR code. Would you like to import it and sign in?",
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 12)
                    });

                    var dialog = new ContentDialog
                    {
                        Title = "Import Session",
                        Content = stackPanel,
                        PrimaryButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary
                    };

                    var btnImport = new Button
                    {
                        Content = "Confirm Import",
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Style = (Style)Application.Current.Resources["AccentButtonStyle"]
                    };

                    bool userConfirmed = false;
                    btnImport.Click += (s, args) => { userConfirmed = true; dialog.Hide(); };
                    stackPanel.Children.Add(btnImport);

                    await dialog.ShowAsync();

                    if (userConfirmed)
                    {
                        RunOnUI(() =>
                        {
                            LoadingProgressRing.IsActive = true;
                            LoadingOverlay.Visibility = Visibility.Visible;
                        });
                        await _auth.ImportAuthStateAsync(importedState);

                        var successDialog = new ContentDialog
                        {
                            Title = "Success",
                            Content = new TextBlock { Text = "Session imported successfully via QR!" },
                            PrimaryButtonText = "OK"
                        };
                        await successDialog.ShowAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"QR Import Error: {ex}");

                var errorDialog = new ContentDialog
                {
                    Title = "Import Failed",
                    Content = new TextBlock { Text = "Failed to read Login QR Code. It may be corrupted or in an invalid format." },
                    PrimaryButtonText = "Close"
                };
                await errorDialog.ShowAsync();
            }
            finally
            {
                RunOnUI(() =>
                {
                    LoadingProgressRing.IsActive = false;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                });
            }
        }
    }
}