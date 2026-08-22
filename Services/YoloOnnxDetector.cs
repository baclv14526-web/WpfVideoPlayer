using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;

namespace WpfVideoPlayer.Services;

public enum DetectedBodyPart
{
    FullBody,
    UpperBody,
    HeadFace,
    GrapplingPose,
    PartialBody
}

public record DetectedPerson(
    Rect BoundingBox,
    float Confidence,
    DetectedBodyPart Part = DetectedBodyPart.FullBody,
    string Label = "Người");

public class YoloOnnxDetector : IDisposable
{
    private InferenceSession? _session;
    private string? _inputName;
    private int _inputWidth = 640;
    private int _inputHeight = 640;
    private bool _isDisposed;
    private readonly HOGDescriptor? _hogFallback;

    public bool IsModelLoaded => _session != null;

    public YoloOnnxDetector(string? modelPath = null)
    {
        // Try locating ONNX model (prioritizing YOLOv11)
        string? targetModel = modelPath;
        if (string.IsNullOrEmpty(targetModel) || !File.Exists(targetModel))
        {
            var searchPaths = new[]
            {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "yolo11n.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "yolo11s.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "yolov11n.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "yolov11s.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "yolo11.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "yolov11.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo11n.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo11s.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolov11n.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolov11s.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "yolov8s.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "yolov8n.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "models", "yolo_small.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolov8s.onnx"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolov8n.onnx")
            };

            targetModel = searchPaths.FirstOrDefault(File.Exists);
        }

        if (!string.IsNullOrEmpty(targetModel) && File.Exists(targetModel))
        {
            try
            {
                var options = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 4) // Laptop CPU optimization
                };
                _session = new InferenceSession(targetModel, options);
                _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? "images";

                var shape = _session.InputMetadata[_inputName].Dimensions;
                if (shape.Length == 4)
                {
                    _inputHeight = shape[2] > 0 ? shape[2] : 640;
                    _inputWidth = shape[3] > 0 ? shape[3] : 640;
                }
            }
            catch
            {
                _session = null;
            }
        }

        // Initialize OpenCV HOG person detector fallback
        try
        {
            _hogFallback = new HOGDescriptor();
            _hogFallback.SetSVMDetector(HOGDescriptor.GetDefaultPeopleDetector());
        }
        catch
        {
            _hogFallback = null;
        }
    }

    /// <summary>
    /// Detects persons and human body parts across varied poses (standing, sitting, lying down, wrestling, close-ups).
    /// </summary>
    public List<DetectedPerson> DetectPersons(Mat frame, float confThreshold = 0.25f, float iouThreshold = 0.45f)
    {
        if (frame.Empty()) return new List<DetectedPerson>();

        if (_session != null && !string.IsNullOrEmpty(_inputName))
        {
            try
            {
                return RunYoloInference(frame, confThreshold, iouThreshold);
            }
            catch
            {
                // Fallback to Multi-Pose OpenCV Detector
            }
        }

        // Multi-Part & Multi-Pose OpenCV Detector Fallback
        return RunMultiPartPoseFallback(frame);
    }

    private List<DetectedPerson> RunYoloInference(Mat frame, float confThreshold, float iouThreshold)
    {
        int origW = frame.Width;
        int origH = frame.Height;

        float scale = Math.Min((float)_inputWidth / origW, (float)_inputHeight / origH);
        int newW = (int)(origW * scale);
        int newH = (int)(origH * scale);
        int padX = (_inputWidth - newW) / 2;
        int padY = (_inputHeight - newH) / 2;

        using var resized = new Mat();
        Cv2.Resize(frame, resized, new Size(newW, newH));

        using var canvas = new Mat(new Size(_inputWidth, _inputHeight), MatType.CV_8UC3, new Scalar(114, 114, 114));
        var roi = new Rect(padX, padY, newW, newH);
        resized.CopyTo(new Mat(canvas, roi));

        // Convert to RGB float tensor [1, 3, H, W] normalized to [0, 1]
        using var rgb = new Mat();
        Cv2.CvtColor(canvas, rgb, ColorConversionCodes.BGR2RGB);

        var tensor = new DenseTensor<float>(new[] { 1, 3, _inputHeight, _inputWidth });
        unsafe
        {
            byte* dataPtr = (byte*)rgb.DataPointer;
            int step = (int)rgb.Step();

            for (int y = 0; y < _inputHeight; y++)
            {
                byte* row = dataPtr + y * step;
                for (int x = 0; x < _inputWidth; x++)
                {
                    int pixelIdx = x * 3;
                    tensor[0, 0, y, x] = row[pixelIdx] / 255.0f;     // R
                    tensor[0, 1, y, x] = row[pixelIdx + 1] / 255.0f; // G
                    tensor[0, 2, y, x] = row[pixelIdx + 2] / 255.0f; // B
                }
            }
        }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputName!, tensor)
        };

        using var outputs = _session!.Run(inputs);
        var outputTensor = outputs.First().Value as DenseTensor<float>;
        if (outputTensor == null) return new List<DetectedPerson>();

        var candidates = new List<(Rect Box, float Score)>();
        var dimensions = outputTensor.Dimensions;

        // YOLOv8 output: [1, 84, 8400] (class 0 is person)
        if (dimensions.Length == 3 && dimensions[1] >= 5 && dimensions[2] > 100)
        {
            int numPredictions = dimensions[2];

            for (int i = 0; i < numPredictions; i++)
            {
                float personScore = outputTensor[0, 4, i]; // Class 0 = person
                if (personScore >= confThreshold)
                {
                    float cx = outputTensor[0, 0, i];
                    float cy = outputTensor[0, 1, i];
                    float w = outputTensor[0, 2, i];
                    float h = outputTensor[0, 3, i];

                    float left = (cx - w / 2f - padX) / scale;
                    float top = (cy - h / 2f - padY) / scale;
                    float width = w / scale;
                    float height = h / scale;

                    int x = Math.Clamp((int)left, 0, origW - 1);
                    int y = Math.Clamp((int)top, 0, origH - 1);
                    int boxW = Math.Clamp((int)width, 1, origW - x);
                    int boxH = Math.Clamp((int)height, 1, origH - y);

                    candidates.Add((new Rect(x, y, boxW, boxH), personScore));
                }
            }
        }
        // YOLOv5 output: [1, 25200, 85]
        else if (dimensions.Length == 3 && dimensions[2] >= 85)
        {
            int numPredictions = dimensions[1];
            for (int i = 0; i < numPredictions; i++)
            {
                float objConf = outputTensor[0, i, 4];
                float personConf = outputTensor[0, i, 5];
                float score = objConf * personConf;

                if (score >= confThreshold)
                {
                    float cx = outputTensor[0, i, 0];
                    float cy = outputTensor[0, i, 1];
                    float w = outputTensor[0, i, 2];
                    float h = outputTensor[0, i, 3];

                    float left = (cx - w / 2f - padX) / scale;
                    float top = (cy - h / 2f - padY) / scale;
                    float width = w / scale;
                    float height = h / scale;

                    int x = Math.Clamp((int)left, 0, origW - 1);
                    int y = Math.Clamp((int)top, 0, origH - 1);
                    int boxW = Math.Clamp((int)width, 1, origW - x);
                    int boxH = Math.Clamp((int)height, 1, origH - y);

                    candidates.Add((new Rect(x, y, boxW, boxH), score));
                }
            }
        }

        // Apply NMS & categorize pose aspect ratio
        var filtered = ApplyNms(candidates, iouThreshold);
        var results = new List<DetectedPerson>();

        foreach (var p in filtered)
        {
            double aspectRatio = (double)p.BoundingBox.Width / Math.Max(1, p.BoundingBox.Height);
            var part = DetectedBodyPart.FullBody;
            string label = "Người";

            if (aspectRatio > 1.25)
            {
                part = DetectedBodyPart.GrapplingPose;
                label = "Tư thế nằm/vật lộn";
            }
            else if (aspectRatio > 0.85)
            {
                part = DetectedBodyPart.UpperBody;
                label = "Bán thân/Ngồi";
            }
            else if (p.BoundingBox.Height < origH * 0.35)
            {
                part = DetectedBodyPart.PartialBody;
                label = "Bộ phận cơ thể";
            }

            results.Add(new DetectedPerson(p.BoundingBox, p.Confidence, part, label));
        }

        return results;
    }

    /// <summary>
    /// Multi-part fallback detector: Combines Full-body HOG, Upper-body/Torso, and horizontal pose analysis.
    /// </summary>
    private List<DetectedPerson> RunMultiPartPoseFallback(Mat frame)
    {
        var rawBoxes = new List<(Rect Box, float Score, DetectedBodyPart Part, string Label)>();
        if (frame.Empty()) return new List<DetectedPerson>();

        int origW = frame.Width;
        int origH = frame.Height;

        // 1. Full Body & Upper Body Scale
        try
        {
            if (_hogFallback != null)
            {
                using var resized = new Mat();
                int targetW = 800;
                double scale = (double)targetW / origW;
                int targetH = (int)(origH * scale);
                Cv2.Resize(frame, resized, new Size(targetW, targetH));

                Rect[] hogBoxes = _hogFallback.DetectMultiScale(resized);
                foreach (var b in hogBoxes)
                {
                    int x = Math.Clamp((int)(b.X / scale), 0, origW - 1);
                    int y = Math.Clamp((int)(b.Y / scale), 0, origH - 1);
                    int w = Math.Clamp((int)(b.Width / scale), 1, origW - x);
                    int h = Math.Clamp((int)(b.Height / scale), 1, origH - y);

                    rawBoxes.Add((new Rect(x, y, w, h), 0.80f, DetectedBodyPart.FullBody, "Người"));
                }
            }
        }
        catch { }

        // 2. Pose & Human-body Shape Analysis (Detects wrestlers on ground, horizontal, crouching, upper bodies)
        try
        {
            using var gray = new Mat();
            using var blurred = new Mat();
            using var thresh = new Mat();

            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.GaussianBlur(gray, blurred, new Size(9, 9), 0);
            Cv2.AdaptiveThreshold(blurred, thresh, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 19, 5);

            // Morphological close to connect body parts (arms, legs, head, torso)
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(13, 13));
            Cv2.MorphologyEx(thresh, thresh, MorphTypes.Close, kernel);

            Cv2.FindContours(thresh, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            int minBodyArea = (origW * origH) / 60;   // At least ~1.6% of screen
            int maxBodyArea = (origW * origH) * 8 / 10; // At most 80%

            foreach (var contour in contours)
            {
                var rect = Cv2.BoundingRect(contour);
                int area = rect.Width * rect.Height;

                if (area >= minBodyArea && area <= maxBodyArea)
                {
                    double aspect = (double)rect.Width / Math.Max(1, rect.Height);

                    // Typical human body or grappling pose aspects: 0.25 (standing) to 2.5 (lying/wrestling)
                    if (aspect >= 0.25 && aspect <= 2.8)
                    {
                        var part = aspect > 1.2 ? DetectedBodyPart.GrapplingPose
                                 : aspect > 0.8 ? DetectedBodyPart.UpperBody
                                 : DetectedBodyPart.FullBody;

                        string label = aspect > 1.2 ? "Vật lộn/Tư thế ngang" : "Người/Thân";
                        rawBoxes.Add((rect, 0.70f, part, label));
                    }
                }
            }
        }
        catch { }

        // 3. Fuse overlapping boxes (Hierarchical Non-Maximum Suppression)
        var sorted = rawBoxes.OrderByDescending(x => x.Score).ThenByDescending(x => x.Box.Width * x.Box.Height).ToList();
        var finalDetections = new List<DetectedPerson>();

        while (sorted.Count > 0)
        {
            var best = sorted[0];
            finalDetections.Add(new DetectedPerson(best.Box, best.Score, best.Part, best.Label));
            sorted.RemoveAt(0);

            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                float iou = ComputeIoU(best.Box, sorted[i].Box);
                bool isEnclosed = best.Box.Contains(new Point(sorted[i].Box.X + sorted[i].Box.Width / 2, sorted[i].Box.Y + sorted[i].Box.Height / 2));

                if (iou > 0.35f || isEnclosed)
                {
                    sorted.RemoveAt(i);
                }
            }
        }

        return finalDetections;
    }

    private static List<DetectedPerson> ApplyNms(List<(Rect Box, float Score)> candidates, float iouThreshold)
    {
        var result = new List<DetectedPerson>();
        var sorted = candidates.OrderByDescending(c => c.Score).ToList();

        while (sorted.Count > 0)
        {
            var best = sorted[0];
            result.Add(new DetectedPerson(best.Box, best.Score));
            sorted.RemoveAt(0);

            for (int i = sorted.Count - 1; i >= 0; i--)
            {
                float iou = ComputeIoU(best.Box, sorted[i].Box);
                if (iou > iouThreshold)
                {
                    sorted.RemoveAt(i);
                }
            }
        }

        return result;
    }

    public static float ComputeIoU(Rect a, Rect b)
    {
        int interX1 = Math.Max(a.Left, b.Left);
        int interY1 = Math.Max(a.Top, b.Top);
        int interX2 = Math.Min(a.Right, b.Right);
        int interY2 = Math.Min(a.Bottom, b.Bottom);

        int interW = Math.Max(0, interX2 - interX1);
        int interH = Math.Max(0, interY2 - interY1);
        int interArea = interW * interH;

        int unionArea = (a.Width * a.Height) + (b.Width * b.Height) - interArea;
        return unionArea > 0 ? (float)interArea / unionArea : 0f;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _session?.Dispose();
            _session = null;
            _hogFallback?.Dispose();
            _isDisposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
