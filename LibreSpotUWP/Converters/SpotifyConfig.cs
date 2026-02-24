using SpotifyAPI.Web;
using System;
using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media.Imaging;

namespace LibreSpotUWP.Converters
{
    public sealed class TimeConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            uint ms = (uint)value;
            TimeSpan t = TimeSpan.FromMilliseconds(ms);
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }

    public sealed class BoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => (bool)value ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }

    public sealed class InverseBoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => !(bool)value ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }

    public sealed class VolumeToPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
            => ((ushort)value * 100.0) / 65535.0;

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }

    public class ImageCollectionToUrlConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is IList<Image> images && images.Count > 0)
            {
                var url = images[0].Url;
                if (!string.IsNullOrEmpty(url))
                {
                    return new BitmapImage(new Uri(url));
                }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}