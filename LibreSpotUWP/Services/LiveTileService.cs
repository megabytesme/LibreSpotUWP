using LibreSpotUWP.Controls;
using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using Newtonsoft.Json;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace LibreSpotUWP.Services
{
    public sealed class LiveTileService
    {
        private const string RecentTileSongsKey = "LiveTileRecentSongs";
        private const string RecentTileArtistsKey = "LiveTileRecentArtists";
        private const string RecentTileAlbumsKey = "LiveTileRecentAlbums";
        private const string RecentTilePlaylistsKey = "LiveTileRecentPlaylists";
        private const string UserTilePlaylistsKey = "LiveTileUserPlaylists";
        private const string FallbackLogoSource = "ms-appx:///Assets/Square150x150Logo.png";
        private const string LiveTileLaunchPrefix = "livetile:";
        private const int MaxVisibleItems = 3;
        private const int MaxCachedItems = 12;
        private const int MaxTileNotifications = 5;
        private const int RecentSourceLimit = 20;
        private static readonly TimeSpan RecentRefreshInterval = TimeSpan.FromMinutes(20);
        private static readonly TimeSpan PlaylistRefreshInterval = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MinimumOsUpdateInterval = TimeSpan.FromSeconds(10);

        private readonly IMediaService _media;
        private readonly ISpotifyAuthService _auth;
        private readonly ISpotifyWebService _web;
        private readonly object _refreshScheduleLock = new object();
        private readonly RefreshRequestCoalescer _refreshRequests = new RefreshRequestCoalescer();
        private readonly Random _random = new Random();

        private bool _initialized;
        private bool _isSignedIn;
        private string _lastMediaSignature;
        private MediaState _lastMediaState;
        private AppUserProfile _currentUser;
        private DateTimeOffset _lastRecentRefreshAt = DateTimeOffset.MinValue;
        private DateTimeOffset _lastPlaylistRefreshAt = DateTimeOffset.MinValue;
        private List<LiveTileItemSnapshot> _recentSongs;
        private List<LiveTileItemSnapshot> _recentArtists;
        private List<LiveTileItemSnapshot> _recentAlbums;
        private List<LiveTileItemSnapshot> _recentPlaylists;
        private List<LiveTileItemSnapshot> _userPlaylists;
        private Task _refreshWorkerTask;
        private DateTimeOffset _lastOsUpdateAt = DateTimeOffset.MinValue;
        private string _lastOsContentSignature;
        private int _osUpdateCount;
        private int _coalescedRequestCount;
        private int _unchangedSkipCount;

        public int OsUpdateCount => Volatile.Read(ref _osUpdateCount);
        public int CoalescedRequestCount => Volatile.Read(ref _coalescedRequestCount);
        public int UnchangedSkipCount => Volatile.Read(ref _unchangedSkipCount);

        public LiveTileService(
            IMediaService media,
            ISpotifyAuthService auth,
            ISpotifyWebService web)
        {
            _media = media ?? throw new ArgumentNullException(nameof(media));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _web = web ?? throw new ArgumentNullException(nameof(web));
            _recentSongs = new List<LiveTileItemSnapshot>();
            _recentArtists = new List<LiveTileItemSnapshot>();
            _recentAlbums = new List<LiveTileItemSnapshot>();
            _recentPlaylists = new List<LiveTileItemSnapshot>();
            _userPlaylists = new List<LiveTileItemSnapshot>();
        }

        public async Task InitializeAsync(bool isSignedIn)
        {
            if (_initialized)
                return;

            using (UiResponsivenessTelemetry.BeginOperation("LiveTile.Initialize"))
            {
                var cached = await Task.Run(() => new[]
                {
                    LoadItemsFromSettings(RecentTileSongsKey),
                    LoadItemsFromSettings(RecentTileArtistsKey),
                    LoadItemsFromSettings(RecentTileAlbumsKey),
                    LoadItemsFromSettings(RecentTilePlaylistsKey),
                    LoadItemsFromSettings(UserTilePlaylistsKey)
                }).ConfigureAwait(true);
                _recentSongs = cached[0];
                _recentArtists = cached[1];
                _recentAlbums = cached[2];
                _recentPlaylists = cached[3];
                _userPlaylists = cached[4];
            }

            _initialized = true;
            _isSignedIn = isSignedIn;
            _currentUser = SpotifyAccountManager.Instance.User;
            _lastMediaState = _media.Current?.Clone();
            _lastMediaSignature = BuildMediaSignature(_lastMediaState);

            _media.MediaStateChanged += OnMediaStateChanged;
            _auth.AuthStateChanged += OnAuthStateChanged;
            SpotifyAccountManager.Instance.UserChanged += OnUserChanged;

            QueueRefresh(
                forceRecentRefresh: isSignedIn && !ShouldShowNowPlaying(_lastMediaState),
                reason: "launch",
                delay: TimeSpan.FromSeconds(3));

            await Task.CompletedTask;
        }

        public void RefreshForSettingsChanged()
        {
            QueueRefresh(
                forceRecentRefresh: true,
                reason: "settings",
                delay: TimeSpan.FromMilliseconds(250));
        }

        public async Task PrepareForSuspendingAsync()
        {
            QueueRefresh(
                forceRecentRefresh: _isSignedIn && !ShouldShowNowPlaying(_lastMediaState),
                reason: "suspending",
                delay: TimeSpan.Zero);
            _refreshRequests.Expedite(DateTimeOffset.UtcNow);
            Task worker;
            lock (_refreshScheduleLock)
                worker = _refreshWorkerTask;
            if (worker != null)
            {
                var completed = await Task.WhenAny(worker, Task.Delay(TimeSpan.FromMilliseconds(1500)))
                    .ConfigureAwait(false);
                if (!ReferenceEquals(completed, worker))
                {
                    LogService.Telemetry(
                        "live-tile-suspend-timeout",
                        "Live-tile refresh remained queued at suspension; released the suspension deferral without blocking it.",
                        warning: true);
                }
            }
        }

        public static string TryGetNavigationTagFromLaunchArguments(string arguments)
        {
            if (!UserSettings.LiveTileOpenRandomItems || string.IsNullOrWhiteSpace(arguments))
                return null;

            if (!arguments.StartsWith(LiveTileLaunchPrefix, StringComparison.OrdinalIgnoreCase))
                return null;

            var tag = arguments.Substring(LiveTileLaunchPrefix.Length);
            if (tag.StartsWith("Artist:", StringComparison.OrdinalIgnoreCase) ||
                tag.StartsWith("Album:", StringComparison.OrdinalIgnoreCase) ||
                tag.StartsWith("Playlist:", StringComparison.OrdinalIgnoreCase))
            {
                return tag;
            }

            return null;
        }

        private void OnMediaStateChanged(object sender, MediaState state)
        {
            try
            {
                var signature = BuildMediaSignature(state);
                if (string.Equals(signature, _lastMediaSignature, StringComparison.Ordinal))
                    return;

                _lastMediaSignature = signature;
                _lastMediaState = state?.Clone();

                QueueRefresh(
                    forceRecentRefresh: _isSignedIn && !ShouldShowNowPlaying(_lastMediaState),
                    reason: "media",
                    delay: TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LiveTileService.OnMediaStateChanged] Unable to refresh tile: {ex.Message}");
            }
        }

        private void OnAuthStateChanged(object sender, AuthState state)
        {
            try
            {
                _isSignedIn = state != null &&
                    !state.IsExpired &&
                    !string.IsNullOrWhiteSpace(state.AccessToken);

                if (!_isSignedIn)
                    _currentUser = null;

                if (!_isSignedIn)
                {
                    QueueRefresh(forceRecentRefresh: false, reason: "auth", delay: TimeSpan.Zero);
                }
                else
                {
                    QueueRefresh(
                        forceRecentRefresh: !ShouldShowNowPlaying(_lastMediaState),
                        reason: "auth",
                        delay: TimeSpan.FromSeconds(2));
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LiveTileService.OnAuthStateChanged] Unable to refresh tile: {ex.Message}");
            }
        }

        private void OnUserChanged(object sender, AppUserProfile user)
        {
            try
            {
                _currentUser = user;
                QueueRefresh(forceRecentRefresh: false, reason: "user", delay: TimeSpan.FromSeconds(1));
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LiveTileService.OnUserChanged] Unable to refresh tile: {ex.Message}");
            }
        }

        private void QueueRefresh(bool forceRecentRefresh, string reason, TimeSpan delay)
        {
            var startWorker = _refreshRequests.Enqueue(
                forceRecentRefresh,
                reason,
                DateTimeOffset.UtcNow.Add(delay));
            lock (_refreshScheduleLock)
            {
                if (!startWorker)
                {
                    Interlocked.Increment(ref _coalescedRequestCount);
                    return;
                }

                _refreshWorkerTask = Task.Run(ProcessRefreshQueueAsync);
            }
        }

        private async Task ProcessRefreshQueueAsync()
        {
            while (true)
            {
                try
                {
                    if (!_refreshRequests.TryTake(
                        DateTimeOffset.UtcNow,
                        out CoalescedRefreshRequest request,
                        out TimeSpan remainingDelay))
                    {
                        if (remainingDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(remainingDelay).ConfigureAwait(false);
                            continue;
                        }

                        _refreshRequests.CompleteOrContinue();
                        return;
                    }

                    await RefreshTileAsync(request.Force, request.Reasons).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogService.Telemetry(
                        "live-tile-refresh-failure",
                        $"Unable to process coalesced live-tile refresh: {ex.Message}",
                        warning: true);
                }

                if (!_refreshRequests.CompleteOrContinue())
                    return;
            }
        }

        private async Task RefreshTileAsync(bool forceRecentRefresh, string reason)
        {
            using (UiResponsivenessTelemetry.BeginOperation("LiveTile.Refresh"))
            {
                try
                {
                    if (!UserSettings.LiveTilesEnabled || !_isSignedIn)
                    {
                        await ApplyLoggedOutTileAsync().ConfigureAwait(false);
                        return;
                    }

                    var remaining = MinimumOsUpdateInterval - (DateTimeOffset.UtcNow - _lastOsUpdateAt);
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining).ConfigureAwait(false);

                    var state = _lastMediaState ?? _media.Current;
                    var nowPlaying = ShouldShowNowPlaying(state);

                    CacheRecentSongFromState(state);
                    if (!nowPlaying)
                        await EnsureTileDataAsync(forceRecentRefresh).ConfigureAwait(false);

                    var notifications = await Task.Run(() =>
                        BuildNotifications(state, nowPlaying)
                            .Where(notification => notification != null)
                            .Take(MaxTileNotifications)
                            .ToList()).ConfigureAwait(false);

                    if (notifications.Count == 0)
                    {
                        await ApplyLoggedOutTileAsync().ConfigureAwait(false);
                        return;
                    }

                    var signature = BuildNotificationSignature(notifications);
                    if (string.Equals(signature, _lastOsContentSignature, StringComparison.Ordinal))
                    {
                        Interlocked.Increment(ref _unchangedSkipCount);
                        LogService.Telemetry(
                            "live-tile-unchanged",
                            $"Skipped unchanged live-tile content for merged reasons={reason}.");
                        return;
                    }

                    await Task.Run(() => ApplyNotifications(notifications)).ConfigureAwait(false);
                    _lastOsContentSignature = signature;
                    _lastOsUpdateAt = DateTimeOffset.UtcNow;
                    Interlocked.Increment(ref _osUpdateCount);
                    LogService.Telemetry(
                        "live-tile-updated",
                        $"Refreshed live tile for merged reasons={reason}; osUpdateCount={OsUpdateCount}, coalescedRequests={CoalescedRequestCount}, unchangedSkips={UnchangedSkipCount}.");
                }
                catch (Exception ex)
                {
                    LogService.Telemetry(
                        "live-tile-refresh-failure",
                        $"Unable to refresh live tile for merged reasons={reason}: {ex.Message}",
                        warning: true);
                }
            }
        }

        private List<TileNotification> BuildNotifications(MediaState state, bool nowPlaying)
        {
            var notifications = new List<TileNotification>();
            var alternates = new List<TileNotification>();

            if (nowPlaying && UserSettings.LiveTileNowPlayingEnabled)
            {
                notifications.Add(CreateNowPlayingNotification(state));
                return notifications;
            }

            if (nowPlaying)
                return notifications;

            if (!nowPlaying && UserSettings.LiveTileRecentSongsEnabled)
                notifications.Add(CreateRecentSongsNotification(state));

            if (UserSettings.LiveTileRecentArtistsEnabled)
                alternates.Add(CreateRecentEntityNotification("Recent artists", "Artist", _recentArtists, "rartists"));

            if (UserSettings.LiveTileRecentPlaylistsEnabled)
                alternates.Add(CreateRecentEntityNotification("Recent playlists", "Playlist", _recentPlaylists, "rplaylists"));

            if (UserSettings.LiveTileRecentAlbumsEnabled)
                alternates.Add(CreateRecentEntityNotification("Recent albums", "Album", _recentAlbums, "ralbums"));

            if (UserSettings.LiveTileSpotifyPlaylistEnabled)
                alternates.Add(CreateSpotifyPlaylistNotification());

            if (UserSettings.LiveTileRandomArtistEnabled)
                alternates.Add(CreateRandomEntityNotification("Random artist", RandomItem(_recentArtists), "rndartist"));

            if (UserSettings.LiveTileRandomPlaylistEnabled)
                alternates.Add(CreateRandomEntityNotification("Random playlist", RandomItem(_userPlaylists), "rndplaylist"));

            if (UserSettings.LiveTileRandomAlbumEnabled)
                alternates.Add(CreateRandomEntityNotification("Random album", RandomItem(_recentAlbums), "rndalbum"));

            if (_currentUser != null && UserSettings.LiveTileProfileEnabled)
                alternates.Add(CreateProfileNotification(_currentUser));

            Shuffle(alternates);
            notifications.AddRange(alternates);

            return notifications;
        }

        private async Task EnsureTileDataAsync(bool force)
        {
            var now = DateTimeOffset.UtcNow;
            var wantsRecentData =
                UserSettings.LiveTileRecentSongsEnabled ||
                UserSettings.LiveTileRecentArtistsEnabled ||
                UserSettings.LiveTileRecentAlbumsEnabled ||
                UserSettings.LiveTileRecentPlaylistsEnabled ||
                UserSettings.LiveTileRandomArtistEnabled ||
                UserSettings.LiveTileRandomAlbumEnabled;
            var wantsPlaylistData =
                UserSettings.LiveTileRecentPlaylistsEnabled ||
                UserSettings.LiveTileRandomPlaylistEnabled ||
                UserSettings.LiveTileSpotifyPlaylistEnabled;

            if (!ConnectivityHelper.HasInternetAccess())
            {
                ReloadRecentItemsFromSettings();
                _userPlaylists = LoadItemsFromSettings(UserTilePlaylistsKey);
                return;
            }

            if (wantsRecentData &&
                (force || _recentSongs.Count == 0 || now - _lastRecentRefreshAt >= RecentRefreshInterval))
            {
                await EnsureRecentlyPlayedAsync(force).ConfigureAwait(false);
            }

            if (wantsPlaylistData &&
                (force || _userPlaylists.Count == 0 || now - _lastPlaylistRefreshAt >= PlaylistRefreshInterval))
            {
                await EnsureUserPlaylistsAsync(force).ConfigureAwait(false);
            }
        }

        private async Task EnsureRecentlyPlayedAsync(bool force)
        {
            try
            {
                var response = await _web.GetRecentlyPlayedAsync(RecentSourceLimit, forceRefresh: force)
                    .ConfigureAwait(false);
                var items = response?.Value?.Items ?? new List<PlayHistoryItem>();

                var songs = new List<LiveTileItemSnapshot>();
                var artists = new List<LiveTileItemSnapshot>();
                var albums = new List<LiveTileItemSnapshot>();
                var playlistIds = new List<string>();

                foreach (var item in items)
                {
                    var track = item.Track;
                    if (track == null)
                        continue;

                    AddUnique(songs, FromFullTrack(track), MaxCachedItems);
                    AddUnique(albums, FromSimpleAlbum(track.Album), MaxCachedItems);

                    foreach (var artist in track.Artists ?? Enumerable.Empty<SimpleArtist>())
                    {
                        AddUnique(
                            artists,
                            FromSimpleArtist(artist, track.Album?.Images?.FirstOrDefault()?.Url),
                            MaxCachedItems);
                    }

                    var contextUri = item.Context?.Uri;
                    if (!string.IsNullOrWhiteSpace(contextUri) &&
                        contextUri.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase))
                    {
                        var playlistId = contextUri.Substring("spotify:playlist:".Length);
                        if (!playlistIds.Any(id => string.Equals(id, playlistId, StringComparison.OrdinalIgnoreCase)))
                            playlistIds.Add(playlistId);
                    }
                }

                var playlists = await LoadRecentPlaylistsAsync(playlistIds, force).ConfigureAwait(false);

                if (songs.Count > 0)
                    _recentSongs = songs;
                if (artists.Count > 0)
                    _recentArtists = artists;
                if (albums.Count > 0)
                    _recentAlbums = albums;
                if (playlists.Count > 0)
                    _recentPlaylists = playlists;

                _lastRecentRefreshAt = DateTimeOffset.UtcNow;
                SaveRecentItemsToSettings();
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LiveTileService.EnsureRecentlyPlayedAsync] Unable to load recent tile data: {ex.Message}");
                ReloadRecentItemsFromSettings();
            }
        }

        private async Task<List<LiveTileItemSnapshot>> LoadRecentPlaylistsAsync(
            IEnumerable<string> playlistIds,
            bool force)
        {
            var playlists = new List<LiveTileItemSnapshot>();

            foreach (var playlistId in playlistIds.Take(MaxCachedItems))
            {
                try
                {
                    var response = await _web.GetPlaylistAsync(playlistId, forceRefresh: force)
                        .ConfigureAwait(false);
                    AddUnique(playlists, FromFullPlaylist(response?.Value), MaxCachedItems);
                }
                catch (Exception ex)
                {
                    LogService.Warn($"[LiveTileService.LoadRecentPlaylistsAsync] Unable to load playlist {playlistId}: {ex.Message}");
                }
            }

            return playlists;
        }

        private async Task EnsureUserPlaylistsAsync(bool force)
        {
            try
            {
                var response = await _web.GetCurrentUserPlaylistsAsync(forceRefresh: force)
                    .ConfigureAwait(false);
                var playlists = response?.Value?.Items?
                    .Select(FromFullPlaylist)
                    .Where(item => item != null)
                    .ToList() ?? new List<LiveTileItemSnapshot>();

                if (playlists.Count > 0)
                {
                    _userPlaylists = playlists.Take(MaxCachedItems).ToList();
                    _lastPlaylistRefreshAt = DateTimeOffset.UtcNow;
                    SaveItemsToSettings(UserTilePlaylistsKey, _userPlaylists);
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LiveTileService.EnsureUserPlaylistsAsync] Unable to load user playlists: {ex.Message}");
                _userPlaylists = LoadItemsFromSettings(UserTilePlaylistsKey);
            }
        }

        private void CacheRecentSongFromState(MediaState state)
        {
            var song = FromMediaState(state);
            if (song == null || string.IsNullOrWhiteSpace(song.Title))
                return;

            AddUniqueToFront(_recentSongs, song, MaxCachedItems);
            SaveItemsToSettings(RecentTileSongsKey, _recentSongs);
        }

        private async Task ApplyLoggedOutTileAsync()
        {
            const string signature = "manifest-default";
            if (string.Equals(_lastOsContentSignature, signature, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _unchangedSkipCount);
                return;
            }

            var remaining = MinimumOsUpdateInterval - (DateTimeOffset.UtcNow - _lastOsUpdateAt);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining).ConfigureAwait(false);

            await Task.Run(ApplyLoggedOutTile).ConfigureAwait(false);
            _lastOsContentSignature = signature;
            _lastOsUpdateAt = DateTimeOffset.UtcNow;
            Interlocked.Increment(ref _osUpdateCount);
        }

        private static void ApplyLoggedOutTile()
        {
            var updater = TileUpdateManager.CreateTileUpdaterForApplication();
            updater.EnableNotificationQueue(false);
            updater.Clear();
        }

        private static void ApplyNotifications(IReadOnlyList<TileNotification> notifications)
        {
            var updater = TileUpdateManager.CreateTileUpdaterForApplication();
            updater.EnableNotificationQueue(notifications.Count > 1);
            updater.Clear();

            foreach (var notification in notifications)
            {
                updater.Update(notification);
            }
        }

        private static string BuildNotificationSignature(IReadOnlyList<TileNotification> notifications)
        {
            if (notifications == null || notifications.Count == 0)
                return "manifest-default";

            return string.Join("\n", notifications.Select(notification =>
                (notification?.Tag ?? string.Empty) + ":" +
                (notification?.Content?.GetXml() ?? string.Empty)));
        }

        private static TileNotification CreateNowPlayingNotification(MediaState state)
        {
            var track = FromMediaState(state) ?? new LiveTileItemSnapshot
            {
                Kind = LiveTileItemKind.Track,
                Title = "Now playing",
                Subtitle = "LibreSpotUWP"
            };
            var imageSource = ResolveTileImageSource(track.ImageUrl, useFallback: true);
            var sourceName = state?.IsSpotifyConnectRemote == true
                ? $"Playing on {SafeText(state.SpotifyConnectDeviceName, "Spotify Connect")}"
                : "Now playing";
            var expiresAt = GetNowPlayingExpiration(state);

            return CreateNotification(
                BuildTileDocument(
                    BuildVisual(
                        BuildSmallBinding("Now", track.Title),
                        BuildNowPlayingMediumBinding(track, imageSource, sourceName),
                        BuildNowPlayingWideBinding(track, imageSource, sourceName),
                        BuildNowPlayingLargeBinding(track, imageSource, sourceName))),
                tag: "now",
                expirationTime: expiresAt);
        }

        private TileNotification CreateRecentSongsNotification(MediaState state)
        {
            var songs = _recentSongs.Count > 0
                ? _recentSongs
                : new List<LiveTileItemSnapshot>();

            if (songs.Count == 0)
            {
                var fallback = FromMediaState(state);
                if (fallback != null)
                    songs.Add(fallback);
            }

            if (songs.Count == 0)
            {
                songs.Add(new LiveTileItemSnapshot
                {
                    Kind = LiveTileItemKind.Track,
                    Title = "Ready to play",
                    Subtitle = "Open LibreSpotUWP"
                });
            }

            var imageSource = ResolveTileImageSource(songs[0].ImageUrl, useFallback: true);
            return CreateNotification(
                BuildTileDocument(
                    BuildVisual(
                        BuildSmallBinding("Songs", songs[0].Title),
                        BuildRecentSongsMediumBinding(songs, imageSource),
                        BuildRecentCollectionWideBinding("Recent songs", songs, imageSource),
                        BuildRecentCollectionLargeBinding("Recent songs", songs, imageSource))),
                tag: "rsongs",
                expirationTime: DateTimeOffset.UtcNow.AddDays(3));
        }

        private static TileNotification CreateRecentEntityNotification(
            string header,
            string smallLabel,
            IReadOnlyList<LiveTileItemSnapshot> items,
            string tag)
        {
            if (items == null || items.Count == 0)
                return null;

            var imageSource = ResolveTileImageSource(items[0].ImageUrl, useFallback: true);
            return CreateNotification(
                BuildTileDocument(
                    BuildVisual(
                        BuildSmallBinding(smallLabel, items[0].Title),
                        BuildRecentEntityMediumBinding(header, items, imageSource),
                        BuildRecentCollectionWideBinding(header, items, imageSource),
                        BuildRecentCollectionLargeBinding(header, items, imageSource))),
                tag: tag,
                expirationTime: DateTimeOffset.UtcNow.AddDays(3));
        }

        private TileNotification CreateRandomEntityNotification(
            string header,
            LiveTileItemSnapshot item,
            string tag)
        {
            if (item == null)
                return null;

            return CreateSingleEntityNotification(header, item, tag);
        }

        private TileNotification CreateSpotifyPlaylistNotification()
        {
            var spotifyOwned = _userPlaylists
                .Where(IsSpotifyOwnedPlaylist)
                .ToList();
            var item = spotifyOwned.Count > 0
                ? RandomItem(spotifyOwned)
                : RandomItem(_userPlaylists);

            if (item == null)
                return null;

            return CreateSingleEntityNotification("Spotify playlist", item, "spplaylist");
        }

        private TileNotification CreateSingleEntityNotification(
            string header,
            LiveTileItemSnapshot item,
            string tag)
        {
            var imageSource = ResolveTileImageSource(item.ImageUrl, useFallback: true);
            var launchArguments = UserSettings.LiveTileOpenRandomItems && !string.IsNullOrWhiteSpace(item.LaunchTag)
                ? LiveTileLaunchPrefix + item.LaunchTag
                : null;

            return CreateNotification(
                BuildTileDocument(
                    BuildVisual(
                        BuildSmallBinding(header, item.Title),
                        BuildSingleEntityMediumBinding(header, item, imageSource),
                        BuildSingleEntityWideBinding(header, item, imageSource),
                        BuildSingleEntityLargeBinding(header, item, imageSource),
                        launchArguments)),
                tag: tag,
                expirationTime: DateTimeOffset.UtcNow.AddDays(3));
        }

        private static TileNotification CreateProfileNotification(AppUserProfile user)
        {
            var displayName = SafeText(user.DisplayName, SafeText(user.Id, "Spotify profile"));
            var imageUrl = user.Images?.FirstOrDefault()?.Url;
            var imageSource = ResolveTileImageSource(imageUrl, useFallback: true);

            return CreateNotification(
                BuildTileDocument(
                    BuildVisual(
                        BuildSmallBinding("Profile", displayName),
                        BuildProfileMediumBinding(displayName, imageSource),
                        BuildProfileWideBinding(displayName, imageSource),
                        BuildProfileLargeBinding(displayName, imageSource))),
                tag: "profile",
                expirationTime: DateTimeOffset.UtcNow.AddDays(3));
        }

        private static XDocument BuildTileDocument(XElement visual)
        {
            return new XDocument(new XElement("tile", visual));
        }

        private static XElement BuildVisual(
            XElement small,
            XElement medium,
            XElement wide,
            XElement large,
            string arguments = null)
        {
            var visual = new XElement(
                "visual",
                new XAttribute("branding", "nameAndLogo"),
                new XAttribute("displayName", "LibreSpotUWP"),
                small,
                medium,
                wide,
                large);

            if (!string.IsNullOrWhiteSpace(arguments))
                visual.SetAttributeValue("arguments", arguments);

            return visual;
        }

        private static XElement BuildSmallBinding(string label, string value)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileSmall"),
                new XAttribute("branding", "logo"),
                Text(label, "captionSubtle"),
                Text(value, "caption", wrap: true, maxLines: 2));
        }

        private static XElement BuildNowPlayingMediumBinding(
            LiveTileItemSnapshot track,
            string imageSource,
            string sourceName)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileMedium"),
                new XAttribute("branding", "name"),
                Image(imageSource, placement: "peek", overlay: 15),
                Text(sourceName, "captionSubtle"),
                Text(track.Title, "base", wrap: true, maxLines: 2),
                Text(track.Subtitle, "captionSubtle", wrap: true, maxLines: 1));
        }

        private static XElement BuildNowPlayingWideBinding(
            LiveTileItemSnapshot track,
            string imageSource,
            string sourceName)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileWide"),
                Image(imageSource, placement: "background", overlay: 65),
                Text(sourceName, "captionSubtle"),
                Text(track.Title, "subtitle", wrap: true, maxLines: 2),
                Text(track.Subtitle, "bodySubtle", wrap: true, maxLines: 1),
                Text(track.Detail, "captionSubtle", wrap: true, maxLines: 1));
        }

        private static XElement BuildNowPlayingLargeBinding(
            LiveTileItemSnapshot track,
            string imageSource,
            string sourceName)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileLarge"),
                Image(imageSource, placement: "background", overlay: 70),
                Text(sourceName, "captionSubtle"),
                Text(track.Title, "title", wrap: true, maxLines: 2),
                Text(track.Subtitle, "subtitleSubtle", wrap: true, maxLines: 2),
                Text(track.Detail, "bodySubtle", wrap: true, maxLines: 2));
        }

        private static XElement BuildRecentSongsMediumBinding(
            IReadOnlyList<LiveTileItemSnapshot> songs,
            string imageSource)
        {
            var first = songs[0];
            return new XElement(
                "binding",
                new XAttribute("template", "TileMedium"),
                new XAttribute("branding", "name"),
                Image(imageSource, placement: "peek", overlay: 20),
                Text("Recent songs", "captionSubtle"),
                Text(first.Title, "base", wrap: true, maxLines: 2),
                Text(first.Subtitle, "captionSubtle", wrap: true, maxLines: 1));
        }

        private static XElement BuildRecentEntityMediumBinding(
            string header,
            IReadOnlyList<LiveTileItemSnapshot> items,
            string imageSource)
        {
            var first = items[0];
            return new XElement(
                "binding",
                new XAttribute("template", "TileMedium"),
                new XAttribute("branding", "name"),
                Image(imageSource, placement: "peek", overlay: 20),
                Text(header, "captionSubtle"),
                Text(first.Title, "base", wrap: true, maxLines: 2),
                Text(first.Subtitle, "captionSubtle", wrap: true, maxLines: 1));
        }

        private static XElement BuildRecentCollectionWideBinding(
            string header,
            IReadOnlyList<LiveTileItemSnapshot> items,
            string imageSource)
        {
            var binding = new XElement(
                "binding",
                new XAttribute("template", "TileWide"),
                Image(imageSource, placement: "background", overlay: 70),
                Text(header, "captionSubtle"));

            foreach (var item in items.Take(MaxVisibleItems))
            {
                binding.Add(Text(item.Title, "base", wrap: true, maxLines: 1));
                binding.Add(Text(item.Subtitle, "captionSubtle", wrap: true, maxLines: 1));
            }

            return binding;
        }

        private static XElement BuildRecentCollectionLargeBinding(
            string header,
            IReadOnlyList<LiveTileItemSnapshot> items,
            string imageSource)
        {
            var binding = new XElement(
                "binding",
                new XAttribute("template", "TileLarge"),
                Image(imageSource, placement: "background", overlay: 75),
                Text(header, "captionSubtle"));

            foreach (var item in items.Take(MaxVisibleItems))
            {
                binding.Add(
                    new XElement(
                        "group",
                        new XElement(
                            "subgroup",
                            Text(item.Title, "base", wrap: true, maxLines: 1),
                            Text(item.Subtitle, "captionSubtle", wrap: true, maxLines: 1))));
            }

            return binding;
        }

        private static XElement BuildSingleEntityMediumBinding(
            string header,
            LiveTileItemSnapshot item,
            string imageSource)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileMedium"),
                new XAttribute("branding", "name"),
                Image(imageSource, placement: "peek", overlay: 20),
                Text(header, "captionSubtle"),
                Text(item.Title, "base", wrap: true, maxLines: 2),
                Text(item.Subtitle, "captionSubtle", wrap: true, maxLines: 1));
        }

        private static XElement BuildSingleEntityWideBinding(
            string header,
            LiveTileItemSnapshot item,
            string imageSource)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileWide"),
                Image(imageSource, placement: "background", overlay: 65),
                Text(header, "captionSubtle"),
                Text(item.Title, "subtitle", wrap: true, maxLines: 2),
                Text(item.Subtitle, "bodySubtle", wrap: true, maxLines: 1),
                Text(item.Detail, "captionSubtle", wrap: true, maxLines: 1));
        }

        private static XElement BuildSingleEntityLargeBinding(
            string header,
            LiveTileItemSnapshot item,
            string imageSource)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileLarge"),
                Image(imageSource, placement: "background", overlay: 70),
                Text(header, "captionSubtle"),
                Text(item.Title, "title", wrap: true, maxLines: 2),
                Text(item.Subtitle, "subtitleSubtle", wrap: true, maxLines: 2),
                Text(item.Detail, "bodySubtle", wrap: true, maxLines: 2));
        }

        private static XElement BuildProfileMediumBinding(string displayName, string imageSource)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileMedium"),
                new XAttribute("branding", "name"),
                new XAttribute("hint-textStacking", "center"),
                Image(imageSource, crop: "circle"),
                Text("Signed in as", "captionSubtle", align: "center"),
                Text(displayName, "base", wrap: true, maxLines: 2, align: "center"));
        }

        private static XElement BuildProfileWideBinding(string displayName, string imageSource)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileWide"),
                new XElement(
                    "group",
                    new XElement(
                        "subgroup",
                        new XAttribute("hint-weight", "35"),
                        Image(imageSource, crop: "circle")),
                    new XElement(
                        "subgroup",
                        new XAttribute("hint-textStacking", "center"),
                        Text("Spotify profile", "captionSubtle"),
                        Text(displayName, "subtitle", wrap: true, maxLines: 2))));
        }

        private static XElement BuildProfileLargeBinding(string displayName, string imageSource)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileLarge"),
                new XAttribute("hint-textStacking", "center"),
                new XElement(
                    "group",
                    new XElement("subgroup", new XAttribute("hint-weight", "1")),
                    new XElement(
                        "subgroup",
                        new XAttribute("hint-weight", "2"),
                        Image(imageSource, crop: "circle")),
                    new XElement("subgroup", new XAttribute("hint-weight", "1"))),
                Text("Spotify profile", "bodySubtle", align: "center"),
                Text(displayName, "title", wrap: true, maxLines: 2, align: "center"));
        }

        private static XElement Text(
            string value,
            string style = null,
            bool wrap = false,
            int? maxLines = null,
            string align = null)
        {
            var element = new XElement("text", TrimForTile(value));

            if (!string.IsNullOrWhiteSpace(style))
                element.SetAttributeValue("hint-style", style);

            if (wrap)
                element.SetAttributeValue("hint-wrap", "true");

            if (maxLines.HasValue)
                element.SetAttributeValue("hint-maxLines", maxLines.Value);

            if (!string.IsNullOrWhiteSpace(align))
                element.SetAttributeValue("hint-align", align);

            return element;
        }

        private static XElement Image(
            string source,
            string placement = null,
            int? overlay = null,
            string crop = null)
        {
            var element = new XElement("image", new XAttribute("src", source));

            if (!string.IsNullOrWhiteSpace(placement))
                element.SetAttributeValue("placement", placement);

            if (overlay.HasValue)
                element.SetAttributeValue("hint-overlay", overlay.Value);

            if (!string.IsNullOrWhiteSpace(crop))
                element.SetAttributeValue("hint-crop", crop);

            return element;
        }

        private static TileNotification CreateNotification(
            XDocument document,
            string tag,
            DateTimeOffset expirationTime)
        {
            var xml = new XmlDocument();
            xml.LoadXml(document.ToString(SaveOptions.DisableFormatting));

            var notification = new TileNotification(xml)
            {
                Tag = tag,
                ExpirationTime = expirationTime
            };

            return notification;
        }

        private static DateTimeOffset GetNowPlayingExpiration(MediaState state)
        {
            var minimum = TimeSpan.FromMinutes(10);
            var maximum = TimeSpan.FromHours(6);
            var remaining = minimum;

            if (state != null && state.DurationMs > state.PositionMs)
            {
                remaining = TimeSpan.FromMilliseconds(state.DurationMs - state.PositionMs)
                    .Add(TimeSpan.FromMinutes(5));
            }

            if (remaining < minimum)
                remaining = minimum;
            if (remaining > maximum)
                remaining = maximum;

            return DateTimeOffset.UtcNow.Add(remaining);
        }

        private static bool ShouldShowNowPlaying(MediaState state)
        {
            return state?.Track != null &&
                (state.PlaybackState == LibrespotPlaybackState.Playing ||
                 state.PlaybackState == LibrespotPlaybackState.Loading);
        }

        private static string BuildMediaSignature(MediaState state)
        {
            if (state == null)
                return string.Empty;

            var track = state.Track;
            return string.Join("|", new[]
            {
                state.PlaybackState.ToString(),
                track?.Uri ?? string.Empty,
                track?.Name ?? string.Empty,
                track?.Artist ?? string.Empty,
                track?.Album ?? string.Empty,
                state.ArtworkUri ?? string.Empty,
                state.ContextName ?? string.Empty,
                state.IsSpotifyConnectRemote.ToString(),
                state.SpotifyConnectDeviceName ?? string.Empty
            });
        }

        private static LiveTileItemSnapshot FromMediaState(MediaState state)
        {
            if (state?.Track == null && state?.Metadata == null)
                return null;

            var metadata = state.Metadata;
            var track = state.Track;
            var id = ExtractSpotifyId(metadata?.Uri ?? track?.Uri, "track");

            return new LiveTileItemSnapshot
            {
                Kind = LiveTileItemKind.Track,
                Id = id,
                Uri = metadata?.Uri ?? track?.Uri,
                Title = SafeText(metadata?.Name, track?.Name),
                Subtitle = SafeText(GetArtistLine(metadata), track?.Artist),
                Detail = SafeText(metadata?.Album?.Name, track?.Album, state.ContextName),
                ImageUrl = SafeText(
                    state.ArtworkUri,
                    metadata?.Album?.Images?.FirstOrDefault()?.Url,
                    track?.CoverUrl)
            };
        }

        private static LiveTileItemSnapshot FromFullTrack(FullTrack track)
        {
            if (track == null)
                return null;

            return new LiveTileItemSnapshot
            {
                Kind = LiveTileItemKind.Track,
                Id = track.Id,
                Uri = track.Uri,
                Title = track.Name,
                Subtitle = GetArtistLine(track),
                Detail = track.Album?.Name,
                ImageUrl = track.Album?.Images?.FirstOrDefault()?.Url
            };
        }

        private static LiveTileItemSnapshot FromSimpleArtist(SimpleArtist artist, string fallbackImageUrl)
        {
            if (artist == null)
                return null;

            return new LiveTileItemSnapshot
            {
                Kind = LiveTileItemKind.Artist,
                Id = artist.Id,
                Uri = artist.Uri,
                Title = artist.Name,
                Subtitle = "Artist",
                ImageUrl = fallbackImageUrl,
                LaunchTag = string.IsNullOrWhiteSpace(artist.Id) ? null : "Artist:" + artist.Id
            };
        }

        private static LiveTileItemSnapshot FromSimpleAlbum(SimpleAlbum album)
        {
            if (album == null)
                return null;

            return new LiveTileItemSnapshot
            {
                Kind = LiveTileItemKind.Album,
                Id = album.Id,
                Uri = album.Uri,
                Title = album.Name,
                Subtitle = GetArtistLine(album),
                Detail = album.ReleaseDate,
                ImageUrl = album.Images?.FirstOrDefault()?.Url,
                LaunchTag = string.IsNullOrWhiteSpace(album.Id) ? null : "Album:" + album.Id
            };
        }

        private static LiveTileItemSnapshot FromFullPlaylist(FullPlaylist playlist)
        {
            if (playlist == null)
                return null;

            return new LiveTileItemSnapshot
            {
                Kind = LiveTileItemKind.Playlist,
                Id = playlist.Id,
                Uri = playlist.Uri,
                Title = playlist.Name,
                Subtitle = SafeText(playlist.Owner?.DisplayName, playlist.Owner?.Id, "Playlist"),
                ImageUrl = playlist.Images?.FirstOrDefault()?.Url,
                LaunchTag = string.IsNullOrWhiteSpace(playlist.Id) ? null : "Playlist:" + playlist.Id,
                OwnerId = playlist.Owner?.Id,
                OwnerName = playlist.Owner?.DisplayName
            };
        }

        private static string GetArtistLine(FullTrack track)
        {
            var artists = track?.Artists?
                .Select(artist => artist?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name));

            return artists == null ? null : string.Join(", ", artists);
        }

        private static string GetArtistLine(SimpleAlbum album)
        {
            var artists = album?.Artists?
                .Select(artist => artist?.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name));

            return artists == null ? null : string.Join(", ", artists);
        }

        private static string ResolveTileImageSource(string value, bool useFallback)
        {
            var normalized = ImageUriHelper.NormalizeImageUrl(value);
            if (!string.IsNullOrWhiteSpace(normalized) &&
                Uri.TryCreate(normalized, UriKind.Absolute, out var _))
            {
                return normalized;
            }

            return useFallback ? FallbackLogoSource : null;
        }

        private LiveTileItemSnapshot RandomItem(IReadOnlyList<LiveTileItemSnapshot> items)
        {
            if (items == null || items.Count == 0)
                return null;

            return items[_random.Next(items.Count)];
        }

        private void Shuffle<T>(IList<T> items)
        {
            if (items == null || items.Count < 2)
                return;

            for (var i = items.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                var temp = items[i];
                items[i] = items[j];
                items[j] = temp;
            }
        }

        private static void AddUnique(
            List<LiveTileItemSnapshot> items,
            LiveTileItemSnapshot item,
            int maxItems)
        {
            if (items == null || item == null || string.IsNullOrWhiteSpace(item.Title))
                return;

            if (items.Any(existing => IsSameItem(existing, item)))
                return;

            items.Add(item);

            if (items.Count > maxItems)
                items.RemoveRange(maxItems, items.Count - maxItems);
        }

        private static void AddUniqueToFront(
            List<LiveTileItemSnapshot> items,
            LiveTileItemSnapshot item,
            int maxItems)
        {
            if (items == null || item == null || string.IsNullOrWhiteSpace(item.Title))
                return;

            items.RemoveAll(existing => IsSameItem(existing, item));
            items.Insert(0, item);

            if (items.Count > maxItems)
                items.RemoveRange(maxItems, items.Count - maxItems);
        }

        private static bool IsSameItem(LiveTileItemSnapshot left, LiveTileItemSnapshot right)
        {
            if (left == null || right == null || left.Kind != right.Kind)
                return false;

            if (!string.IsNullOrWhiteSpace(left.Id) &&
                !string.IsNullOrWhiteSpace(right.Id))
            {
                return string.Equals(left.Id, right.Id, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(left.Uri) &&
                !string.IsNullOrWhiteSpace(right.Uri))
            {
                return string.Equals(left.Uri, right.Uri, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(left.Title, right.Title, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSpotifyOwnedPlaylist(LiveTileItemSnapshot item)
        {
            if (item == null || item.Kind != LiveTileItemKind.Playlist)
                return false;

            return string.Equals(item.OwnerId, "spotify", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.OwnerName, "Spotify", StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractSpotifyId(string uri, string type)
        {
            if (string.IsNullOrWhiteSpace(uri))
                return null;

            var prefix = "spotify:" + type + ":";
            return uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? uri.Substring(prefix.Length)
                : null;
        }

        private static string SafeText(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return string.Empty;
        }

        private static string TrimForTile(string value)
        {
            value = SafeText(value);
            return value.Length <= 96 ? value : value.Substring(0, 93) + "...";
        }

        private static List<LiveTileItemSnapshot> LoadItemsFromSettings(string key)
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (!settings.Values.TryGetValue(key, out object raw) ||
                    !(raw is string json) ||
                    string.IsNullOrWhiteSpace(json))
                {
                    return new List<LiveTileItemSnapshot>();
                }

                return JsonConvert.DeserializeObject<List<LiveTileItemSnapshot>>(json) ??
                    new List<LiveTileItemSnapshot>();
            }
            catch
            {
                return new List<LiveTileItemSnapshot>();
            }
        }

        private void ReloadRecentItemsFromSettings()
        {
            if (_recentSongs.Count == 0)
                _recentSongs = LoadItemsFromSettings(RecentTileSongsKey);
            if (_recentArtists.Count == 0)
                _recentArtists = LoadItemsFromSettings(RecentTileArtistsKey);
            if (_recentAlbums.Count == 0)
                _recentAlbums = LoadItemsFromSettings(RecentTileAlbumsKey);
            if (_recentPlaylists.Count == 0)
                _recentPlaylists = LoadItemsFromSettings(RecentTilePlaylistsKey);
        }

        private void SaveRecentItemsToSettings()
        {
            SaveItemsToSettings(RecentTileSongsKey, _recentSongs);
            SaveItemsToSettings(RecentTileArtistsKey, _recentArtists);
            SaveItemsToSettings(RecentTileAlbumsKey, _recentAlbums);
            SaveItemsToSettings(RecentTilePlaylistsKey, _recentPlaylists);
        }

        private static void SaveItemsToSettings(string key, IReadOnlyList<LiveTileItemSnapshot> items)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[key] =
                    JsonConvert.SerializeObject((items ?? new List<LiveTileItemSnapshot>()).Take(MaxCachedItems).ToList());
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LiveTileService.SaveItemsToSettings] Unable to save {key}: {ex.Message}");
            }
        }

        private enum LiveTileItemKind
        {
            Track,
            Artist,
            Album,
            Playlist
        }

        private sealed class LiveTileItemSnapshot
        {
            public LiveTileItemKind Kind { get; set; }
            public string Id { get; set; }
            public string Uri { get; set; }
            public string Title { get; set; }
            public string Subtitle { get; set; }
            public string Detail { get; set; }
            public string ImageUrl { get; set; }
            public string LaunchTag { get; set; }
            public string OwnerId { get; set; }
            public string OwnerName { get; set; }
        }
    }
}
