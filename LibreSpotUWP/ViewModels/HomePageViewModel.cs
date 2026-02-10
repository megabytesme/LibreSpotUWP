using LibreSpotUWP.Interfaces;
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

        public async Task LoadAsync(ISpotifyWebService spotify, CancellationToken ct)
        {
            var tasks = new Task[]
            {
                LoadRecentlyPlayedAsync(spotify, ct),
                LoadUserPlaylistsAsync(spotify, ct),
                LoadTopArtistsAsync(spotify, ct),
                LoadTopTracksAsync(spotify, ct),
                LoadSavedAlbumsAsync(spotify, ct),
                LoadFollowedArtistsAsync(spotify, ct)
            };

            await Task.WhenAll(tasks);

            await LoadAlbumsYouStartedAsync(spotify, ct);
            await LoadAlbumsFromTopArtistsAsync(spotify, ct);
            await LoadMixedForYouAsync(spotify, ct);
        }

        private void AddGroup<T>(string title, ObservableCollection<T> sourceItems)
        {
            if (sourceItems == null || sourceItems.Count == 0) return;

            var group = new HomeSectionGroup { Title = title };
            foreach (var item in sourceItems)
            {
                group.Items.Add(item);
            }
            GroupedHomeContent.Add(group);
        }

        private async Task LoadRecentlyPlayedAsync(ISpotifyWebService spotify, CancellationToken ct)
        {
            try
            {
                var resp = await spotify.GetRecentlyPlayedAsync(20, false, ct);
                var rp = resp.Value;

                System.Diagnostics.Debug.WriteLine("Recently played returned: " + rp.Items.Count + " items");

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
                        var albumId = track.Album.Id;

                        if (!_skipIds.Contains(albumId))
                        {
                            try
                            {
                                var fullAlbumResp = await spotify.GetAlbumAsync(albumId, false, ct);
                                RecentlyPlayedAlbums.Add(fullAlbumResp.Value);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed album {albumId}: {ex.Message}");
                                _skipIds.Add(albumId);
                            }
                        }
                    }

                    foreach (var artist in track.Artists)
                    {
                        var artistId = artist.Id;

                        if (!_skipIds.Contains(artistId))
                        {
                            try
                            {
                                var fullArtistResp = await spotify.GetArtistAsync(artistId, false, ct);
                                RecentlyPlayedArtists.Add(fullArtistResp.Value);
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine($"Failed artist {artistId}: {ex.Message}");
                                _skipIds.Add(artistId);
                            }
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
                            var playlistResp = await spotify.GetPlaylistAsync(id, false, ct);
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

                        if (_skipIds.Contains(id))
                            continue;

                        try
                        {
                            var albumResp = await spotify.GetAlbumAsync(id, false, ct);
                            RecentlyPlayedAlbums.Add(albumResp.Value);
                        }
                        catch
                        {
                            _skipIds.Add(id);
                        }

                        continue;
                    }

                    if (uri.StartsWith("spotify:artist:"))
                    {
                        var id = uri.Substring("spotify:artist:".Length);

                        if (_skipIds.Contains(id))
                            continue;

                        try
                        {
                            var artistResp = await spotify.GetArtistAsync(id, false, ct);
                            RecentlyPlayedArtists.Add(artistResp.Value);
                        }
                        catch
                        {
                            _skipIds.Add(id);
                        }

                        continue;
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

        private async Task LoadUserPlaylistsAsync(ISpotifyWebService spotify, CancellationToken ct)
        {
            try
            {
                var resp = await spotify.GetCurrentUserPlaylistsAsync(false, ct);
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

        private async Task LoadTopArtistsAsync(ISpotifyWebService spotify, CancellationToken ct)
        {
            try
            {
                var resp = await spotify.GetUserTopArtistsAsync(20, false, ct);
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

        private async Task LoadTopTracksAsync(ISpotifyWebService spotify, CancellationToken ct)
        {
            try
            {
                var resp = await spotify.GetUserTopTracksAsync(20, false, ct);
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

        private async Task LoadSavedAlbumsAsync(ISpotifyWebService spotify, CancellationToken ct)
        {
            try
            {
                var resp = await spotify.GetSavedAlbumsAsync(false, ct);
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

        private async Task LoadFollowedArtistsAsync(ISpotifyWebService spotify, CancellationToken ct)
        {
            try
            {
                var resp = await spotify.GetFollowedArtistsAsync(false, ct);
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

        private async Task LoadAlbumsFromTopArtistsAsync(ISpotifyWebService spotify, CancellationToken ct)
        {
            AlbumsFromTopArtists.Clear();
            var seen = new HashSet<string>();

            foreach (var artist in UserTopArtistsShortTerm)
            {
                try
                {
                    var respAlbums = await spotify.GetArtistAlbumsAsync(artist.Id, false, ct);
                    var albums = respAlbums.Value;

                    foreach (var a in albums.Items)
                    {
                        if (seen.Contains(a.Id))
                            continue;

                        seen.Add(a.Id);

                        try
                        {
                            var respFull = await spotify.GetAlbumAsync(a.Id, false, ct);
                            AlbumsFromTopArtists.Add(respFull.Value);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"AlbumsFromTopArtists: failed album {a.Id}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AlbumsFromTopArtists: failed artist {artist.Id}: {ex.Message}");
                }
            }

            AddGroup("Albums From Your Top Artists", AlbumsFromTopArtists);
        }

        private async Task LoadAlbumsYouStartedAsync(ISpotifyWebService spotify, CancellationToken ct)
        {
            AlbumsYouStarted.Clear();
            var seen = new HashSet<string>();

            foreach (var track in RecentlyPlayedTracks)
            {
                var albumId = track.Album?.Id;
                if (albumId == null || seen.Contains(albumId))
                    continue;

                seen.Add(albumId);

                try
                {
                    var resp = await spotify.GetAlbumAsync(albumId, false, ct);
                    AlbumsYouStarted.Add(resp.Value);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"AlbumsYouStarted: failed album {albumId}: {ex.Message}");
                }
            }

            AddGroup("Albums You Started", AlbumsYouStarted);
        }

        private async Task LoadMixedForYouAsync(ISpotifyWebService spotify, CancellationToken ct)
        {
            MixedForYou.Clear();
            var seen = new HashSet<string>();

            void add(FullTrack t)
            {
                if (t != null && seen.Add(t.Id))
                    MixedForYou.Add(t);
            }

            foreach (var t in RecentlyPlayedTracks.Take(20))
                add(t);

            foreach (var t in UserTopTracksShortTerm.Take(20))
                add(t);

            foreach (var artist in UserTopArtistsShortTerm.Take(10))
            {
                try
                {
                    var respAlbums = await spotify.GetArtistAlbumsAsync(artist.Id, false, ct);
                    var albums = respAlbums.Value;

                    foreach (var simpleAlbum in albums.Items.Take(3))
                    {
                        try
                        {
                            var respTracks = await spotify.GetAlbumTracksAsync(simpleAlbum.Id, false, ct);
                            var albumTracks = respTracks.Value;

                            foreach (var simpleTrack in albumTracks.Items.Take(5))
                            {
                                try
                                {
                                    var respFull = await spotify.GetTrackAsync(simpleTrack.Id, false, ct);
                                    add(respFull.Value);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine(
                                        $"MixedForYou: failed to promote track {simpleTrack.Id}: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"MixedForYou: failed to load album tracks for {simpleAlbum.Id}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"MixedForYou: failed to load albums for artist {artist.Id}: {ex.Message}");
                }
            }

            AddGroup("Mixed For You", MixedForYou);
        }
    }
}