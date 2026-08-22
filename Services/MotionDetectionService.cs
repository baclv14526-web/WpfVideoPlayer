using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using WpfVideoPlayer.Models;

namespace WpfVideoPlayer.Services;

public class MotionDetectionService
{
    private const int AnalysisWidth = 480;
    private const int AnalysisHeight = 270;
    private const double SampleIntervalSeconds = 0.5; // Analyze 2 frames per second
    private const double SceneChangeHistThreshold = 0.50; // Correlation < 0.50 indicates scene cut

    /// <summary>
    /// Analyzes a video file to detect person struggles, intense actions, and scene transitions using YOLO + OpenCV.
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

            using var yoloDetector = new YoloOnnxDetector();

            var rawEvents = new List<RawDetectionEvent>();

            using var frame = new Mat();
            using var resized = new Mat();
            using var gray = new Mat();
            using var blurred = new Mat();
            using var prevGray = new Mat();
            using var diff = new Mat();
            using var thresh = new Mat();
            using var prevHist = new Mat();
            using var currentHist = new Mat();

            int currentFrameIndex = 0;

            while (currentFrameIndex < totalFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                capture.Set(VideoCaptureProperties.PosFrames, currentFrameIndex);
                if (!capture.Read(frame) || frame.Empty())
                    break;

                double timeSec = currentFrameIndex / fps;

                // Resize for fast analysis
                Cv2.Resize(frame, resized, new Size(AnalysisWidth, AnalysisHeight));
                Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, blurred, new Size(15, 15), 0);

                // 1. Calculate color histogram for Scene Change Detection
                CalculateHsvHist(resized, currentHist);
                bool isSceneChange = false;
                double histCorr = 1.0;

                if (!prevHist.Empty())
                {
                    histCorr = Cv2.CompareHist(prevHist, currentHist, HistCompMethods.Correl);
                    if (histCorr < SceneChangeHistThreshold)
                    {
                        isSceneChange = true;
                    }
                }

                // 2. Calculate Motion / Frame Difference
                double motionRatio = 0.0;
                if (!prevGray.Empty())
                {
                    Cv2.Absdiff(prevGray, blurred, diff);
                    Cv2.Threshold(diff, thresh, 25, 255, ThresholdTypes.Binary);
                    int nonZero = Cv2.CountNonZero(thresh);
                    motionRatio = (double)nonZero / (AnalysisWidth * AnalysisHeight);
                }

                // 3. Person detection with YOLO / HOG
                var detectedPersons = yoloDetector.DetectPersons(resized, confThreshold: 0.30f);
                int personCount = detectedPersons.Count;

                // Check for group struggle / wrestling or intense person action
                bool isGroupStruggle = false;
                bool isPersonAction = false;
                bool isGroupDetected = false;

                if (personCount >= 2)
                {
                    // Check bounding boxes overlap / proximity
                    bool hasBoxOverlap = false;
                    for (int i = 0; i < detectedPersons.Count; i++)
                    {
                        for (int j = i + 1; j < detectedPersons.Count; j++)
                        {
                            float iou = YoloOnnxDetector.ComputeIoU(detectedPersons[i].BoundingBox, detectedPersons[j].BoundingBox);
                            // Also check distance between centers
                            var centerA = new Point(detectedPersons[i].BoundingBox.X + detectedPersons[i].BoundingBox.Width / 2,
                                                    detectedPersons[i].BoundingBox.Y + detectedPersons[i].BoundingBox.Height / 2);
                            var centerB = new Point(detectedPersons[j].BoundingBox.X + detectedPersons[j].BoundingBox.Width / 2,
                                                    detectedPersons[j].BoundingBox.Y + detectedPersons[j].BoundingBox.Height / 2);
                            double distance = Math.Sqrt(Math.Pow(centerA.X - centerB.X, 2) + Math.Pow(centerA.Y - centerB.Y, 2));

                            if (iou > 0.08f || distance < (detectedPersons[i].BoundingBox.Width + detectedPersons[j].BoundingBox.Width) * 0.45)
                            {
                                hasBoxOverlap = true;
                                break;
                            }
                        }
                        if (hasBoxOverlap) break;
                    }

                    if (hasBoxOverlap && motionRatio >= 0.03)
                    {
                        isGroupStruggle = true;
                    }
                    else
                    {
                        isGroupDetected = true;
                    }
                }
                else if (personCount == 1)
                {
                    if (motionRatio >= 0.04)
                    {
                        isPersonAction = true;
                    }
                }

                // Determine if this frame is a significant event to bookmark
                if (isGroupStruggle || isPersonAction || isSceneChange || isGroupDetected || motionRatio >= 0.06)
                {
                    var intensity = MotionIntensity.Medium;
                    if (isGroupStruggle) intensity = MotionIntensity.GroupStruggle;
                    else if (isPersonAction) intensity = MotionIntensity.PersonAction;
                    else if (isSceneChange) intensity = MotionIntensity.SceneChange;
                    else if (isGroupDetected) intensity = MotionIntensity.GroupDetected;
                    else if (motionRatio >= 0.15) intensity = MotionIntensity.High;

                    // Generate annotated thumbnail preview
                    var thumbnail = CreateAnnotatedThumbnail(frame, detectedPersons, isGroupStruggle, isPersonAction, isSceneChange);

                    rawEvents.Add(new RawDetectionEvent
                    {
                        TimeSec = timeSec,
                        Intensity = intensity,
                        MotionRatio = motionRatio,
                        PersonCount = personCount,
                        Thumbnail = thumbnail
                    });
                }

                // Save states for next iteration
                blurred.CopyTo(prevGray);
                currentHist.CopyTo(prevHist);

                currentFrameIndex += frameStep;
                if (totalFrames > 0)
                {
                    double progressPercent = Math.Min(100.0, (currentFrameIndex / (double)totalFrames) * 100.0);
                    progress?.Report(progressPercent);
                }
            }

            progress?.Report(100.0);

            // Group adjacent events within 3.5 seconds
            if (rawEvents.Count > 0)
            {
                var groups = new List<List<RawDetectionEvent>>();
                var currentGroup = new List<RawDetectionEvent> { rawEvents[0] };

                for (int i = 1; i < rawEvents.Count; i++)
                {
                    if (rawEvents[i].TimeSec - currentGroup.Last().TimeSec <= 3.5 &&
                        (rawEvents[i].Intensity == currentGroup.First().Intensity ||
                         rawEvents[i].Intensity == MotionIntensity.GroupStruggle ||
                         currentGroup.First().Intensity == MotionIntensity.GroupStruggle))
                    {
                        currentGroup.Add(rawEvents[i]);
                    }
                    else
                    {
                        groups.Add(currentGroup);
                        currentGroup = new List<RawDetectionEvent> { rawEvents[i] };
                    }
                }
                groups.Add(currentGroup);

                foreach (var g in groups)
                {
                    var primaryEvent = g.OrderByDescending(x => (int)x.Intensity)
                                        .ThenByDescending(x => x.MotionRatio)
                                        .First();

                    double startTime = g.First().TimeSec;
                    double endTime = g.Last().TimeSec;
                    double maxRatio = g.Max(x => x.MotionRatio);
                    double duration = Math.Max(1.5, endTime - startTime);
                    int maxPersons = g.Max(x => x.PersonCount);

                    double normPos = durationSeconds > 0 ? Math.Clamp(startTime / durationSeconds, 0.0, 1.0) : 0;

                    results.Add(new MotionBookmark
                    {
                        TimeSeconds = startTime,
                        TimeText = FormatTime((long)startTime),
                        NormalizedPosition = normPos,
                        IntensityRatio = maxRatio,
                        Intensity = primaryEvent.Intensity,
                        DurationSeconds = duration,
                        PersonCount = maxPersons,
                        PreviewImage = primaryEvent.Thumbnail
                    });
                }
            }

            return results;
        }, cancellationToken);
    }

    private static void CalculateHsvHist(Mat bgrImage, Mat hist)
    {
        using var hsv = new Mat();
        Cv2.CvtColor(bgrImage, hsv, ColorConversionCodes.BGR2HSV);

        int[] hBins = { 30, 32 };
        int[] channels = { 0, 1 };
        Rangef[] ranges = { new Rangef(0, 180), new Rangef(0, 256) };

        Cv2.CalcHist(new[] { hsv }, channels, null, hist, 2, hBins, ranges);
        Cv2.Normalize(hist, hist, 0, 1, NormTypes.MinMax);
    }

    private static BitmapSource? CreateAnnotatedThumbnail(
        Mat originalFrame,
        List<DetectedPerson> persons,
        bool isGroupStruggle,
        bool isPersonAction,
        bool isSceneChange)
    {
        try
        {
            using var preview = new Mat();
            // Scale thumbnail to 280x158 (16:9)
            int thumbW = 280;
            int thumbH = 158;
            Cv2.Resize(originalFrame, preview, new Size(thumbW, thumbH));

            double scaleX = (double)thumbW / originalFrame.Width;
            double scaleY = (double)thumbH / originalFrame.Height;

            // Draw bounding boxes on preview
            foreach (var p in persons)
            {
                int bx = (int)(p.BoundingBox.X * scaleX);
                int by = (int)(p.BoundingBox.Y * scaleY);
                int bw = (int)(p.BoundingBox.Width * scaleX);
                int bh = (int)(p.BoundingBox.Height * scaleY);

                var boxColor = isGroupStruggle ? new Scalar(56, 56, 255) // Red BGR
                             : isPersonAction  ? new Scalar(63, 121, 255) // Orange BGR
                             : new Scalar(66, 177, 255); // Amber

                Cv2.Rectangle(preview, new Rect(bx, by, bw, bh), boxColor, 2);

                // Small badge
                string label = isGroupStruggle ? "Vat lon" : "Nguoi";
                Cv2.PutText(preview, label, new Point(bx, Math.Max(14, by - 4)),
                    HersheyFonts.HersheySimplex, 0.4, boxColor, 1, LineTypes.AntiAlias);
            }

            // Top-left indicator badge
            if (isGroupStruggle)
            {
                Cv2.Rectangle(preview, new Rect(6, 6, 88, 20), new Scalar(0, 0, 0), -1);
                Cv2.PutText(preview, "XO XAT/VAT LON", new Point(10, 20), HersheyFonts.HersheySimplex, 0.35, new Scalar(56, 56, 255), 1);
            }
            else if (isSceneChange)
            {
                Cv2.Rectangle(preview, new Rect(6, 6, 75, 20), new Scalar(0, 0, 0), -1);
                Cv2.PutText(preview, "CHUYEN CANH", new Point(10, 20), HersheyFonts.HersheySimplex, 0.35, new Scalar(211, 111, 112), 1);
            }

            var bitmapSource = preview.ToBitmapSource();
            if (bitmapSource.CanFreeze)
            {
                bitmapSource.Freeze(); // Critical for cross-thread WPF rendering
            }
            return bitmapSource;
        }
        catch
        {
            return null;
        }
    }

    private static string FormatTime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private record RawDetectionEvent
    {
        public double TimeSec { get; init; }
        public MotionIntensity Intensity { get; init; }
        public double MotionRatio { get; init; }
        public int PersonCount { get; init; }
        public BitmapSource? Thumbnail { get; init; }
    }
}
