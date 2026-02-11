using SpotifyAPI.Web;
using System;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Controls {
    public static class HyperlinkTag
    {
        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.RegisterAttached(
                "Value",
                typeof(string),
                typeof(HyperlinkTag),
                new PropertyMetadata(null));

        public static void SetValue(DependencyObject obj, string value)
        {
            obj.SetValue(ValueProperty, value);
        }

        public static string GetValue(DependencyObject obj)
        {
            return (string)obj.GetValue(ValueProperty);
        }
    }

    public sealed partial class HeaderImageControl : UserControl
    {
        public event EventHandler<string> ArtistClicked;

        private string _currentId;
        private string _currentType;

        public HeaderImageControl()
        {
            InitializeComponent();

            ArtistClicked += (s, artistId) =>
            {
                var frame = Window.Current.Content as Frame;
                var main = frame?.Content as MainPage;
                main?.NavigateToArtist(artistId);
            };
        }

        private bool IsArtistId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return false;

            if (id.Length != 22)
                return false;

            foreach (char c in id)
            {
                bool ok =
                    (c >= 'A' && c <= 'Z') ||
                    (c >= 'a' && c <= 'z') ||
                    (c >= '0' && c <= '9');

                if (!ok)
                    return false;
            }

            return true;
        }

        private void Artist_Click(Hyperlink sender, HyperlinkClickEventArgs e)
        {
            var id = HyperlinkTag.GetValue(sender);

            if (string.IsNullOrEmpty(id))
                return;

            if (IsArtistId(id))
            {
                ArtistClicked?.Invoke(this, id);
            }
            else
            {
                var url = $"https://open.spotify.com/user/{id}";
                Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
        }

        public void SetAlbum(FullAlbum album)
        {
            _currentId = album.Id;
            _currentType = "album";

            TitleText.Text = album.Name;

            SubtitleText.Inlines.Clear();
            bool first = true;

            foreach (var artist in album.Artists)
            {
                if (!first)
                    SubtitleText.Inlines.Add(new Run { Text = ", " });
                first = false;

                var link = new Hyperlink();
                link.Inlines.Add(new Run { Text = artist.Name });
                HyperlinkTag.SetValue(link, artist.Id);
                link.Click += Artist_Click;

                SubtitleText.Inlines.Add(link);
            }

            var img = album.Images?.FirstOrDefault()?.Url;
            MainImage.Source = img != null ? new BitmapImage(new Uri(img)) : null;
            MetadataPanel.Children.Clear();
            MetadataPanel.Children.Add(new TextBlock { Text = $"Released: {album.ReleaseDate}", TextWrapping = TextWrapping.Wrap });
            MetadataPanel.Children.Add(new TextBlock { Text = $"Label: {album.Label}", TextWrapping = TextWrapping.Wrap });
            MetadataPanel.Children.Add(new TextBlock { Text = $"Tracks: {album.TotalTracks}", TextWrapping = TextWrapping.Wrap });
            MetadataPanel.Children.Add(new TextBlock { Text = $"Popularity: {album.Popularity}", TextWrapping = TextWrapping.Wrap });
        }

        public void SetArtist(FullArtist artist)
        {
            _currentId = artist.Id;
            _currentType = "artist";

            TitleText.Text = artist.Name;

            SubtitleText.Inlines.Clear();
            SubtitleText.Inlines.Add(new Run
            {
                Text = $"{artist.Followers?.Total ?? 0:N0} followers"
            });

            var img = artist.Images?.FirstOrDefault()?.Url;
            MainImage.Source = img != null ? new BitmapImage(new Uri(img)) : null;

            ImageBorder.CornerRadius = new CornerRadius(70);

            MetadataPanel.Children.Clear();
            MetadataPanel.Children.Add(new TextBlock { Text = $"Popularity: {artist.Popularity}", TextWrapping = TextWrapping.Wrap });

            if (artist.Genres?.Any() == true)
                MetadataPanel.Children.Add(new TextBlock { Text = $"Genres: {string.Join(", ", artist.Genres)}", TextWrapping = TextWrapping.Wrap });
        }

        public void SetPlaylist(FullPlaylist playlist)
        {
            _currentId = playlist.Id;
            _currentType = "playlist";

            TitleText.Text = playlist.Name;

            SubtitleText.Inlines.Clear();

            if (playlist.Owner != null)
            {
                var link = new Hyperlink();
                link.Inlines.Add(new Run { Text = playlist.Owner.DisplayName });
                HyperlinkTag.SetValue(link, playlist.Owner.Id);
                link.Click += Artist_Click;

                SubtitleText.Inlines.Add(link);
            }

            var img = playlist.Images?.FirstOrDefault()?.Url;
            MainImage.Source = img != null ? new BitmapImage(new Uri(img)) : null;

            ImageBorder.CornerRadius = new CornerRadius(4);

            MetadataPanel.Children.Clear();
            MetadataPanel.Children.Add(new TextBlock { Text = $"{playlist.Followers?.Total ?? 0:N0} followers", TextWrapping = TextWrapping.Wrap });
            MetadataPanel.Children.Add(new TextBlock { Text = $"Tracks: {playlist.Tracks?.Total ?? 0}", TextWrapping = TextWrapping.Wrap });
            MetadataPanel.Children.Add(new TextBlock { Text = playlist.Public == true ? "Public" : "Private", TextWrapping = TextWrapping.Wrap });

            if (!string.IsNullOrEmpty(playlist.Description))
                MetadataPanel.Children.Add(new TextBlock { Text = playlist.Description, TextWrapping = TextWrapping.Wrap });
        }
    }
}
