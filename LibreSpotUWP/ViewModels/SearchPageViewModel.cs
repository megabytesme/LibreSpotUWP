using LibreSpotUWP.Helpers;
using LibreSpotUWP.Interfaces;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace LibreSpotUWP.ViewModels
{
    public class SearchSectionGroup { public string Title { get; set; } public ObservableCollection<object> Items { get; set; } = new ObservableCollection<object>(); }

    public class SearchPageViewModel
    {
        private readonly ISpotifyWebService _web = App.SpotifyWeb;

        public List<FullArtist> Artists { get; private set; }
        public List<SimpleAlbum> Albums { get; private set; }
        public List<FullTrack> Tracks { get; private set; }
        public List<FullPlaylist> Playlists { get; private set; }
        public string StatusMessage { get; private set; }
        public DateTimeOffset? CachedAt { get; private set; }

        public ObservableCollection<SearchSectionGroup> GroupedSearchContent { get; }
            = new ObservableCollection<SearchSectionGroup>();

        public async Task LoadAsync(string query, bool forceRefresh = false)
        {
            try
            {
                var searchResponse = await _web.SearchAsync(
                    query,
                    SearchRequest.Types.All,
                    forceRefresh
                );
                var response = searchResponse.Value;

                Artists = response.Artists?.Items ?? new List<FullArtist>();
                Albums = response.Albums?.Items ?? new List<SimpleAlbum>();
                Tracks = response.Tracks?.Items ?? new List<FullTrack>();
                Playlists = response.Playlists?.Items ?? new List<FullPlaylist>();

                GroupedSearchContent.Clear();

                AddGroup("Artists", Artists);
                AddGroup("Albums", Albums);
                AddGroup("Tracks", Tracks);
                AddGroup("Playlists", Playlists);

                if (searchResponse.IsOfflineFallback)
                    StatusMessage = "Offline. Search results are coming from cache.";
                else if (!ConnectivityHelper.HasInternetAccess())
                    StatusMessage = GroupedSearchContent.Count > 0
                        ? "Offline. Only searches you've already opened are available right now."
                        : "Offline. Search needs a connection before results can be cached.";
                else if (searchResponse.IsFromCache)
                    StatusMessage = "Showing cached search results.";
                else
                    StatusMessage = null;

                CachedAt = searchResponse.IsFromCache || searchResponse.IsOfflineFallback
                    ? searchResponse.Timestamp
                    : (DateTimeOffset?)null;
            }
            catch (Exception)
            {
                Artists = new List<FullArtist>();
                Albums = new List<SimpleAlbum>();
                Tracks = new List<FullTrack>();
                Playlists = new List<FullPlaylist>();
                GroupedSearchContent.Clear();
                StatusMessage = ConnectivityHelper.HasInternetAccess()
                    ? "Search could not be loaded right now."
                    : "Offline. Search is unavailable because these results have not been cached yet.";
                CachedAt = null;
            }
        }

        private void AddGroup(string title, IEnumerable<object> items)
        {
            var group = new SearchSectionGroup { Title = title };

            foreach (var item in items)
                group.Items.Add(item);

            if (group.Items.Count > 0)
                GroupedSearchContent.Add(group);
        }
    }
}
