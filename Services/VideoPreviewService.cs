using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace WpfVideoPlayer.Services;

public class VideoPreviewService : IDisposable
{
    private const int PreviewWidth = 200;
    private const int PreviewHeight = 112;
    private const int MaxMemoryCacheSize = 300;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, BitmapSource>> _cache = new();
    private VideoCapture? _activeCapture;
    private string? _activeCapturePath;
    private readonly object _captureLock = new();
    private bool _isDisposed;

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern uint GetShortPathName(
        [MarshalAs(UnmanagedType.LPTStr)] string lpszLongPath,
        [MarshalAs(UnmanagedType.LPTStr)] StringBuilder lpszShortPath,
        uint cchBuffer);

    private static string GetSafeAsciiPath(string fullPath)
    {
        try
        {
            var shortBuffer = new StringBuilder(1024);
            uint result = GetShortPathName(fullPath, shortBuffer, (uint)shortBuffer.Capacity);
            if (result > 0 && result < shortBuffer.Capacity)
            {
                string shortPath = shortBuffer.ToString();
                if (File.Exists(shortPath)) return shortPath;
            }
        }
        catch { }
        return fullPath;
    }

    private VideoCapture? GetOrCreateCapture(string videoPath)
    {
        lock (_captureLock)
        {
            if (_activeCapture != null && string.Equals(_activeCapturePath, videoPath, StringComparison.OrdinalIgnoreCase))
            {
                if (_activeCapture.IsOpened())
                    return _activeCapture;
            }

            _activeCapture?.Dispose();
            _activeCapture = null;
            _activeCapturePath = null;

            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                return null;

            try
            {
                var cap = new VideoCapture(videoPath, VideoCaptureAPIs.FFMPEG);
                if (cap.IsOpened())
                {
                    _activeCapture = cap;
                    _activeCapturePath = videoPath;
                    return cap;
                }
                cap.Dispose();
            }
            catch { }

            try
            {
                var cap = new VideoCapture(videoPath);
                if (cap.IsOpened())
                {
                    _activeCapture = cap;
                    _activeCapturePath = videoPath;
                    return cap;
                }
                cap.Dispose();
            }
            catch { }

            try
            {
                string shortPath = GetSafeAsciiPath(videoPath);
                var cap = new VideoCapture(shortPath);
                if (cap.IsOpened())
                {
                    _activeCapture = cap;
                    _activeCapturePath = videoPath;
                    return cap;
                }
                cap.Dispose();
            }
            catch { }

            return null;
        }
    }

    /// <summary>
    /// Asynchronously extracts a preview thumbnail for the given second in video.
    /// </summary>
    public async Task<BitmapSource?> GetPreviewAsync(
        string videoPath,
        double timeSeconds,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath) || timeSeconds < 0)
            return null;

        int secondKey = (int)Math.Round(timeSeconds);

        var videoCache = _cache.GetOrAdd(videoPath, _ => new ConcurrentDictionary<int, BitmapSource>());
        if (videoCache.TryGetValue(secondKey, out var cachedBmp))
        {
            return cachedBmp;
        }

        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_captureLock)
            {
                var cap = GetOrCreateCapture(videoPath);
                if (cap == null || !cap.IsOpened())
                    return null;

                try
                {
                    double fps = cap.Get(VideoCaptureProperties.Fps);
                    if (fps <= 0 || double.IsNaN(fps)) fps = 30.0;

                    int targetFrame = (int)(timeSeconds * fps);
                    cap.Set(VideoCaptureProperties.PosFrames, targetFrame);

                    using var frame = new Mat();
                    if (!cap.Read(frame) || frame.Empty())
                    {
                        // Fallback seek via PosMsec
                        cap.Set(VideoCaptureProperties.PosMsec, timeSeconds * 1000.0);
                        if (!cap.Read(frame) || frame.Empty())
                            return null;
                    }

                    using var resized = new Mat();
                    Cv2.Resize(frame, resized, new Size(PreviewWidth, PreviewHeight));

                    var bmp = MatToBitmapSource(resized);
                    if (bmp != null)
                    {
                        if (videoCache.Count < MaxMemoryCacheSize)
                        {
                            videoCache.TryAdd(secondKey, bmp);
                        }
                        return bmp;
                    }
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }, cancellationToken);
    }

    private static BitmapSource? MatToBitmapSource(Mat mat)
    {
        if (mat.Empty()) return null;

        int width = mat.Width;
        int height = mat.Height;
        int stride = (int)mat.Step();

        PixelFormat format = mat.Channels() switch
        {
            1 => PixelFormats.Gray8,
            3 => PixelFormats.Bgr24,
            4 => PixelFormats.Bgra32,
            _ => PixelFormats.Bgr24
        };

        var bs = BitmapSource.Create(
            width,
            height,
            96,
            96,
            format,
            null,
            mat.Data,
            stride * height,
            stride);

        if (bs.CanFreeze)
        {
            bs.Freeze();
        }

        return bs;
    }

    public void ClearCache()
    {
        _cache.Clear();
        lock (_captureLock)
        {
            _activeCapture?.Dispose();
            _activeCapture = null;
            _activeCapturePath = null;
        }
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            ClearCache();
            _isDisposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
