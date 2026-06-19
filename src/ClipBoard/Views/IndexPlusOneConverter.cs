using System.Globalization;
using System.Windows.Data;

namespace ClipBoard.Views;

public class IndexPlusOneConverter : IValueConverter
{
    public static readonly IndexPlusOneConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int i) return (i + 1).ToString("D2");
        return "??";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class SkewAngleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is Guid id)
        {
            uint hash = (uint)id.GetHashCode();
            // Map to -1.5 .. +1.5 degrees
            return ((hash % 301) / 100.0) - 1.5;
        }
        return 0.0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
