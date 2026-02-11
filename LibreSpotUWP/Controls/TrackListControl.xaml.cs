using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Controls
{
    public sealed partial class TrackListControl : UserControl
    {
        public event EventHandler<TrackClickedEventArgs> TrackClicked;
        public event EventHandler<string> ArtistClicked;
        public event EventHandler<string> AlbumClicked;

        public TrackListControl()
        {
            InitializeComponent();
        }

        public void SetTracks(IEnumerable<SimpleTrack> tracks)
        {
            var formatted = tracks.Select((t, i) => new TrackListItem
            {
                TrackNumber = i + 1,
                Name = t.Name,
                ArtistName = string.Join(", ", t.Artists.Select(a => a.Name)),
                ArtistObjects = t.Artists.ToList(),
                AlbumName = "",
                AlbumId = null,
                AlbumArt = null,
                Duration = TimeSpan.FromMilliseconds(t.DurationMs).ToString(@"m\:ss"),
                RawTrack = t
            }).ToList();

            RenderTrackList(formatted);
        }

        public void SetFullTracks(IEnumerable<FullTrack> tracks)
        {
            var formatted = tracks.Select((t, i) => new TrackListItem
            {
                TrackNumber = i + 1,
                Name = t?.Name ?? "",
                ArtistName = t?.Artists != null
                    ? string.Join(", ", t.Artists.Select(a => a.Name))
                    : "",
                ArtistObjects = t?.Artists?.ToList() ?? new List<SimpleArtist>(),
                AlbumName = t?.Album?.Name ?? "",
                AlbumId = t?.Album?.Id,
                AlbumArt = t?.Album?.Images?.Count > 0
                    ? new BitmapImage(new Uri(t.Album.Images[0].Url))
                    : null,
                Duration = TimeSpan.FromMilliseconds(t?.DurationMs ?? 0).ToString(@"m\:ss"),
                RawTrack = t
            }).ToList();

            RenderTrackList(formatted);
        }

        public void SetPlaylistTracks(IEnumerable<PlaylistTrack<IPlayableItem>> tracks)
        {
            var formatted = tracks.Select((t, i) =>
            {
                var track = t.Track as FullTrack;

                return new TrackListItem
                {
                    TrackNumber = i + 1,
                    Name = track?.Name ?? "",
                    ArtistName = string.Join(", ", track?.Artists?.Select(a => a.Name) ?? Enumerable.Empty<string>()),
                    ArtistObjects = track?.Artists?.ToList() ?? new List<SimpleArtist>(),
                    AlbumName = track?.Album?.Name ?? "",
                    AlbumId = track?.Album?.Id,
                    AlbumArt = track?.Album?.Images?.Count > 0
                        ? new BitmapImage(new Uri(track.Album.Images[0].Url))
                        : null,
                    Duration = track != null
                        ? TimeSpan.FromMilliseconds(track.DurationMs).ToString(@"m\:ss")
                        : "",
                    RawTrack = track
                };
            }).ToList();

            RenderTrackList(formatted);
        }

        private bool _showAlbum;

        private void RenderTrackList(List<TrackListItem> items)
        {
            TrackListPanel.Children.Clear();

            _showAlbum = ShouldShowAlbumColumn(items);

            AddHeader();
            foreach (var item in items)
                AddTrackRow(item);
        }

        private bool ShouldShowAlbumColumn(List<TrackListItem> items)
        {
            return items.Any(i =>
                (!string.IsNullOrWhiteSpace(i.AlbumName)) ||
                (i.AlbumArt != null)
            );
        }

        private ColumnDefinition[] CreateColumns(bool showAlbum)
        {
            if (showAlbum)
            {
                return new[]
                {
                    new ColumnDefinition { Width = new GridLength(40) },   // Track number
                    new ColumnDefinition { Width = new GridLength(60) },   // Album art
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, // Title and artists
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, // Album name
                    new ColumnDefinition { Width = GridLength.Auto }       // Duration
                };
            }
            else
            {
                return new[]
                {
                    new ColumnDefinition { Width = new GridLength(40) },   // Track number
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }, // Title and artists
                    new ColumnDefinition { Width = GridLength.Auto }       // Duration
                };
            }
        }

        private void AddHeader()
        {
            var grid = new Grid { Padding = new Thickness(8) };
            foreach (var column in CreateColumns(_showAlbum)) grid.ColumnDefinitions.Add(column);

            int col = 0;

            AddHeaderText(grid, "#", col++);
            if (_showAlbum) AddHeaderText(grid, "", col++);
            AddHeaderText(grid, "Title", col++);
            if (_showAlbum) AddHeaderText(grid, "Album", col++);
            AddHeaderText(grid, "Time", col++);

            TrackListPanel.Children.Add(grid);
        }

        private void AddHeaderText(Grid grid, string text, int col)
        {
            var tb = new TextBlock
            {
                Text = text,
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        private void AddTrackRow(TrackListItem item)
        {
            var rowButton = new Button
            {
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                MinWidth = 0
            };

            rowButton.PointerEntered += (s, e) =>
                rowButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255));
            rowButton.PointerExited += (s, e) =>
                rowButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
            rowButton.PointerPressed += (s, e) =>
                rowButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 255, 255, 255));
            rowButton.PointerReleased += (s, e) =>
                rowButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255));

            rowButton.Click += (s, e) =>
                TrackClicked?.Invoke(this, new TrackClickedEventArgs(item.RawTrack));

            var grid = new Grid { Padding = new Thickness(8) };
            foreach (var column in CreateColumns(_showAlbum)) grid.ColumnDefinitions.Add(column);
            rowButton.Content = grid;

            int col = 0;

            // Track number
            var num = new TextBlock
            {
                Text = item.TrackNumber.ToString(),
                Opacity = 0.6,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(num, col++);
            grid.Children.Add(num);

            // Album art
            if (_showAlbum)
            {
                var img = new Windows.UI.Xaml.Controls.Image
                {
                    Width = 48,
                    Height = 48,
                    Source = item.AlbumArt,
                    Stretch = Windows.UI.Xaml.Media.Stretch.UniformToFill
                };
                Grid.SetColumn(img, col++);
                grid.Children.Add(img);
            }

            // Title and artists
            var titleStack = new StackPanel();

            // Title
            titleStack.Children.Add(new TextBlock
            {
                Text = item.Name,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.WordEllipsis
            });

            // Artists
            var artistText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.WordEllipsis
            };

            bool first = true;

            foreach (var artist in item.ArtistObjects)
            {
                if (!first)
                {
                    artistText.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = ", " });
                }
                first = false;

                var artistLink = new Windows.UI.Xaml.Documents.Hyperlink();
                artistLink.Inlines.Add(new Windows.UI.Xaml.Documents.Run
                {
                    Text = artist.Name
                });

                artistLink.Click += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(artist.Id))
                        ArtistClicked?.Invoke(this, artist.Id);
                };

                artistText.Inlines.Add(artistLink);
            }

            titleStack.Children.Add(artistText);

            Grid.SetColumn(titleStack, col++);
            grid.Children.Add(titleStack);

            // Album name
            if (_showAlbum)
            {
                var albumContainer = new Grid
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var albumText = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.WordEllipsis
                };

                var albumHyperlink = new Windows.UI.Xaml.Documents.Hyperlink();
                albumHyperlink.Inlines.Add(new Windows.UI.Xaml.Documents.Run
                {
                    Text = item.AlbumName
                });

                albumHyperlink.Click += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(item.AlbumId))
                        AlbumClicked?.Invoke(this, item.AlbumId);
                };

                albumText.Inlines.Add(albumHyperlink);
                albumContainer.Children.Add(albumText);

                Grid.SetColumn(albumContainer, col++);
                grid.Children.Add(albumContainer);
            }

            // Duration
            var dur = new TextBlock
            {
                Text = item.Duration,
                Opacity = 0.7,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(dur, col++);
            grid.Children.Add(dur);

            TrackListPanel.Children.Add(rowButton);
        }
    }

    public class TrackListItem
    {
        public int TrackNumber { get; set; }
        public string Name { get; set; }
        public string ArtistName { get; set; }
        public List<SimpleArtist> ArtistObjects { get; set; }
        public string AlbumName { get; set; }
        public string AlbumId { get; set; }
        public BitmapImage AlbumArt { get; set; }
        public string Duration { get; set; }
        public object RawTrack { get; set; }
    }

    public class TrackClickedEventArgs : EventArgs
    {
        public object Track { get; }
        public TrackClickedEventArgs(object track) => Track = track;
    }
}