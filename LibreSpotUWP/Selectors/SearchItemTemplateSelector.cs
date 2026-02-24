using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using SpotifyAPI.Web;

namespace LibreSpotUWP.Selectors
{
    public class SearchItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate ArtistTemplate { get; set; }
        public DataTemplate AlbumTemplate { get; set; }
        public DataTemplate TrackTemplate { get; set; }
        public DataTemplate PlaylistTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item)
        {
            if (item is FullArtist) return ArtistTemplate;
            if (item is SimpleAlbum) return AlbumTemplate;
            if (item is FullTrack) return TrackTemplate;
            if (item is FullPlaylist) return PlaylistTemplate;

            return base.SelectTemplateCore(item);
        }
    }
}
