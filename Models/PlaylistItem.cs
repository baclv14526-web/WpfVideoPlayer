using System.IO;

namespace WpfVideoPlayer.Models;

public class PlaylistItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public string FileNameWithoutExt => Path.GetFileNameWithoutExtension(FilePath);
    public string Duration { get; set; } = "--:--";
    public bool IsCurrentlyPlaying { get; set; }
}
