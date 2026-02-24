using LibreSpotUWP.Interfaces;
using SpotifyAPI.Web;
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

        public ObservableCollection<SearchSectionGroup> GroupedSearchContent { get; }
            = new ObservableCollection<SearchSectionGroup>();

        public async Task LoadAsync(string query)
        {
            var response = (await _web.SearchAsync(
                query,
                SearchRequest.Types.All
            )).Value;

            Artists = response.Artists?.Items ?? new List<FullArtist>();
            Albums = response.Albums?.Items ?? new List<SimpleAlbum>();
            Tracks = response.Tracks?.Items ?? new List<FullTrack>();
            Playlists = response.Playlists?.Items ?? new List<FullPlaylist>();

            GroupedSearchContent.Clear();

            AddGroup("Artists", Artists);
            AddGroup("Albums", Albums);
            AddGroup("Tracks", Tracks);
            AddGroup("Playlists", Playlists);
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
