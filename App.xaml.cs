using System.Windows;

namespace WpfVideoPlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Handle unhandled exceptions
        DispatcherUnhandledException += (s, ex) =>
        {
            MessageBox.Show($"Lỗi không mong muốn: {ex.Exception.Message}", 
                "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}
