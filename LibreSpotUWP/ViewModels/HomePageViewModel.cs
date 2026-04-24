using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LibreSpotUWP.ViewModels
{
    public class HomeSectionGroup
    {
        public string Title { get; set; }
        public ObservableCollection<object> Items { get; set; } = new ObservableCollection<object>();
    }

    public class HomePageViewModel
    {
        private static readonly HashSet<string> _skipIds = new HashSet<string>();
        private bool _usedCachedData;
        private bool _usedOfflineFallback;
        private DateTimeOffset? _cachedAt;

        public string StatusMessage { get; private set; }
        public DateTimeOffset? CachedAt => _cachedAt;
        public ObservableCollection<HomeSectionGroup> GroupedHomeContent { get; } = new ObservableCollection<HomeSectionGroup>();

        public ObservableCollection<FullPlaylist> RecentlyPlayedPlaylists { get; } = new ObservableCollection<FullPlaylist>();
        public ObservableCollection<FullAlbum> RecentlyPlayedAlbums { get; } = new ObservableCollection<FullAlbum>();
        public ObservableCollection<FullArtist> RecentlyPlayedArtists { get; } = new ObservableCollection<FullArtist>();
        public ObservableCollection<FullTrack> RecentlyPlayedTracks { get; } = new ObservableCollection<FullTrack>();
        public ObservableCollection<FullTrack> UserTopTracksShortTerm { get; } = new ObservableCollection<FullTrack>();
        public ObservableCollection<FullArtist> UserTopArtistsShortTerm { get; } = new ObservableCollection<FullArtist>();
        public ObservableCollection<SavedAlbum> SavedAlbumsFull { get; } = new ObservableCollection<SavedAlbum>();
        public ObservableCollection<FullPlaylist> UserPlaylists { get; } = new ObservableCollection<FullPlaylist>();
        public ObservableCollection<FullArtist> FollowedArtists { get; } = new ObservableCollection<FullArtist>();
        public ObservableCollection<FullAlbum> AlbumsFromTopArtists { get; } = new ObservableCollection<FullAlbum>();
        public ObservableCollection<FullAlbum> AlbumsYouStarted { get; } = new ObservableCollection<FullAlbum>();
        public ObservableCollection<FullTrack> MixedForYou { get; } = new ObservableCollection<FullTrack>();

        public HomePageViewModel()
        {
            InitializeGroups();
        }

        private void InitializeGroups()
        {
            GroupedHomeContent.Add(new HomeSectionGroup { Title = "Home" });
        }

        public async Task LoadAsync(ISpotifyWebService spotify, CancellationToken ct, bool forceRefresh = false)
        {
            GroupedHomeContent.Clear();
            InitializeGroups();
            _usedCachedData = false;
            _usedOfflineFallback = false;
            _cachedAt = null;
            StatusMessage = null;

            var tasks = new Task[]
            {
                LoadRecentlyPlayedAsync(spotify, ct, forceRefresh),
                LoadUserPlaylistsAsync(spotify, ct, forceRefresh),
                LoadTopArtistsAsync(spotify, ct, forceRefresh),
                LoadTopTracksAsync(spotify, ct, forceRefresh),
                LoadSavedAlbumsAsync(spotify, ct, forceRefresh),
                LoadFollowedArtistsAsync(spotify, ct, forceRefresh)
            };

            await Task.WhenAll(tasks);

            await LoadAlbumsYouStartedAsync(spotify, ct, forceRefresh);
            await LoadAlbumsFromTopArtistsAsync(spotify, ct, forceRefresh);

            if (_usedOfflineFallback)
                StatusMessage = "Offline. Home is showing cached sections from earlier sessions.";
            else if (!ConnectivityHelper.HasInternetAccess())
                StatusMessage = GroupedHomeContent.Count > 1
                    ? "Offline. Only cached home sections are available right now."
                    : "Offline. Home needs a connection before it can be cached.";
            else if (_usedCachedData)
                StatusMessage = "Showing cached home data.";
        }

        public async Task LoadOfflineAsync(IOfflineCatalogService offlineCatalog)
        {
            GroupedHomeContent.Clear();
            InitializeGroups();
            _cachedAt = null;

            var tracks = await offlineCatalog.GetDownloadedTracksAsync();
            var albums = await offlineCatalog.GetDownloadedAlbumsAsync();
            var playlists = await offlineCatalog.GetDownloadedPlaylistsAsync();
            var mixed = tracks.Take(30).ToList();

            AddGroup("Downloaded Playlists", new ObservableCollection<OfflinePlaylistEntry>(playlists));
            AddGroup("Downloaded Albums", new ObservableCollection<OfflineAlbumEntry>(albums));
            AddGroup("Downloaded Songs", new ObservableCollection<OfflineTrackEntry>(tracks));
            AddGroup("Mixed For You", new ObservableCollection<OfflineTrackEntry>(mixed));

            StatusMessage = GroupedHomeContent.Count > 1
                ? "Offline. Home is showing your downloaded music."
                : "Offline. Download music while online and it will appear here.";
        }

        private void AddGroup<T>(string title, ObservableCollection<T> sourceItems)
        {
            if (sourceItems == null || sourceItems.Count == 0)
                return;

            var group = new HomeSectionGroup { Title = title };
            foreach (var item in sourceItems)
                group.Items.Add(item);

            GroupedHomeContent.Add(group);
        }

        private async Task LoadRecentlyPlayedAsync(ISpotifyWebService spotify, CancellationToken ct, bool forceRefresh)
        {
            try
            {
                var resp = await spotify.GetRecentlyPlayedAsync(20, forceRefresh, ct);
                RegisterCacheUse(resp);
                var rp = resp.Value;

                RecentlyPlayedTracks.Clear();
                RecentlyPlayedPlaylists.Clear();
                RecentlyPlayedAlbums.Clear();
                RecentlyPlayedArtists.Clear();

                foreach (var item in rp.Items)
                {
                    var track = item.Track;
                    if (track == null)
                        continue;

                    RecentlyPlayedTracks.Add(track);

                    if (track.Album != null)
                    {
                        var album = ToFullAlbum(track.Album);
                        if (album != null && !_skipIds.Contains(album.Id))
                        {
                            RecentlyPlayedAlbums.Add(album);
                            _skipIds.Add(album.Id);
                        }
                    }

                    foreach (var artist in track.Artists)
                    {
                        var fullArtist = ToFullArtist(artist);
                        if (fullArtist != null && !_skipIds.Contains(fullArtist.Id))
                        {
                            RecentlyPlayedArtists.Add(fullArtist);
                            _skipIds.Add(fullArtist.Id);
                        }
                    }

                    if (item.Context == null)
                        continue;

                    var uri = item.Context.Uri;

                    if (uri.StartsWith("spotify:playlist:"))
                    {
                        var id = uri.Substring("spotify:playlist:".Length);

                        if (_skipIds.Contains(id))
                            continue;

                        try
                        {
                            var playlistResp = await spotify.GetPlaylistAsync(id, forceRefresh, ct);
                            RegisterCacheUse(playlistResp);
                            RecentlyPlayedPlaylists.Add(playlistResp.Value);
                        }
                        catch
                        {
                            _skipIds.Add(id);
                        }

                        continue;
                    }

                    if (uri.StartsWith("spotify:album:"))
                    {
                        var id = uri.Substring("spotify:album:".Length);

                        if (_skipIds.Contains(id) || track.Album == null || track.Album.Id != id)
                            continue;

                        RecentlyPlayedAlbums.Add(ToFullAlbum(track.Album));
                        _skipIds.Add(id);

                        continue;
                    }

                    if (uri.StartsWith("spotify:artist:"))
                    {
                        var id = uri.Substring("spotify:artist:".Length);

                        if (_skipIds.Contains(id))
                            continue;

                        var artist = track.Artists?.FirstOrDefault(a => a.Id == id);
                        if (artist != null)
                        {
                            RecentlyPlayedArtists.Add(ToFullArtist(artist));
                            _skipIds.Add(id);
                        }
                    }
                }

                AddGroup("Recently Played Playlists", RecentlyPlayedPlaylists);
                AddGroup("Recently Played Albums", RecentlyPlayedAlbums);
                AddGroup("Recently Played Artists", RecentlyPlayedArtists);
                AddGroup("Recently Played Tracks", RecentlyPlayedTracks);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadRecentlyPlayedAsync failed: " + ex);
            }
        }

        private async Task LoadUserPlaylistsAsync(ISpotifyWebService spotify, CancellationToken ct, bool forceRefresh)
        {
            try
            {
                var resp = await spotify.GetCurrentUserPlaylistsAsync(forceRefresh, ct);
                RegisterCacheUse(resp);
                var playlists = resp.Value;

                UserPlaylists.Clear();
                foreach (var p in playlists.Items)
                    UserPlaylists.Add(p);

                AddGroup("Your Playlists", UserPlaylists);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadUserPlaylistsAsync failed: " + ex);
            }
        }

        private async Task LoadTopArtistsAsync(ISpotifyWebService spotify, CancellationToken ct, bool forceRefresh)
        {
            try
            {
                var resp = await spotify.GetUserTopArtistsAsync(20, forceRefresh, ct);
                RegisterCacheUse(resp);
                var top = resp.Value;

                UserTopArtistsShortTerm.Clear();
                foreach (var a in top.Items)
                    UserTopArtistsShortTerm.Add(a);

                AddGroup("Top Artists", UserTopArtistsShortTerm);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadTopArtistsAsync failed: " + ex);
            }
        }

        private async Task LoadTopTracksAsync(ISpotifyWebService spotify, CancellationToken ct, bool forceRefresh)
        {
            try
            {
                var resp = await spotify.GetUserTopTracksAsync(20, forceRefresh, ct);
                RegisterCacheUse(resp);
                var top = resp.Value;

                UserTopTracksShortTerm.Clear();
                foreach (var t in top.Items)
                    UserTopTracksShortTerm.Add(t);

                AddGroup("Top Tracks", UserTopTracksShortTerm);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadTopTracksAsync failed: " + ex);
            }
        }

        private async Task LoadSavedAlbumsAsync(ISpotifyWebService spotify, CancellationToken ct, bool forceRefresh)
        {
            try
            {
                var resp = await spotify.GetSavedAlbumsAsync(forceRefresh, ct);
                RegisterCacheUse(resp);
                var saved = resp.Value;

                SavedAlbumsFull.Clear();
                foreach (var a in saved.Items)
                    SavedAlbumsFull.Add(a);

                AddGroup("Saved Albums", SavedAlbumsFull);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadSavedAlbumsAsync failed: " + ex);
            }
        }

        private async Task LoadFollowedArtistsAsync(ISpotifyWebService spotify, CancellationToken ct, bool forceRefresh)
        {
            try
            {
                var resp = await spotify.GetFollowedArtistsAsync(forceRefresh, ct);
                RegisterCacheUse(resp);
                var followed = resp.Value;

                FollowedArtists.Clear();
                foreach (var a in followed.Artists.Items)
                    FollowedArtists.Add(a);

                AddGroup("Artists You Follow", FollowedArtists);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadFollowedArtistsAsync failed: " + ex);
            }
        }

        private async Task LoadAlbumsFromTopArtistsAsync(ISpotifyWebService spotify, CancellationToken ct, bool forceRefresh)
        {
            AlbumsFromTopArtists.Clear();
            var seen = new HashSet<string>();

            foreach (var artist in UserTopArtistsShortTerm)
            {
                try
                {
                    var respAlbums = await spotify.GetArtistAlbumsAsync(artist.Id, forceRefresh, ct);
                    RegisterCacheUse(respAlbums);
                    var albums = respAlbums.Value;

                    foreach (var a in albums.Items)
                    {
                        if (seen.Contains(a.Id))
                            continue;

                        seen.Add(a.Id);
                        AlbumsFromTopArtists.Add(ToFullAlbum(a));
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AlbumsFromTopArtists: failed artist {artist.Id}: {ex.Message}");
                }
            }

            AddGroup("Albums From Your Top Artists", AlbumsFromTopArtists);
        }

        private async Task LoadAlbumsYouStartedAsync(ISpotifyWebService spotify, CancellationToken ct, bool forceRefresh)
        {
            AlbumsYouStarted.Clear();
            var seen = new HashSet<string>();

            foreach (var track in RecentlyPlayedTracks)
            {
                var albumId = track.Album?.Id;
                if (albumId == null || seen.Contains(albumId))
                    continue;

                seen.Add(albumId);

                AlbumsYouStarted.Add(ToFullAlbum(track.Album));
            }

            AddGroup("Albums You Started", AlbumsYouStarted);
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

        private void RegisterCacheUse<T>(CacheResponse<T> response)
        {
            if (response == null)
                return;

            _usedCachedData |= response.IsFromCache;
            _usedOfflineFallback |= response.IsOfflineFallback;

            if ((response.IsFromCache || response.IsOfflineFallback) &&
                (!_cachedAt.HasValue || response.Timestamp > _cachedAt.Value))
            {
                _cachedAt = response.Timestamp;
            }
        }
    }
}
