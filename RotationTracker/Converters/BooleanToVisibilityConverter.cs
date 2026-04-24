using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Data;

namespace RotationTracker.Converters
{
    public sealed class BooleanToVisibilityConverter : IValueConverter
    {
        public bool Invert { get; set; }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool asBool = value is bool b && b;
            if (Invert) asBool = !asBool;
            return asBool ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            bool asBool = value is Visibility v && v == Visibility.Visible;
            if (Invert) asBool = !asBool;
            return asBool;
        }
    }
}
