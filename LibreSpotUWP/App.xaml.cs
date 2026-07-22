using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Services;
using LibreSpotUWP.Controls;
using LibreSpotUWP.Exceptions;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

namespace LibreSpotUWP
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : Application
    {
        public static string AuthToken { get; set; }
        public static ILibrespotService Librespot { get; private set; }
        public static ISpotifyAuthService SpotifyAuth { get; private set; }
        public static ISpotifyWebService SpotifyWeb { get; private set; }
        public static IMediaService Media { get; private set; }
        public static IOfflineCatalogService OfflineCatalog { get; private set; }
        public static DownloadTrackerService Downloads { get; private set; }
        public static IBackgroundExecutionManager BackgroundExecution { get; private set; }
        public static LiveTileService LiveTiles { get; private set; }
        private ISecureStorage _secureStorage;
        private IFileSystem _fileSystem;
        private IMetadataCache _metadataCache;
        private readonly SemaphoreSlim _launchGate = new SemaphoreSlim(1, 1);
        private bool _servicesInitialized;
        private bool _startupUpdateCheckQueued;
        private SpotifyPremiumRequiredException _startupPremiumRequiredException;
        private static bool _fatalDialogOpen;
        public static AudioKeyCache KeyCache { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            Resuming += OnResuming;
            UnhandledException += App_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        public static void ApplyThemeResources()
        {
            var app = Current;
            if (app == null)
                return;

            var appResources = app.Resources;
            if (appResources == null)
                return;

            string themePath;
            switch (AppearanceService.Current)
            {
                case Models.AppearanceMode.Win11:
                    themePath = "ms-appx:///Themes/Win11.xaml";
                    break;
                case Models.AppearanceMode.Win10_1709:
                    themePath = "ms-appx:///Themes/Win10_1709.xaml";
                    break;
                default:
                    themePath = "ms-appx:///Themes/Win10_1507.xaml";
                    break;
            }

            if (HasThemeResource(appResources, themePath))
                return;

            appResources.MergedDictionaries.Clear();
            appResources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(themePath) });
        }

        private static bool HasThemeResource(ResourceDictionary appResources, string themePath)
        {
            var themeFile = themePath.Substring(themePath.LastIndexOf('/') + 1);
            return appResources.MergedDictionaries.Any(dictionary =>
                dictionary.Source != null &&
                dictionary.Source.OriginalString.EndsWith(themeFile, StringComparison.OrdinalIgnoreCase));
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            try
            {
                ApplyThemeResources();

                if (args.Kind == ActivationKind.Protocol)
                {
                    await EnsureServicesInitializedForActivationAsync();

                    var p = (ProtocolActivatedEventArgs)args;
                    var uri = p.Uri;
                    LogService.Info("[App.OnActivated] PKCE callback received.");

                    var query = uri.Query;
                    var parsed = System.Web.HttpUtility.ParseQueryString(query);

                    var code = parsed["code"];
                    var error = parsed["error"];

                    if (!string.IsNullOrEmpty(error))
                    {
                        LogService.Warn("PKCE Error: " + error);
                    }
                    else if (!string.IsNullOrEmpty(code))
                    {
                        LogService.Info("PKCE Code received.");
                        await SpotifyAuth.ExchangePkceCodeAsync(code);
                    }

                    Window.Current.Activate();
                }
            }
            catch (SpotifyPremiumRequiredException ex)
            {
                LogService.Warn($"Spotify Premium required during activation: {ex.Message}");
                await RunOnUiAsync(() => PremiumRequiredDialog.ShowAsync(ex));
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Unhandled exception during activation");
                await ShowFatalErrorAsync(ex);
            }
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                ApplyThemeResources();

                bool isSignedIn;
                bool shouldCheckForUpdates = false;
                bool shouldNavigateToLaunchTarget = false;
                var launchNavigationTag = GetLiveTileNavigationTag(e);

                await _launchGate.WaitAsync();
                try
                {
                    isSignedIn = await EnsureServicesInitializedAsync();

                    Frame rootFrame = Window.Current.Content as Frame;
                    if (rootFrame == null)
                    {
                        rootFrame = new Frame();
                        rootFrame.NavigationFailed += OnNavigationFailed;

                        if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                        {
                        }

                        Window.Current.Content = rootFrame;
                    }

                    if (e.PrelaunchActivated == false)
                    {
                        if (rootFrame.Content == null)
                        {
                            rootFrame.Navigate(
                                isSignedIn ? NavigationHelper.GetPageType("Shell") : typeof(OobePage),
                                launchNavigationTag ?? e.Arguments);
                        }
                        else
                        {
                            shouldNavigateToLaunchTarget = !string.IsNullOrWhiteSpace(launchNavigationTag);
                        }

                        shouldCheckForUpdates = !_startupUpdateCheckQueued;
                        _startupUpdateCheckQueued = true;
                    }
                }
                finally
                {
                    _launchGate.Release();
                }

                if (e.PrelaunchActivated == false)
                {
                    Window.Current.Activate();

                    if (shouldCheckForUpdates)
                        _ = CheckForUpdatesAtStartup();

                    if (_startupPremiumRequiredException != null)
                    {
                        var premiumRequired = _startupPremiumRequiredException;
                        _startupPremiumRequiredException = null;
                        await PremiumRequiredDialog.ShowAsync(premiumRequired);
                    }

                    if (shouldNavigateToLaunchTarget)
                        await NavigateToLiveTileTargetAsync(launchNavigationTag);
                }
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Unhandled exception during launch");
                await ShowFatalErrorAsync(ex);
            }
        }

        private async Task EnsureServicesInitializedForActivationAsync()
        {
            await _launchGate.WaitAsync();
            try
            {
                await EnsureServicesInitializedAsync();
            }
            finally
            {
                _launchGate.Release();
            }
        }

        private async Task<bool> EnsureServicesInitializedAsync()
        {
            if (_servicesInitialized)
            {
                LogService.Info("[App.EnsureServicesInitializedAsync] Existing app instance activated; skipping service initialization.");
                return IsCurrentUserSignedIn();
            }

            await LogService.InitializeAsync();
            _fileSystem = new FileSystem();
            _metadataCache = new FileMetadataCache(_fileSystem);
            _secureStorage = new SecureStorage();
            KeyCache = new AudioKeyCache();
            await KeyCache.InitializeAsync();
            Librespot = new LibrespotService(KeyCache);
            SpotifyAuth = new SpotifyAuthService(_secureStorage);
            SpotifyWeb = new SpotifyWebService(SpotifyAuth, _metadataCache, Librespot);
            OfflineCatalog = new OfflineCatalogService();
            Downloads = new DownloadTrackerService();
            Media = new MediaService(Librespot, SpotifyAuth, SpotifyWeb);
            BackgroundExecution = new UwpBackgroundExecutionManager();

            await Librespot.InitializeAsync();
            await OfflineCatalog.InitializeAsync();

            var hasInternet = ConnectivityHelper.HasInternetAccess();
            string token = null;

            try
            {
                token = hasInternet
                    ? await SpotifyAuth.EnsureValidAccessTokenAsync()
                    : await SpotifyAuth.GetAccessToken();

                if (hasInternet && !string.IsNullOrEmpty(token))
                    await SpotifyAuth.EnsureCurrentAccountIsPremiumAsync();
            }
            catch (SpotifyPremiumRequiredException ex)
            {
                LogService.Warn($"Spotify Premium required during launch: {ex.Message}");
                _startupPremiumRequiredException = ex;
                token = null;
            }

            var isSignedIn = !string.IsNullOrEmpty(token) ||
                (!hasInternet && HasCachedAuthState());

            if (!string.IsNullOrEmpty(token))
            {
                await Librespot.ConnectWithAccessTokenAsync(token);

                if (hasInternet)
                    await OfflineCatalog.RemoveExpiredPersistedTracksAsync();
            }

            if (hasInternet && isSignedIn)
            {
                try
                {
                    var currentUser = await SpotifyWeb.GetCurrentUserProfileAsync(forceRefresh: false);
                    SpotifyAccountManager.Instance.SetUser(currentUser?.Value);
                }
                catch (Exception ex)
                {
                    LogService.Warn($"Unable to preload current user profile during launch: {ex.Message}");
                }
            }

            await Media.InitializeAsync();
            LiveTiles = new LiveTileService(Media, SpotifyAuth, SpotifyWeb);
            await LiveTiles.InitializeAsync(isSignedIn);

            _servicesInitialized = true;
            return isSignedIn;
        }

        private static bool IsCurrentUserSignedIn()
        {
            var auth = SpotifyAuth?.Current;
            return auth != null &&
                !string.IsNullOrEmpty(auth.AccessToken) &&
                (!auth.IsExpired || !ConnectivityHelper.HasInternetAccess());
        }

        private static bool HasCachedAuthState()
        {
            var auth = SpotifyAuth?.Current;
            return auth != null &&
                !string.IsNullOrEmpty(auth.AccessToken) &&
                !string.IsNullOrEmpty(auth.RefreshToken);
        }

        private static string GetLiveTileNavigationTag(LaunchActivatedEventArgs args)
        {
            var tileArguments = args?.TileActivatedInfo?.RecentlyShownNotifications?.FirstOrDefault()?.Arguments;
            var navigationTag = LiveTileService.TryGetNavigationTagFromLaunchArguments(tileArguments);
            if (!string.IsNullOrWhiteSpace(navigationTag))
                return navigationTag;

            return LiveTileService.TryGetNavigationTagFromLaunchArguments(args?.Arguments);
        }

        private static async Task NavigateToLiveTileTargetAsync(string navigationTag)
        {
            if (string.IsNullOrWhiteSpace(navigationTag))
                return;

            await RunOnUiAsync(() =>
            {
                var shell = (Window.Current.Content as Frame)?.Content as IAppShell;
                shell?.NavigateTo(navigationTag, forceReload: true);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        private async Task CheckForUpdatesAtStartup()
        {
            try
            {
                var updateInfo = await UpdateService.CheckForUpdatesAsync();

                if (updateInfo.IsUpdateAvailable)
                {
                    await Window.Current.Dispatcher.RunAsync(
                        CoreDispatcherPriority.Normal,
                        async () =>
                        {
                            var scrollViewer = new ScrollViewer
                            {
                                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                                MaxHeight = 350
                            };

                            var panel = new StackPanel();

                            var headerText = new TextBlock
                            {
                                Text = $"Version {updateInfo.LatestVersion} is available to download!",
                                FontWeight = Windows.UI.Text.FontWeights.Bold,
                                Margin = new Thickness(0, 0, 0, 12)
                            };

                            var bodyText = new TextBlock
                            {
                                Text = updateInfo.Body,
                                TextWrapping = TextWrapping.Wrap
                            };

                            panel.Children.Add(headerText);
                            panel.Children.Add(bodyText);
                            scrollViewer.Content = panel;

                            var dialog = new ContentDialog
                            {
                                Title = "Update Available",
                                Content = scrollViewer,
                                PrimaryButtonText = "Download",
                                CloseButtonText = "Skip",
                                DefaultButton = ContentDialogButton.Primary
                            };

                            try
                            {
                                var result = await dialog.ShowAsync();
                                if (result == ContentDialogResult.Primary)
                                {
                                    if (!string.IsNullOrEmpty(updateInfo.ReleaseUrl))
                                    {
                                        await Windows.System.Launcher.LaunchUriAsync(new Uri(updateInfo.ReleaseUrl));
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                LogService.Warn($"Update dialog failed to show: {ex.Message}");
                            }
                        });
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"Update check failed during startup: {ex.Message}");
            }
        }

        /// <summary>
        /// Invoked when application execution is being suspended.  Application state is saved
        /// without knowing whether the application will be terminated or resumed with the contents
        /// of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private async void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            try
            {
                if (Media != null)
                    await Media.PrepareForSuspendingAsync();
                if (LiveTiles != null)
                    await LiveTiles.PrepareForSuspendingAsync();
            }
            catch (Exception ex)
            {
                LogService.Warn($"Unable to refresh live tile while suspending: {ex.Message}");
            }
            finally
            {
                deferral.Complete();
            }
        }

        private async void OnResuming(object sender, object e)
        {
            try
            {
                if (Media != null)
                    await Media.ResumeAfterSuspendingAsync();
            }
            catch (Exception ex)
            {
                LogService.Warn($"Unable to resume media services: {ex.Message}");
            }
        }

        private async void App_UnhandledException(object sender, Windows.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            LogService.Error(e.Exception, "Application unhandled exception");
            e.Handled = true;
            await ShowFatalErrorAsync(e.Exception);
        }

        private async void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            LogService.Error(e.Exception, "Unobserved task exception");
            e.SetObserved();
            await ShowFatalErrorAsync(e.Exception);
        }

        private static async Task ShowFatalErrorAsync(Exception ex)
        {
            try
            {
                await RunOnUiAsync(async () =>
                {
                    if (_fatalDialogOpen)
                        return;

                    _fatalDialogOpen = true;
                    try
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "Something went wrong",
                            Content = $"An error occurred.\n\n{ex.Message}\n\nLog: {LogService.LogPath}",
                            CloseButtonText = "Close"
                        };

                        await dialog.ShowAsync();
                    }
                    catch (Exception dialogEx)
                    {
                        LogService.Warn($"Unable to show fatal error dialog: {dialogEx.Message}");
                    }
                    finally
                    {
                        _fatalDialogOpen = false;
                    }
                });
            }
            catch
            {
            }
        }

        private static Task RunOnUiAsync(Func<Task> action)
        {
            var dispatcher = CoreApplication.MainView?.CoreWindow?.Dispatcher;
            if (dispatcher == null || dispatcher.HasThreadAccess)
                return action();

            var completion = new TaskCompletionSource<object>();
            var ignored = dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    await action();
                    completion.SetResult(null);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            });

            return completion.Task;
        }
    }
}
