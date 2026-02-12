using LibreSpotUWP.Interfaces;
using SpotifyAPI.Web;
using System.Threading.Tasks;

namespace LibreSpotUWP.ViewModels
{
    public class PlaylistPageViewModel
    {
        private readonly ISpotifyWebService _web = App.SpotifyWeb;

        public FullPlaylist Playlist { get; private set; }
        public Paging<PlaylistTrack<IPlayableItem>> Tracks { get; private set; }

        public async Task LoadAsync(string id)
        {
            Playlist = (await _web.GetPlaylistAsync(id)).Value;
            Tracks = (await _web.GetPlaylistItemsAsync(id)).Value;
        }

        public async void PlayPlaylist()
        {
            if (Playlist == null)
                return;

            await App.Media.SetShuffleAsync(false);
            await App.Media.PlayAsync($"spotify:playlist:{Playlist.Id}", "");
        }

        public async void ShufflePlaylist()
        {
            if (Playlist == null)
                return;

            await App.Media.SetShuffleAsync(true);
            await App.Media.PlayAsync($"spotify:playlist:{Playlist.Id}", "");
        }

    }
}