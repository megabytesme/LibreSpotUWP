using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using Newtonsoft.Json;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LibreSpotUWP.Services
{
    public sealed class OfflineCatalogService : IOfflineCatalogService
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly string _catalogPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "offline-catalog.json");
        private readonly string _imageFolderPath = Path.Combine(ApplicationData.Current.LocalFolder.Path, "cached-images");
        private OfflineCatalogData _catalog = new OfflineCatalogData();
        private bool _initialized;

        public async Task InitializeAsync()
        {
            await _gate.WaitAsync();
            try
            {
                if (_initialized)
                    return;

                if (File.Exists(_catalogPath))
                {
                    var json = await File.ReadAllTextAsync(_catalogPath).ConfigureAwait(false);
                    _catalog = JsonConvert.DeserializeObject<OfflineCatalogData>(json) ?? new OfflineCatalogData();
                    MigrateDownloadedState();
                }

                _initialized = true;
                LogService.Info($"Loaded offline catalog from {_catalogPath}.");
            }
            finally
            {
                _gate.Release();
            }
        }

        public bool IsTrackPersisted(string trackUri)
        {
            var track = _catalog.Tracks.FirstOrDefault(t => t.TrackUri == trackUri);
            return track != null && TrackHasPersistence(track);
        }

        public bool IsAlbumPersisted(string albumId)
        {
            return _catalog.Albums.Any(a => a.AlbumId == albumId);
        }

        public bool IsPlaylistPersisted(string playlistId)
        {
            return _catalog.Playlists.Any(p => p.PlaylistId == playlistId);
        }

        public async Task SetTrackPersistedAsync(FullTrack track, bool persisted)
        {
            if (track == null || string.IsNullOrWhiteSpace(track.Uri))
                return;

            await InitializeAsync().ConfigureAwait(false);

            LogService.Info($"[OfflineCatalogService.SetTrackPersistedAsync] Persisted={persisted} track={track.Uri}.");
            var groupId = persisted ? App.Downloads.BeginGroup(track.Name ?? "Song download", 1) : null;
            var imageLocalUri = await CacheImageAsync(track.Album?.Images?.FirstOrDefault()?.Url, track.Album?.Id ?? track.Id).ConfigureAwait(false);
            var alreadyDownloaded = false;

            if (persisted)
            {
                App.Downloads.TrackQueued(groupId, track.Uri, track.Name);
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var entry = GetOrCreateTrackEntry(track);
                    entry.ImageLocalUri = imageLocalUri ?? entry.ImageLocalUri;
                    entry.IsExplicitlySaved = true;
                    alreadyDownloaded = entry.DownloadState == DownloadTrackState.Completed;
                    if (entry.DownloadState != DownloadTrackState.Completed)
                        entry.DownloadState = DownloadTrackState.Queued;
                    await SaveAsync().ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }

                App.Downloads.TrackStarted(groupId, track.Uri, track.Name);
            }
            else
            {
                App.Downloads.ClearTrack(track.Uri);
                var shouldRemoveNativePersistence = false;
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    var entry = _catalog.Tracks.FirstOrDefault(t => t.TrackUri == track.Uri);
                    if (entry != null)
                    {
                        PopulateTrackEntry(entry, track);
                        entry.ImageLocalUri = imageLocalUri ?? entry.ImageLocalUri;
                        entry.IsExplicitlySaved = false;
                        shouldRemoveNativePersistence = !TrackIsRequested(entry);
                        if (shouldRemoveNativePersistence)
                            entry.DownloadState = DownloadTrackState.Idle;
                        CleanupTrackIfNeeded(entry.TrackUri);
                    }

                    await SaveAsync().ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }

                if (!shouldRemoveNativePersistence)
                    return;
            }

            if (alreadyDownloaded)
            {
                App.Downloads.TrackCompleted(groupId, track.Uri, track.Name);
                return;
            }

            try
            {
                await App.Librespot.SetTrackPersistedAsync(track.Uri, persisted).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                if (persisted)
                {
                    await UpdateTrackDownloadStateAsync(track.Uri, DownloadTrackState.Failed).ConfigureAwait(false);
                    App.Downloads.TrackFailed(groupId, track.Uri, track.Name, ToUserFriendlyPersistenceError(ex));
                }

                LogService.Error(ex, $"Failed to persist track {track.Uri}");
                return;
            }

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var entry = GetOrCreateTrackEntry(track);
                entry.ImageLocalUri = imageLocalUri ?? entry.ImageLocalUri;
                entry.IsExplicitlySaved = persisted;
                entry.DownloadState = persisted ? DownloadTrackState.Completed : DownloadTrackState.Idle;
                CleanupTrackIfNeeded(entry.TrackUri);
                await SaveAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            if (persisted)
                App.Downloads.TrackCompleted(groupId, track.Uri, track.Name);
        }

        public async Task SetAlbumPersistedAsync(FullAlbum album, IEnumerable<SimpleTrack> tracks, bool persisted)
        {
            if (album == null || string.IsNullOrWhiteSpace(album.Id))
                return;

            var albumTracks = tracks?.Where(t => !string.IsNullOrWhiteSpace(t?.Uri)).ToList() ?? new List<SimpleTrack>();

            await InitializeAsync().ConfigureAwait(false);

            LogService.Info($"[OfflineCatalogService.SetAlbumPersistedAsync] Persisted={persisted} album={album.Id}.");
            var albumImageUrl = album.Images?.FirstOrDefault()?.Url;
            var albumImageLocalUri = await CacheImageAsync(albumImageUrl, album.Id).ConfigureAwait(false);

            if (!persisted)
            {
                var tracksToRemove = new List<SimpleTrack>();
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    _catalog.Albums.RemoveAll(a => a.AlbumId == album.Id);
                    foreach (var track in albumTracks)
                    {
                        App.Downloads.ClearTrack(track.Uri);
                        var entry = _catalog.Tracks.FirstOrDefault(t => t.TrackUri == track.Uri);
                        if (entry == null)
                            continue;

                        UpdateMembership(entry.AlbumMembershipIds, album.Id, false);
                        if (!TrackIsRequested(entry))
                        {
                            entry.DownloadState = DownloadTrackState.Idle;
                            tracksToRemove.Add(track);
                        }

                        CleanupTrackIfNeeded(entry.TrackUri);
                    }

                    await SaveAsync().ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }

                foreach (var track in tracksToRemove)
                {
                    try
                    {
                        await App.Librespot.SetTrackPersistedAsync(track.Uri, false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogService.Error(ex, $"Failed to remove persisted album track {track.Uri}");
                    }
                }

                return;
            }

            var groupId = App.Downloads.BeginGroup(album.Name ?? "Album download", albumTracks.Count);
            foreach (var track in albumTracks)
                App.Downloads.TrackQueued(groupId, track.Uri, track.Name);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var track in albumTracks)
                {
                    var entry = GetOrCreateTrackEntry(track, album);
                    entry.ImageLocalUri = albumImageLocalUri ?? entry.ImageLocalUri;
                    UpdateMembership(entry.AlbumMembershipIds, album.Id, true);
                    if (entry.DownloadState != DownloadTrackState.Completed)
                        entry.DownloadState = DownloadTrackState.Queued;
                }

                var existing = _catalog.Albums.FirstOrDefault(a => a.AlbumId == album.Id);
                if (existing == null)
                {
                    _catalog.Albums.Add(new OfflineAlbumEntry
                    {
                        AlbumId = album.Id,
                        Name = album.Name,
                        ArtistLine = string.Join(", ", album.Artists?.Select(a => a.Name) ?? Enumerable.Empty<string>()),
                        ImageUrl = albumImageUrl,
                        ImageLocalUri = albumImageLocalUri,
                        TrackUris = albumTracks.Select(t => t.Uri).ToList(),
                        SavedAtUtc = DateTimeOffset.UtcNow
                    });
                }
                else
                {
                    existing.Name = album.Name;
                    existing.ArtistLine = string.Join(", ", album.Artists?.Select(a => a.Name) ?? Enumerable.Empty<string>());
                    existing.ImageUrl = albumImageUrl;
                    existing.ImageLocalUri = albumImageLocalUri ?? existing.ImageLocalUri;
                    existing.TrackUris = albumTracks.Select(t => t.Uri).ToList();
                }

                await SaveAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            foreach (var track in albumTracks)
            {
                try
                {
                    App.Downloads.TrackStarted(groupId, track.Uri, track.Name);
                    await App.Librespot.SetTrackPersistedAsync(track.Uri, true).ConfigureAwait(false);
                    await UpdateTrackDownloadStateAsync(track.Uri, DownloadTrackState.Completed).ConfigureAwait(false);
                    App.Downloads.TrackCompleted(groupId, track.Uri, track.Name);
                }
                catch (Exception ex)
                {
                    await UpdateTrackDownloadStateAsync(track.Uri, DownloadTrackState.Failed).ConfigureAwait(false);
                    App.Downloads.TrackFailed(groupId, track.Uri, track.Name, ToUserFriendlyPersistenceError(ex));
                    LogService.Error(ex, $"Failed to persist album track {track.Uri}");
                }
            }
        }

        public async Task SetPlaylistPersistedAsync(FullPlaylist playlist, IEnumerable<FullTrack> tracks, bool persisted)
        {
            if (playlist == null || string.IsNullOrWhiteSpace(playlist.Id))
                return;

            var playlistTracks = tracks?.Where(t => !string.IsNullOrWhiteSpace(t?.Uri)).ToList() ?? new List<FullTrack>();

            await InitializeAsync().ConfigureAwait(false);

            LogService.Info($"[OfflineCatalogService.SetPlaylistPersistedAsync] Persisted={persisted} playlist={playlist.Id}.");
            var playlistImageUrl = playlist.Images?.FirstOrDefault()?.Url;
            var playlistImageLocalUri = await CacheImageAsync(playlistImageUrl, playlist.Id).ConfigureAwait(false);
            var trackImageLocalUris = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (persisted)
            {
                foreach (var track in playlistTracks)
                {
                    trackImageLocalUris[track.Uri] = await CacheImageAsync(
                        track.Album?.Images?.FirstOrDefault()?.Url,
                        track.Album?.Id ?? track.Id).ConfigureAwait(false);
                }
            }

            if (!persisted)
            {
                var tracksToRemove = new List<FullTrack>();
                await _gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    _catalog.Playlists.RemoveAll(p => p.PlaylistId == playlist.Id);
                    foreach (var track in playlistTracks)
                    {
                        App.Downloads.ClearTrack(track.Uri);
                        var entry = _catalog.Tracks.FirstOrDefault(t => t.TrackUri == track.Uri);
                        if (entry == null)
                            continue;

                        UpdateMembership(entry.PlaylistMembershipIds, playlist.Id, false);
                        if (!TrackIsRequested(entry))
                        {
                            entry.DownloadState = DownloadTrackState.Idle;
                            tracksToRemove.Add(track);
                        }

                        CleanupTrackIfNeeded(entry.TrackUri);
                    }

                    await SaveAsync().ConfigureAwait(false);
                }
                finally
                {
                    _gate.Release();
                }

                foreach (var track in tracksToRemove)
                {
                    try
                    {
                        await App.Librespot.SetTrackPersistedAsync(track.Uri, false).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        LogService.Error(ex, $"Failed to remove persisted playlist track {track.Uri}");
                    }
                }

                return;
            }

            var groupId = App.Downloads.BeginGroup(playlist.Name ?? "Playlist download", playlistTracks.Count);
            foreach (var track in playlistTracks)
                App.Downloads.TrackQueued(groupId, track.Uri, track.Name);

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var track in playlistTracks)
                {
                    var entry = GetOrCreateTrackEntry(track);
                    entry.ImageLocalUri = trackImageLocalUris.TryGetValue(track.Uri, out var imageLocalUri)
                        ? imageLocalUri ?? entry.ImageLocalUri
                        : entry.ImageLocalUri;
                    UpdateMembership(entry.PlaylistMembershipIds, playlist.Id, true);
                    if (entry.DownloadState != DownloadTrackState.Completed)
                        entry.DownloadState = DownloadTrackState.Queued;
                }

                var existing = _catalog.Playlists.FirstOrDefault(p => p.PlaylistId == playlist.Id);
                if (existing == null)
                {
                    _catalog.Playlists.Add(new OfflinePlaylistEntry
                    {
                        PlaylistId = playlist.Id,
                        Name = playlist.Name,
                        OwnerName = playlist.Owner?.DisplayName,
                        ImageUrl = playlistImageUrl,
                        ImageLocalUri = playlistImageLocalUri,
                        TrackUris = playlistTracks.Select(t => t.Uri).ToList(),
                        SavedAtUtc = DateTimeOffset.UtcNow
                    });
                }
                else
                {
                    existing.Name = playlist.Name;
                    existing.OwnerName = playlist.Owner?.DisplayName;
                    existing.ImageUrl = playlistImageUrl;
                    existing.ImageLocalUri = playlistImageLocalUri ?? existing.ImageLocalUri;
                    existing.TrackUris = playlistTracks.Select(t => t.Uri).ToList();
                }

                await SaveAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }

            foreach (var track in playlistTracks)
            {
                try
                {
                    App.Downloads.TrackStarted(groupId, track.Uri, track.Name);
                    await App.Librespot.SetTrackPersistedAsync(track.Uri, true).ConfigureAwait(false);
                    await UpdateTrackDownloadStateAsync(track.Uri, DownloadTrackState.Completed).ConfigureAwait(false);
                    App.Downloads.TrackCompleted(groupId, track.Uri, track.Name);
                }
                catch (Exception ex)
                {
                    await UpdateTrackDownloadStateAsync(track.Uri, DownloadTrackState.Failed).ConfigureAwait(false);
                    App.Downloads.TrackFailed(groupId, track.Uri, track.Name, ToUserFriendlyPersistenceError(ex));
                    LogService.Error(ex, $"Failed to persist playlist track {track.Uri}");
                }
            }
        }

        public async Task<IReadOnlyList<OfflineTrackEntry>> GetDownloadedTracksAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            IReadOnlyList<OfflineTrackEntry> tracks = _catalog.Tracks
                .Where(TrackHasPersistence)
                .OrderBy(t => t.Name)
                .ToList();
            return tracks;
        }

        public async Task<IReadOnlyList<OfflineAlbumEntry>> GetDownloadedAlbumsAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            IReadOnlyList<OfflineAlbumEntry> albums = _catalog.Albums
                .OrderByDescending(a => a.SavedAtUtc)
                .ToList();
            return albums;
        }

        public async Task<IReadOnlyList<OfflinePlaylistEntry>> GetDownloadedPlaylistsAsync()
        {
            await InitializeAsync().ConfigureAwait(false);
            IReadOnlyList<OfflinePlaylistEntry> playlists = _catalog.Playlists
                .OrderByDescending(p => p.SavedAtUtc)
                .ToList();
            return playlists;
        }

        public async Task<IReadOnlyList<string>> GetTrackUrisForContextAsync(string contextUri)
        {
            await InitializeAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(contextUri))
                return Array.Empty<string>();

            if (contextUri.StartsWith("spotify:album:", StringComparison.OrdinalIgnoreCase))
            {
                var albumId = contextUri.Substring("spotify:album:".Length);
                return FilterDownloadedTrackUris(
                    _catalog.Albums.FirstOrDefault(a => a.AlbumId == albumId)?.TrackUris);
            }

            if (contextUri.StartsWith("spotify:playlist:", StringComparison.OrdinalIgnoreCase))
            {
                var playlistId = contextUri.Substring("spotify:playlist:".Length);
                return FilterDownloadedTrackUris(
                    _catalog.Playlists.FirstOrDefault(p => p.PlaylistId == playlistId)?.TrackUris);
            }

            if (contextUri.StartsWith("spotify:track:", StringComparison.OrdinalIgnoreCase))
                return IsTrackPersisted(contextUri)
                    ? new List<string> { contextUri }
                    : new List<string>();

            return Array.Empty<string>();
        }

        public async Task<OfflineTrackEntry> GetDownloadedTrackAsync(string trackUri)
        {
            await InitializeAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(trackUri))
                return null;

            var track = _catalog.Tracks.FirstOrDefault(t => t.TrackUri == trackUri && TrackHasPersistence(t));
            if (track == null)
                return null;

            return new OfflineTrackEntry
            {
                TrackUri = track.TrackUri,
                TrackId = track.TrackId,
                Name = track.Name,
                ArtistNames = track.ArtistNames?.ToList() ?? new List<string>(),
                ArtistLine = track.ArtistLine,
                AlbumId = track.AlbumId,
                AlbumName = track.AlbumName,
                ImageUrl = track.ImageUrl,
                ImageLocalUri = track.ImageLocalUri,
                DurationMs = track.DurationMs,
                IsExplicitlySaved = track.IsExplicitlySaved,
                DownloadState = track.DownloadState,
                AlbumMembershipIds = track.AlbumMembershipIds?.ToList() ?? new List<string>(),
                PlaylistMembershipIds = track.PlaylistMembershipIds?.ToList() ?? new List<string>()
            };
        }

        private OfflineTrackEntry GetOrCreateTrackEntry(FullTrack track)
        {
            var entry = _catalog.Tracks.FirstOrDefault(t => t.TrackUri == track.Uri);
            if (entry != null)
            {
                PopulateTrackEntry(entry, track);
                return entry;
            }

            entry = new OfflineTrackEntry();
            PopulateTrackEntry(entry, track);
            _catalog.Tracks.Add(entry);
            return entry;
        }

        private OfflineTrackEntry GetOrCreateTrackEntry(SimpleTrack track, FullAlbum album)
        {
            var entry = _catalog.Tracks.FirstOrDefault(t => t.TrackUri == track.Uri);
            if (entry == null)
            {
                entry = new OfflineTrackEntry();
                _catalog.Tracks.Add(entry);
            }

            entry.TrackUri = track.Uri;
            entry.TrackId = track.Id;
            entry.Name = track.Name;
            entry.ArtistNames = track.Artists?.Select(a => a.Name).ToList() ?? new List<string>();
            entry.ArtistLine = string.Join(", ", entry.ArtistNames);
            entry.AlbumId = album?.Id;
            entry.AlbumName = album?.Name;
            entry.ImageUrl = album?.Images?.FirstOrDefault()?.Url;
            entry.DurationMs = track.DurationMs;
            return entry;
        }

        private void PopulateTrackEntry(OfflineTrackEntry entry, FullTrack track)
        {
            entry.TrackUri = track.Uri;
            entry.TrackId = track.Id;
            entry.Name = track.Name;
            entry.ArtistNames = track.Artists?.Select(a => a.Name).ToList() ?? new List<string>();
            entry.ArtistLine = string.Join(", ", entry.ArtistNames);
            entry.AlbumId = track.Album?.Id;
            entry.AlbumName = track.Album?.Name;
            entry.ImageUrl = track.Album?.Images?.FirstOrDefault()?.Url;
            entry.DurationMs = track.DurationMs;
        }

        private async Task UpdateTrackDownloadStateAsync(string trackUri, DownloadTrackState state)
        {
            if (string.IsNullOrWhiteSpace(trackUri))
                return;

            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                var entry = _catalog.Tracks.FirstOrDefault(t => t.TrackUri == trackUri);
                if (entry == null)
                    return;

                entry.DownloadState = state;
                CleanupTrackIfNeeded(trackUri);
                await SaveAsync().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        private void MigrateDownloadedState()
        {
            foreach (var track in _catalog.Tracks.Where(track =>
                track.DownloadState == DownloadTrackState.Idle && TrackIsRequested(track)))
            {
                track.DownloadState = DownloadTrackState.Completed;
            }
        }

        private void UpdateMembership(List<string> memberships, string id, bool persisted)
        {
            if (persisted)
            {
                if (!memberships.Contains(id))
                    memberships.Add(id);
            }
            else
            {
                memberships.RemoveAll(existing => existing == id);
            }
        }

        private void CleanupTrackIfNeeded(string trackUri)
        {
            var track = _catalog.Tracks.FirstOrDefault(t => t.TrackUri == trackUri);
            if (track != null && !TrackIsRequested(track))
            {
                _catalog.Tracks.Remove(track);
            }
        }

        private IReadOnlyList<string> FilterDownloadedTrackUris(IEnumerable<string> trackUris)
        {
            if (trackUris == null)
                return new List<string>();

            var downloaded = new HashSet<string>(
                _catalog.Tracks
                    .Where(TrackHasPersistence)
                    .Select(t => t.TrackUri),
                StringComparer.OrdinalIgnoreCase);

            return trackUris
                .Where(uri => !string.IsNullOrWhiteSpace(uri) && downloaded.Contains(uri))
                .ToList();
        }

        private static bool TrackHasPersistence(OfflineTrackEntry track)
        {
            return track.DownloadState == DownloadTrackState.Completed && TrackIsRequested(track);
        }

        private static bool TrackIsRequested(OfflineTrackEntry track)
        {
            return track.IsExplicitlySaved
                || (track.AlbumMembershipIds?.Count ?? 0) > 0
                || (track.PlaylistMembershipIds?.Count ?? 0) > 0;
        }

        private Task SaveAsync()
        {
            var json = JsonConvert.SerializeObject(_catalog, Formatting.Indented);
            LogService.Info($"[OfflineCatalogService.SaveAsync] Saving offline catalog to {_catalogPath}.");
            return File.WriteAllTextAsync(_catalogPath, json);
        }

        private async Task<string> CacheImageAsync(string imageUrl, string cacheKey)
        {
            if (string.IsNullOrWhiteSpace(imageUrl) || string.IsNullOrWhiteSpace(cacheKey))
                return null;

            try
            {
                Directory.CreateDirectory(_imageFolderPath);

                var extension = ".img";
                if (Uri.TryCreate(imageUrl, UriKind.Absolute, out var imageUri))
                {
                    var candidate = Path.GetExtension(imageUri.AbsolutePath);
                    if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 8)
                        extension = candidate;
                }

                var fileName = $"{ComputeSha1($"{cacheKey}|{imageUrl}")}{extension}";
                var filePath = Path.Combine(_imageFolderPath, fileName);
                if (!File.Exists(filePath))
                {
                    var bytes = await _httpClient.GetByteArrayAsync(imageUrl).ConfigureAwait(false);
                    await File.WriteAllBytesAsync(filePath, bytes).ConfigureAwait(false);
                }

                return $"ms-appdata:///local/cached-images/{fileName}";
            }
            catch (Exception ex)
            {
                LogService.Warn($"[OfflineCatalogService.CacheImageAsync] Unable to cache image {imageUrl}: {ex.Message}");
                return null;
            }
        }

        private static string ComputeSha1(string value)
        {
            using (var sha1 = SHA1.Create())
            {
                var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(value));
                return string.Concat(bytes.Select(b => b.ToString("x2")));
            }
        }

        private static string ToUserFriendlyPersistenceError(Exception ex)
        {
            if (ex == null)
                return "Download failed.";

            var message = ex.ToString();
            if (message.IndexOf("track has no supported audio file", StringComparison.OrdinalIgnoreCase) >= 0)
                return "This track cannot be downloaded because Spotify did not provide a supported audio file.";

            return ex.Message;
        }
    }
}
