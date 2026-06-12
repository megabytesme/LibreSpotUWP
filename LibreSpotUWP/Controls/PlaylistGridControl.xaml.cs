using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using LibreSpotUWP.Helpers;

namespace LibreSpotUWP.Controls
{
    public sealed partial class PlaylistGridControl : UserControl
    {
        public event EventHandler<string> PlaylistClicked;

        public PlaylistGridControl()
        {
            InitializeComponent();
        }

        public void SetPlaylists(IEnumerable<FullPlaylist> playlists)
        {
            var items = new List<PlaylistItem>();

            foreach (var p in playlists)
            {
                if (p?.Id == null)
                    continue;

                items.Add(new PlaylistItem
                {
                    Id = p.Id,
                    Name = p.Name ?? "(Unknown Playlist)",
                    Image = ImageUriHelper.CreateBitmapImage(
                        p.Images?.Count > 0 ? p.Images[0].Url : null,
                        useFallback: true)
                });
            }

            PlaylistRepeater.ItemsSource = items;
        }

        private void OnPlaylistTapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is PlaylistItem item)
                PlaylistClicked?.Invoke(this, item.Id);
        }

        private class PlaylistItem
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public ImageSource Image { get; set; }
        }
    }
}
