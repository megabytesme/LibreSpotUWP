using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace LibreSpotUWP.Controls
{
    public sealed partial class PlayActionsControl : UserControl
    {
        public event EventHandler PlayRequested;
        public event EventHandler ShuffleRequested;
        public event EventHandler AddToRequested;
        public event EventHandler DownloadRequested;

        public PlayActionsControl()
        {
            InitializeComponent();

            BtnPlay.Click += (s, e) => PlayRequested?.Invoke(this, EventArgs.Empty);
            BtnShuffle.Click += (s, e) => ShuffleRequested?.Invoke(this, EventArgs.Empty);
            BtnAddTo.Click += (s, e) => AddToRequested?.Invoke(this, EventArgs.Empty);
            BtnDownload.Click += (s, e) => DownloadRequested?.Invoke(this, EventArgs.Empty);
        }

        public void SetAdded(bool added, string addedTooltip = "Remove from library", string addTooltip = "Add to library")
        {
            AddToIcon.Foreground = GetStateBrush(added);
            ToolTipService.SetToolTip(BtnAddTo, added ? addedTooltip : addTooltip);
        }

        public void SetDownloaded(bool downloaded)
        {
            DownloadIcon.Foreground = GetStateBrush(downloaded);
            ToolTipService.SetToolTip(BtnDownload, downloaded ? "Remove download" : "Download for offline");
        }

        private static Brush GetStateBrush(bool active)
        {
            return (Brush)Application.Current.Resources[active
                ? "SystemControlHighlightAccentBrush"
                : "SystemControlForegroundBaseHighBrush"];
        }
    }
}
