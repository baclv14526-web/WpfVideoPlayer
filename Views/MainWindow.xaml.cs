using System.Windows;
using System.Windows.Input;
using WpfVideoPlayer.ViewModels;

namespace WpfVideoPlayer.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Watch fullscreen toggle
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsFullscreen))
                ApplyFullscreen(_vm.IsFullscreen);
        };

        // Add extra commands that need window reference
        _vm.ToggleShuffleCommand = new RelayCommand(() => _vm.IsShuffle = !_vm.IsShuffle);
        _vm.ToggleRepeatCommand  = new RelayCommand(() => _vm.IsRepeat  = !_vm.IsRepeat);
        _vm.ExitFullscreenCommand = new RelayCommand(() => { if (_vm.IsFullscreen) _vm.IsFullscreen = false; });
    }

    // ── Title bar drag ────────────────────────────────────────────────────────
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) MaximizeButton_Click(sender, e);
        else DragMove();
    }

    // ── Window chrome buttons ─────────────────────────────────────────────────
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) 
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ── Fullscreen ────────────────────────────────────────────────────────────
    private WindowStyle _prevStyle;
    private WindowState _prevState;

    private void ApplyFullscreen(bool full)
    {
        if (full)
        {
            _prevStyle = WindowStyle;
            _prevState = WindowState;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _prevStyle;
            WindowState = _prevState == WindowState.Minimized ? WindowState.Normal : _prevState;
        }
    }

    // ── Seek bar mouse handling ───────────────────────────────────────────────
    private void SeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _vm.SeekStartCommand.Execute(null);

    private void SeekBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        => _vm.SeekEndCommand.Execute(null);

    // ── Drag & Drop ───────────────────────────────────────────────────────────
    private void VideoArea_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) 
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void VideoArea_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            var videoExts = new[] { ".mp4",".mkv",".avi",".mov",".wmv",".flv",".webm",".m4v",".ts",".m2ts",".vob",".ogv",".3gp",".mpg",".mpeg",".rmvb",".f4v",".asf",".divx",".hevc" };
            var videoFiles = files.Where(f => videoExts.Contains(System.IO.Path.GetExtension(f).ToLower()));
            _vm.AddToPlaylist(videoFiles);
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────
    private void Window_Closed(object sender, EventArgs e) => _vm.Dispose();
}
