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

    // ── Motion detection & Cache services ────────────────────────────────────
    private readonly MotionDetectionService _motionService = new();
    private readonly BookmarkCacheService _cacheService = new();
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
    private bool _isLxMode;
    private bool _isPsMode;
    // ── Ps mode state ─────────────────────────────────────────────────────────
    private int  _psPhase = -1;           // -1=off, 0=intro, 1..N=step
    private long _psIntroStartMs;         // media time (ms) when intro began
    private long _psPhaseTargetEndMs;     // media time (ms) when current phase ends
    private int  _psCurrentStep;          // index into _psStepPositions
    private readonly List<long> _psStepPositions = new();
    private bool _isPbMode;
    // ── Pb mode state (Play Balanced – evenly-spaced steps) ───────────────────
    private int  _pbPhase = -1;           // -1=off, 0=intro, 1..N=step
    private long _pbPhaseTargetEndMs;     // media time (ms) when current phase ends
    private int  _pbCurrentStep;          // index into _pbStepPositions
    private readonly List<long> _pbStepPositions = new();
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

    // ── Motion Detection & Cache fields ───────────────────────────────────────
    private bool _isScanning;
    private double _scanProgress;
    private string _scanStatusText = "Chưa quét video (Nhấn 'Quét cảnh ≥2 người' để bắt đầu)";
    private bool _autoScanOnOpen = false;
    private int _activeSidebarTabIndex = 0; // 0 = Playlist, 1 = Motion Bookmarks
    private string? _scanningVideoPath; // Tracks the video currently being processed in the background

    public static readonly double[] AvailableSpeeds = { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 2.5, 3.0, 4.0, 6.0, 8.0, 10.0, 16.0, 32.0, 64.0 };

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

    public bool IsLxMode
    {
        get => _isLxMode;
        set
        {
            if (Set(ref _isLxMode, value))
            {
                if (value)
                {
                    // Bật Lx → tắt Ps/Pb nếu đang chạy
                    if (_isPsMode) StopPsMode(silent: true);
                    if (_isPbMode) StopPbMode(silent: true);
                }
                else
                {
                    // Khi tắt Lx, reset về 1x
                    _playbackSpeed = 1.0;
                    _mediaPlayer?.SetRate(1.0f);
                    OnPropertyChanged(nameof(PlaybackSpeedText));
                }
                StatusText = value ? "Chế độ Lx: tăng tốc tuyến tính 1x→4x" : "Tốc độ: 1x";
            }
        }
    }

    public bool IsPsMode
    {
        get => _isPsMode;
        set
        {
            if (Set(ref _isPsMode, value))
            {
                if (value) BeginPsMode();
                else       StopPsMode();
            }
        }
    }

    public bool IsPbMode
    {
        get => _isPbMode;
        set
        {
            if (Set(ref _isPbMode, value))
            {
                if (value) BeginPbMode();
                else       StopPbMode();
            }
        }
    }

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

    // ── Timeline Hover Preview properties ─────────────────────────────────────
    private readonly VideoPreviewService _previewService = new();
    private CancellationTokenSource? _hoverPreviewCts;
    private bool _isTimelineHoverPreviewVisible;
    private double _timelineHoverLeft;
    private string _timelineHoverTimeText = "00:00";
    private BitmapSource? _timelineHoverPreviewImage;
    private MotionBookmark? _timelineHoverBookmark;

    public bool IsTimelineHoverPreviewVisible { get => _isTimelineHoverPreviewVisible; set => Set(ref _isTimelineHoverPreviewVisible, value); }
    public double TimelineHoverLeft { get => _timelineHoverLeft; set => Set(ref _timelineHoverLeft, value); }
    public string TimelineHoverTimeText { get => _timelineHoverTimeText; set => Set(ref _timelineHoverTimeText, value); }
    public BitmapSource? TimelineHoverPreviewImage { get => _timelineHoverPreviewImage; set => Set(ref _timelineHoverPreviewImage, value); }
    public MotionBookmark? TimelineHoverBookmark { get => _timelineHoverBookmark; set => Set(ref _timelineHoverBookmark, value); }

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
    public ICommand ToggleLxModeCommand { get; }
    public ICommand TogglePsModeCommand { get; }
    public ICommand TogglePbModeCommand { get; }
    public ICommand ScanMotionCommand { get; }
    public ICommand GenerateRandomBookmarksCommand { get; }
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
        ToggleLxModeCommand  = new RelayCommand(() => IsLxMode = !IsLxMode);
        TogglePsModeCommand  = new RelayCommand(() => IsPsMode = !IsPsMode, () => HasMedia);
        TogglePbModeCommand  = new RelayCommand(() => IsPbMode = !IsPbMode, () => HasMedia);
        ScanMotionCommand    = new RelayCommand(() => _ = StartMotionScan(), () => HasMedia && !IsScanning);
        GenerateRandomBookmarksCommand = new RelayCommand(() => _ = GenerateRandomBookmarksAsync(), () => HasMedia && !IsScanning);
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

        // Check if cached bookmarks exist for this video
        var cached = _cacheService.LoadBookmarks(path);
        if (cached != null && cached.Count > 0)
        {
            Bookmarks.Clear();
            foreach (var bm in cached)
            {
                Bookmarks.Add(bm);
            }
            if (_isScanning && _scanningVideoPath != null && !string.Equals(_scanningVideoPath, path, StringComparison.OrdinalIgnoreCase))
            {
                ScanStatusText = $"Đã nạp {Bookmarks.Count} cảnh (Cache) | Đang quét ngầm: {Path.GetFileName(_scanningVideoPath)}";
            }
            else
            {
                ScanStatusText = $"Đã nạp {Bookmarks.Count} cảnh từ bộ nhớ đệm (Cache)";
            }
            StatusText = $"Đã nạp {Bookmarks.Count} cảnh (Cache)";
        }
        else
        {
            Bookmarks.Clear();
            if (_isScanning && _scanningVideoPath != null && !string.Equals(_scanningVideoPath, path, StringComparison.OrdinalIgnoreCase))
            {
                // Video mới chưa quét, vẫn để chế độ quét tay, không hủy tiến trình quét video cũ
                ScanStatusText = $"Chưa quét video này (Đang quét ngầm: {Path.GetFileName(_scanningVideoPath)})";
            }
            else if (AutoScanOnOpen)
            {
                _ = StartMotionScan(path);
            }
            else
            {
                ScanStatusText = "Chưa quét video (Nhấn 'Quét cảnh ≥2 người' để bắt đầu)";
            }
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
        if (_isLxMode) IsLxMode = false;
        if (_isPsMode) StopPsMode(silent: true);
        if (_isPbMode) StopPbMode(silent: true);
        _mediaPlayer?.Stop();
        _uiTimer.Stop();
        Position = 0;
        CurrentTimeText = "0:00:00";
    }

    private void PlayNext()
    {
        if (Playlist.Count <= 1) return;
        int nextIndex = IsShuffle 
            ? new Random().Next(0, Playlist.Count)
            : (SelectedPlaylistIndex + 1) % Playlist.Count;
        SelectedPlaylistIndex = nextIndex;
        PlayFile(Playlist[nextIndex].FilePath);
    }

    private void PlayPrevious()
    {
        if (Playlist.Count <= 1) return;
        int prevIndex = SelectedPlaylistIndex <= 0 ? Playlist.Count - 1 : SelectedPlaylistIndex - 1;
        SelectedPlaylistIndex = prevIndex;
        PlayFile(Playlist[prevIndex].FilePath);
    }

    private int GetCurrentPlaylistIndex()
    {
        for (int i = 0; i < Playlist.Count; i++)
            if (Playlist[i].FilePath == _currentFilePath) return i;
        return -1;
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
            var pos = currentMs / (double)totalMs;
            Position = pos;
            CurrentTimeText = FormatTime(currentMs / 1000);
            TotalTimeText = FormatTime(totalMs / 1000);

            // ── Lx mode: tuyến tính 1x→4x ────────────────────────────────────
            if (_isLxMode && _mediaPlayer.IsPlaying)
            {
                double lxSpeed = 1.0 + 3.0 * pos;   // 1x tại pos=0, 4x tại pos=1
                _mediaPlayer.SetRate((float)lxSpeed);
                _playbackSpeed = lxSpeed;
                OnPropertyChanged(nameof(PlaybackSpeedText));
            }

            // ── Ps mode: Play Step state machine ─────────────────────────────
            if (_isPsMode && _mediaPlayer.IsPlaying)
            {
                if (_psPhase == 0) // intro 30s
                {
                    if (currentMs >= _psPhaseTargetEndMs)
                    {
                        // Intro done → generate random steps & jump to first
                        GeneratePsSteps();
                        _psCurrentStep = 0;
                        AdvancePsStep();
                    }
                }
                else if (_psPhase > 0) // a step
                {
                    if (currentMs >= _psPhaseTargetEndMs)
                    {
                        _psCurrentStep++;
                        AdvancePsStep();
                    }
                }
            }

            // ── Pb mode: Play Balanced state machine ──────────────────────────
            if (_isPbMode && _mediaPlayer.IsPlaying)
            {
                if (_pbPhase == 0) // intro 30s
                {
                    if (currentMs >= _pbPhaseTargetEndMs)
                    {
                        // Intro done → generate even steps & jump to first
                        GeneratePbSteps();
                        _pbCurrentStep = 0;
                        AdvancePbStep();
                    }
                }
                else if (_pbPhase > 0) // a step
                {
                    if (currentMs >= _pbPhaseTargetEndMs)
                    {
                        _pbCurrentStep++;
                        AdvancePbStep();
                    }
                }
            }
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

            // Tắt Lx mode khi video kết thúc
            if (_isLxMode)
            {
                _isLxMode = false;
                OnPropertyChanged(nameof(IsLxMode));
                _playbackSpeed = 1.0;
                OnPropertyChanged(nameof(PlaybackSpeedText));
            }

            // Tắt Ps mode khi video kết thúc
            if (_isPsMode) StopPsMode(silent: true);

            // Tắt Pb mode khi video kết thúc
            if (_isPbMode) StopPbMode(silent: true);

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
        // Chọn tốc độ thủ công → tắt Lx/Ps/Pb mode
        if (_isLxMode) { _isLxMode = false; OnPropertyChanged(nameof(IsLxMode)); }
        if (_isPsMode) StopPsMode(silent: true);
        if (_isPbMode) StopPbMode(silent: true);
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

    // ── Ps (Play Step) Mode ───────────────────────────────────────────────────
    private void BeginPsMode()
    {
        if (_mediaPlayer == null || _duration <= 0) { _isPsMode = false; OnPropertyChanged(nameof(IsPsMode)); return; }
        // Tắt Lx/Pb nếu đang bật
        if (_isLxMode) { _isLxMode = false; OnPropertyChanged(nameof(IsLxMode)); }
        if (_isPbMode) StopPbMode(silent: true);

        _psPhase          = 0;
        _psIntroStartMs   = _mediaPlayer.Time;
        _psPhaseTargetEndMs = _psIntroStartMs + 30_000L;
        _psCurrentStep    = 0;
        _psStepPositions.Clear();

        _mediaPlayer.SetRate(2.0f);
        _playbackSpeed = 2.0;
        OnPropertyChanged(nameof(PlaybackSpeedText));
        StatusText = "Ps: Intro 30s @2x...";
    }

    /// <summary>Tắt Ps mode, reset tốc độ về 1x.</summary>
    /// <param name="silent">Không dùng — giữ để tương thích các call site nội bộ.</param>
    private void StopPsMode(bool silent = false)
    {
        _psPhase = -1;
        _psStepPositions.Clear();
        _isPsMode = false;
        OnPropertyChanged(nameof(IsPsMode));
        _mediaPlayer?.SetRate(1.0f);
        _playbackSpeed = 1.0;
        OnPropertyChanged(nameof(PlaybackSpeedText));
        StatusText = "Ps: Kết thúc";
    }

    private void GeneratePsSteps()
    {
        _psStepPositions.Clear();
        if (_duration <= 0) return;

        var  rnd        = new Random();
        int  count      = rnd.Next(3, 6);      // 3 đến 5 điểm
        long durMs      = (long)(_duration * 1000);
        const long kGap = 30_000L;             // khoảng cách tối thiểu 30s

        // Vùng hợp lệ: sau intro và đủ 30s để phát tại điểm cuối
        long rangeStart = _psIntroStartMs + kGap;
        long rangeEnd   = durMs - kGap;        // cần ít nhất 30s phát tại mỗi điểm

        if (rangeEnd <= rangeStart)
        {
            StopPsMode();
            return;
        }

        // Giảm count nếu không đủ khoảng cách
        while (count > 1 && (rangeEnd - rangeStart) < (long)(count - 1) * kGap)
            count--;
        count = Math.Max(1, count);

        // Sinh tuần tự: mỗi điểm = điểm trước + kGap + random extra
        long available = rangeEnd - rangeStart - (long)(count - 1) * kGap;
        long cur = rangeStart + (available > 0 ? (long)(rnd.NextDouble() * available) : 0);
        _psStepPositions.Add(cur);

        for (int i = 1; i < count; i++)
        {
            long remaining = count - 1 - i;
            long minNext   = cur + kGap;
            long maxNext   = rangeEnd - remaining * kGap;
            if (maxNext <= minNext) maxNext = minNext;
            long extra = maxNext > minNext ? (long)(rnd.NextDouble() * (maxNext - minNext)) : 0;
            cur = minNext + extra;
            _psStepPositions.Add(cur);
        }
    }

    private void AdvancePsStep()
    {
        if (_psCurrentStep >= _psStepPositions.Count)
        {
            // Hết tất cả bước → kết thúc Ps
            StopPsMode();
            return;
        }

        long pos = _psStepPositions[_psCurrentStep];
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Time = pos;
            _psPhaseTargetEndMs = pos + 30_000L;
            _psPhase = _psCurrentStep + 1;
            _mediaPlayer.SetRate(2.0f);
            _playbackSpeed = 2.0;
            OnPropertyChanged(nameof(PlaybackSpeedText));
            StatusText = $"Ps: Bước {_psPhase}/{_psStepPositions.Count} @2x ({FormatTime(pos / 1000)})";
        }
    }

    // ── Pb (Play Balanced) Mode ────────────────────────────────────────────────
    private void BeginPbMode()
    {
        if (_mediaPlayer == null || _duration <= 0) { _isPbMode = false; OnPropertyChanged(nameof(IsPbMode)); return; }
        // Tắt Lx/Ps nếu đang bật
        if (_isLxMode) { _isLxMode = false; OnPropertyChanged(nameof(IsLxMode)); }
        if (_isPsMode) StopPsMode(silent: true);

        _pbPhase            = 0;
        _pbPhaseTargetEndMs = _mediaPlayer.Time + 30_000L;
        _pbCurrentStep      = 0;
        _pbStepPositions.Clear();

        _mediaPlayer.SetRate(2.0f);
        _playbackSpeed = 2.0;
        OnPropertyChanged(nameof(PlaybackSpeedText));
        StatusText = "Pb: Intro 30s @2x...";
    }

    private void StopPbMode(bool silent = false)
    {
        _pbPhase = -1;
        _pbStepPositions.Clear();
        _isPbMode = false;
        OnPropertyChanged(nameof(IsPbMode));
        _mediaPlayer?.SetRate(1.0f);
        _playbackSpeed = 1.0;
        OnPropertyChanged(nameof(PlaybackSpeedText));
        StatusText = "Pb: Kết thúc";
    }

    private void GeneratePbSteps()
    {
        _pbStepPositions.Clear();
        if (_duration <= 0) return;

        long durMs = (long)(_duration * 1000);
        double durMin = _duration / 60.0;

        // Số điểm theo độ dài video
        int count;
        if (durMin < 3.0)        count = 1;   // < 3 phút: 1 điểm giữa
        else if (durMin <= 20.0) count = 5;   // 3–20 phút: 5 điểm
        else                     count = 10;  // > 20 phút: 10 điểm

        // Chia đều: điểm i = (i+1) * dur / (count+1)  (i = 0..count-1)
        // Ví dụ: count=5 → D/6, 2D/6, 3D/6, 4D/6, 5D/6
        for (int i = 0; i < count; i++)
        {
            long pos = (long)((i + 1L) * durMs / (count + 1));
            _pbStepPositions.Add(pos);
        }

        // Loại các điểm quá gần cuối (không đủ 30s để phát)
        _pbStepPositions.RemoveAll(p => p + 30_000L > durMs);

        if (_pbStepPositions.Count == 0)
        {
            StopPbMode();
        }
    }

    private void AdvancePbStep()
    {
        if (_pbCurrentStep >= _pbStepPositions.Count)
        {
            // Hết tất cả bước → kết thúc Pb
            StopPbMode();
            return;
        }

        long pos = _pbStepPositions[_pbCurrentStep];
        if (_mediaPlayer != null)
        {
            _mediaPlayer.Time = pos;
            _pbPhaseTargetEndMs = pos + 30_000L;
            _pbPhase = _pbCurrentStep + 1;
            _mediaPlayer.SetRate(2.0f);
            _playbackSpeed = 2.0;
            OnPropertyChanged(nameof(PlaybackSpeedText));
            StatusText = $"Pb: Bước {_pbPhase}/{_pbStepPositions.Count} @2x ({FormatTime(pos / 1000)})";
        }
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

        _scanningVideoPath = target;
        IsScanning = true;
        ScanProgress = 0;

        string targetName = Path.GetFileName(target);
        ScanStatusText = $"Đang phân tích AI YOLO11: {targetName} (≥2 người)...";
        if (string.Equals(_currentFilePath, target, StringComparison.OrdinalIgnoreCase))
        {
            Bookmarks.Clear();
        }

        try
        {
            var progress = new Progress<double>(p =>
            {
                ScanProgress = p;
                ScanStatusText = $"Đang quét AI YOLO11 [{targetName}]: {p:0.#}%";
            });

            var scanResult = await _motionService.ScanVideoAsync(target, progress, token);

            if (!scanResult.Success)
            {
                if (string.Equals(_currentFilePath, target, StringComparison.OrdinalIgnoreCase))
                {
                    ScanStatusText = $"Lỗi: {scanResult.ErrorMessage}";
                    StatusText = "Lỗi khi quét video";
                }
                return;
            }

            // Save to disk cache for subsequent playback
            if (scanResult.Bookmarks.Count > 0)
            {
                _cacheService.SaveBookmarks(target, scanResult.Bookmarks);
            }

            // Update UI if the user is still viewing this video
            if (string.Equals(_currentFilePath, target, StringComparison.OrdinalIgnoreCase))
            {
                Bookmarks.Clear();
                foreach (var bm in scanResult.Bookmarks)
                {
                    Bookmarks.Add(bm);
                }

                if (Bookmarks.Count > 0)
                {
                    ScanStatusText = $"Đã phát hiện và lưu cache {Bookmarks.Count} cảnh (≥2 người)";
                    StatusText = $"Phát hiện {Bookmarks.Count} cảnh (≥2 người)";
                }
                else
                {
                    ScanStatusText = $"Đã quét xong {scanResult.FramesProcessed} khung hình (Không có cảnh ≥2 người)";
                    StatusText = "Không có cảnh ≥2 người";
                }
            }
            else
            {
                // User switched to another video while target was scanning
                StatusText = $"Đã quét xong & lưu cache cho: {targetName} ({scanResult.Bookmarks.Count} cảnh)";
                if (Bookmarks.Count == 0)
                {
                    ScanStatusText = $"Đã quét xong video trước [{targetName}] | Bấm 'Quét cảnh' để quét video hiện tại";
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (string.Equals(_currentFilePath, target, StringComparison.OrdinalIgnoreCase))
            {
                ScanStatusText = "Đã dừng quét";
            }
        }
        catch (Exception ex)
        {
            if (string.Equals(_currentFilePath, target, StringComparison.OrdinalIgnoreCase))
            {
                ScanStatusText = $"Lỗi khi quét: {ex.Message}";
            }
        }
        finally
        {
            if (string.Equals(_scanningVideoPath, target, StringComparison.OrdinalIgnoreCase))
            {
                _scanningVideoPath = null;
                IsScanning = false;
            }
        }
    }

    public async Task GenerateRandomBookmarksAsync(string? path = null)
    {
        string target = path ?? _currentFilePath;
        if (string.IsNullOrEmpty(target) || !File.Exists(target))
        {
            ScanStatusText = "Chưa có video để tạo mốc";
            return;
        }

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        IsScanning = true;
        ScanProgress = 0;
        string targetName = Path.GetFileName(target);
        ScanStatusText = $"Đang trích xuất 10 mốc ngẫu nhiên: {targetName}...";

        try
        {
            var results = await _motionService.GenerateRandomBookmarksAsync(target, 10, token);

            if (string.Equals(_currentFilePath, target, StringComparison.OrdinalIgnoreCase))
            {
                Bookmarks.Clear();
                foreach (var bm in results)
                {
                    Bookmarks.Add(bm);
                }

                if (Bookmarks.Count > 0)
                {
                    _cacheService.SaveBookmarks(target, Bookmarks);
                    ScanStatusText = $"Đã tạo {Bookmarks.Count} mốc ngẫu nhiên và lưu cache";
                    StatusText = $"Đã tạo {Bookmarks.Count} mốc ngẫu nhiên";
                }
                else
                {
                    ScanStatusText = "Không thể trích xuất khung hình từ video";
                }
            }
            else
            {
                if (results.Count > 0)
                {
                    _cacheService.SaveBookmarks(target, results);
                    StatusText = $"Đã lưu {results.Count} mốc ngẫu nhiên cho: {targetName}";
                }
            }
        }
        catch (OperationCanceledException)
        {
            ScanStatusText = "Đã dừng";
        }
        catch (Exception ex)
        {
            ScanStatusText = $"Lỗi: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    public void CancelMotionScan()
    {
        _scanCts?.Cancel();
        string? name = _scanningVideoPath != null ? Path.GetFileName(_scanningVideoPath) : null;
        _scanningVideoPath = null;
        IsScanning = false;
        ScanStatusText = name != null ? $"Đã hủy tiến trình quét: {name}" : "Đã hủy quét";
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
        if (!string.IsNullOrEmpty(_currentFilePath))
        {
            _cacheService.DeleteCache(_currentFilePath);
        }
        Bookmarks.Clear();
        ScanStatusText = "Đã xóa danh sách bookmark và bộ nhớ đệm";
    }

    // ── Timeline Hover Preview ────────────────────────────────────────────────
    public void UpdateTimelineHover(double normalizedPos, double trackWidth, double tooltipWidth = 190.0)
    {
        if (!HasMedia || _duration <= 0 || trackWidth <= 0)
        {
            HideTimelineHover();
            return;
        }

        normalizedPos = Math.Clamp(normalizedPos, 0.0, 1.0);
        double hoverSec = normalizedPos * _duration;
        TimelineHoverTimeText = FormatTime((long)hoverSec);

        // Center the tooltip on cursor, clamping within track bounds
        double targetLeft = (normalizedPos * trackWidth) - (tooltipWidth / 2.0);
        TimelineHoverLeft = Math.Clamp(targetLeft, 0, Math.Max(0, trackWidth - tooltipWidth));

        // Find if there is an active bookmark nearby (+/- 3 seconds)
        TimelineHoverBookmark = Bookmarks.FirstOrDefault(b => Math.Abs(b.TimeSeconds - hoverSec) <= 3.0);

        IsTimelineHoverPreviewVisible = true;

        // Debounce & fetch frame preview asynchronously
        _hoverPreviewCts?.Cancel();
        _hoverPreviewCts = new CancellationTokenSource();
        var token = _hoverPreviewCts.Token;

        string videoPath = _currentFilePath;
        _ = Task.Run(async () =>
        {
            try
            {
                var previewBmp = await _previewService.GetPreviewAsync(videoPath, hoverSec, token);
                if (!token.IsCancellationRequested && previewBmp != null)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (IsTimelineHoverPreviewVisible)
                        {
                            TimelineHoverPreviewImage = previewBmp;
                        }
                    });
                }
            }
            catch { }
        }, token);
    }

    public void HideTimelineHover()
    {
        _hoverPreviewCts?.Cancel();
        IsTimelineHoverPreviewVisible = false;
        TimelineHoverBookmark = null;
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
        _hoverPreviewCts?.Cancel();
        _previewService.Dispose();
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
