using System;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Controls
{
    public sealed partial class PlayActionsControl : UserControl
    {
        public event EventHandler PlayRequested;
        public event EventHandler ShuffleRequested;

        public PlayActionsControl()
        {
            InitializeComponent();

            BtnPlay.Click += (s, e) => PlayRequested?.Invoke(this, EventArgs.Empty);
            BtnShuffle.Click += (s, e) => ShuffleRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}