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
        => (bool)v ? new GridLength(280) : new GridLength(0);
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => Binding.DoNothing;
}
