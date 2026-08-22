using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using WpfVideoPlayer.Models;
using WpfVideoPlayer.Services;

namespace WpfVideoPlayer.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    // ── LibVLC core ──────────────────────────────────────────────────────────
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;

    // ── Motion detection service ─────────────────────────────────────────────
    private readonly MotionDetectionService _motionService = new();
    private CancellationTokenSource? _scanCts;

    // ── UI timer ─────────────────────────────────────────────────────────────
    private readonly DispatcherTimer _uiTimer;

    // ── Backing fields ────────────────────────────────────────────────────────
    private bool _isPlaying;
    private bool _isMuted;
    private bool _isLoading;
    private bool _hasMedia;
    private bool _isSliderBeingDragged;
    private bool _isFullscreen;
    private bool _isPlaylistVisible = true;
    private bool _isRepeat;
    private bool _isShuffle;
    private double _playbackSpeed = 1.0;
    private double _volume = 80;
    private double _position;
    private double _duration;
    private string _currentTimeText = "0:00:00";
    private string _totalTimeText = "0:00:00";
    private string _statusText = "Chào mừng đến với VPlayer";
    private string _currentTitle = "Chưa có video";
    private string _videoInfo = "";
    private string _currentFilePath = "";
    private int _selectedPlaylistIndex = -1;

    // ── Motion Detection fields ───────────────────────────────────────────────
    private bool _isScanning;
    private double _scanProgress;
    private string _scanStatusText = "Chưa quét chuyển động";
    private bool _autoScanOnOpen = true;
    private int _activeSidebarTabIndex = 0; // 0 = Playlist, 1 = Motion Bookmarks

    public static readonly double[] AvailableSpeeds = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 6.0, 8.0, 10.0 };

    public ObservableCollection<PlaylistItem> Playlist { get; } = new();
    public ObservableCollection<MotionBookmark> Bookmarks { get; } = new();

    // ── Public properties ─────────────────────────────────────────────────────
    public MediaPlayer? MediaPlayer => _mediaPlayer;
    public LibVLC? LibVLC => _libVLC;

    public bool IsPlaying { get => _isPlaying; set => Set(ref _isPlaying, value); }
    public bool IsMuted { get => _isMuted; set { Set(ref _isMuted, value); if (_mediaPlayer != null) _mediaPlayer.Mute = value; } }
    public bool IsLoading { get => _isLoading; set => Set(ref _isLoading, value); }
    public bool HasMedia { get => _hasMedia; set => Set(ref _hasMedia, value); }
    public bool IsFullscreen { get => _isFullscreen; set => Set(ref _isFullscreen, value); }
    public bool IsPlaylistVisible { get => _isPlaylistVisible; set => Set(ref _isPlaylistVisible, value); }
    public bool IsRepeat { get => _isRepeat; set => Set(ref _isRepeat, value); }
    public bool IsShuffle { get => _isShuffle; set => Set(ref _isShuffle, value); }

    public bool IsScanning { get => _isScanning; set => Set(ref _isScanning, value); }
    public double ScanProgress { get => _scanProgress; set => Set(ref _scanProgress, value); }
    public string ScanStatusText { get => _scanStatusText; set => Set(ref _scanStatusText, value); }
    public bool AutoScanOnOpen { get => _autoScanOnOpen; set => Set(ref _autoScanOnOpen, value); }
    public int ActiveSidebarTabIndex { get => _activeSidebarTabIndex; set => Set(ref _activeSidebarTabIndex, value); }
    public string CurrentFilePath { get => _currentFilePath; set => Set(ref _currentFilePath, value); }

    public double PlaybackSpeed
    {
        get => _playbackSpeed;
        set
        {
            if (Set(ref _playbackSpeed, value))
            {
                if (_mediaPlayer != null)
                    _mediaPlayer.SetRate((float)value);
                OnPropertyChanged(nameof(PlaybackSpeedText));
                StatusText = $"Tốc độ: {PlaybackSpeedText}";
            }
        }
    }

    public string PlaybackSpeedText => $"{_playbackSpeed:0.##}x";

    public double Volume
    {
        get => _volume;
        set
        {
            Set(ref _volume, value);
            if (_mediaPlayer != null)
                _mediaPlayer.Volume = (int)value;
        }
    }

    public double Position
    {
        get => _position;
        set
        {
            Set(ref _position, value);
            if (_isSliderBeingDragged && _mediaPlayer != null && _duration > 0)
                _mediaPlayer.Time = (long)(value * _duration * 1000);
        }
    }

    public double Duration { get => _duration; set => Set(ref _duration, value); }
    public string CurrentTimeText { get => _currentTimeText; set => Set(ref _currentTimeText, value); }
    public string TotalTimeText { get => _totalTimeText; set => Set(ref _totalTimeText, value); }
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }
    public string CurrentTitle { get => _currentTitle; set => Set(ref _currentTitle, value); }
    public string VideoInfo { get => _videoInfo; set => Set(ref _videoInfo, value); }

    public int SelectedPlaylistIndex
    {
        get => _selectedPlaylistIndex;
        set
        {
            Set(ref _selectedPlaylistIndex, value);
            if (value >= 0 && value < Playlist.Count)
                PlayFile(Playlist[value].FilePath);
        }
    }

    // ── Commands ──────────────────────────────────────────────────────────────
    public ICommand PlayPauseCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenFileCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand ToggleMuteCommand { get; }
    public ICommand ToggleFullscreenCommand { get; }
    public ICommand TogglePlaylistCommand { get; }
    public ICommand SeekStartCommand { get; }
    public ICommand SeekEndCommand { get; }
    public ICommand SeekBackCommand { get; }
    public ICommand SeekForwardCommand { get; }
    public ICommand SeekBack30SecCommand { get; }
    public ICommand SeekForward1MinCommand { get; }
    public ICommand SetSpeedCommand { get; }
    public ICommand SpeedUpCommand { get; }
    public ICommand SpeedDownCommand { get; }
    public ICommand ScanMotionCommand { get; }
    public ICommand CancelScanCommand { get; }
    public ICommand JumpToBookmarkCommand { get; }
    public ICommand ClearBookmarksCommand { get; }
    public ICommand SwitchSidebarTabCommand { get; }
    public ICommand RemoveFromPlaylistCommand { get; }
    public ICommand ClearPlaylistCommand { get; }
    public ICommand VolumeUpCommand { get; }
    public ICommand VolumeDownCommand { get; }
    public ICommand ToggleShuffleCommand { get; set; } = new RelayCommand(() => { });
    public ICommand ToggleRepeatCommand  { get; set; } = new RelayCommand(() => { });
    public ICommand ExitFullscreenCommand { get; set; } = new RelayCommand(() => { });

    // ── Constructor ───────────────────────────────────────────────────────────
    public MainViewModel()
    {
        PlayPauseCommand     = new RelayCommand(PlayPause, () => HasMedia || Playlist.Count > 0);
        StopCommand          = new RelayCommand(Stop, () => HasMedia);
        OpenFileCommand      = new RelayCommand(OpenFile);
        OpenFolderCommand    = new RelayCommand(OpenFolder);
        PreviousCommand      = new RelayCommand(PlayPrevious, () => Playlist.Count > 1);
        NextCommand          = new RelayCommand(PlayNext, () => Playlist.Count > 1);
        ToggleMuteCommand    = new RelayCommand(() => IsMuted = !IsMuted);
        ToggleFullscreenCommand = new RelayCommand(() => IsFullscreen = !IsFullscreen);
        TogglePlaylistCommand   = new RelayCommand(() => IsPlaylistVisible = !IsPlaylistVisible);
        SeekStartCommand     = new RelayCommand(() => _isSliderBeingDragged = true);
        SeekEndCommand       = new RelayCommand(SeekEnd);
        SeekBackCommand      = new RelayCommand(() => SeekRelative(-5000));
        SeekForwardCommand   = new RelayCommand(() => SeekRelative(5000));
        SeekBack30SecCommand = new RelayCommand(() => SeekRelative(-30000));
        SeekForward1MinCommand = new RelayCommand(() => SeekRelative(60000));
        SetSpeedCommand      = new RelayCommand<object>(SetSpeed);
        SpeedUpCommand       = new RelayCommand(SpeedUp);
        SpeedDownCommand     = new RelayCommand(SpeedDown);
        ScanMotionCommand    = new RelayCommand(() => _ = StartMotionScan(), () => HasMedia && !IsScanning);
        CancelScanCommand    = new RelayCommand(CancelMotionScan, () => IsScanning);
        JumpToBookmarkCommand = new RelayCommand<MotionBookmark>(JumpToBookmark);
        ClearBookmarksCommand = new RelayCommand(ClearBookmarks);
        SwitchSidebarTabCommand = new RelayCommand<object>(param =>
        {
            if (param != null && int.TryParse(param.ToString(), out int tab))
                ActiveSidebarTabIndex = tab;
        });
        RemoveFromPlaylistCommand = new RelayCommand<PlaylistItem>(RemoveFromPlaylist);
        ClearPlaylistCommand = new RelayCommand(ClearPlaylist, () => Playlist.Count > 0);
        VolumeUpCommand      = new RelayCommand(() => Volume = Math.Min(200, Volume + 5));
        VolumeDownCommand    = new RelayCommand(() => Volume = Math.Max(0, Volume - 5));

        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _uiTimer.Tick += OnUiTimer;

        InitLibVLC();
    }

    // ── LibVLC init ───────────────────────────────────────────────────────────
    private void InitLibVLC()
    {
        try
        {
            Core.Initialize();
            _libVLC = new LibVLC(enableDebugLogs: false,
                "--no-video-title-show",
                "--no-osd",
                "--verbose=0");
            _mediaPlayer = new MediaPlayer(_libVLC);
            _mediaPlayer.Volume = (int)_volume;

            _mediaPlayer.Playing  += (_, _) => Application.Current?.Dispatcher.InvokeAsync(OnPlaying);
            _mediaPlayer.Paused   += (_, _) => Application.Current?.Dispatcher.InvokeAsync(OnPaused);
            _mediaPlayer.Stopped  += (_, _) => Application.Current?.Dispatcher.InvokeAsync(OnStopped);
            _mediaPlayer.EndReached += (_, _) => ThreadPool.QueueUserWorkItem(_ => HandleEndReached());
            _mediaPlayer.EncounteredError += (_, _) => Application.Current?.Dispatcher.InvokeAsync(OnError);

            OnPropertyChanged(nameof(MediaPlayer));
            OnPropertyChanged(nameof(LibVLC));
        }
        catch (Exception ex)
        {
            StatusText = $"Lỗi khởi tạo LibVLC: {ex.Message}";
        }
    }

    // ── Playback ──────────────────────────────────────────────────────────────
    public void PlayFile(string path)
    {
        if (_libVLC == null || _mediaPlayer == null) return;
        if (!File.Exists(path)) { StatusText = "File không tồn tại"; return; }

        IsLoading = true;
        StatusText = "Đang tải...";
        CurrentFilePath = path;

        var media = new Media(_libVLC, path, FromType.FromPath);
        _mediaPlayer.Media = media;
        _mediaPlayer.Play();

        HasMedia = true;
        CurrentTitle = Path.GetFileNameWithoutExtension(path);
        _uiTimer.Start();

        // Mark current item in playlist
        for (int i = 0; i < Playlist.Count; i++)
            Playlist[i].IsCurrentlyPlaying = Playlist[i].FilePath == path;

        // Auto scan motion bookmarks if enabled
        if (AutoScanOnOpen)
        {
            _ = StartMotionScan(path);
        }
    }

    private void PlayPause()
    {
        if (_mediaPlayer == null) return;
        if (!HasMedia && Playlist.Count > 0) { PlayFile(Playlist[0].FilePath); return; }

        if (_mediaPlayer.IsPlaying) _mediaPlayer.Pause();
        else _mediaPlayer.Play();
    }

    private void Stop()
    {
        _mediaPlayer?.Stop();
        _uiTimer.Stop();
        Position = 0;
        CurrentTimeText = "0:00:00";
    }

    private void PlayNext()
    {
        if (Playlist.Count == 0) return;
        int idx = GetCurrentPlaylistIndex();
        if (IsShuffle) idx = new Random().Next(Playlist.Count);
        else idx = (idx + 1) % Playlist.Count;
        _selectedPlaylistIndex = idx;
        OnPropertyChanged(nameof(SelectedPlaylistIndex));
        PlayFile(Playlist[idx].FilePath);
    }

    private void PlayPrevious()
    {
        if (Playlist.Count == 0) return;
        int idx = GetCurrentPlaylistIndex();
        idx = (idx - 1 + Playlist.Count) % Playlist.Count;
        _selectedPlaylistIndex = idx;
        OnPropertyChanged(nameof(SelectedPlaylistIndex));
        PlayFile(Playlist[idx].FilePath);
    }

    private int GetCurrentPlaylistIndex()
    {
        if (_selectedPlaylistIndex >= 0) return _selectedPlaylistIndex;
        return 0;
    }

    // ── File/Folder open ──────────────────────────────────────────────────────
    private void OpenFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Mở video",
            Filter = "Video files|*.mp4;*.mkv;*.avi;*.mov;*.wmv;*.flv;*.webm;*.m4v;*.ts;*.m2ts;*.vob;*.ogv;*.3gp;*.3g2;*.rmvb;*.rm;*.divx;*.xvid;*.hevc;*.h264;*.h265;*.mpg;*.mpeg;*.mpe;*.mpv;*.mp2;*.m2v;*.asf;*.f4v;*.mxf|Tất cả file|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() == true)
            AddToPlaylist(dlg.FileNames);
    }

    private void OpenFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Chọn thư mục chứa video"
        };
        if (dlg.ShowDialog() == true)
        {
            var exts = new[] { ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".webm", ".m4v", ".ts", ".m2ts", ".vob", ".ogv", ".3gp", ".mpg", ".mpeg", ".rmvb", ".f4v", ".asf" };
            var files = Directory.GetFiles(dlg.FolderName, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => exts.Contains(Path.GetExtension(f).ToLower()))
                .OrderBy(f => f)
                .ToArray();
            AddToPlaylist(files);
        }
    }

    public void AddToPlaylist(IEnumerable<string> paths)
    {
        bool firstNew = Playlist.Count == 0;
        foreach (var p in paths)
        {
            if (Playlist.Any(x => x.FilePath == p)) continue;
            Playlist.Add(new PlaylistItem { FilePath = p });
        }
        if (firstNew && Playlist.Count > 0)
        {
            _selectedPlaylistIndex = 0;
            OnPropertyChanged(nameof(SelectedPlaylistIndex));
            PlayFile(Playlist[0].FilePath);
        }
    }

    private void RemoveFromPlaylist(PlaylistItem? item)
    {
        if (item == null) return;
        Playlist.Remove(item);
    }

    private void ClearPlaylist()
    {
        Stop();
        Playlist.Clear();
        HasMedia = false;
        CurrentTitle = "Chưa có video";
        StatusText = "Danh sách đã được xóa";
    }

    // ── Seek ──────────────────────────────────────────────────────────────────
    private void SeekEnd()
    {
        _isSliderBeingDragged = false;
        if (_mediaPlayer != null && _duration > 0)
            _mediaPlayer.Time = (long)(_position * _duration * 1000);
    }

    // ── UI Timer ──────────────────────────────────────────────────────────────
    private void OnUiTimer(object? sender, EventArgs e)
    {
        if (_mediaPlayer == null || _isSliderBeingDragged) return;
        var totalMs = _mediaPlayer.Length;
        var currentMs = _mediaPlayer.Time;
        if (totalMs > 0)
        {
            Duration = totalMs / 1000.0;
            Position = currentMs / (double)totalMs;
            CurrentTimeText = FormatTime(currentMs / 1000);
            TotalTimeText = FormatTime(totalMs / 1000);
        }
    }

    // ── Events ────────────────────────────────────────────────────────────────
    private void OnPlaying()
    {
        IsPlaying = true;
        IsLoading = false;
        StatusText = "Đang phát";
        VideoInfo = GetVideoInfo();
        if (_playbackSpeed != 1.0 && _mediaPlayer != null)
            _mediaPlayer.SetRate((float)_playbackSpeed);
    }

    private void OnPaused()
    {
        IsPlaying = false;
        StatusText = "Tạm dừng";
    }

    private void OnStopped()
    {
        IsPlaying = false;
        IsLoading = false;
        StatusText = "Đã dừng";
    }

    private void HandleEndReached()
    {
        // LibVLC triggers EndReached on its own internal worker thread.
        // Calling Stop() or Play() directly from that callback causes a deadlock.
        // We must stop the media player on a background thread first.
        _mediaPlayer?.Stop();

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            _uiTimer.Stop();
            Position = 0;
            CurrentTimeText = "0:00:00";

            if (IsRepeat)
            {
                var idx = GetCurrentPlaylistIndex();
                if (idx >= 0 && idx < Playlist.Count)
                {
                    PlayFile(Playlist[idx].FilePath);
                    return;
                }
            }

            if (Playlist.Count > 1)
            {
                PlayNext();
            }
            else
            {
                IsPlaying = false;
                StatusText = "Phát xong";
            }
        });
    }

    private void OnError()
    {
        IsLoading = false;
        IsPlaying = false;
        StatusText = "Lỗi phát video – định dạng không được hỗ trợ hoặc file bị hỏng";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void SetSpeed(object? param)
    {
        if (param == null) return;
        if (param is double d)
        {
            PlaybackSpeed = d;
        }
        else if (double.TryParse(param.ToString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double s))
        {
            PlaybackSpeed = s;
        }
    }

    private void SpeedUp()
    {
        int idx = Array.IndexOf(AvailableSpeeds, _playbackSpeed);
        if (idx >= 0 && idx < AvailableSpeeds.Length - 1)
            PlaybackSpeed = AvailableSpeeds[idx + 1];
        else if (idx == -1)
        {
            var next = AvailableSpeeds.FirstOrDefault(s => s > _playbackSpeed);
            if (next > 0) PlaybackSpeed = next;
        }
    }

    private void SpeedDown()
    {
        int idx = Array.IndexOf(AvailableSpeeds, _playbackSpeed);
        if (idx > 0)
            PlaybackSpeed = AvailableSpeeds[idx - 1];
        else if (idx == -1)
        {
            var prev = AvailableSpeeds.LastOrDefault(s => s < _playbackSpeed);
            if (prev > 0) PlaybackSpeed = prev;
        }
    }

    private void SeekRelative(long msDelta)
    {
        if (_mediaPlayer == null || !HasMedia || _duration <= 0) return;
        var newTime = Math.Clamp(_mediaPlayer.Time + msDelta, 0, (long)(_duration * 1000));
        _mediaPlayer.Time = newTime;

        long newSec = newTime / 1000;
        string sign = msDelta > 0 ? "+" : "-";
        long absSec = Math.Abs(msDelta) / 1000;
        string deltaStr = absSec >= 60 ? $"{absSec / 60}p{absSec % 60:D2}" : $"{absSec}s";
        StatusText = $"{sign}{deltaStr} ({FormatTime(newSec)} / {FormatTime((long)_duration)})";
    }

    private string GetVideoInfo()
    {
        var tracks = _mediaPlayer?.Media?.Tracks;
        if (tracks == null) return "";
        var vt = tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
        if (vt.TrackType == TrackType.Video)
        {
            var v = vt.Data.Video;
            if (v.Width > 0 && v.Height > 0)
                return $"{v.Width}×{v.Height}";
        }
        return "";
    }

    private static string FormatTime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    // ── Motion Detection ──────────────────────────────────────────────────────
    public async Task StartMotionScan(string? path = null)
    {
        string target = path ?? _currentFilePath;
        if (string.IsNullOrEmpty(target) || !File.Exists(target))
        {
            ScanStatusText = "Chưa có video để quét";
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        IsScanning = true;
        ScanProgress = 0;
        ScanStatusText = "Đang phân tích khung hình chuyển động...";
        Bookmarks.Clear();

        try
        {
            var progress = new Progress<double>(p =>
            {
                ScanProgress = p;
                ScanStatusText = $"Đang quét chuyển động: {p:0.#}%";
            });

            var results = await _motionService.ScanVideoAsync(target, progress, token);

            Bookmarks.Clear();
            foreach (var bm in results)
            {
                Bookmarks.Add(bm);
            }

            ScanStatusText = Bookmarks.Count > 0
                ? $"Đã phát hiện {Bookmarks.Count} mốc chuyển động"
                : "Không phát hiện chuyển động đáng kể";
            StatusText = $"Phát hiện {Bookmarks.Count} cảnh chuyển động";
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = "Đã hủy quét chuyển động";
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Lỗi khi quét: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    public void CancelMotionScan()
    {
        _scanCts?.Cancel();
        IsScanning = false;
        ScanStatusText = "Đã hủy quét";
    }

    public void JumpToBookmark(MotionBookmark? bookmark)
    {
        if (bookmark == null || _mediaPlayer == null) return;
        _mediaPlayer.Time = (long)(bookmark.TimeSeconds * 1000);
        StatusText = $"Nhảy tới {bookmark.TimeText} - {bookmark.Title}";
    }

    public void ClearBookmarks()
    {
        _scanCts?.Cancel();
        Bookmarks.Clear();
        ScanStatusText = "Đã xóa danh sách bookmark";
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value; OnPropertyChanged(name); return true;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────
    public void Dispose()
    {
        _scanCts?.Cancel();
        _uiTimer.Stop();
        if (_mediaPlayer != null)
        {
            if (_mediaPlayer.IsPlaying)
                _mediaPlayer.Stop();
            _mediaPlayer.Dispose();
        }
        _libVLC?.Dispose();
    }
}

// ── Relay commands ────────────────────────────────────────────────────────────
public class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? p) => _canExecute?.Invoke() ?? true;
    public void Execute(object? p) => _execute();
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}

public class RelayCommand<T> : ICommand
{
    private readonly Action<T?> _execute;
    private readonly Func<T?, bool>? _canExecute;
    public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
    { _execute = execute; _canExecute = canExecute; }
    public bool CanExecute(object? p) => _canExecute?.Invoke((T?)p) ?? true;
    public void Execute(object? p) => _execute((T?)p);
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
}
