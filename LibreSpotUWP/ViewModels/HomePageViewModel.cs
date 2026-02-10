using System.Collections.ObjectModel;
using SpotifyAPI.Web;

namespace LibreSpotUWP.ViewModels
{
    public class HomePageViewModel
    {
        public ObservableCollection<PlayHistoryItem> RecentlyPlayed { get; } = new ObservableCollection<PlayHistoryItem>();
        public ObservableCollection<FullPlaylist> UserPlaylists { get; } = new ObservableCollection<FullPlaylist>();
        public ObservableCollection<FullArtist> TopArtists { get; } = new ObservableCollection<FullArtist>();
        public ObservableCollection<FullTrack> TopTracks { get; } = new ObservableCollection<FullTrack>();
    }
}