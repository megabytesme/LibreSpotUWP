using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
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
        private ISecureStorage _secureStorage;
        private IFileSystem _fileSystem;
        private IMetadataCache _metadataCache;

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;
        }

        protected override async void OnActivated(IActivatedEventArgs args)
        {
            if (args.Kind == ActivationKind.Protocol)
            {
                var p = (ProtocolActivatedEventArgs)args;
                var uri = p.Uri;

                System.Diagnostics.Debug.WriteLine("PKCE Callback URI: " + uri);

                var query = uri.Query;
                var parsed = System.Web.HttpUtility.ParseQueryString(query);

                var code = parsed["code"];
                var error = parsed["error"];

                if (!string.IsNullOrEmpty(error))
                {
                    System.Diagnostics.Debug.WriteLine("PKCE Error: " + error);
                }
                else if (!string.IsNullOrEmpty(code))
                {
                    System.Diagnostics.Debug.WriteLine("PKCE Code received: " + code);
                    await SpotifyAuth.ExchangePkceCodeAsync(code);
                }

                Window.Current.Activate();
            }
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override async void OnLaunched(LaunchActivatedEventArgs e)
        {
            _fileSystem = new FileSystem();
            _metadataCache = new FileMetadataCache(_fileSystem);
            _secureStorage = new SecureStorage();
            Librespot = new LibrespotService();
            SpotifyAuth = new SpotifyAuthService(_secureStorage);
            SpotifyWeb = new SpotifyWebService(SpotifyAuth, _metadataCache);
            Media = new MediaService(Librespot, SpotifyAuth, SpotifyWeb);

            await Librespot.InitializeAsync();
            if (!string.IsNullOrEmpty(await SpotifyAuth.GetAccessToken()))
                await Librespot.ConnectWithAccessTokenAsync(App.AuthToken);
            await Media.InitializeAsync();

            Frame rootFrame = Window.Current.Content as Frame;

            // Do not repeat app initialization when the Window already has content,
            // just ensure that the window is active
            if (rootFrame == null)
            {
                // Create a Frame to act as the navigation context and navigate to the first page
                rootFrame = new Frame();

                rootFrame.NavigationFailed += OnNavigationFailed;

                if (e.PreviousExecutionState == ApplicationExecutionState.Terminated)
                {
                    //TODO: Load state from previously suspended application
                }

                // Place the frame in the current Window
                Window.Current.Content = rootFrame;
            }

            if (e.PrelaunchActivated == false)
            {
                if (rootFrame.Content == null)
                {
                    // When the navigation stack isn't restored navigate to the first page,
                    // configuring the new page by passing required information as a navigation
                    // parameter
                    rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }
                // Ensure the current window is active
                Window.Current.Activate();
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
            //TODO: Save application state and stop any background activity
            deferral.Complete();
        }
    }
}
