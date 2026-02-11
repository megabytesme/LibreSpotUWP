using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;

namespace LibreSpotUWP.Controls
{
    public sealed partial class AlbumGridControl : UserControl
    {
        public event EventHandler<string> AlbumClicked;
        public event EventHandler<string> ArtistClicked;

        public AlbumGridControl()
        {
            InitializeComponent();

            ArtistClicked += (s, artistId) =>
            {
                var frame = Window.Current.Content as Frame;
                var main = frame?.Content as MainPage;
                main?.NavigateToArtist(artistId);
            };

            AlbumClicked += (s, albumId) =>
            {
                var frame = Window.Current.Content as Frame;
                var main = frame?.Content as MainPage;
                main?.NavigateToAlbum(albumId);
            };
        }

        public void SetAlbums(IEnumerable<SimpleAlbum> albums)
        {
            AlbumItems.ItemsSource = albums;
        }

        private void Album_Click(object sender, RoutedEventArgs e)
        {
            var id = (sender as FrameworkElement)?.Tag as string;

            if (!string.IsNullOrEmpty(id))
                AlbumClicked?.Invoke(this, id);
        }

        private void Artist_Click(Hyperlink sender, HyperlinkClickEventArgs e)
        {
            var parent = sender.ElementStart?.Parent as TextBlock;
            var id = parent?.Tag as string;

            if (!string.IsNullOrEmpty(id))
                ArtistClicked?.Invoke(this, id);
        }
    }
}