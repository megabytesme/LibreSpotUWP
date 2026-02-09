using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using ZXing;
using Windows.UI.Core;

namespace LibreSpotUWP
{
    public sealed partial class ScannerPage : Page
    {
        public static ScannerResult LastScanResult { get; internal set; }

        public ScannerPage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            StartScanning();
        }

        protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
        {
            scannerControl?.StopScanning();
            base.OnNavigatingFrom(e);
        }

        private void StartScanning()
        {
            scannerControl?.StartScanning(result =>
            {
                if (result != null)
                {
                    LastScanResult = new ScannerResult { Text = result.Text, Format = result.BarcodeFormat };
                }
                else
                {
                    LastScanResult = null;
                }

                _ = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (Frame != null && Frame.CanGoBack)
                    {
                        Frame.GoBack();
                    }
                });
            });
        }

        public class ScannerResult
        {
            public string Text { get; set; }
            public BarcodeFormat Format { get; set; }
        }
    }
}