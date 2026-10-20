namespace VRLink.Capture;

/// <summary>appsettings.json section "Capture".</summary>
public sealed class CaptureOptions
{
    /// <summary>Kestrel URL; localhost only (the footage API has no authentication).</summary>
    public string Urls { get; set; } = "http://localhost:5090";
    /// <summary>Segments, index, pins, offsets. Empty = %ProgramData%\VRLinkCapture.</summary>
    public string DataDir { get; set; } = "";
    /// <summary>ffmpeg.exe; empty = PATH (process, machine, user) and the WinGet links folder.</summary>
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public int SegmentSeconds { get; set; } = 10;
    /// <summary>Unpinned segments older than this are deleted.</summary>
    public int RetentionMinutes { get; set; } = 15;
    /// <summary>Total segment size cap; the oldest unpinned segments go first (pinned ones only as a last resort, logged).</summary>
    public double DiskCapGb { get; set; } = 20;
    public int RetentionCheckSeconds { get; set; } = 30;
    public List<CameraConfig> Cameras { get; set; } = new();

    public string ResolveDataDir() => string.IsNullOrWhiteSpace(DataDir)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "VRLinkCapture")
        : Environment.ExpandEnvironmentVariables(DataDir);
}

public sealed class CameraConfig
{
    public string Id { get; set; } = "cam1";
    public bool Enabled { get; set; } = true;
    /// <summary>"dshow" (USB camera + mic via DirectShow) or "test" (ffmpeg testsrc2 + wall clock text + 1 kHz beep at every UTC multiple of 5 s).</summary>
    public string Source { get; set; } = "test";
    /// <summary>DirectShow names as printed by GET /api/devices (ffmpeg -list_devices).</summary>
    public string VideoDevice { get; set; } = "";
    public string AudioDevice { get; set; } = "";
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public int Fps { get; set; } = 30;
    /// <summary>Extra dshow input options, e.g. "-vcodec mjpeg" for 4K webcams (space separated).</summary>
    public string InputArgs { get; set; } = "";
    /// <summary>Video encoder options (space separated). For 4K on an NVIDIA GPU e.g. "-c:v h264_nvenc -preset p4 -cq 23".</summary>
    public string VideoCodecArgs { get; set; } = "-c:v libx264 -preset veryfast -crf 23";
}
