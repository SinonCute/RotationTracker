using System;
using Windows.UI;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Media;

namespace RotationTracker.Converters
{
    /// <summary>
    /// Compares the bound step's index (passed as ConverterParameter-friendly tag via MultiBinding alternatives is not
    /// available on UWP). Expects a boolean "IsCurrent" input and returns a highlight brush for the row.
    /// </summary>
    public sealed class CurrentStepHighlightConverter : IValueConverter
    {
        public Color CurrentColor { get; set; } = Color.FromArgb(0xFF, 0x4C, 0xAF, 0x50);
        public Color InactiveColor { get; set; } = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            bool isCurrent = value is bool b && b;
            return new SolidColorBrush(isCurrent ? CurrentColor : InactiveColor);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
