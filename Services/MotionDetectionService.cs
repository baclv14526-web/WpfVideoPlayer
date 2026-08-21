using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using WpfVideoPlayer.Models;

namespace WpfVideoPlayer.Services;

public class MotionDetectionService
{
    private const int TargetWidth = 320;
    private const int TargetHeight = 180;
    private const double MotionThreshold = 0.02; // 2% of pixels changed
    private const double SampleIntervalSeconds = 0.5; // Analyze 2 frames per second for high performance

    /// <summary>
    /// Analyzes a video file in the background to detect motion events and return bookmarks.
    /// </summary>
    public async Task<List<MotionBookmark>> ScanVideoAsync(
        string videoPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<MotionBookmark>();

            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                return results;

            using var capture = new VideoCapture(videoPath);
            if (!capture.IsOpened())
                return results;

            int totalFrames = (int)capture.Get(VideoCaptureProperties.FrameCount);
            double fps = capture.Get(VideoCaptureProperties.Fps);
            if (fps <= 0 || double.IsNaN(fps)) fps = 30.0;

            double durationSeconds = totalFrames > 0 ? totalFrames / fps : 0;
            int frameStep = Math.Max(1, (int)(fps * SampleIntervalSeconds));

            var rawDetections = new List<(double TimeSec, double Ratio)>();

            using var frame = new Mat();
            using var resized = new Mat();
            using var gray = new Mat();
            using var blurred = new Mat();
            using var prevGray = new Mat();
            using var diff = new Mat();
            using var thresh = new Mat();

            int currentFrameIndex = 0;

            while (currentFrameIndex < totalFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                capture.Set(VideoCaptureProperties.PosFrames, currentFrameIndex);
                if (!capture.Read(frame) || frame.Empty())
                    break;

                Cv2.Resize(frame, resized, new Size(TargetWidth, TargetHeight));
                Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, blurred, new Size(21, 21), 0);

                if (!prevGray.Empty())
                {
                    Cv2.Absdiff(prevGray, blurred, diff);
                    Cv2.Threshold(diff, thresh, 25, 255, ThresholdTypes.Binary);
                    Cv2.Dilate(thresh, thresh, new Mat(), iterations: 2);

                    int nonZero = Cv2.CountNonZero(thresh);
                    double ratio = (double)nonZero / (TargetWidth * TargetHeight);

                    if (ratio >= MotionThreshold)
                    {
                        double timeSec = currentFrameIndex / fps;
                        rawDetections.Add((timeSec, ratio));
                    }
                }

                blurred.CopyTo(prevGray);

                currentFrameIndex += frameStep;
                if (totalFrames > 0)
                {
                    double progressPercent = Math.Min(100.0, (currentFrameIndex / (double)totalFrames) * 100.0);
                    progress?.Report(progressPercent);
                }
            }

            progress?.Report(100.0);

            // Group adjacent detections within 3 seconds of each other
            if (rawDetections.Count > 0)
            {
                var groups = new List<List<(double TimeSec, double Ratio)>>();
                var currentGroup = new List<(double TimeSec, double Ratio)> { rawDetections[0] };

                for (int i = 1; i < rawDetections.Count; i++)
                {
                    if (rawDetections[i].TimeSec - currentGroup.Last().TimeSec <= 3.0)
                    {
                        currentGroup.Add(rawDetections[i]);
                    }
                    else
                    {
                        groups.Add(currentGroup);
                        currentGroup = new List<(double TimeSec, double Ratio)> { rawDetections[i] };
                    }
                }
                groups.Add(currentGroup);

                foreach (var g in groups)
                {
                    double startTime = g.First().TimeSec;
                    double endTime = g.Last().TimeSec;
                    double maxRatio = g.Max(x => x.Ratio);
                    double duration = Math.Max(1.0, endTime - startTime);

                    var intensity = maxRatio switch
                    {
                        >= 0.25 => MotionIntensity.SceneChange,
                        >= 0.12 => MotionIntensity.High,
                        >= 0.06 => MotionIntensity.Medium,
                        _ => MotionIntensity.Light
                    };

                    double normPos = durationSeconds > 0 ? Math.Clamp(startTime / durationSeconds, 0.0, 1.0) : 0;

                    results.Add(new MotionBookmark
                    {
                        TimeSeconds = startTime,
                        TimeText = FormatTime((long)startTime),
                        NormalizedPosition = normPos,
                        IntensityRatio = maxRatio,
                        Intensity = intensity,
                        DurationSeconds = duration
                    });
                }
            }

            return results;
        }, cancellationToken);
    }

    private static string FormatTime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
