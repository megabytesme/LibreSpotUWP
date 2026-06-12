using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using LibreSpotUWP.Helpers;

namespace LibreSpotUWP.Controls
{
    public sealed partial class ArtistGridControl : UserControl
    {
        public event EventHandler<string> ArtistClicked;

        public ArtistGridControl()
        {
            InitializeComponent();
        }

        public void SetArtists(IEnumerable<FullArtist> artists)
        {
            var items = new List<ArtistItem>();

            foreach (var a in artists)
            {
                items.Add(new ArtistItem
                {
                    Id = a.Id,
                    Name = a.Name,
                    Image = ImageUriHelper.CreateBitmapImage(
                        a.Images?.Count > 0 ? a.Images[0].Url : null,
                        useFallback: true)
                });
            }

            ArtistRepeater.ItemsSource = items;
        }

        private void OnArtistTapped(object sender, Windows.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is ArtistItem item)
                ArtistClicked?.Invoke(this, item.Id);
        }

        private class ArtistItem
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public ImageSource Image { get; set; }
        }
    }
}
