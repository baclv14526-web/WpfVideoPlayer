using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media.Imaging;
using WpfVideoPlayer.Models;

namespace WpfVideoPlayer.Services;

public class BookmarkCacheService
{
    private readonly string _cacheDirectory;

    public BookmarkCacheService()
    {
        _cacheDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WpfVideoPlayer",
            "BookmarkCache");

        try
        {
            if (!Directory.Exists(_cacheDirectory))
            {
                Directory.CreateDirectory(_cacheDirectory);
            }
        }
        catch
        {
            // Ignore directory creation errors
        }
    }

    /// <summary>
    /// Computes a unique cache key based on video file path, file size, and last write time.
    /// </summary>
    private static string GetCacheKey(string filePath)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            string rawKey = $"{filePath.ToLowerInvariant()}_{fileInfo.Length}_{fileInfo.LastWriteTimeUtc.Ticks}";
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(rawKey));
            return Convert.ToHexString(hash);
        }
        catch
        {
            using var md5 = MD5.Create();
            byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(filePath.ToLowerInvariant()));
            return Convert.ToHexString(hash);
        }
    }

    private string GetCacheFilePath(string videoPath)
    {
        string key = GetCacheKey(videoPath);
        return Path.Combine(_cacheDirectory, $"{key}.json");
    }

    /// <summary>
    /// Checks if cache exists for the given video file.
    /// </summary>
    public bool HasCache(string videoPath)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            return false;

        string cacheFile = GetCacheFilePath(videoPath);
        return File.Exists(cacheFile);
    }

    /// <summary>
    /// Attempts to load cached bookmarks for the given video. Returns null if not found or corrupted.
    /// </summary>
    public List<MotionBookmark>? LoadBookmarks(string videoPath)
    {
        if (!HasCache(videoPath))
            return null;

        try
        {
            string cacheFile = GetCacheFilePath(videoPath);
            string json = File.ReadAllText(cacheFile);
            var dtos = JsonSerializer.Deserialize<List<BookmarkDto>>(json);
            if (dtos == null || dtos.Count == 0)
                return null;

            var results = new List<MotionBookmark>();
            foreach (var dto in dtos)
            {
                var bm = new MotionBookmark
                {
                    TimeSeconds = dto.TimeSeconds,
                    TimeText = dto.TimeText,
                    NormalizedPosition = dto.NormalizedPosition,
                    IntensityRatio = dto.IntensityRatio,
                    Intensity = dto.Intensity,
                    DurationSeconds = dto.DurationSeconds,
                    PersonCount = dto.PersonCount,
                    CustomTitle = dto.CustomTitle,
                    PreviewImage = Base64ToBitmapSource(dto.ThumbnailBase64)
                };
                results.Add(bm);
            }

            return results;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Saves the bookmarks list and thumbnails to disk cache for the given video.
    /// </summary>
    public void SaveBookmarks(string videoPath, IEnumerable<MotionBookmark> bookmarks)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            return;

        try
        {
            var dtos = new List<BookmarkDto>();
            foreach (var bm in bookmarks)
            {
                dtos.Add(new BookmarkDto
                {
                    TimeSeconds = bm.TimeSeconds,
                    TimeText = bm.TimeText,
                    NormalizedPosition = bm.NormalizedPosition,
                    IntensityRatio = bm.IntensityRatio,
                    Intensity = bm.Intensity,
                    DurationSeconds = bm.DurationSeconds,
                    PersonCount = bm.PersonCount,
                    CustomTitle = bm.CustomTitle,
                    ThumbnailBase64 = BitmapSourceToBase64(bm.PreviewImage)
                });
            }

            string cacheFile = GetCacheFilePath(videoPath);
            string json = JsonSerializer.Serialize(dtos, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(cacheFile, json);
        }
        catch
        {
            // Ignore cache write errors
        }
    }

    /// <summary>
    /// Clears the cache for a specific video file or all cache files.
    /// </summary>
    public void DeleteCache(string videoPath)
    {
        try
        {
            string cacheFile = GetCacheFilePath(videoPath);
            if (File.Exists(cacheFile))
            {
                File.Delete(cacheFile);
            }
        }
        catch { }
    }

    private static string? BitmapSourceToBase64(BitmapSource? image)
    {
        if (image == null) return null;

        try
        {
            using var memory = new MemoryStream();
            var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(memory);
            return Convert.ToBase64String(memory.ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? Base64ToBitmapSource(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return null;

        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using var memory = new MemoryStream(bytes);
            var decoder = new JpegBitmapDecoder(memory, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            if (frame.CanFreeze)
            {
                frame.Freeze();
            }
            return frame;
        }
        catch
        {
            return null;
        }
    }

    private class BookmarkDto
    {
        public double TimeSeconds { get; set; }
        public string TimeText { get; set; } = "00:00";
        public double NormalizedPosition { get; set; }
        public double IntensityRatio { get; set; }
        public MotionIntensity Intensity { get; set; }
        public double DurationSeconds { get; set; }
        public int PersonCount { get; set; }
        public string CustomTitle { get; set; } = string.Empty;
        public string? ThumbnailBase64 { get; set; }
    }
}
