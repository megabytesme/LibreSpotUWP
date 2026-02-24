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
        public event EventHandler LoadMoreRequested;

        private bool _showAlbum;
        private bool _isLoadingMore = false;

        public TrackListControl()
        {
            this.InitializeComponent();
            this.TrackListView.Loaded += TrackListView_Loaded;
        }

        private void TrackListView_Loaded(object sender, RoutedEventArgs e)
        {
            var scrollViewer = FindVisualChild<ScrollViewer>(TrackListView);
            if (scrollViewer != null)
            {
                scrollViewer.ViewChanged += OnScrollViewerViewChanged;
            }
        }

        private void OnScrollViewerViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var sv = sender as ScrollViewer;
            if (sv == null) return;

            if (sv.VerticalOffset >= sv.ScrollableHeight - 200 && sv.ScrollableHeight > 0)
            {
                if (!_isLoadingMore)
                {
                    _isLoadingMore = true;
                    LoadingIndicator.Visibility = Visibility.Visible;
                    LoadMoreRequested?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        public void SetIsLoading(bool loading)
        {
            _isLoadingMore = loading;
            LoadingIndicator.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        }

        public void AddTracks(IEnumerable<FullTrack> tracks, bool clearExisting, int startingIndex = 0)
        {
            if (clearExisting)
            {
                TrackListView.Items.Clear();
                _showAlbum = tracks.Any(t => t.Album != null);
                AddHeader();
            }

            foreach (var t in tracks)
            {
                var item = new TrackListItem
                {
                    TrackNumber = ++startingIndex,
                    Name = t?.Name ?? "",
                    ArtistName = t?.Artists != null ? string.Join(", ", t.Artists.Select(a => a.Name)) : "",
                    ArtistObjects = t?.Artists?.ToList() ?? new List<SimpleArtist>(),
                    AlbumName = t?.Album?.Name ?? "",
                    AlbumId = t?.Album?.Id,
                    AlbumArt = t?.Album?.Images?.Count > 0 ? new BitmapImage(new Uri(t.Album.Images[0].Url)) : null,
                    Duration = TimeSpan.FromMilliseconds(t?.DurationMs ?? 0).ToString(@"m\:ss"),
                    RawTrack = t
                };

                TrackListView.Items.Add(CreateTrackRow(item));
            }

            _isLoadingMore = false;
            LoadingIndicator.Visibility = Visibility.Collapsed;
        }

        private void AddHeader()
        {
            var grid = new Grid { Padding = new Thickness(8), Background = (Brush)Application.Current.Resources["SystemControlBackgroundChromeMediumLowBrush"] };
            foreach (var column in CreateColumns(_showAlbum)) grid.ColumnDefinitions.Add(column);

            int col = 0;
            AddHeaderText(grid, "#", col++);
            if (_showAlbum) AddHeaderText(grid, "", col++);
            AddHeaderText(grid, "Title", col++);
            if (_showAlbum) AddHeaderText(grid, "Album", col++);
            AddHeaderText(grid, "Time", col++);

            TrackListView.Header = grid;
        }

        private void AddHeaderText(Grid grid, string text, int col)
        {
            var tb = new TextBlock { Text = text, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }

        private ColumnDefinition[] CreateColumns(bool showAlbum)
        {
            if (showAlbum)
            {
                return new[]
                {
                    new ColumnDefinition { Width = new GridLength(40) },
                    new ColumnDefinition { Width = new GridLength(60) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                };
            }
            return new[]
            {
                new ColumnDefinition { Width = new GridLength(40) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            };
        }

        private UIElement CreateTrackRow(TrackListItem item)
        {
            var rowButton = new Button
            {
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0
            };

            rowButton.PointerEntered += (s, e) => rowButton.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255));
            rowButton.PointerExited += (s, e) => rowButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
            rowButton.Click += (s, e) => TrackClicked?.Invoke(this, new TrackClickedEventArgs(item.RawTrack));

            var grid = new Grid { Padding = new Thickness(8) };
            foreach (var column in CreateColumns(_showAlbum)) grid.ColumnDefinitions.Add(column);
            rowButton.Content = grid;

            int col = 0;

            // Track number
            var num = new TextBlock { Text = item.TrackNumber.ToString(), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(num, col++);
            grid.Children.Add(num);

            // Album art
            if (_showAlbum)
            {
                var img = new Windows.UI.Xaml.Controls.Image { Width = 48, Height = 48, Source = item.AlbumArt, Stretch = Stretch.UniformToFill };
                Grid.SetColumn(img, col++);
                grid.Children.Add(img);
            }

            // Title/Artist
            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock { Text = item.Name, FontWeight = Windows.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.WordEllipsis });

            var artistText = new TextBlock { TextTrimming = TextTrimming.WordEllipsis, FontSize = 12, Opacity = 0.8 };
            bool first = true;
            foreach (var artist in item.ArtistObjects)
            {
                if (!first) artistText.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = ", " });
                first = false;
                var artistLink = new Windows.UI.Xaml.Documents.Hyperlink();
                artistLink.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = artist.Name });
                artistLink.Click += (s, e) => { if (!string.IsNullOrEmpty(artist.Id)) ArtistClicked?.Invoke(this, artist.Id); };
                artistText.Inlines.Add(artistLink);
            }
            titleStack.Children.Add(artistText);
            Grid.SetColumn(titleStack, col++);
            grid.Children.Add(titleStack);

            // Album name
            if (_showAlbum)
            {
                var albumText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.WordEllipsis };
                var albumHyperlink = new Windows.UI.Xaml.Documents.Hyperlink();
                albumHyperlink.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = item.AlbumName });
                albumHyperlink.Click += (s, e) => { if (!string.IsNullOrEmpty(item.AlbumId)) AlbumClicked?.Invoke(this, item.AlbumId); };
                albumText.Inlines.Add(albumHyperlink);
                Grid.SetColumn(albumText, col++);
                grid.Children.Add(albumText);
            }

            // Duration
            var dur = new TextBlock { Text = item.Duration, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dur, col++);
            grid.Children.Add(dur);

            return rowButton;
        }

        private T FindVisualChild<T>(DependencyObject obj) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                if (child is T t) return t;
                T childItem = FindVisualChild<T>(child);
                if (childItem != null) return childItem;
            }
            return null;
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