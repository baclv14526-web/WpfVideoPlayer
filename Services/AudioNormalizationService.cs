using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace WpfVideoPlayer.Services;

/// <summary>
/// Analyzes audio loudness of video files using a headless LibVLC decoder
/// and computes a volume gain factor to normalize perceived loudness
/// across different videos to a consistent target level.
/// </summary>
public sealed class AudioNormalizationService : IDisposable
{
    // ── Normalization targets ─────────────────────────────────────────────────
    /// <summary>Target RMS level (~-22 dBFS). Matches typical streaming standards.</summary>
    private const double TargetRms = 0.08;
    /// <summary>Below this RMS → treat audio as silent, return gain 1.0.</summary>
    private const double MinRms    = 0.002;
    /// <summary>Maximum gain to apply (+8 dB ceiling).</summary>
    private const double MaxGain   = 2.5;
    /// <summary>Minimum gain to apply (-10 dB floor).</summary>
    private const double MinGain   = 0.30;
    /// <summary>Seconds of audio to analyze from the start of the video.</summary>
    private const int    AnalyzeSec = 20;
    /// <summary>Hard timeout for the analysis task (ms).</summary>
    private const int    TimeoutMs  = 14_000;
    /// <summary>Sample rate for analysis (mono, lower rate = faster decode).</summary>
    private const uint   SampleRate = 22_050;

    /// <summary>Thread-safe cache: filePath → computed gain factor.</summary>
    private readonly ConcurrentDictionary<string, double> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Asynchronously computes a volume gain multiplier for the given video file.
    /// The result should be multiplied by the user base volume (0-200 VLC scale)
    /// to produce a normalized playback volume. Returns 1.0 on error/silent audio.
    /// Results are cached per file path.
    /// </summary>
    public async Task<double> GetGainFactorAsync(string filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            return 1.0;

        if (_cache.TryGetValue(filePath, out double cached))
            return cached;

        try
        {
            double rms  = await Task.Run(() => MeasureRms(filePath, ct), ct);
            double gain = rms < MinRms
                ? 1.0
                : Math.Clamp(TargetRms / rms, MinGain, MaxGain);

            _cache.TryAdd(filePath, gain);
            return gain;
        }
        catch (OperationCanceledException) { return 1.0; }
        catch                              { return 1.0; }
    }

    /// <summary>Removes a specific file from the analysis cache.</summary>
    public void InvalidateCache(string filePath) => _cache.TryRemove(filePath, out _);

    /// <summary>Clears all cached gain values.</summary>
    public void ClearCache() => _cache.Clear();

    // ── Core: headless LibVLC decode → RMS measurement ────────────────────────

    private unsafe double MeasureRms(string filePath, CancellationToken ct)
    {
        double sumSq       = 0.0;
        long   sampleCount = 0L;
        long   maxSamples  = SampleRate * (long)AnalyzeSec;

        var doneEvent  = new ManualResetEventSlim(initialState: false);
        var sampleLock = new object();

        LibVLC?      lib = null;
        MediaPlayer? mp  = null;

        try
        {
            lib = new LibVLC(enableDebugLogs: false,
                "--no-video", "--intf=dummy", "--no-osd", "--verbose=-1");

            mp = new MediaPlayer(lib);

            // Force mono S16N output: count (in callback) = number of mono frames
            mp.SetAudioFormat("S16N", SampleRate, 1);

            mp.SetAudioCallbacks(
                (data, samplesPtr, count, pts) =>
                {
                    lock (sampleLock)
                    {
                        if (sampleCount >= maxSamples) return;
                        short* ptr    = (short*)samplesPtr.ToPointer();
                        long   toRead = Math.Min((long)count, maxSamples - sampleCount);
                        for (long i = 0; i < toRead; i++)
                        {
                            double s = ptr[i] / 32768.0;
                            sumSq += s * s;
                        }
                        sampleCount += toRead;
                        if (sampleCount >= maxSamples) doneEvent.Set();
                    }
                },
                null,
                null,
                null,
                _ => doneEvent.Set()
            );

            mp.EndReached       += (_, _) => doneEvent.Set();
            mp.EncounteredError += (_, _) => doneEvent.Set();

            var media = new Media(lib, filePath, FromType.FromPath);
            media.AddOption(":no-video");
            media.AddOption($":stop-time={AnalyzeSec}");
            mp.Media = media;
            mp.Play();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeoutMs);
            try   { doneEvent.Wait(linked.Token); }
            catch (OperationCanceledException) { /* use what we have */ }
        }
        finally
        {
            try { mp?.Stop(); } catch { }
            mp?.Dispose();
            lib?.Dispose();
        }

        lock (sampleLock)
        {
            if (sampleCount == 0) return 0.0;
            return Math.Sqrt(sumSq / sampleCount);
        }
    }

    // ── Disposal ─────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (!_disposed)
        {
            _cache.Clear();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
