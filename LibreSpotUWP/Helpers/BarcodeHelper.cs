using System;
using ZXing.Common;
using ZXing;

namespace LibreSpotUWP.Helpers
{
    public static class BarcodeHelper
    {
        public static bool IsSupportedDisplayType(BarcodeFormat displayType)
        {
            switch (displayType)
            {
                case BarcodeFormat.AZTEC:
                case BarcodeFormat.CODABAR:
                case BarcodeFormat.CODE_39:
                case BarcodeFormat.CODE_128:
                case BarcodeFormat.DATA_MATRIX:
                case BarcodeFormat.EAN_8:
                case BarcodeFormat.EAN_13:
                case BarcodeFormat.ITF:
                case BarcodeFormat.PDF_417:
                case BarcodeFormat.QR_CODE:
                case BarcodeFormat.UPC_A:
                case BarcodeFormat.UPC_E:
                case BarcodeFormat.MSI:
                case BarcodeFormat.PLESSEY:
                    return true;
                default:
                    return false;
            }
        }

        public static bool ValidateBarcode(string value, BarcodeFormat displayType, out string errorMessage)
        {
            try
            {
                BarcodeWriterPixelData writer = new BarcodeWriterPixelData
                {
                    Format = displayType,
                    Options = new EncodingOptions
                    {
                        Height = 200,
                        Width = 200,
                        Margin = 1
                    }
                };

                var pixelData = writer.Write(value);
                errorMessage = null;
                return true;
            }
            catch (ArgumentException ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }
}
