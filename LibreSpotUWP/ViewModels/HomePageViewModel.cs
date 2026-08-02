using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using LibreSpotUWP.Services;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.ViewModels
{
    public class HomeSectionGroup
    {
        public string Title { get; set; }
        public BulkObservableCollection<object> Items { get; set; } = new BulkObservableCollection<object>();
    }

    public class HomePageViewModel
    {
        private const int MaximumRequestConcurrency = 3;
        private const int MaximumAlbumsFromTopArtists = 60;
        private const int InitialPublishBudgetMs = 750;
        private static readonly TimeSpan ArtistAlbumCacheLifetime = TimeSpan.FromMinutes(15);

        private sealed class ArtistAlbumCacheEntry
        {
            public DateTimeOffset CachedAt;
            public List<FullAlbum> Albums;
        }

        private sealed class HomeLoadContext
        {
            private readonly object _sync = new object();
            private readonly List<string> _failures = new List<string>();

            public bool UsedCachedData { get; private set; }
            public bool UsedOfflineFallback { get; private set; }
            public DateTimeOffset? CachedAt { get; private set; }

            public void RegisterCacheUse<T>(CacheResponse<T> response)
            {
                if (response == null)
                    return;

                lock (_sync)
                {
                    UsedCachedData |= response.IsFromCache;
                    UsedOfflineFallback |= response.IsOfflineFallback;
                    if ((response.IsFromCache || response.IsOfflineFallback) &&
                        (!CachedAt.HasValue || response.Timestamp > CachedAt.Value))
                    {
                        CachedAt = response.Timestamp;
                    }
                }
            }

            public void RecordFailure(string operation, Exception exception)
            {
                lock (_sync)
                {
                    _failures.Add($"{operation}: {exception.GetType().Name}: {exception.Message}");
                }
            }

            public string BuildFailureSummary()
            {
                lock (_sync)
                {
                    if (_failures.Count == 0)
                        return null;

                    return $"Home load completed with {_failures.Count} failed request(s): " +
                        string.Join(" | ", _failures.Take(6)) +
                        (_failures.Count > 6 ? $" | +{_failures.Count - 6} more" : string.Empty);
                }
            }
        }

        private sealed class HomeSnapshot
        {
            public List<FullPlaylist> RecentlyPlayedPlaylists = new List<FullPlaylist>();
            public List<FullAlbum> RecentlyPlayedAlbums = new List<FullAlbum>();
            public List<FullArtist> RecentlyPlayedArtists = new List<FullArtist>();
            public List<FullTrack> RecentlyPlayedTracks = new List<FullTrack>();
            public List<FullTrack> TopTracks = new List<FullTrack>();
            public List<FullArtist> TopArtists = new List<FullArtist>();
            public List<SavedAlbum> SavedAlbums = new List<SavedAlbum>();
            public List<FullPlaylist> UserPlaylists = new List<FullPlaylist>();
            public List<FullArtist> FollowedArtists = new List<FullArtist>();
            public List<FullAlbum> AlbumsFromTopArtists = new List<FullAlbum>();
            public List<FullAlbum> AlbumsYouStarted = new List<FullAlbum>();
            public List<FullTrack> MixedForYou = new List<FullTrack>();
            public List<OfflinePlaylistEntry> OfflinePlaylists;
            public List<OfflineAlbumEntry> OfflineAlbums;
            public List<OfflineTrackEntry> OfflineTracks;
            public bool IsOfflineOnly;
            public bool UsedCachedData;
            public bool UsedOfflineFallback;
            public DateTimeOffset? CachedAt;
            public string FailureSummary;
        }

        private sealed class RecentSnapshot
        {
            public List<FullPlaylist> Playlists = new List<FullPlaylist>();
            public List<FullAlbum> Albums = new List<FullAlbum>();
            public List<FullArtist> Artists = new List<FullArtist>();
            public List<FullTrack> Tracks = new List<FullTrack>();
        }

        private sealed class StagedHomeSnapshot
        {
            public HomeSnapshot Snapshot;
            public string Section;
            public int Stage;
            public long ElapsedMs;
        }

        private readonly OperationGeneration _loadGeneration = new OperationGeneration();
        private readonly OperationGeneration _albumEnrichmentGeneration = new OperationGeneration();
        private readonly object _artistAlbumCacheSync = new object();
        private readonly Dictionary<string, ArtistAlbumCacheEntry> _artistAlbumCache =
            new Dictionary<string, ArtistAlbumCacheEntry>(StringComparer.OrdinalIgnoreCase);
        private DateTimeOffset? _cachedAt;

        public string StatusMessage { get; private set; }
        public DateTimeOffset? CachedAt => _cachedAt;
        public long CurrentGeneration => _loadGeneration.CurrentGeneration;
        public int LastMaximumRequestConcurrency { get; private set; }

        public BulkObservableCollection<HomeSectionGroup> GroupedHomeContent { get; } = new BulkObservableCollection<HomeSectionGroup>();
        public BulkObservableCollection<FullPlaylist> RecentlyPlayedPlaylists { get; } = new BulkObservableCollection<FullPlaylist>();
        public BulkObservableCollection<FullAlbum> RecentlyPlayedAlbums { get; } = new BulkObservableCollection<FullAlbum>();
        public BulkObservableCollection<FullArtist> RecentlyPlayedArtists { get; } = new BulkObservableCollection<FullArtist>();
        public BulkObservableCollection<FullTrack> RecentlyPlayedTracks { get; } = new BulkObservableCollection<FullTrack>();
        public BulkObservableCollection<FullTrack> UserTopTracksShortTerm { get; } = new BulkObservableCollection<FullTrack>();
        public BulkObservableCollection<FullArtist> UserTopArtistsShortTerm { get; } = new BulkObservableCollection<FullArtist>();
        public BulkObservableCollection<SavedAlbum> SavedAlbumsFull { get; } = new BulkObservableCollection<SavedAlbum>();
        public BulkObservableCollection<FullPlaylist> UserPlaylists { get; } = new BulkObservableCollection<FullPlaylist>();
        public BulkObservableCollection<FullArtist> FollowedArtists { get; } = new BulkObservableCollection<FullArtist>();
        public BulkObservableCollection<FullAlbum> AlbumsFromTopArtists { get; } = new BulkObservableCollection<FullAlbum>();
        public BulkObservableCollection<FullAlbum> AlbumsYouStarted { get; } = new BulkObservableCollection<FullAlbum>();
        public BulkObservableCollection<FullTrack> MixedForYou { get; } = new BulkObservableCollection<FullTrack>();

        public HomePageViewModel()
        {
            GroupedHomeContent.ReplaceAll(new[] { new HomeSectionGroup { Title = "Home" } });
        }

        public void CancelCurrentLoad()
        {
            _loadGeneration.CancelCurrent();
            _albumEnrichmentGeneration.CancelCurrent();
        }

        public async Task<bool> LoadAsync(ISpotifyWebService spotify, CancellationToken ct, bool forceRefresh = false)
        {
            if (spotify == null)
                throw new ArgumentNullException(nameof(spotify));

            _albumEnrichmentGeneration.CancelCurrent();
            var lease = _loadGeneration.Begin(ct);
            var requestGate = new BoundedAsyncGate(MaximumRequestConcurrency);
            var fastSnapshotReady = new TaskCompletionSource<StagedHomeSnapshot>();
            var initialPublishComplete = new TaskCompletionSource<bool>();
            var pendingStages = new List<StagedHomeSnapshot>();
            var lastAppliedStage = 0;
            var initialPublishSucceeded = false;
            var incrementalProgress = new Progress<StagedHomeSnapshot>(update =>
            {
                if (update == null ||
                    update.Stage <= lastAppliedStage ||
                    !_loadGeneration.IsCurrent(lease.Generation))
                {
                    return;
                }

                if (!initialPublishComplete.Task.IsCompleted)
                {
                    pendingStages.Add(update);
                    return;
                }

                if (initialPublishComplete.Task.Status != TaskStatus.RanToCompletion ||
                    !initialPublishSucceeded)
                {
                    return;
                }

                ApplyIncrementalSnapshot(update);
                if (string.Equals(update.Section, "Top Artists", StringComparison.Ordinal))
                    StartAlbumEnrichment(spotify, update.Snapshot, forceRefresh, lease.Token);
                lastAppliedStage = update.Stage;
            });
            var snapshotTask = Task.Run(
                () => BuildOnlineSnapshotAsync(
                    spotify,
                    requestGate,
                    lease.Token,
                    forceRefresh,
                    fastSnapshotReady,
                    incrementalProgress),
                lease.Token);
            _ = CompleteOnlineLoadAsync(
                spotify,
                snapshotTask,
                initialPublishComplete.Task,
                requestGate,
                lease,
                forceRefresh);

            using (UiResponsivenessTelemetry.BeginOperation("Home.Load.Initial", lease.Generation))
            {
                try
                {
                    var publishBudget = Task.Delay(InitialPublishBudgetMs, lease.Token);
                    await Task.WhenAny(fastSnapshotReady.Task, snapshotTask, publishBudget).ConfigureAwait(true);
                    lease.Token.ThrowIfCancellationRequested();
                    if (snapshotTask.IsFaulted || snapshotTask.IsCanceled)
                        await snapshotTask.ConfigureAwait(true);
                    if (!_loadGeneration.IsCurrent(lease.Generation))
                        return false;

                    HomeSnapshot initialSnapshot;
                    if (fastSnapshotReady.Task.IsCompleted &&
                        fastSnapshotReady.Task.Status == TaskStatus.RanToCompletion)
                    {
                        var initialStage = await fastSnapshotReady.Task.ConfigureAwait(true);
                        initialSnapshot = initialStage.Snapshot;
                        lastAppliedStage = initialStage.Stage;
                    }
                    else
                    {
                        initialSnapshot = new HomeSnapshot();
                    }

                    ApplySnapshot(initialSnapshot);
                    initialPublishSucceeded = true;
                    initialPublishComplete.TrySetResult(true);
                    foreach (var pendingStage in pendingStages
                        .Where(stage => stage.Stage > lastAppliedStage)
                        .OrderBy(stage => stage.Stage))
                    {
                        ApplyIncrementalSnapshot(pendingStage);
                        if (string.Equals(pendingStage.Section, "Top Artists", StringComparison.Ordinal))
                            StartAlbumEnrichment(spotify, pendingStage.Snapshot, forceRefresh, lease.Token);
                        lastAppliedStage = pendingStage.Stage;
                    }
                    pendingStages.Clear();
                    return true;
                }
                catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
                {
                    LogService.Telemetry(
                        "home-load-cancelled",
                        $"Home load generation {lease.Generation} cancelled; maximumRequestConcurrency={requestGate.MaximumObserved}.");
                    initialPublishComplete.TrySetResult(false);
                    return false;
                }
                catch
                {
                    initialPublishComplete.TrySetResult(false);
                    throw;
                }
            }
        }

        private async Task CompleteOnlineLoadAsync(
            ISpotifyWebService spotify,
            Task<HomeSnapshot> snapshotTask,
            Task<bool> initialPublishTask,
            BoundedAsyncGate requestGate,
            OperationGenerationLease lease,
            bool forceRefresh)
        {
            try
            {
                var snapshot = await snapshotTask.ConfigureAwait(true);
                var initialPublished = await initialPublishTask.ConfigureAwait(true);
                lease.Token.ThrowIfCancellationRequested();
                if (!initialPublished || !_loadGeneration.IsCurrent(lease.Generation))
                    return;

                LastMaximumRequestConcurrency = requestGate.MaximumObserved;
                ApplySnapshotMetadata(snapshot);
                if (!string.IsNullOrWhiteSpace(snapshot.FailureSummary))
                    LogService.Warn(snapshot.FailureSummary);
            }
            catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                LogService.Warn($"Home background load failed: {ex.Message}");
            }
            finally
            {
                requestGate.Dispose();
                _loadGeneration.Complete(lease.Generation);
            }
        }

        public async Task<bool> LoadOfflineAsync(IOfflineCatalogService offlineCatalog, CancellationToken ct = default(CancellationToken))
        {
            if (offlineCatalog == null)
                throw new ArgumentNullException(nameof(offlineCatalog));

            _albumEnrichmentGeneration.CancelCurrent();
            var lease = _loadGeneration.Begin(ct);
            using (UiResponsivenessTelemetry.BeginOperation("Home.LoadOffline", lease.Generation))
            {
                try
                {
                    var snapshot = await Task.Run(async () =>
                    {
                        UiResponsivenessTelemetry.VerifyBackgroundThread("Home offline snapshot assembly");
                        var tracksTask = offlineCatalog.GetDownloadedTracksAsync();
                        var albumsTask = offlineCatalog.GetDownloadedAlbumsAsync();
                        var playlistsTask = offlineCatalog.GetDownloadedPlaylistsAsync();
                        await Task.WhenAll(tracksTask, albumsTask, playlistsTask).ConfigureAwait(false);
                        lease.Token.ThrowIfCancellationRequested();

                        var tracks = (await tracksTask.ConfigureAwait(false)).ToList();
                        return new HomeSnapshot
                        {
                            IsOfflineOnly = true,
                            OfflineTracks = tracks,
                            OfflineAlbums = (await albumsTask.ConfigureAwait(false)).ToList(),
                            OfflinePlaylists = (await playlistsTask.ConfigureAwait(false)).ToList(),
                            MixedForYou = new List<FullTrack>()
                        };
                    }, lease.Token).ConfigureAwait(true);

                    lease.Token.ThrowIfCancellationRequested();
                    if (!_loadGeneration.IsCurrent(lease.Generation))
                        return false;

                    ApplySnapshot(snapshot);
                    return true;
                }
                catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
                {
                    LogService.Telemetry("home-offline-load-cancelled", $"Offline Home load generation {lease.Generation} cancelled.");
                    return false;
                }
                finally
                {
                    _loadGeneration.Complete(lease.Generation);
                }
            }
        }

        private async Task<HomeSnapshot> BuildOnlineSnapshotAsync(
            ISpotifyWebService spotify,
            BoundedAsyncGate requestGate,
            CancellationToken ct,
            bool forceRefresh,
            TaskCompletionSource<StagedHomeSnapshot> fastSnapshotReady,
            IProgress<StagedHomeSnapshot> incrementalProgress)
        {
            UiResponsivenessTelemetry.VerifyBackgroundThread("Home online snapshot assembly");
            var stopwatch = Stopwatch.StartNew();
            var context = new HomeLoadContext();
            var recentTask = LoadRecentlyPlayedAsync(spotify, requestGate, context, ct, forceRefresh);
            var playlistsTask = LoadUserPlaylistsAsync(spotify, requestGate, context, ct, forceRefresh);
            var artistsTask = LoadTopArtistsAsync(spotify, requestGate, context, ct, forceRefresh);
            var tracksTask = LoadTopTracksAsync(spotify, requestGate, context, ct, forceRefresh);
            var savedTask = LoadSavedAlbumsAsync(spotify, requestGate, context, ct, forceRefresh);
            var followedTask = LoadFollowedArtistsAsync(spotify, requestGate, context, ct, forceRefresh);

            var recent = await recentTask.ConfigureAwait(false);
            var albumsYouStarted = recent.Tracks
                .Select(track => ToFullAlbum(track?.Album))
                .Where(album => album != null && !string.IsNullOrWhiteSpace(album.Id))
                .GroupBy(album => album.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
            var snapshot = new HomeSnapshot
            {
                RecentlyPlayedPlaylists = recent.Playlists,
                RecentlyPlayedAlbums = recent.Albums,
                RecentlyPlayedArtists = recent.Artists,
                RecentlyPlayedTracks = recent.Tracks,
                AlbumsYouStarted = albumsYouStarted
            };
            ApplyLoadMetadata(snapshot, context);

            var stage = 1;
            var recentStage = CreateStage(snapshot, "Recently Played", stage, stopwatch.ElapsedMilliseconds);
            fastSnapshotReady.TrySetResult(recentStage);
            incrementalProgress?.Report(recentStage);
            LogHomeStage(recentStage);

            var remaining = new List<Task>
            {
                playlistsTask,
                artistsTask,
                tracksTask,
                savedTask,
                followedTask
            };

            while (remaining.Count > 0)
            {
                var completed = await Task.WhenAny(remaining).ConfigureAwait(false);
                remaining.Remove(completed);
                ct.ThrowIfCancellationRequested();

                string section;
                if (ReferenceEquals(completed, playlistsTask))
                {
                    snapshot.UserPlaylists = await playlistsTask.ConfigureAwait(false);
                    section = "Your Playlists";
                }
                else if (ReferenceEquals(completed, artistsTask))
                {
                    snapshot.TopArtists = await artistsTask.ConfigureAwait(false);
                    section = "Top Artists";
                }
                else if (ReferenceEquals(completed, tracksTask))
                {
                    snapshot.TopTracks = await tracksTask.ConfigureAwait(false);
                    section = "Top Tracks";
                }
                else if (ReferenceEquals(completed, savedTask))
                {
                    snapshot.SavedAlbums = await savedTask.ConfigureAwait(false);
                    section = "Saved Albums";
                }
                else
                {
                    snapshot.FollowedArtists = await followedTask.ConfigureAwait(false);
                    section = "Artists You Follow";
                }

                ApplyLoadMetadata(snapshot, context);
                var update = CreateStage(snapshot, section, ++stage, stopwatch.ElapsedMilliseconds);
                incrementalProgress?.Report(update);
                LogHomeStage(update);
            }

            return snapshot;
        }

        private void StartAlbumEnrichment(
            ISpotifyWebService spotify,
            HomeSnapshot snapshot,
            bool forceRefresh,
            CancellationToken cancellationToken)
        {
            if (snapshot?.TopArtists == null || snapshot.TopArtists.Count == 0)
                return;

            var lease = _albumEnrichmentGeneration.Begin(cancellationToken);
            _ = LoadAndApplyAlbumEnrichmentAsync(spotify, snapshot, forceRefresh, lease);
        }

        private async Task LoadAndApplyAlbumEnrichmentAsync(
            ISpotifyWebService spotify,
            HomeSnapshot snapshot,
            bool forceRefresh,
            OperationGenerationLease lease)
        {
            using (var requestGate = new BoundedAsyncGate(MaximumRequestConcurrency))
            {
                var context = new HomeLoadContext();
                try
                {
                    var albums = await Task.Run(
                        () => LoadAlbumsFromTopArtistsAsync(
                            spotify,
                            requestGate,
                            context,
                            snapshot.TopArtists,
                            lease.Token,
                            forceRefresh),
                        lease.Token).ConfigureAwait(true);

                    lease.Token.ThrowIfCancellationRequested();
                    if (!_albumEnrichmentGeneration.IsCurrent(lease.Generation))
                        return;

                    snapshot.AlbumsFromTopArtists = albums;
                    AlbumsFromTopArtists.ReplaceAll(albums);
                    GroupedHomeContent.ReplaceAll(BuildGroups(snapshot));

                    var failureSummary = context.BuildFailureSummary();
                    if (!string.IsNullOrWhiteSpace(failureSummary))
                        LogService.Warn(failureSummary);
                }
                catch (OperationCanceledException) when (lease.Token.IsCancellationRequested)
                {
                    LogService.Telemetry(
                        "home-album-enrichment-cancelled",
                        $"Home album enrichment generation {lease.Generation} cancelled.");
                }
                catch (Exception ex)
                {
                    LogService.Warn($"Home album enrichment failed: {ex.Message}");
                }
                finally
                {
                    _albumEnrichmentGeneration.Complete(lease.Generation);
                }
            }
        }

        private async Task<RecentSnapshot> LoadRecentlyPlayedAsync(
            ISpotifyWebService spotify,
            BoundedAsyncGate gate,
            HomeLoadContext context,
            CancellationToken ct,
            bool forceRefresh)
        {
            var result = new RecentSnapshot();
            try
            {
                var response = await gate.RunAsync(
                    token => spotify.GetRecentlyPlayedAsync(20, forceRefresh, token), ct).ConfigureAwait(false);
                context.RegisterCacheUse(response);
                var items = response?.Value?.Items ?? new List<PlayHistoryItem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var playlistIds = new List<string>();

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    var track = item?.Track;
                    if (track == null)
                        continue;

                    result.Tracks.Add(track);
                    var album = ToFullAlbum(track.Album);
                    if (album != null && seen.Add("album:" + album.Id))
                        result.Albums.Add(album);

                    foreach (var artist in track.Artists ?? Enumerable.Empty<SimpleArtist>())
                    {
                        var fullArtist = ToFullArtist(artist);
                        if (fullArtist != null && seen.Add("artist:" + fullArtist.Id))
                            result.Artists.Add(fullArtist);
                    }

                    var uri = item.Context?.Uri;
                    if (!string.IsNullOrWhiteSpace(uri) &&
                        uri.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase))
                    {
                        var id = uri.Substring("spotify:playlist:".Length);
                        if (seen.Add("playlist:" + id))
                            playlistIds.Add(id);
                    }
                }

                foreach (var id in playlistIds)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var playlist = await gate.RunAsync(
                            token => spotify.GetPlaylistAsync(id, forceRefresh, token), ct).ConfigureAwait(false);
                        context.RegisterCacheUse(playlist);
                        if (playlist?.Value != null)
                            result.Playlists.Add(playlist.Value);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        context.RecordFailure("recent-playlist", ex);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.RecordFailure("recently-played", ex);
            }

            return result;
        }

        private Task<List<FullPlaylist>> LoadUserPlaylistsAsync(
            ISpotifyWebService spotify, BoundedAsyncGate gate, HomeLoadContext context, CancellationToken ct, bool forceRefresh)
        {
            return LoadSectionAsync(
                "user-playlists",
                async () =>
                {
                    var response = await gate.RunAsync(
                        token => spotify.GetCurrentUserPlaylistsAsync(forceRefresh, token), ct).ConfigureAwait(false);
                    context.RegisterCacheUse(response);
                    return response?.Value?.Items?.Where(item => item != null).ToList() ?? new List<FullPlaylist>();
                }, context, ct);
        }

        private Task<List<FullArtist>> LoadTopArtistsAsync(
            ISpotifyWebService spotify, BoundedAsyncGate gate, HomeLoadContext context, CancellationToken ct, bool forceRefresh)
        {
            return LoadSectionAsync(
                "top-artists",
                async () =>
                {
                    var response = await gate.RunAsync(
                        token => spotify.GetUserTopArtistsAsync(20, forceRefresh, token), ct).ConfigureAwait(false);
                    context.RegisterCacheUse(response);
                    return response?.Value?.Items?.Where(item => item != null).ToList() ?? new List<FullArtist>();
                }, context, ct);
        }

        private Task<List<FullTrack>> LoadTopTracksAsync(
            ISpotifyWebService spotify, BoundedAsyncGate gate, HomeLoadContext context, CancellationToken ct, bool forceRefresh)
        {
            return LoadSectionAsync(
                "top-tracks",
                async () =>
                {
                    var response = await gate.RunAsync(
                        token => spotify.GetUserTopTracksAsync(20, forceRefresh, token), ct).ConfigureAwait(false);
                    context.RegisterCacheUse(response);
                    return response?.Value?.Items?.Where(item => item != null).ToList() ?? new List<FullTrack>();
                }, context, ct);
        }

        private Task<List<SavedAlbum>> LoadSavedAlbumsAsync(
            ISpotifyWebService spotify, BoundedAsyncGate gate, HomeLoadContext context, CancellationToken ct, bool forceRefresh)
        {
            return LoadSectionAsync(
                "saved-albums",
                async () =>
                {
                    var response = await gate.RunAsync(
                        token => spotify.GetSavedAlbumsAsync(forceRefresh, token), ct).ConfigureAwait(false);
                    context.RegisterCacheUse(response);
                    return response?.Value?.Items?.Where(item => item != null).ToList() ?? new List<SavedAlbum>();
                }, context, ct);
        }

        private Task<List<FullArtist>> LoadFollowedArtistsAsync(
            ISpotifyWebService spotify, BoundedAsyncGate gate, HomeLoadContext context, CancellationToken ct, bool forceRefresh)
        {
            return LoadSectionAsync(
                "followed-artists",
                async () =>
                {
                    var response = await gate.RunAsync(
                        token => spotify.GetFollowedArtistsAsync(forceRefresh, token), ct).ConfigureAwait(false);
                    context.RegisterCacheUse(response);
                    return response?.Value?.Artists?.Items?.Where(item => item != null).ToList() ?? new List<FullArtist>();
                }, context, ct);
        }

        private async Task<List<FullAlbum>> LoadAlbumsFromTopArtistsAsync(
            ISpotifyWebService spotify,
            BoundedAsyncGate gate,
            HomeLoadContext context,
            IReadOnlyList<FullArtist> artists,
            CancellationToken ct,
            bool forceRefresh)
        {
            var artistIds = (artists ?? new List<FullArtist>())
                .Select(artist => artist?.Id)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (artistIds.Count == 0)
                return new List<FullAlbum>();

            var queue = new Queue<string>(artistIds);
            var queueSync = new object();
            var results = new List<FullAlbum>();
            var resultsSync = new object();
            var workers = new List<Task>();
            var workerCount = Math.Min(MaximumRequestConcurrency, artistIds.Count);

            for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
            {
                workers.Add(Task.Run(async () =>
                {
                    while (true)
                    {
                        string artistId;
                        lock (queueSync)
                        {
                            artistId = queue.Count > 0 ? queue.Dequeue() : null;
                        }

                        if (artistId == null)
                            return;

                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var albums = TryGetCachedArtistAlbums(artistId, forceRefresh);
                            if (albums == null)
                            {
                                var response = await gate.RunAsync(
                                    token => spotify.GetArtistAlbumsAsync(artistId, forceRefresh, token), ct)
                                    .ConfigureAwait(false);
                                context.RegisterCacheUse(response);
                                albums = (response?.Value?.Items ?? new List<SimpleAlbum>())
                                    .Select(ToFullAlbum)
                                    .Where(album => album != null)
                                    .ToList();
                                CacheArtistAlbums(artistId, albums);
                            }

                            lock (resultsSync)
                                results.AddRange(albums);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            context.RecordFailure("top-artist-albums", ex);
                        }
                    }
                }, ct));
            }

            await Task.WhenAll(workers).ConfigureAwait(false);
            return results
                .Where(album => !string.IsNullOrWhiteSpace(album.Id))
                .GroupBy(album => album.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(MaximumAlbumsFromTopArtists)
                .ToList();
        }

        private static async Task<List<T>> LoadSectionAsync<T>(
            string operation,
            Func<Task<List<T>>> loader,
            HomeLoadContext context,
            CancellationToken ct)
        {
            try
            {
                return await loader().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                context.RecordFailure(operation, ex);
                return new List<T>();
            }
        }

        private List<FullAlbum> TryGetCachedArtistAlbums(string artistId, bool forceRefresh)
        {
            if (forceRefresh)
                return null;

            lock (_artistAlbumCacheSync)
            {
                if (!_artistAlbumCache.TryGetValue(artistId, out ArtistAlbumCacheEntry entry) ||
                    DateTimeOffset.UtcNow - entry.CachedAt >= ArtistAlbumCacheLifetime)
                {
                    return null;
                }

                return new List<FullAlbum>(entry.Albums);
            }
        }

        private void CacheArtistAlbums(string artistId, List<FullAlbum> albums)
        {
            lock (_artistAlbumCacheSync)
            {
                var expiredKeys = _artistAlbumCache
                    .Where(pair => DateTimeOffset.UtcNow - pair.Value.CachedAt >= ArtistAlbumCacheLifetime)
                    .Select(pair => pair.Key)
                    .ToList();
                foreach (var key in expiredKeys)
                    _artistAlbumCache.Remove(key);

                while (_artistAlbumCache.Count >= 100)
                {
                    var oldestKey = _artistAlbumCache
                        .OrderBy(pair => pair.Value.CachedAt)
                        .Select(pair => pair.Key)
                        .First();
                    _artistAlbumCache.Remove(oldestKey);
                }

                _artistAlbumCache[artistId] = new ArtistAlbumCacheEntry
                {
                    CachedAt = DateTimeOffset.UtcNow,
                    Albums = new List<FullAlbum>(albums ?? new List<FullAlbum>())
                };
            }
        }

        private static StagedHomeSnapshot CreateStage(
            HomeSnapshot snapshot,
            string section,
            int stage,
            long elapsedMs)
        {
            return new StagedHomeSnapshot
            {
                Snapshot = CloneSnapshot(snapshot),
                Section = section,
                Stage = stage,
                ElapsedMs = elapsedMs
            };
        }

        private static HomeSnapshot CloneSnapshot(HomeSnapshot snapshot)
        {
            return new HomeSnapshot
            {
                RecentlyPlayedPlaylists = new List<FullPlaylist>(snapshot.RecentlyPlayedPlaylists),
                RecentlyPlayedAlbums = new List<FullAlbum>(snapshot.RecentlyPlayedAlbums),
                RecentlyPlayedArtists = new List<FullArtist>(snapshot.RecentlyPlayedArtists),
                RecentlyPlayedTracks = new List<FullTrack>(snapshot.RecentlyPlayedTracks),
                TopTracks = new List<FullTrack>(snapshot.TopTracks),
                TopArtists = new List<FullArtist>(snapshot.TopArtists),
                SavedAlbums = new List<SavedAlbum>(snapshot.SavedAlbums),
                UserPlaylists = new List<FullPlaylist>(snapshot.UserPlaylists),
                FollowedArtists = new List<FullArtist>(snapshot.FollowedArtists),
                AlbumsFromTopArtists = new List<FullAlbum>(snapshot.AlbumsFromTopArtists),
                AlbumsYouStarted = new List<FullAlbum>(snapshot.AlbumsYouStarted),
                MixedForYou = new List<FullTrack>(snapshot.MixedForYou),
                OfflinePlaylists = snapshot.OfflinePlaylists == null
                    ? null
                    : new List<OfflinePlaylistEntry>(snapshot.OfflinePlaylists),
                OfflineAlbums = snapshot.OfflineAlbums == null
                    ? null
                    : new List<OfflineAlbumEntry>(snapshot.OfflineAlbums),
                OfflineTracks = snapshot.OfflineTracks == null
                    ? null
                    : new List<OfflineTrackEntry>(snapshot.OfflineTracks),
                IsOfflineOnly = snapshot.IsOfflineOnly,
                UsedCachedData = snapshot.UsedCachedData,
                UsedOfflineFallback = snapshot.UsedOfflineFallback,
                CachedAt = snapshot.CachedAt,
                FailureSummary = snapshot.FailureSummary
            };
        }

        private static void ApplyLoadMetadata(HomeSnapshot snapshot, HomeLoadContext context)
        {
            snapshot.UsedCachedData = context.UsedCachedData;
            snapshot.UsedOfflineFallback = context.UsedOfflineFallback;
            snapshot.CachedAt = context.CachedAt;
            snapshot.FailureSummary = context.BuildFailureSummary();
        }

        private static void LogHomeStage(StagedHomeSnapshot update)
        {
            LogService.Info(
                $"[HomePageViewModel.BuildOnlineSnapshotAsync] section={update.Section}, " +
                $"stage={update.Stage}, elapsedMs={update.ElapsedMs}.");
        }

        private void ApplyIncrementalSnapshot(StagedHomeSnapshot update)
        {
            var snapshot = update.Snapshot;
            switch (update.Section)
            {
                case "Recently Played":
                    ApplySnapshot(snapshot);
                    break;
                case "Your Playlists":
                    UserPlaylists.ReplaceAll(snapshot.UserPlaylists);
                    ReplaceHomeGroup("Your Playlists", snapshot.UserPlaylists);
                    break;
                case "Top Artists":
                    UserTopArtistsShortTerm.ReplaceAll(snapshot.TopArtists);
                    ReplaceHomeGroup("Top Artists", snapshot.TopArtists);
                    break;
                case "Top Tracks":
                    UserTopTracksShortTerm.ReplaceAll(snapshot.TopTracks);
                    ReplaceHomeGroup("Top Tracks", snapshot.TopTracks);
                    break;
                case "Saved Albums":
                    SavedAlbumsFull.ReplaceAll(snapshot.SavedAlbums);
                    ReplaceHomeGroup("Saved Albums", snapshot.SavedAlbums);
                    break;
                case "Artists You Follow":
                    FollowedArtists.ReplaceAll(snapshot.FollowedArtists);
                    ReplaceHomeGroup("Artists You Follow", snapshot.FollowedArtists);
                    break;
            }

            ApplySnapshotMetadata(snapshot);
            LogService.Info(
                $"[HomePageViewModel.ApplyIncrementalSnapshot] section={update.Section}, " +
                $"stage={update.Stage}, elapsedMs={update.ElapsedMs}.");
        }

        private void ReplaceHomeGroup<T>(string title, IEnumerable<T> items)
        {
            var materialized = items?
                .Where(item => item != null)
                .Cast<object>()
                .ToList() ?? new List<object>();
            var existing = GroupedHomeContent.FirstOrDefault(
                group => string.Equals(group.Title, title, StringComparison.OrdinalIgnoreCase));

            if (materialized.Count == 0)
            {
                if (existing != null)
                    GroupedHomeContent.Remove(existing);
                return;
            }

            if (existing != null)
            {
                existing.Items.ReplaceAll(materialized);
                return;
            }

            var newGroup = new HomeSectionGroup { Title = title };
            newGroup.Items.ReplaceAll(materialized);
            var insertIndex = 1;
            while (insertIndex < GroupedHomeContent.Count &&
                   CompareSections(GroupedHomeContent[insertIndex].Title, title) <= 0)
            {
                insertIndex++;
            }
            GroupedHomeContent.Insert(insertIndex, newGroup);
        }

        private static int CompareSections(string left, string right)
        {
            var rankComparison = GetSectionRank(left).CompareTo(GetSectionRank(right));
            return rankComparison != 0
                ? rankComparison
                : StringComparer.OrdinalIgnoreCase.Compare(left, right);
        }

        private void ApplySnapshot(HomeSnapshot snapshot)
        {
            RecentlyPlayedPlaylists.ReplaceAll(snapshot.RecentlyPlayedPlaylists);
            RecentlyPlayedAlbums.ReplaceAll(snapshot.RecentlyPlayedAlbums);
            RecentlyPlayedArtists.ReplaceAll(snapshot.RecentlyPlayedArtists);
            RecentlyPlayedTracks.ReplaceAll(snapshot.RecentlyPlayedTracks);
            UserTopTracksShortTerm.ReplaceAll(snapshot.TopTracks);
            UserTopArtistsShortTerm.ReplaceAll(snapshot.TopArtists);
            SavedAlbumsFull.ReplaceAll(snapshot.SavedAlbums);
            UserPlaylists.ReplaceAll(snapshot.UserPlaylists);
            FollowedArtists.ReplaceAll(snapshot.FollowedArtists);
            AlbumsFromTopArtists.ReplaceAll(snapshot.AlbumsFromTopArtists);
            AlbumsYouStarted.ReplaceAll(snapshot.AlbumsYouStarted);
            MixedForYou.ReplaceAll(snapshot.MixedForYou);

            ApplySnapshotMetadata(snapshot);
            GroupedHomeContent.ReplaceAll(BuildGroups(snapshot));

            if (!string.IsNullOrWhiteSpace(snapshot.FailureSummary))
                LogService.Warn(snapshot.FailureSummary);
        }

        private void ApplySnapshotMetadata(HomeSnapshot snapshot)
        {
            _cachedAt = snapshot.CachedAt;
            StatusMessage = BuildStatusMessage(snapshot);
        }

        private static List<HomeSectionGroup> BuildGroups(HomeSnapshot snapshot)
        {
            var groups = new List<HomeSectionGroup> { new HomeSectionGroup { Title = "Home" } };
            if (snapshot.IsOfflineOnly)
            {
                AddGroup(groups, "Downloaded Playlists", snapshot.OfflinePlaylists);
                AddGroup(groups, "Downloaded Albums", snapshot.OfflineAlbums);
                AddGroup(groups, "Downloaded Songs", snapshot.OfflineTracks);
                AddGroup(groups, "Mixed For You", (snapshot.OfflineTracks ?? new List<OfflineTrackEntry>()).Take(30));
            }
            else
            {
                AddGroup(groups, "Recently Played Playlists", snapshot.RecentlyPlayedPlaylists);
                AddGroup(groups, "Recently Played Albums", snapshot.RecentlyPlayedAlbums);
                AddGroup(groups, "Recently Played Artists", snapshot.RecentlyPlayedArtists);
                AddGroup(groups, "Recently Played Tracks", snapshot.RecentlyPlayedTracks);
                AddGroup(groups, "Your Playlists", snapshot.UserPlaylists);
                AddGroup(groups, "Top Artists", snapshot.TopArtists);
                AddGroup(groups, "Top Tracks", snapshot.TopTracks);
                AddGroup(groups, "Saved Albums", snapshot.SavedAlbums);
                AddGroup(groups, "Artists You Follow", snapshot.FollowedArtists);
                AddGroup(groups, "Albums You Started", snapshot.AlbumsYouStarted);
                AddGroup(groups, "Albums From Your Top Artists", snapshot.AlbumsFromTopArtists);
            }

            var ordered = groups.Skip(1)
                .OrderBy(group => GetSectionRank(group.Title))
                .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            ordered.Insert(0, groups[0]);
            return ordered;
        }

        private static void AddGroup<T>(List<HomeSectionGroup> groups, string title, IEnumerable<T> items)
        {
            var materialized = items?.Where(item => item != null).Cast<object>().ToList() ?? new List<object>();
            if (materialized.Count == 0)
                return;

            var group = new HomeSectionGroup { Title = title };
            group.Items.ReplaceAll(materialized);
            groups.Add(group);
        }

        private static int GetSectionRank(string title)
        {
            var orderedSections = UserSettings.GetHomeSectionOrder();
            var configuredIndex = Array.FindIndex(
                orderedSections,
                item => string.Equals(item, title, StringComparison.OrdinalIgnoreCase));
            return configuredIndex >= 0 ? configuredIndex : int.MaxValue - 1;
        }

        private static string BuildStatusMessage(HomeSnapshot snapshot)
        {
            if (snapshot.IsOfflineOnly)
            {
                return snapshot.OfflineTracks?.Count > 0 ||
                       snapshot.OfflineAlbums?.Count > 0 ||
                       snapshot.OfflinePlaylists?.Count > 0
                    ? "Offline. Home is showing your downloaded music."
                    : "Offline. Download music while online and it will appear here.";
            }

            if (snapshot.UsedOfflineFallback)
                return "Offline. Home is showing cached sections from earlier sessions.";
            if (!ConnectivityHelper.HasInternetAccess())
                return "Offline. Only cached home sections are available right now.";
            if (snapshot.UsedCachedData)
                return "Showing cached home data.";
            return null;
        }

        private static FullAlbum ToFullAlbum(SimpleAlbum album)
        {
            if (album == null)
                return null;

            return new FullAlbum
            {
                Id = album.Id,
                Uri = album.Uri,
                Name = album.Name,
                AlbumType = album.AlbumType,
                Images = album.Images ?? new List<Image>(),
                Artists = album.Artists ?? new List<SimpleArtist>(),
                ReleaseDate = album.ReleaseDate,
                TotalTracks = album.TotalTracks
            };
        }

        private static FullArtist ToFullArtist(SimpleArtist artist)
        {
            if (artist == null)
                return null;

            return new FullArtist
            {
                Id = artist.Id,
                Uri = artist.Uri,
                Name = artist.Name,
                Images = new List<Image>()
            };
        }
    }
}
