using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using WpfVideoPlayer.Models;

namespace WpfVideoPlayer.Services;

public class MotionDetectionService
{
    private const int AnalysisWidth = 480;
    private const int AnalysisHeight = 270;
    private const double SampleIntervalSeconds = 0.5; // Analyze 2 frames per second for high speed
    private const double SceneChangeHistThreshold = 0.52; // Histogram correlation < 0.52 indicates scene cut
    private const double MinBookmarkDistanceSeconds = 6.0; // Minimum time gap between similar bookmarks
    private const double VisualSimilarityThreshold = 0.72; // Hist correlation >= 0.72 means same visual scene
    private const int MinRequiredPersons = 2; // ONLY bookmark moments with 2 or more people

    /// <summary>
    /// Analyzes video using YOLO Small and OpenCV, ONLY bookmarking distinct moments 
    /// containing 2 or more persons (struggles, wrestling, group interactions) with image previews.
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

            var candidateEvents = new List<CandidateDetection>();

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

                // 1. Resize for fast AI processing
                Cv2.Resize(frame, resized, new Size(AnalysisWidth, AnalysisHeight));
                Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.GaussianBlur(gray, blurred, new Size(15, 15), 0);

                // 2. Color histogram for Scene Fingerprinting & Scene Cut Detection
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

                // 3. Motion calculation
                double motionRatio = 0.0;
                if (!prevGray.Empty())
                {
                    Cv2.Absdiff(prevGray, blurred, diff);
                    Cv2.Threshold(diff, thresh, 25, 255, ThresholdTypes.Binary);
                    int nonZero = Cv2.CountNonZero(thresh);
                    motionRatio = (double)nonZero / (AnalysisWidth * AnalysisHeight);
                }

                // 4. Person detection with YOLO Small
                var detectedPersons = yoloDetector.DetectPersons(resized, confThreshold: 0.30f);
                int personCount = detectedPersons.Count;

                // ── ONLY FILTER MOMENTS WITH 2 OR MORE PERSONS ─────────────────
                if (personCount >= MinRequiredPersons)
                {
                    bool isGroupStruggle = false;
                    bool hasBoxOverlap = false;

                    for (int i = 0; i < detectedPersons.Count; i++)
                    {
                        for (int j = i + 1; j < detectedPersons.Count; j++)
                        {
                            float iou = YoloOnnxDetector.ComputeIoU(detectedPersons[i].BoundingBox, detectedPersons[j].BoundingBox);
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

                    if (hasBoxOverlap && motionRatio >= 0.025)
                    {
                        isGroupStruggle = true;
                    }

                    var intensity = isGroupStruggle ? MotionIntensity.GroupStruggle : MotionIntensity.GroupDetected;
                    double importanceScore = isGroupStruggle
                        ? (100.0 + (motionRatio * 60.0) + (personCount * 12.0))
                        : (50.0 + (personCount * 10.0) + (motionRatio * 20.0));

                    // Store candidate event
                    candidateEvents.Add(new CandidateDetection
                    {
                        TimeSec = timeSec,
                        Intensity = intensity,
                        ImportanceScore = importanceScore,
                        MotionRatio = motionRatio,
                        PersonCount = personCount,
                        Hist = currentHist.Clone(),
                        RawFrame = frame.Clone(),
                        DetectedPersons = detectedPersons,
                        IsGroupStruggle = isGroupStruggle,
                        IsSceneChange = isSceneChange
                    });
                }

                // Save states
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

            // ═════════════════════════════════════════════════════════════════════
            // SMART DEDUPLICATION & DISTINCT SCENE FILTERING (2+ PERSONS ONLY)
            // ═════════════════════════════════════════════════════════════════════
            if (candidateEvents.Count > 0)
            {
                var distinctClusters = new List<List<CandidateDetection>>();
                var currentCluster = new List<CandidateDetection> { candidateEvents[0] };

                for (int i = 1; i < candidateEvents.Count; i++)
                {
                    var prev = currentCluster.Last();
                    var curr = candidateEvents[i];
                    double timeGap = curr.TimeSec - prev.TimeSec;

                    // Calculate visual similarity between candidate frames
                    double visualSimilarity = 0.0;
                    if (prev.Hist != null && curr.Hist != null && !prev.Hist.Empty() && !curr.Hist.Empty())
                    {
                        visualSimilarity = Cv2.CompareHist(prev.Hist, curr.Hist, HistCompMethods.Correl);
                    }

                    // If within close time proximity (< 6s) OR high visual similarity (< 12s with similar background)
                    bool isSameScene = (timeGap < MinBookmarkDistanceSeconds) ||
                                       (timeGap < 12.0 && visualSimilarity >= VisualSimilarityThreshold && !curr.IsSceneChange);

                    if (isSameScene)
                    {
                        currentCluster.Add(curr);
                    }
                    else
                    {
                        distinctClusters.Add(currentCluster);
                        currentCluster = new List<CandidateDetection> { curr };
                    }
                }
                distinctClusters.Add(currentCluster);

                // Convert each distinct cluster into 1 single high-quality bookmark
                foreach (var cluster in distinctClusters)
                {
                    // Select the peak representative frame with 2+ persons
                    var bestEvent = cluster.OrderByDescending(x => x.ImportanceScore)
                                           .ThenByDescending(x => x.PersonCount)
                                           .ThenByDescending(x => x.MotionRatio)
                                           .First();

                    double startTime = cluster.First().TimeSec;
                    double endTime = cluster.Last().TimeSec;
                    double duration = Math.Max(2.0, endTime - startTime);
                    double maxRatio = cluster.Max(x => x.MotionRatio);
                    int maxPersons = cluster.Max(x => x.PersonCount);

                    double normPos = durationSeconds > 0 ? Math.Clamp(bestEvent.TimeSec / durationSeconds, 0.0, 1.0) : 0;

                    // Generate clean annotated thumbnail with bounding boxes on all detected persons
                    var preview = CreateAnnotatedThumbnail(
                        bestEvent.RawFrame,
                        bestEvent.DetectedPersons,
                        bestEvent.IsGroupStruggle,
                        bestEvent.IsSceneChange);

                    results.Add(new MotionBookmark
                    {
                        TimeSeconds = bestEvent.TimeSec,
                        TimeText = FormatTime((long)bestEvent.TimeSec),
                        NormalizedPosition = normPos,
                        IntensityRatio = maxRatio,
                        Intensity = bestEvent.Intensity,
                        DurationSeconds = duration,
                        PersonCount = maxPersons,
                        PreviewImage = preview
                    });

                    // Dispose unneeded candidate resources
                    foreach (var c in cluster)
                    {
                        c.Hist?.Dispose();
                        c.RawFrame?.Dispose();
                    }
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
        Mat? originalFrame,
        List<DetectedPerson> persons,
        bool isGroupStruggle,
        bool isSceneChange)
    {
        if (originalFrame == null || originalFrame.Empty()) return null;

        try
        {
            using var preview = new Mat();
            int thumbW = 280;
            int thumbH = 158;
            Cv2.Resize(originalFrame, preview, new Size(thumbW, thumbH));

            double scaleX = (double)thumbW / originalFrame.Width;
            double scaleY = (double)thumbH / originalFrame.Height;

            // Draw bounding boxes on all detected persons in the group
            int personIdx = 1;
            foreach (var p in persons)
            {
                int bx = (int)(p.BoundingBox.X * scaleX);
                int by = (int)(p.BoundingBox.Y * scaleY);
                int bw = (int)(p.BoundingBox.Width * scaleX);
                int bh = (int)(p.BoundingBox.Height * scaleY);

                var boxColor = isGroupStruggle ? new Scalar(56, 56, 255)  // Red BGR
                                               : new Scalar(66, 177, 255); // Amber BGR

                Cv2.Rectangle(preview, new Rect(bx, by, bw, bh), boxColor, 2);

                string label = isGroupStruggle ? $"Vat lon #{personIdx}" : $"Nguoi #{personIdx}";
                Cv2.PutText(preview, label, new Point(bx, Math.Max(14, by - 4)),
                    HersheyFonts.HersheySimplex, 0.38, boxColor, 1, LineTypes.AntiAlias);
                personIdx++;
            }

            // Top-left indicator badge
            if (isGroupStruggle)
            {
                Cv2.Rectangle(preview, new Rect(6, 6, 96, 20), new Scalar(0, 0, 0), -1);
                Cv2.PutText(preview, $"XO XAT ({persons.Count}P)", new Point(10, 20), HersheyFonts.HersheySimplex, 0.35, new Scalar(56, 56, 255), 1);
            }
            else
            {
                Cv2.Rectangle(preview, new Rect(6, 6, 88, 20), new Scalar(0, 0, 0), -1);
                Cv2.PutText(preview, $"NHOM {persons.Count} NGUOI", new Point(10, 20), HersheyFonts.HersheySimplex, 0.35, new Scalar(66, 177, 255), 1);
            }

            return MatToBitmapSource(preview);
        }
        catch
        {
            return null;
        }
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

    private static string FormatTime(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private class CandidateDetection
    {
        public double TimeSec { get; init; }
        public MotionIntensity Intensity { get; init; }
        public double ImportanceScore { get; init; }
        public double MotionRatio { get; init; }
        public int PersonCount { get; init; }
        public Mat? Hist { get; init; }
        public Mat? RawFrame { get; init; }
        public List<DetectedPerson> DetectedPersons { get; init; } = new();
        public bool IsGroupStruggle { get; init; }
        public bool IsSceneChange { get; init; }
    }
}
