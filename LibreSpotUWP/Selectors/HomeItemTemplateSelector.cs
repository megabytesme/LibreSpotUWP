using SpotifyAPI.Web;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Selectors
{
    public class HomeItemTemplateSelector : DataTemplateSelector
    {
        public DataTemplate PlaylistTemplate { get; set; }
        public DataTemplate AlbumTemplate { get; set; }
        public DataTemplate SavedAlbumTemplate { get; set; }
        public DataTemplate ArtistTemplate { get; set; }
        public DataTemplate TrackTemplate { get; set; }

        protected override DataTemplate SelectTemplateCore(object item, DependencyObject container)
        {
            if (item is FullPlaylist) return PlaylistTemplate;
            if (item is FullArtist) return ArtistTemplate;
            if (item is FullTrack) return TrackTemplate;
            if (item is FullAlbum) return AlbumTemplate;
            if (item is SavedAlbum) return SavedAlbumTemplate;

            return base.SelectTemplateCore(item, container);
        }
    }
}