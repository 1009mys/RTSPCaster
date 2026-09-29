using System;

namespace RTSPCaster.Models;

public enum StreamStatus
{
    Idle,
    Probing,
    Converting,
    Ready,
    Streaming,
    Stopping,
    Error
}

public class VideoFile
{
    public int Id { get; set; }
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    public double DurationSeconds { get; set; }
    public bool StreamCopyCompatible { get; set; }
    public string? IncompatibleReason { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Channel
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int VideoFileId { get; set; }
    public string RtspPath { get; set; } = string.Empty;
    public string MediaMtxHost { get; set; } = "127.0.0.1";
    public int MediaMtxPort { get; set; } = 8554;
    public string RtspUrl => $"rtsp://{MediaMtxHost}:{MediaMtxPort}/{RtspPath}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class ConversionCacheEntry
{
    public int Id { get; set; }
    public int SourceVideoFileId { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string ConvertedPath { get; set; } = string.Empty;
    public string SourceHash { get; set; } = string.Empty;
    public DateTime ConvertedAt { get; set; } = DateTime.UtcNow;
}

public class StreamHistory
{
    public int Id { get; set; }
    public int ChannelId { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public string Result { get; set; } = string.Empty;
    public string? Message { get; set; }
}

public class ProbeResult
{
    public string? VideoCodec { get; set; }
    public string? AudioCodec { get; set; }
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    public double DurationSeconds { get; set; }
    public bool StreamCopyCompatible { get; set; }
    public string? IncompatibleReason { get; set; }
    public string? RawJson { get; set; }
}
