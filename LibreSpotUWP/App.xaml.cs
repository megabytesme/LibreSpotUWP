using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
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
        private ISecureStorage _secureStorage;
        private IFileSystem _fileSystem;
        private IMetadataCache _metadataCache;
        public static AudioKeyCache KeyCache { get; private set; }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            InitializeComponent();
            Suspending += OnSuspending;
            UnhandledException += App_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            try
            {
                if (args.Kind == ActivationKind.Protocol)
                {
                    var p = (ProtocolActivatedEventArgs)args;
                    var uri = p.Uri;
                    LogService.Info("PKCE Callback URI: " + uri);

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
                var token = hasInternet
                    ? await SpotifyAuth.EnsureValidAccessTokenAsync()
                    : await SpotifyAuth.GetAccessToken();

                if (!string.IsNullOrEmpty(token))
                    await Librespot.ConnectWithAccessTokenAsync(token);

                await Media.InitializeAsync();

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
                        rootFrame.Navigate(typeof(MainPage), e.Arguments);
                    }

                    Window.Current.Activate();
                }
            }
            catch (Exception ex)
            {
                LogService.Error(ex, "Unhandled exception during launch");
                await ShowFatalErrorAsync(ex);
            }
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

        /// <summary>
        /// Invoked when application execution is being suspended.  Application state is saved
        /// without knowing whether the application will be terminated or resumed with the contents
        /// of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            deferral.Complete();
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
                if (Window.Current?.Content is Frame frame && frame.Content is ContentDialog)
                    return;

                var dialog = new ContentDialog
                {
                    Title = "Something went wrong",
                    Content = $"An error occurred.\n\n{ex.Message}\n\nLog: {LogService.LogPath}",
                    CloseButtonText = "Close"
                };

                await dialog.ShowAsync();
            }
            catch
            {
            }
        }
    }
}
