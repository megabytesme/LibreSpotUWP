using LibreSpotUWP.Interfaces;
using SpotifyAPI.Web;
using System.Threading.Tasks;

namespace LibreSpotUWP.ViewModels
{
    public class AlbumPageViewModel
    {
        private readonly ISpotifyWebService _web = App.SpotifyWeb;

        public FullAlbum Album { get; private set; }
        public Paging<SimpleTrack> Tracks { get; private set; }

        public async Task LoadAsync(string id)
        {
            Album = (await _web.GetAlbumAsync(id)).Value;
            Tracks = (await _web.GetAlbumTracksAsync(id)).Value;
        }

        public async void PlayAlbum()
        {
            if (Album == null)
                return;

            await App.Media.SetShuffleAsync(false);
            await App.Media.PlayAsync($"spotify:album:{Album.Id}", "");
        }

        public async void ShuffleAlbum()
        {
            if (Album == null)
                return;

            await App.Media.SetShuffleAsync(true);
            await App.Media.PlayAsync($"spotify:album:{Album.Id}", "");
        }
    }
}