using LibreSpotUWP.Helpers;
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using Windows.UI.Xaml.Media.Imaging;
using ZXing;
using ZXing.Common;

namespace LibreSpotUWP.Services
{
    public static class BarcodeUIService
    {
        public static WriteableBitmap GenerateBarcodeBitmap(string value, BarcodeFormat displayType, int width, int height)
        {
            if (string.IsNullOrEmpty(value)) return null;

            if (!BarcodeHelper.IsSupportedDisplayType(displayType) ||
                !BarcodeHelper.ValidateBarcode(value, displayType, out _))
            {
                System.Diagnostics.Debug.WriteLine($"GenerateBarcodeBitmap: Invalid type ({displayType}) or value for barcode generation. Falling back to QR_CODE.");
                displayType = BarcodeFormat.QR_CODE;
                if (!BarcodeHelper.ValidateBarcode(value, displayType, out _))
                {
                    System.Diagnostics.Debug.WriteLine($"GenerateBarcodeBitmap: Value invalid even for QR_CODE: {value}");
                    return null;
                }
            }

            BarcodeWriterPixelData writer = new BarcodeWriterPixelData
            {
                Format = displayType,
                Options = new EncodingOptions
                {
                    Height = height,
                    Width = width,
                    Margin = (width > 300 || height > 300) ? 10 : 2
                }
            };

            try
            {
                var pixelData = writer.Write(value);
                if (pixelData?.Pixels == null)
                {
                    System.Diagnostics.Debug.WriteLine($"ZXing writer.Write returned null pixel data for value: {value}, type: {displayType}");
                    return null;
                }

                WriteableBitmap bitmap = new WriteableBitmap(pixelData.Width, pixelData.Height);

                using (var stream = bitmap.PixelBuffer.AsStream())
                {
                    stream.Write(pixelData.Pixels, 0, pixelData.Pixels.Length);
                }
                return bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error generating barcode WriteableBitmap: {ex.Message}");
                return null;
            }
        }

        public static async Task<WriteableBitmap> GenerateQrCodeBitmapAsync(string content, int size = 450)
        {
            if (string.IsNullOrEmpty(content)) return null;

            return await Task.Run(async () =>
            {
                BarcodeWriterPixelData writer = new BarcodeWriterPixelData
                {
                    Format = BarcodeFormat.QR_CODE,
                    Options = new EncodingOptions
                    {
                        Height = size,
                        Width = size,
                        Margin = 0
                    }
                };

                try
                {
                    var pixelData = writer.Write(content);
                    if (pixelData?.Pixels == null)
                    {
                        System.Diagnostics.Debug.WriteLine($"ZXing QR writer.Write returned null pixel data.");
                        return null;
                    }

                    WriteableBitmap bitmap = null;
                    await CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        bitmap = new WriteableBitmap(pixelData.Width, pixelData.Height);
                        using (var stream = bitmap.PixelBuffer.AsStream())
                        {
                            stream.Write(pixelData.Pixels, 0, pixelData.Pixels.Length);
                        }
                    });

                    return bitmap;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Error generating QR code WriteableBitmap: {ex.Message}");
                    return null;
                }
            });
        }
    }
}