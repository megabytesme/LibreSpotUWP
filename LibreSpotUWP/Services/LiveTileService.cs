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
        private const string RecentTileTracksKey = "LiveTileRecentTracks";
        private const string FallbackLogoSource = "ms-appx:///Assets/Square150x150Logo.png";
        private const int MaxRecentTracks = 3;
        private static readonly TimeSpan RecentRefreshInterval = TimeSpan.FromMinutes(20);

        private readonly IMediaService _media;
        private readonly ISpotifyAuthService _auth;
        private readonly ISpotifyWebService _web;
        private readonly SemaphoreSlim _updateGate = new SemaphoreSlim(1, 1);
        private readonly object _refreshScheduleLock = new object();
        private readonly Random _random = new Random();

        private bool _initialized;
        private bool _isSignedIn;
        private string _lastMediaSignature;
        private MediaState _lastMediaState;
        private AppUserProfile _currentUser;
        private DateTimeOffset _lastRecentRefreshAt = DateTimeOffset.MinValue;
        private List<LiveTileTrackSnapshot> _recentTracks;
        private int _updateVersion;
        private CancellationTokenSource _refreshDebounceCts;
        private bool _queuedForceRecentRefresh;
        private string _queuedRefreshReason;

        public LiveTileService(
            IMediaService media,
            ISpotifyAuthService auth,
            ISpotifyWebService web)
        {
            _media = media ?? throw new ArgumentNullException(nameof(media));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _web = web ?? throw new ArgumentNullException(nameof(web));
            _recentTracks = LoadRecentTracksFromSettings();
        }

        public async Task InitializeAsync(bool isSignedIn)
        {
            if (_initialized)
                return;

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

        public async Task PrepareForSuspendingAsync()
        {
            CancelQueuedRefresh();
            await RefreshTileAsync(
                forceRecentRefresh: _isSignedIn && !ShouldShowNowPlaying(_lastMediaState),
                reason: "suspending").ConfigureAwait(false);
        }

        private async void OnMediaStateChanged(object sender, MediaState state)
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

        private async void OnAuthStateChanged(object sender, AuthState state)
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
                    CancelQueuedRefresh();
                    await RefreshTileAsync(forceRecentRefresh: false, reason: "auth").ConfigureAwait(false);
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

        private async void OnUserChanged(object sender, AppUserProfile user)
        {
            try
            {
                _currentUser = user;
                QueueRefresh(forceRecentRefresh: false, reason: "user", delay: TimeSpan.FromSeconds(1));
                await Task.CompletedTask;
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LiveTileService.OnUserChanged] Unable to refresh tile: {ex.Message}");
            }
        }

        private void QueueRefresh(bool forceRecentRefresh, string reason, TimeSpan delay)
        {
            CancellationTokenSource previous;
            CancellationTokenSource current;
            bool queuedForce;
            string queuedReason;

            lock (_refreshScheduleLock)
            {
                previous = _refreshDebounceCts;
                previous?.Cancel();

                _queuedForceRecentRefresh = _queuedForceRecentRefresh || forceRecentRefresh;
                _queuedRefreshReason = string.IsNullOrWhiteSpace(_queuedRefreshReason)
                    ? reason
                    : _queuedRefreshReason + "+" + reason;

                queuedForce = _queuedForceRecentRefresh;
                queuedReason = _queuedRefreshReason;
                current = new CancellationTokenSource();
                _refreshDebounceCts = current;
            }

            var token = current.Token;
            var ignored = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(false);

                    lock (_refreshScheduleLock)
                    {
                        if (!ReferenceEquals(_refreshDebounceCts, current))
                            return;

                        _refreshDebounceCts = null;
                        _queuedForceRecentRefresh = false;
                        _queuedRefreshReason = null;
                    }

                    await RefreshTileAsync(queuedForce, queuedReason).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                }
                catch (Exception ex)
                {
                    LogService.Warn($"[LiveTileService.QueueRefresh] Unable to refresh queued tile for {reason}: {ex.Message}");
                }
                finally
                {
                    current.Dispose();
                }
            });
        }

        private void CancelQueuedRefresh()
        {
            lock (_refreshScheduleLock)
            {
                _refreshDebounceCts?.Cancel();
                _refreshDebounceCts?.Dispose();
                _refreshDebounceCts = null;
                _queuedForceRecentRefresh = false;
                _queuedRefreshReason = null;
            }
        }

        private async Task RefreshTileAsync(bool forceRecentRefresh, string reason)
        {
            var version = Interlocked.Increment(ref _updateVersion);
            await _updateGate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (version != _updateVersion)
                    return;

                if (!_isSignedIn)
                {
                    ApplyLoggedOutTile();
                    return;
                }

                var state = _lastMediaState ?? _media.Current;
                var nowPlaying = ShouldShowNowPlaying(state);

                CacheRecentTrackFromState(state);

                if (!nowPlaying)
                    await EnsureRecentlyPlayedAsync(forceRecentRefresh).ConfigureAwait(false);

                var notifications = new List<TileNotification>();
                notifications.Add(nowPlaying
                    ? CreateNowPlayingNotification(state)
                    : CreateRecentlyPlayedNotification(state));

                if (_currentUser != null)
                    notifications.Add(CreateProfileNotification(_currentUser));

                if (notifications.Count > 1 && _random.Next(0, 2) == 0)
                {
                    var first = notifications[0];
                    notifications[0] = notifications[1];
                    notifications[1] = first;
                }

                ApplyNotifications(notifications);
                LogService.Info($"[LiveTileService.RefreshTileAsync] Refreshed tile for {reason}.");
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LiveTileService.RefreshTileAsync] Unable to refresh tile for {reason}: {ex.Message}");
            }
            finally
            {
                _updateGate.Release();
            }
        }

        private async Task EnsureRecentlyPlayedAsync(bool force)
        {
            var now = DateTimeOffset.UtcNow;
            if (!force &&
                _recentTracks.Count > 0 &&
                now - _lastRecentRefreshAt < RecentRefreshInterval)
            {
                return;
            }

            try
            {
                var response = await _web.GetRecentlyPlayedAsync(MaxRecentTracks, forceRefresh: false)
                    .ConfigureAwait(false);
                var tracks = response?.Value?.Items?
                    .Select(item => FromFullTrack(item.Track))
                    .Where(track => track != null && !string.IsNullOrWhiteSpace(track.Title))
                    .Take(MaxRecentTracks)
                    .ToList();

                if (tracks != null && tracks.Count > 0)
                {
                    _recentTracks = tracks;
                    _lastRecentRefreshAt = now;
                    SaveRecentTracksToSettings();
                }
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LiveTileService.EnsureRecentlyPlayedAsync] Unable to load recently played tracks: {ex.Message}");
                if (_recentTracks.Count == 0)
                    _recentTracks = LoadRecentTracksFromSettings();
            }
        }

        private void CacheRecentTrackFromState(MediaState state)
        {
            var track = FromMediaState(state);
            if (track == null || string.IsNullOrWhiteSpace(track.Title))
                return;

            _recentTracks.RemoveAll(existing =>
                !string.IsNullOrWhiteSpace(existing.Uri) &&
                string.Equals(existing.Uri, track.Uri, StringComparison.OrdinalIgnoreCase));
            _recentTracks.Insert(0, track);

            if (_recentTracks.Count > MaxRecentTracks)
                _recentTracks = _recentTracks.Take(MaxRecentTracks).ToList();

            SaveRecentTracksToSettings();
        }

        private void ApplyLoggedOutTile()
        {
            var updater = TileUpdateManager.CreateTileUpdaterForApplication();
            updater.EnableNotificationQueue(false);
            updater.Clear();
            LogService.Info("[LiveTileService.ApplyLoggedOutTile] Cleared live tile to manifest default.");
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

        private static TileNotification CreateNowPlayingNotification(MediaState state)
        {
            var track = FromMediaState(state) ?? new LiveTileTrackSnapshot
            {
                Title = "Now playing",
                Artist = "LibreSpotUWP"
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

        private TileNotification CreateRecentlyPlayedNotification(MediaState state)
        {
            var tracks = _recentTracks.Count > 0
                ? _recentTracks
                : new List<LiveTileTrackSnapshot>();

            if (tracks.Count == 0)
            {
                var fallback = FromMediaState(state);
                if (fallback != null)
                    tracks.Add(fallback);
            }

            if (tracks.Count == 0)
            {
                tracks.Add(new LiveTileTrackSnapshot
                {
                    Title = "Ready to play",
                    Artist = "Open LibreSpotUWP"
                });
            }

            var imageSource = ResolveTileImageSource(tracks[0].ImageUrl, useFallback: true);
            return CreateNotification(
                BuildTileDocument(
                    BuildVisual(
                        BuildSmallBinding("Recent", tracks[0].Title),
                        BuildRecentlyPlayedMediumBinding(tracks, imageSource),
                        BuildRecentlyPlayedWideBinding(tracks, imageSource),
                        BuildRecentlyPlayedLargeBinding(tracks, imageSource))),
                tag: "recent",
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
            XElement large)
        {
            return new XElement(
                "visual",
                new XAttribute("branding", "nameAndLogo"),
                new XAttribute("displayName", "LibreSpotUWP"),
                small,
                medium,
                wide,
                large);
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
            LiveTileTrackSnapshot track,
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
                Text(track.Artist, "captionSubtle", wrap: true, maxLines: 1));
        }

        private static XElement BuildNowPlayingWideBinding(
            LiveTileTrackSnapshot track,
            string imageSource,
            string sourceName)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileWide"),
                Image(imageSource, placement: "background", overlay: 65),
                Text(sourceName, "captionSubtle"),
                Text(track.Title, "subtitle", wrap: true, maxLines: 2),
                Text(track.Artist, "bodySubtle", wrap: true, maxLines: 1),
                Text(track.Album, "captionSubtle", wrap: true, maxLines: 1));
        }

        private static XElement BuildNowPlayingLargeBinding(
            LiveTileTrackSnapshot track,
            string imageSource,
            string sourceName)
        {
            return new XElement(
                "binding",
                new XAttribute("template", "TileLarge"),
                Image(imageSource, placement: "background", overlay: 70),
                Text(sourceName, "captionSubtle"),
                Text(track.Title, "title", wrap: true, maxLines: 2),
                Text(track.Artist, "subtitleSubtle", wrap: true, maxLines: 2),
                Text(track.Album, "bodySubtle", wrap: true, maxLines: 2));
        }

        private static XElement BuildRecentlyPlayedMediumBinding(
            IReadOnlyList<LiveTileTrackSnapshot> tracks,
            string imageSource)
        {
            var first = tracks[0];
            return new XElement(
                "binding",
                new XAttribute("template", "TileMedium"),
                new XAttribute("branding", "name"),
                Image(imageSource, placement: "peek", overlay: 20),
                Text("Recently played", "captionSubtle"),
                Text(first.Title, "base", wrap: true, maxLines: 2),
                Text(first.Artist, "captionSubtle", wrap: true, maxLines: 1));
        }

        private static XElement BuildRecentlyPlayedWideBinding(
            IReadOnlyList<LiveTileTrackSnapshot> tracks,
            string imageSource)
        {
            var binding = new XElement(
                "binding",
                new XAttribute("template", "TileWide"),
                Image(imageSource, placement: "background", overlay: 70),
                Text("Recently played", "captionSubtle"));

            foreach (var track in tracks.Take(3))
            {
                binding.Add(Text(track.Title, "base", wrap: true, maxLines: 1));
                binding.Add(Text(track.Artist, "captionSubtle", wrap: true, maxLines: 1));
            }

            return binding;
        }

        private static XElement BuildRecentlyPlayedLargeBinding(
            IReadOnlyList<LiveTileTrackSnapshot> tracks,
            string imageSource)
        {
            var binding = new XElement(
                "binding",
                new XAttribute("template", "TileLarge"),
                Image(imageSource, placement: "background", overlay: 75),
                Text("Recently played", "captionSubtle"));

            foreach (var track in tracks.Take(3))
            {
                binding.Add(
                    new XElement(
                        "group",
                        new XElement(
                            "subgroup",
                            Text(track.Title, "base", wrap: true, maxLines: 1),
                            Text(track.Artist, "captionSubtle", wrap: true, maxLines: 1))));
            }

            return binding;
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

        private static LiveTileTrackSnapshot FromMediaState(MediaState state)
        {
            if (state?.Track == null && state?.Metadata == null)
                return null;

            var metadata = state.Metadata;
            var track = state.Track;

            return new LiveTileTrackSnapshot
            {
                Uri = metadata?.Uri ?? track?.Uri,
                Title = SafeText(metadata?.Name, track?.Name),
                Artist = SafeText(GetArtistLine(metadata), track?.Artist),
                Album = SafeText(metadata?.Album?.Name, track?.Album, state.ContextName),
                ImageUrl = SafeText(
                    state.ArtworkUri,
                    metadata?.Album?.Images?.FirstOrDefault()?.Url,
                    track?.CoverUrl)
            };
        }

        private static LiveTileTrackSnapshot FromFullTrack(FullTrack track)
        {
            if (track == null)
                return null;

            return new LiveTileTrackSnapshot
            {
                Uri = track.Uri,
                Title = track.Name,
                Artist = GetArtistLine(track),
                Album = track.Album?.Name,
                ImageUrl = track.Album?.Images?.FirstOrDefault()?.Url
            };
        }

        private static string GetArtistLine(FullTrack track)
        {
            var artists = track?.Artists?
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

        private static List<LiveTileTrackSnapshot> LoadRecentTracksFromSettings()
        {
            try
            {
                var settings = ApplicationData.Current.LocalSettings;
                if (!settings.Values.TryGetValue(RecentTileTracksKey, out object raw) ||
                    !(raw is string json) ||
                    string.IsNullOrWhiteSpace(json))
                {
                    return new List<LiveTileTrackSnapshot>();
                }

                return JsonConvert.DeserializeObject<List<LiveTileTrackSnapshot>>(json) ??
                    new List<LiveTileTrackSnapshot>();
            }
            catch
            {
                return new List<LiveTileTrackSnapshot>();
            }
        }

        private void SaveRecentTracksToSettings()
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[RecentTileTracksKey] =
                    JsonConvert.SerializeObject(_recentTracks.Take(MaxRecentTracks).ToList());
            }
            catch (Exception ex)
            {
                LogService.Warn($"[LiveTileService.SaveRecentTracksToSettings] Unable to save recent tracks: {ex.Message}");
            }
        }

        private sealed class LiveTileTrackSnapshot
        {
            public string Uri { get; set; }
            public string Title { get; set; }
            public string Artist { get; set; }
            public string Album { get; set; }
            public string ImageUrl { get; set; }
        }
    }
}
