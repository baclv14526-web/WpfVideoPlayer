using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace WpfVideoPlayer.Converters;

public class BoolToPlayPauseConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => (bool)v ? "⏸" : "▶";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class BoolToMuteIconConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => (bool)v ? "🔇" : "🔊";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        bool b = (bool)v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class BoolToAccentBrushConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => (bool)v
            ? new SolidColorBrush(Color.FromRgb(0x6C, 0x63, 0xFF))
            : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0));
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class BoolToFullscreenIconConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => (bool)v ? "⊠" : "⛶";
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class BoolToPlaylistWidthConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => (bool)v ? new GridLength(320) : new GridLength(0);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class NormalizedToLeftConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length >= 2 &&
            values[0] is double norm &&
            values[1] is double width &&
            width > 0)
        {
            double offset = 8;
            if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double pOffset))
                offset = pOffset;
            return Math.Clamp((norm * width) - offset, 0, Math.Max(0, width - (offset * 2)));
        }
        return 0.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v == null || p == null) return Visibility.Collapsed;
        return v.ToString() == p.ToString() ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class StringToSolidBrushConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is string colorStr && !string.IsNullOrEmpty(colorStr))
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(colorStr);
                return new SolidColorBrush(color);
            }
            catch { }
        }
        return new SolidColorBrush(Colors.Orange);
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}

public class EqualityToBrushConverter : IValueConverter
{
    public Brush ActiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x6C, 0x63, 0xFF));
    public Brush InactiveBrush { get; set; } = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0xA0));

    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v == null || p == null) return InactiveBrush;
        return v.ToString() == p.ToString() ? ActiveBrush : InactiveBrush;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
