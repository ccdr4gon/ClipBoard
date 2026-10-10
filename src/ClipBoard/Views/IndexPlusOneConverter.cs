using System.Globalization;
using System.Windows.Data;

namespace ClipBoard.Views;

public class IndexPlusOneConverter : IValueConverter
{
    public static readonly IndexPlusOneConverter Instance = new();

    // 行号每次建行都要转换一次：按需缓存 1..999 的字符串（AlternationCount=999），不再每次分配。
    private static readonly string?[] Labels = new string?[1000];

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int i)
        {
            int n = i + 1;
            return (uint)n < (uint)Labels.Length ? Labels[n] ??= n.ToString("D2") : n.ToString("D2");
        }
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
