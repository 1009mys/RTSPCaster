using System.ComponentModel.DataAnnotations;
using RTSPCaster.Models;

namespace RTSPCaster.Backend.Contracts;

public sealed record CasterSettings
{
    [Required, StringLength(253)] public string MediaMtxHost { get; init; } = "127.0.0.1";
    [Range(1, 65535)] public int MediaMtxPort { get; init; } = 8554;
    [Required, StringLength(1024)] public string BulkRtspTemplate { get; init; } = "rtsp://{host}:{port}/stream_{index}";
    public bool AutoRestartEnabled { get; init; } = true;
    [Range(0, 20)] public int MaxAutoRestartAttempts { get; init; } = 3;
    [Range(1, 120)] public int AutoRestartBaseDelaySeconds { get; init; } = 2;
    [Range(5, 3600)] public int AutoRestartResetThresholdSeconds { get; init; } = 30;
}

public sealed record ChannelEndpointRequest(
    [Required, StringLength(512)] string RtspPath,
    [Required, StringLength(253)] string MediaMtxHost,
    [Range(1, 65535)] int MediaMtxPort);

public sealed record TemplateRequest([Required, StringLength(1024)] string Template);
public sealed record VideoDetails(int Id, string FileName, long FileSize, string? VideoCodec,
    string? AudioCodec, int VideoWidth, int VideoHeight, double DurationSeconds,
    bool StreamCopyCompatible, string? IncompatibleReason);
public sealed record HealthSample(DateTime Timestamp, double Fps, double BitrateKbps, double Speed, double? LatencyMs);
public sealed record ChannelSnapshot(int Id, string Name, string RtspPath, string MediaMtxHost,
    int MediaMtxPort, string RtspUrl, VideoDetails Video, StreamStatus Status, string StatusMessage,
    double ConversionProgress, bool OperationInProgress, HealthSample[] HealthSamples);
public sealed record LogEntry(long Id, DateTime Timestamp, int? ChannelId, string Message);
public sealed record LogPage(long LastId, LogEntry[] Entries);
public sealed record MediaMtxConnection(string Host, int Port, bool? Reachable, DateTime? CheckedAt);
public sealed record CasterSnapshot(CasterSettings Settings, MediaMtxConnection MediaMtx,
    ChannelSnapshot[] Channels, LogPage Logs);
public sealed record ChannelActionResult(int ChannelId, bool Accepted, string? Error);
public sealed record TemplateResult(int[] Updated, int[] Skipped);
public sealed record UploadResult(string FileName, ChannelSnapshot? Channel, string? Error);
