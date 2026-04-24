using LibreSpotUWP.Models;
using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

using LibreSpotUWP.Helpers;

namespace LibreSpotUWP.Controls
{
    public sealed partial class TrackListControl : UserControl
    {
        public event EventHandler<TrackClickedEventArgs> TrackClicked;
        public event EventHandler<TrackClickedEventArgs> TrackPersistRequested;
        public event EventHandler<string> ArtistClicked;
        public event EventHandler<string> AlbumClicked;
        public event EventHandler LoadMoreRequested;

        private bool _showAlbum;
        private bool _isLoadingMore = false;
        private readonly Dictionary<string, TrackRowVisuals> _rowVisuals = new Dictionary<string, TrackRowVisuals>(StringComparer.OrdinalIgnoreCase);
        public Func<FullTrack, bool> IsTrackPersistedResolver { get; set; }

        public TrackListControl()
        {
            this.InitializeComponent();
            this.TrackListView.Loaded += TrackListView_Loaded;
            this.Unloaded += TrackListControl_Unloaded;
            if (App.Downloads != null)
                App.Downloads.TrackStatusChanged += OnTrackStatusChanged;
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
                _rowVisuals.Clear();
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
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = GridLength.Auto }
                };
            }
            return new[]
            {
                new ColumnDefinition { Width = new GridLength(40) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            };
        }

        private UIElement CreateTrackRow(TrackListItem item)
        {
            var rootGrid = new Grid();
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var rowGrid = new Grid
            {
                Padding = new Thickness(8),
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent)
            };

            rowGrid.PointerEntered += (s, e) => rowGrid.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 255, 255, 255));
            rowGrid.PointerExited += (s, e) => rowGrid.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
            foreach (var column in CreateColumns(_showAlbum)) rowGrid.ColumnDefinitions.Add(column);

            int col = 0;

            var num = new TextBlock { Text = item.TrackNumber.ToString(), Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(num, col++);
            rowGrid.Children.Add(num);

            if (_showAlbum)
            {
                var img = new Windows.UI.Xaml.Controls.Image { Width = 48, Height = 48, Source = item.AlbumArt, Stretch = Stretch.UniformToFill };
                Grid.SetColumn(img, col++);
                rowGrid.Children.Add(img);
            }

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
            rowGrid.Children.Add(titleStack);

            if (_showAlbum)
            {
                var albumText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.WordEllipsis };
                var albumHyperlink = new Windows.UI.Xaml.Documents.Hyperlink();
                albumHyperlink.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = item.AlbumName });
                albumHyperlink.Click += (s, e) => { if (!string.IsNullOrEmpty(item.AlbumId)) AlbumClicked?.Invoke(this, item.AlbumId); };
                albumText.Inlines.Add(albumHyperlink);
                Grid.SetColumn(albumText, col++);
                rowGrid.Children.Add(albumText);
            }

            var dur = new TextBlock { Text = item.Duration, Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(dur, col++);
            rowGrid.Children.Add(dur);

            var track = item.RawTrack as FullTrack;
            var persisted = track != null && IsTrackPersistedResolver?.Invoke(track) == true;
            var persistIcon = new FontIcon
            {
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                Glyph = persisted ? "\uE738" : "\uE710"
            };
            var persistButton = new Button
            {
                Content = persistIcon,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 36
            };
            var progressRing = new ProgressRing
            {
                Width = 18,
                Height = 18,
                IsActive = false,
                Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            var actionHost = new Grid();
            actionHost.Children.Add(persistButton);
            actionHost.Children.Add(progressRing);

            persistButton.Tapped += (s, e) =>
            {
                e.Handled = true;
                TrackPersistRequested?.Invoke(this, new TrackClickedEventArgs(item.RawTrack));
            };
            Grid.SetColumn(actionHost, col++);
            rowGrid.Children.Add(actionHost);

            var progressBar = new ProgressBar
            {
                IsIndeterminate = true,
                Height = 3,
                Visibility = Visibility.Collapsed
            };

            Grid.SetRow(rowGrid, 0);
            Grid.SetRow(progressBar, 1);
            rootGrid.Children.Add(rowGrid);
            rootGrid.Children.Add(progressBar);

            if (item.RawTrack is FullTrack fullTrack && !string.IsNullOrWhiteSpace(fullTrack.Uri))
            {
                var visuals = new TrackRowVisuals
                {
                    TrackUri = fullTrack.Uri,
                    RowGrid = rowGrid,
                    PersistButton = persistButton,
                    PersistIcon = persistIcon,
                    ProgressRing = progressRing,
                    ProgressBar = progressBar
                };
                _rowVisuals[fullTrack.Uri] = visuals;
                UpdateRowVisuals(visuals, fullTrack.Uri);
            }

            rowGrid.Tapped += (s, e) =>
            {
                if (item.RawTrack is FullTrack tappedTrack && !IsTrackAvailable(tappedTrack.Uri))
                    return;

                TrackClicked?.Invoke(this, new TrackClickedEventArgs(item.RawTrack));
            };

            return rootGrid;
        }

        private async void OnTrackStatusChanged(object sender, TrackDownloadStatus status)
        {
            await Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () =>
                {
                    if (status?.TrackUri == null)
                        return;

                    if (_rowVisuals.TryGetValue(status.TrackUri, out var visuals))
                        UpdateRowVisuals(visuals, status.TrackUri);
                });
        }

        private void UpdateRowVisuals(TrackRowVisuals visuals, string trackUri)
        {
            var persisted = IsTrackPersistedResolver?.Invoke(new FullTrack { Uri = trackUri }) == true;
            var downloadState = App.Downloads?.GetTrackStatus(trackUri)?.State ?? DownloadTrackState.Idle;
            var isDownloading = downloadState == DownloadTrackState.Queued || downloadState == DownloadTrackState.Downloading;

            visuals.ProgressRing.IsActive = isDownloading;
            visuals.ProgressRing.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
            visuals.ProgressBar.Visibility = isDownloading ? Visibility.Visible : Visibility.Collapsed;
            visuals.PersistButton.Visibility = isDownloading ? Visibility.Collapsed : Visibility.Visible;
            visuals.PersistButton.IsEnabled = !isDownloading;
            visuals.PersistIcon.Glyph = (persisted || downloadState == DownloadTrackState.Completed) ? "\uE738" : "\uE710";
            visuals.RowGrid.Opacity = IsTrackAvailable(trackUri) ? 1.0 : 0.45;
        }

        private static bool IsTrackAvailable(string trackUri)
        {
            return ConnectivityHelper.HasInternetAccess() || App.OfflineCatalog.IsTrackPersisted(trackUri);
        }

        private void TrackListControl_Unloaded(object sender, RoutedEventArgs e)
        {
            if (App.Downloads != null)
                App.Downloads.TrackStatusChanged -= OnTrackStatusChanged;
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

    internal sealed class TrackRowVisuals
    {
        public string TrackUri { get; set; }
        public Grid RowGrid { get; set; }
        public Button PersistButton { get; set; }
        public FontIcon PersistIcon { get; set; }
        public ProgressRing ProgressRing { get; set; }
        public ProgressBar ProgressBar { get; set; }
    }
}
