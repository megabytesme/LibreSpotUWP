using System;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Controls
{
    public sealed partial class PlayActionsControl : UserControl
    {
        public event EventHandler PlayRequested;
        public event EventHandler ShuffleRequested;
        public event EventHandler PersistRequested;

        public PlayActionsControl()
        {
            InitializeComponent();

            BtnPlay.Click += (s, e) => PlayRequested?.Invoke(this, EventArgs.Empty);
            BtnShuffle.Click += (s, e) => ShuffleRequested?.Invoke(this, EventArgs.Empty);
            BtnPersist.Click += (s, e) => PersistRequested?.Invoke(this, EventArgs.Empty);
        }

        public void SetPersisted(bool persisted)
        {
            PersistIcon.Glyph = persisted ? "\uE738" : "\uE710";
            ToolTipService.SetToolTip(BtnPersist, persisted ? "Remove download" : "Download for offline");
        }
    }
}
