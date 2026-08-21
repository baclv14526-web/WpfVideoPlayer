using System;

namespace WpfVideoPlayer.Models;

public enum MotionIntensity
{
    Light,
    Medium,
    High,
    SceneChange
}

public class MotionBookmark
{
    public double TimeSeconds { get; set; }
    public string TimeText { get; set; } = "00:00";
    public double NormalizedPosition { get; set; } // 0.0 to 1.0 for Timeline placement
    public double IntensityRatio { get; set; }
    public string IntensityPercent => $"{IntensityRatio * 100:0.#}%";
    public MotionIntensity Intensity { get; set; } = MotionIntensity.Medium;
    public double DurationSeconds { get; set; } = 2.0;

    public string Icon => Intensity switch
    {
        MotionIntensity.Light => "🚶",
        MotionIntensity.Medium => "🏃",
        MotionIntensity.High => "🏃‍♂️",
        MotionIntensity.SceneChange => "⚡",
        _ => "🎬"
    };

    public string BadgeColor => Intensity switch
    {
        MotionIntensity.Light => "#2ED573",      // Green
        MotionIntensity.Medium => "#FFA502",     // Orange
        MotionIntensity.High => "#FF4757",       // Red/Coral
        MotionIntensity.SceneChange => "#6C63FF", // Purple Accent
        _ => "#70A1FF"
    };

    public string Title => Intensity switch
    {
        MotionIntensity.Light => $"Chuyển động nhẹ ({IntensityPercent})",
        MotionIntensity.Medium => $"Chuyển động vừa ({IntensityPercent})",
        MotionIntensity.High => $"Chuyển động mạnh ({IntensityPercent})",
        MotionIntensity.SceneChange => $"Đổi cảnh / Biến đổi lớn ({IntensityPercent})",
        _ => $"Chuyển động ({IntensityPercent})"
    };

    public string Description => $"{Icon} {Title} tại {TimeText}";
}
