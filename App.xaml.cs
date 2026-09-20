using System.Threading.Tasks;
using System.Windows;
using LibVLCSharp.Shared;

namespace WpfVideoPlayer;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Pre-warm LibVLC native libraries asynchronously in background to eliminate UI freeze
        Task.Run(() =>
        {
            try
            {
                Core.Initialize();
            }
            catch { }
        });

        // Handle unhandled exceptions
        DispatcherUnhandledException += (s, ex) =>
        {
            MessageBox.Show($"Lỗi không mong muốn: {ex.Exception.Message}", 
                "Lỗi", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
    }
}
