using System;
using System.Windows.Media.Imaging;

namespace WpfVideoPlayer.Models;

public enum MotionIntensity
{
    Light,
    Medium,
    High,
    SceneChange,
    PersonAction,
    GroupStruggle,
    GroupDetected,
    RandomSnapshot
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
    public int PersonCount { get; set; } = 0;
    public string CustomTitle { get; set; } = string.Empty;

    /// <summary>
    /// Frame thumbnail preview image for the bookmark (Frozen BitmapSource for safe WPF binding across threads).
    /// </summary>
    public BitmapSource? PreviewImage { get; set; }

    public string Icon => Intensity switch
    {
        MotionIntensity.RandomSnapshot => "🎲",
        MotionIntensity.GroupStruggle  => "🤼",
        MotionIntensity.PersonAction   => "🏃‍♂️",
        MotionIntensity.GroupDetected  => "👥",
        MotionIntensity.SceneChange    => "🎬",
        MotionIntensity.High           => "⚡",
        MotionIntensity.Medium         => "🚶",
        _                              => "👤"
    };

    public string BadgeColor => Intensity switch
    {
        MotionIntensity.RandomSnapshot => "#3B82F6", // Royal Blue
        MotionIntensity.GroupStruggle  => "#FF3838", // Vivid Red
        MotionIntensity.PersonAction   => "#FF793F", // Action Orange
        MotionIntensity.GroupDetected  => "#FFB142", // Amber / Group
        MotionIntensity.SceneChange    => "#706FD3", // Cinematic Purple
        MotionIntensity.High           => "#FF5252", // Bright Red
        MotionIntensity.Medium         => "#34ACE0", // Cyan Blue
        _                              => "#33D9B2"  // Mint
    };

    public string Title
    {
        get
        {
            if (!string.IsNullOrEmpty(CustomTitle))
                return CustomTitle;

            return Intensity switch
            {
                MotionIntensity.RandomSnapshot => "Khoảnh khắc ngẫu nhiên",
                MotionIntensity.GroupStruggle  => $"Nhóm người xô xát/vật lộn ({PersonCount} người)",
                MotionIntensity.PersonAction   => $"Hành động/Vật lộn mạnh ({PersonCount} người)",
                MotionIntensity.GroupDetected  => $"Nhóm {PersonCount} người xuất hiện",
                MotionIntensity.SceneChange    => $"Chuyển cảnh / Góc quay mới ({IntensityPercent})",
                MotionIntensity.High           => $"Chuyển động mạnh ({IntensityPercent})",
                MotionIntensity.Medium         => $"Chuyển động vừa ({IntensityPercent})",
                _                              => $"Chuyển động ({IntensityPercent})"
            };
        }
    }

    public string Description => $"{Icon} {Title} tại {TimeText}";
}
