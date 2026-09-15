using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using RTSPCaster.Models;

namespace RTSPCaster.Services;

public class ChannelStreamContext
{
    public int ChannelId { get; init; }
    public Process? Process { get; set; }
    public CancellationTokenSource Cts { get; } = new();
    public StreamStatus Status { get; set; } = StreamStatus.Idle;
    public int HistoryId { get; set; }
    public bool StreamCopyFailedEarly { get; set; }
    public bool RtspBadRequestOnHeader { get; set; }
    public DateTime StartedAt { get; set; }
}

public class StreamEventArgs : EventArgs
{
    public int ChannelId { get; init; }
    public StreamStatus Status { get; init; }
    public string? Message { get; init; }
}

public class StreamingService : IDisposable
{
    private readonly ConcurrentDictionary<int, ChannelStreamContext> _channels = new();
    private readonly ChildProcessTracker _tracker;
    private readonly SqliteService _db;

    public string FfmpegPath { get; set; } = ToolLocator.Find("ffmpeg.exe") ?? "ffmpeg.exe";

    public event EventHandler<StreamEventArgs>? StatusChanged;
    public event EventHandler<(int ChannelId, string Line)>? Log;

    public StreamingService(SqliteService db, ChildProcessTracker tracker)
    {
        _db = db;
        _tracker = tracker;
    }

    public bool IsStreaming(int channelId) =>
        _channels.TryGetValue(channelId, out var ctx) &&
        (ctx.Status == StreamStatus.Streaming || ctx.Status == StreamStatus.Stopping);

    public Task StartAsync(Channel channel, string sourceFilePath)
    {
        if (_channels.ContainsKey(channel.Id))
            throw new InvalidOperationException("Channel already streaming.");

        var ctx = new ChannelStreamContext { ChannelId = channel.Id, StartedAt = DateTime.UtcNow };
        _channels[channel.Id] = ctx;
        SetStatus(ctx, StreamStatus.Streaming, "Starting stream copy");

        var args = $"-re -stream_loop -1 -i \"{sourceFilePath}\" -c copy -f rtsp -rtsp_transport tcp \"{channel.RtspUrl}\"";

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = args,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        ctx.Process = proc;
        ctx.HistoryId = _db.StartHistory(channel.Id);

        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Log?.Invoke(this, (channel.Id, e.Data));
            if (DetectStreamCopyFailure(e.Data))
                ctx.StreamCopyFailedEarly = true;
            if (DetectRtspBadRequest(e.Data))
                ctx.RtspBadRequestOnHeader = true;
        };
        proc.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Log?.Invoke(this, (channel.Id, e.Data));
        };
        proc.Exited += (_, _) => HandleExit(channel, ctx);

        proc.Start();
        proc.BeginErrorReadLine();
        proc.BeginOutputReadLine();
        _tracker.Track(proc);
        return Task.CompletedTask;
    }

    private void HandleExit(Channel channel, ChannelStreamContext ctx)
    {
        var exitCode = ctx.Process?.ExitCode ?? -1;
        var wasCancelled = ctx.Cts.IsCancellationRequested;
        var earlyFail = ctx.StreamCopyFailedEarly && (DateTime.UtcNow - ctx.StartedAt).TotalSeconds < 5;

        string result;
        string? message;
        if (wasCancelled)
        {
            result = "stopped";
            message = "User stopped";
        }
        else if (ctx.RtspBadRequestOnHeader)
        {
            result = "error";
            message = "RTSP server rejected publish request (400 Bad Request). Check duplicate path or publish permission.";
            SetStatus(ctx, StreamStatus.Error, message);
        }
        else if (earlyFail)
        {
            result = "error";
            message = $"stream copy failed (exit {exitCode}). Requires pre-conversion.";
            SetStatus(ctx, StreamStatus.Error, message);
        }
        else if (exitCode != 0)
        {
            result = "error";
            message = $"streaming failed (exit {exitCode}).";
            SetStatus(ctx, StreamStatus.Error, message);
        }
        else
        {
            result = "ended";
            message = $"exit {exitCode}";
        }

        _db.EndHistory(ctx.HistoryId, result, message);
        if (ctx.Status != StreamStatus.Error)
            SetStatus(ctx, StreamStatus.Idle, message);
        _channels.TryRemove(channel.Id, out _);
    }

    private static bool DetectStreamCopyFailure(string line)
    {
        // Common ffmpeg stream-copy failure signals
        if (line.Contains("Could not find tag for codec", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("codec not currently supported in container", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Non-monotonous DTS", StringComparison.OrdinalIgnoreCase)) return false; // warning
        if (line.Contains("bitstream malformed", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("Error muxing a packet", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool DetectRtspBadRequest(string line)
    {
        return line.Contains("Could not write header", StringComparison.OrdinalIgnoreCase)
               && line.Contains("400 Bad Request", StringComparison.OrdinalIgnoreCase);
    }

    public void Stop(int channelId)
    {
        if (!_channels.TryGetValue(channelId, out var ctx)) return;
        SetStatus(ctx, StreamStatus.Stopping, "Stopping");
        try { ctx.Cts.Cancel(); } catch { }
        try
        {
            if (ctx.Process is { HasExited: false } p)
            {
                p.Kill(true);
                p.WaitForExit(3000);
            }
        }
        catch { }
    }

    public void StopAll()
    {
        foreach (var id in _channels.Keys)
            Stop(id);
    }

    private void SetStatus(ChannelStreamContext ctx, StreamStatus status, string? message)
    {
        ctx.Status = status;
        StatusChanged?.Invoke(this, new StreamEventArgs { ChannelId = ctx.ChannelId, Status = status, Message = message });
    }

    public void Dispose() => StopAll();
}
