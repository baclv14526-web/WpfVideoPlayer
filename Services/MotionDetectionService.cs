using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using WpfVideoPlayer.Models;

namespace WpfVideoPlayer.Services;

public class MotionDetectionService
{
    private const int AnalysisWidth = 640;
    private const int AnalysisHeight = 360;
    private const double SampleIntervalSeconds = 0.5; // Analyze 2 frames per second for high speed
    private const double SceneChangeHistThreshold = 0.52; // Histogram correlation < 0.52 indicates scene cut
    private const double MinBookmarkDistanceSeconds = 6.0; // Minimum time gap between similar bookmarks
    private const double VisualSimilarityThreshold = 0.72; // Hist correlation >= 0.72 means same visual scene
    private const int MinRequiredPersons = 2; // ONLY bookmark moments with 2 or more people

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

    /// <summary>
    /// Opens VideoCapture safely with FFMPEG / Unicode fallback.
    /// </summary>
    private static VideoCapture? OpenVideoCapture(string videoPath)
    {
        if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
            return null;

        // 1. Try standard open
        try
        {
            var cap = new VideoCapture(videoPath, VideoCaptureAPIs.FFMPEG);
            if (cap.IsOpened()) return cap;
            cap.Dispose();
        }
        catch { }

        // 2. Try default backend
        try
        {
            var cap = new VideoCapture(videoPath);
            if (cap.IsOpened()) return cap;
            cap.Dispose();
        }
        catch { }

        // 3. Try Windows short path (8.3 ASCII for Vietnamese / Unicode names)
        try
        {
            string shortPath = GetSafeAsciiPath(videoPath);
            if (!string.Equals(shortPath, videoPath, StringComparison.OrdinalIgnoreCase))
            {
                var cap = new VideoCapture(shortPath, VideoCaptureAPIs.FFMPEG);
                if (cap.IsOpened()) return cap;
                cap.Dispose();

                cap = new VideoCapture(shortPath);
                if (cap.IsOpened()) return cap;
                cap.Dispose();
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// Analyzes video using YOLO Small and OpenCV, ONLY bookmarking distinct moments 
    /// containing 2 or more persons (struggles, wrestling, group interactions) with image previews.
    /// </summary>
    public async Task<ScanResult> ScanVideoAsync(
        string videoPath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var bookmarks = new List<MotionBookmark>();

            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                return new ScanResult(bookmarks, 0, false, "Tệp không tồn tại");

            using var capture = OpenVideoCapture(videoPath);
            if (capture == null || !capture.IsOpened())
            {
                return new ScanResult(bookmarks, 0, false, "Không thể mở luồng giải mã video (Codec hoặc định dạng không hỗ trợ)");
            }

            int totalFrames = (int)capture.Get(VideoCaptureProperties.FrameCount);
            double fps = capture.Get(VideoCaptureProperties.Fps);
            if (fps <= 0 || double.IsNaN(fps)) fps = 30.0;

            double durationSeconds = 0;
            if (totalFrames > 0)
            {
                durationSeconds = totalFrames / fps;
            }
            else
            {
                // Fallback duration estimation from VideoCapture
                double msec = capture.Get(VideoCaptureProperties.PosMsec);
                if (msec > 0) durationSeconds = msec / 1000.0;
            }

            // Optimize scan speed for medium-spec laptops: scan 3 to 5 frames/sec, jumping 6 to 10 frames per step
            int frameStep = Math.Clamp((int)Math.Round(fps / 4.0), 6, 10);

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

            int framesProcessed = 0;
            int totalReadAttempts = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Stop condition when totalFrames is known
                if (totalFrames > 0 && totalReadAttempts >= totalFrames)
                    break;

                if (!capture.Read(frame) || frame.Empty())
                {
                    // If read returns false, we have reached the end of stream
                    break;
                }

                totalReadAttempts++;
                framesProcessed++;

                double timeSec = (totalReadAttempts - 1) / fps;

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
                var detectedPersons = yoloDetector.DetectPersons(resized, confThreshold: 0.28f);
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

                            if (iou > 0.06f || distance < (detectedPersons[i].BoundingBox.Width + detectedPersons[j].BoundingBox.Width) * 0.50)
                            {
                                hasBoxOverlap = true;
                                break;
                            }
                        }
                        if (hasBoxOverlap) break;
                    }

                    if (hasBoxOverlap && motionRatio >= 0.02)
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

                // Skip next (frameStep - 1) frames quickly via Grab()
                for (int s = 1; s < frameStep; s++)
                {
                    if (totalFrames > 0 && totalReadAttempts >= totalFrames)
                        break;

                    if (!capture.Grab()) break;
                    totalReadAttempts++;
                }

                // Report progress
                if (totalFrames > 0)
                {
                    double progressPercent = Math.Min(99.0, (totalReadAttempts / (double)totalFrames) * 100.0);
                    progress?.Report(progressPercent);
                }
            }

            if (durationSeconds <= 0 && framesProcessed > 0)
            {
                durationSeconds = totalReadAttempts / fps;
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

                    bookmarks.Add(new MotionBookmark
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

            return new ScanResult(bookmarks, framesProcessed, true, string.Empty);
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

            // Draw bounding boxes on all detected persons/body parts in the group
            int personIdx = 1;
            foreach (var p in persons)
            {
                int bx = (int)(p.BoundingBox.X * scaleX);
                int by = (int)(p.BoundingBox.Y * scaleY);
                int bw = (int)(p.BoundingBox.Width * scaleX);
                int bh = (int)(p.BoundingBox.Height * scaleY);

                var boxColor = isGroupStruggle || p.Part == DetectedBodyPart.GrapplingPose
                             ? new Scalar(56, 56, 255)  // Red BGR
                             : p.Part == DetectedBodyPart.UpperBody
                             ? new Scalar(63, 121, 255) // Orange BGR
                             : new Scalar(66, 177, 255); // Amber BGR

                Cv2.Rectangle(preview, new Rect(bx, by, bw, bh), boxColor, 2);

                string label = isGroupStruggle ? $"Vat lon #{personIdx}"
                             : p.Part == DetectedBodyPart.GrapplingPose ? $"Tu the vat #{personIdx}"
                             : p.Part == DetectedBodyPart.UpperBody ? $"Ban than #{personIdx}"
                             : $"Nguoi #{personIdx}";

                Cv2.PutText(preview, label, new Point(bx, Math.Max(14, by - 4)),
                    HersheyFonts.HersheySimplex, 0.38, boxColor, 1, LineTypes.AntiAlias);
                personIdx++;
            }

            // Top-left indicator badge
            if (isGroupStruggle)
            {
                Cv2.Rectangle(preview, new Rect(6, 6, 106, 20), new Scalar(0, 0, 0), -1);
                Cv2.PutText(preview, $"XO XAT ({persons.Count}P)", new Point(10, 20), HersheyFonts.HersheySimplex, 0.35, new Scalar(56, 56, 255), 1);
            }
            else
            {
                bool hasGrapple = persons.Any(x => x.Part == DetectedBodyPart.GrapplingPose);
                string badgeText = hasGrapple ? $"VAT LON ({persons.Count}P)" : $"NHOM {persons.Count} NGUOI";
                int badgeW = hasGrapple ? 98 : 92;

                Cv2.Rectangle(preview, new Rect(6, 6, badgeW, 20), new Scalar(0, 0, 0), -1);
                Cv2.PutText(preview, badgeText, new Point(10, 20), HersheyFonts.HersheySimplex, 0.35, new Scalar(66, 177, 255), 1);
            }

            return MatToBitmapSource(preview);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Rapidly creates N random bookmarks across the video timeline with preview thumbnails (without using YOLO AI).
    /// </summary>
    public async Task<List<MotionBookmark>> GenerateRandomBookmarksAsync(
        string videoPath,
        int count = 10,
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var bookmarks = new List<MotionBookmark>();
            if (string.IsNullOrEmpty(videoPath) || !File.Exists(videoPath))
                return bookmarks;

            using var capture = OpenVideoCapture(videoPath);
            if (capture == null || !capture.IsOpened())
                return bookmarks;

            int totalFrames = (int)capture.Get(VideoCaptureProperties.FrameCount);
            double fps = capture.Get(VideoCaptureProperties.Fps);
            if (fps <= 0 || double.IsNaN(fps)) fps = 30.0;

            double durationSeconds = totalFrames > 0 ? totalFrames / fps : 0;
            if (durationSeconds <= 0)
            {
                double msec = capture.Get(VideoCaptureProperties.PosMsec);
                if (msec > 0) durationSeconds = msec / 1000.0;
            }

            if (totalFrames <= 0) totalFrames = (int)(durationSeconds * fps);
            if (totalFrames <= 0) totalFrames = 3000;
            if (durationSeconds <= 0) durationSeconds = totalFrames / fps;

            var random = new Random();
            var targetFrames = new List<int>();

            int startFrame = (int)(totalFrames * 0.05);
            int endFrame = (int)(totalFrames * 0.95);
            if (endFrame <= startFrame)
            {
                startFrame = 0;
                endFrame = Math.Max(1, totalFrames);
            }

            double segmentSize = (double)(endFrame - startFrame) / count;
            for (int i = 0; i < count; i++)
            {
                int segStart = startFrame + (int)(i * segmentSize);
                int segEnd = startFrame + (int)((i + 1) * segmentSize);
                int randFrame = random.Next(segStart, Math.Max(segStart + 1, segEnd));
                targetFrames.Add(randFrame);
            }

            targetFrames.Sort();

            using var frame = new Mat();
            using var preview = new Mat();
            int thumbW = 280;
            int thumbH = 158;

            int bookmarkIndex = 1;
            foreach (int f in targetFrames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                capture.Set(VideoCaptureProperties.PosFrames, f);
                if (!capture.Read(frame) || frame.Empty())
                    continue;

                double timeSec = f / fps;
                double normPos = durationSeconds > 0 ? Math.Clamp(timeSec / durationSeconds, 0.0, 1.0) : 0;

                Cv2.Resize(frame, preview, new Size(thumbW, thumbH));

                // Draw decorative badge
                Cv2.Rectangle(preview, new Rect(6, 6, 96, 20), new Scalar(0, 0, 0), -1);
                Cv2.PutText(preview, $"NGAU NHIEN #{bookmarkIndex}", new Point(10, 20),
                    HersheyFonts.HersheySimplex, 0.35, new Scalar(246, 130, 59), 1); // Blue BGR

                var previewBmp = MatToBitmapSource(preview);

                bookmarks.Add(new MotionBookmark
                {
                    TimeSeconds = timeSec,
                    TimeText = FormatTime((long)timeSec),
                    NormalizedPosition = normPos,
                    Intensity = MotionIntensity.RandomSnapshot,
                    IntensityRatio = 0.5,
                    DurationSeconds = 3.0,
                    PersonCount = 0,
                    CustomTitle = $"Mốc ngẫu nhiên #{bookmarkIndex}",
                    PreviewImage = previewBmp
                });

                bookmarkIndex++;
            }

            return bookmarks;
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

public record ScanResult(
    List<MotionBookmark> Bookmarks,
    int FramesProcessed,
    bool Success,
    string ErrorMessage);
