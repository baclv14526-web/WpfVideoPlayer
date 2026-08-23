using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using WpfVideoPlayer.ViewModels;

namespace WpfVideoPlayer.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    // ── Fullscreen state ──────────────────────────────────────────────────────
    private double _prevLeft, _prevTop, _prevWidth, _prevHeight;
    private WindowState _prevWindowState;
    private ResizeMode _prevResizeMode;

    // ── Mouse idle timer (auto-hide controls in fullscreen) ───────────────────
    private readonly DispatcherTimer _mouseIdleTimer;
    private const double MouseIdleSeconds = 2.5;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Watch fullscreen toggle from ViewModel
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsFullscreen))
                ApplyFullscreen(_vm.IsFullscreen);
        };

        // Commands that need window reference
        _vm.ToggleShuffleCommand  = new RelayCommand(() => _vm.IsShuffle = !_vm.IsShuffle);
        _vm.ToggleRepeatCommand   = new RelayCommand(() => _vm.IsRepeat  = !_vm.IsRepeat);
        _vm.ExitFullscreenCommand = new RelayCommand(() => { if (_vm.IsFullscreen) _vm.IsFullscreen = false; });

        // Mouse idle timer – fires after 2.5 s without mouse movement in fullscreen
        _mouseIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(MouseIdleSeconds) };
        _mouseIdleTimer.Tick += MouseIdleTimer_Tick;
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
    private void ApplyFullscreen(bool full)
    {
        if (full)
        {
            // Save current window geometry
            _prevLeft        = Left;
            _prevTop         = Top;
            _prevWidth       = Width;
            _prevHeight      = Height;
            _prevWindowState = WindowState;
            _prevResizeMode  = ResizeMode;

            // For borderless WPF windows, we must go to Normal first then set exact screen size
            // Otherwise Maximized+None clips to WorkArea (leaving taskbar gap)
            WindowState = WindowState.Normal;
            ResizeMode  = ResizeMode.NoResize;
            Topmost     = true;

            // Cover the entire primary screen (overrides taskbar)
            Left   = 0;
            Top    = 0;
            Width  = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;

            // Start idle timer to auto-hide controls
            RestoreControlsAndCursor();
            _mouseIdleTimer.Start();
        }
        else
        {
            // Stop idle timer and restore controls
            _mouseIdleTimer.Stop();
            RestoreControlsAndCursor();

            // Restore geometry
            ResizeMode  = _prevResizeMode == ResizeMode.NoResize ? ResizeMode.CanResizeWithGrip : _prevResizeMode;
            Topmost     = false;
            Left        = _prevLeft;
            Top         = _prevTop;
            Width       = _prevWidth;
            Height      = _prevHeight;
            WindowState = _prevWindowState == WindowState.Minimized ? WindowState.Normal : _prevWindowState;
        }
    }

    // ── Mouse idle: hide controls + cursor after 2.5s idle in fullscreen ──────
    private void MouseIdleTimer_Tick(object? sender, EventArgs e)
    {
        _mouseIdleTimer.Stop();
        if (_vm.IsFullscreen)
        {
            ControlsBar.Visibility = Visibility.Collapsed;
            Mouse.OverrideCursor   = Cursors.None;
        }
    }

    private void RestoreControlsAndCursor()
    {
        ControlsBar.Visibility = Visibility.Visible;
        Mouse.OverrideCursor   = null;
    }

    // ── Window_MouseMove: reset idle timer when mouse moves ──────────────────
    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_vm.IsFullscreen) return;

        RestoreControlsAndCursor();
        _mouseIdleTimer.Stop();
        _mouseIdleTimer.Start();
    }

    // ── VideoArea double-click: toggle fullscreen ─────────────────────────────
    private void VideoArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            _vm.IsFullscreen = !_vm.IsFullscreen;
            e.Handled = true;
        }
    }

    // ── Seek bar mouse handling ───────────────────────────────────────────────
    private void SeekBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        => _vm.SeekStartCommand.Execute(null);

    private void SeekBar_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        => _vm.SeekEndCommand.Execute(null);

    private void SeekBar_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.ActualWidth > 0 && _vm.HasMedia)
        {
            var pos = e.GetPosition(fe);
            double norm = Math.Clamp(pos.X / fe.ActualWidth, 0.0, 1.0);
            _vm.UpdateTimelineHover(norm, fe.ActualWidth);

            if (TimelinePreviewPopup != null)
            {
                TimelinePreviewPopup.HorizontalOffset = _vm.TimelineHoverLeft;
            }
        }
    }

    private void SeekBar_MouseLeave(object sender, MouseEventArgs e)
    {
        _vm.HideTimelineHover();
    }

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
            var videoExts = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm",
                                    ".m4v", ".ts", ".m2ts", ".vob", ".ogv", ".3gp", ".mpg",
                                    ".mpeg", ".rmvb", ".f4v", ".asf", ".divx", ".hevc" };
            var videoFiles = files.Where(f => videoExts.Contains(
                System.IO.Path.GetExtension(f).ToLower()));
            _vm.AddToPlaylist(videoFiles);
        }
    }

    // ── Speed / Zoom / Rotate menu popups ─────────────────────────────────────
    private void SpeedButton_Click(object sender, RoutedEventArgs e)
        => OpenContextMenuTop(sender);

    private void ZoomButton_Click(object sender, RoutedEventArgs e)
        => OpenContextMenuTop(sender);

    private void RotateButton_Click(object sender, RoutedEventArgs e)
        => OpenContextMenuTop(sender);

    private static void OpenContextMenuTop(object sender)
    {
        if (sender is FrameworkElement fe && fe.ContextMenu != null)
        {
            fe.ContextMenu.PlacementTarget = fe;
            fe.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Top;
            fe.ContextMenu.IsOpen = true;
        }
    }

    // ── Cleanup ───────────────────────────────────────────────────────────────
    private void Window_Closed(object sender, EventArgs e)
    {
        _mouseIdleTimer.Stop();
        VideoView.MediaPlayer = null;
        _vm.Dispose();
    }
}
