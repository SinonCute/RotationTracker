using System;
using Windows.UI.Xaml.Data;

namespace RotationTracker.Converters
{
    public sealed class BooleanToOpacityConverter : IValueConverter
    {
        public double TrueOpacity { get; set; } = 1.0;
        public double FalseOpacity { get; set; } = 0.35;

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool asBool = value is bool b && b;
            return asBool ? TrueOpacity : FalseOpacity;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
