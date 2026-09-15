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
    public string SourceFilePath { get; init; } = string.Empty;
    public Process? Process { get; set; }
    public CancellationTokenSource Cts { get; } = new();
    public StreamStatus Status { get; set; } = StreamStatus.Idle;
    public int HistoryId { get; set; }
    public int AutoRestartAttempts { get; set; }
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
    public bool AutoRestartEnabled { get; set; } = true;
    public int MaxAutoRestartAttempts { get; set; } = 3;
    public TimeSpan AutoRestartBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan AutoRestartAttemptResetThreshold { get; set; } = TimeSpan.FromSeconds(30);

    public event EventHandler<StreamEventArgs>? StatusChanged;
    public event EventHandler<(int ChannelId, string Line)>? Log;

    public StreamingService(SqliteService db, ChildProcessTracker tracker)
    {
        _db = db;
        _tracker = tracker;
    }

    public bool IsStreaming(int channelId) =>
        _channels.TryGetValue(channelId, out var ctx) &&
        (ctx.Status == StreamStatus.Streaming || ctx.Status == StreamStatus.Stopping || ctx.Status == StreamStatus.Ready);

    public Task StartAsync(Channel channel, string sourceFilePath)
    {
        if (_channels.ContainsKey(channel.Id))
            throw new InvalidOperationException("Channel already streaming.");

        var ctx = new ChannelStreamContext
        {
            ChannelId = channel.Id,
            SourceFilePath = sourceFilePath,
            StartedAt = DateTime.UtcNow
        };
        _channels[channel.Id] = ctx;

        if (!TryStartProcess(channel, ctx, restartMessage: "Starting stream copy", out var startError))
        {
            _channels.TryRemove(channel.Id, out _);
            throw new InvalidOperationException(startError ?? "Failed to start stream.");
        }
        return Task.CompletedTask;
    }

    private bool TryStartProcess(Channel channel, ChannelStreamContext ctx, string restartMessage, out string? error)
    {
        error = null;
        ctx.StartedAt = DateTime.UtcNow;
        ctx.StreamCopyFailedEarly = false;
        ctx.RtspBadRequestOnHeader = false;
        SetStatus(ctx, StreamStatus.Streaming, restartMessage);

        var args = $"-re -stream_loop -1 -i \"{ctx.SourceFilePath}\" -c copy -f rtsp -rtsp_transport tcp \"{channel.RtspUrl}\"";

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

        try
        {
            proc.Start();
            proc.BeginErrorReadLine();
            proc.BeginOutputReadLine();
            _tracker.Track(proc);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _db.EndHistory(ctx.HistoryId, "error", $"failed to start ffmpeg: {ex.Message}");
            SetStatus(ctx, StreamStatus.Error, $"failed to start ffmpeg: {ex.Message}");
            return false;
        }
    }

    private void HandleExit(Channel channel, ChannelStreamContext ctx)
    {
        var exitCode = ctx.Process?.ExitCode ?? -1;
        var wasCancelled = ctx.Cts.IsCancellationRequested;
        var runtime = DateTime.UtcNow - ctx.StartedAt;
        var earlyFail = ctx.StreamCopyFailedEarly && runtime.TotalSeconds < 5;

        // 자동 재시도 횟수는 "연속 실패" 기준으로 관리한다.
        // 일정 시간 이상 정상 송출된 뒤 발생한 오류는 새 장애로 보고 재시도 카운트를 초기화한다.
        if (!wasCancelled && runtime >= AutoRestartAttemptResetThreshold)
            ctx.AutoRestartAttempts = 0;

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
            if (ShouldAutoRestart(ctx, earlyFail: false))
            {
                ScheduleRestart(channel, ctx, exitCode);
                return;
            }

            result = "error";
            if (AutoRestartEnabled && ctx.AutoRestartAttempts >= MaxAutoRestartAttempts)
                message = $"streaming failed (exit {exitCode}). auto-restart limit reached ({ctx.AutoRestartAttempts}/{MaxAutoRestartAttempts}).";
            else
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

    private bool ShouldAutoRestart(ChannelStreamContext ctx, bool earlyFail)
    {
        if (!AutoRestartEnabled) return false;
        if (ctx.Cts.IsCancellationRequested) return false;
        if (ctx.RtspBadRequestOnHeader) return false;
        if (earlyFail) return false;
        return ctx.AutoRestartAttempts < MaxAutoRestartAttempts;
    }

    private void ScheduleRestart(Channel channel, ChannelStreamContext ctx, int exitCode)
    {
        ctx.AutoRestartAttempts++;
        var delaySeconds = AutoRestartBaseDelay.TotalSeconds * Math.Pow(2, Math.Max(0, ctx.AutoRestartAttempts - 1));
        var delay = TimeSpan.FromSeconds(Math.Clamp(delaySeconds, 1, 30));

        var message = $"streaming failed (exit {exitCode}). auto-restart {ctx.AutoRestartAttempts}/{MaxAutoRestartAttempts} in {delay.TotalSeconds:0}s";
        _db.EndHistory(ctx.HistoryId, "restart", message);
        SetStatus(ctx, StreamStatus.Ready, message);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay, ctx.Cts.Token).ConfigureAwait(false);
                if (ctx.Cts.IsCancellationRequested)
                {
                    FinalizeStoppedChannel(channel.Id, ctx, "User stopped");
                    return;
                }

                if (!_channels.TryGetValue(channel.Id, out var current) || !ReferenceEquals(current, ctx))
                    return;

                if (!TryStartProcess(channel, ctx, "Auto-restarting stream", out var restartError))
                {
                    FinalizeErrorChannel(channel.Id, ctx, restartError ?? "auto-restart failed");
                }
            }
            catch (OperationCanceledException)
            {
                FinalizeStoppedChannel(channel.Id, ctx, "User stopped");
            }
            catch (Exception ex)
            {
                FinalizeErrorChannel(channel.Id, ctx, $"auto-restart error: {ex.Message}");
            }
        });
    }

    private void FinalizeStoppedChannel(int channelId, ChannelStreamContext ctx, string message)
    {
        if (!_channels.TryGetValue(channelId, out var current) || !ReferenceEquals(current, ctx))
            return;

        SetStatus(ctx, StreamStatus.Idle, message);
        _channels.TryRemove(channelId, out _);
    }

    private void FinalizeErrorChannel(int channelId, ChannelStreamContext ctx, string message)
    {
        if (!_channels.TryGetValue(channelId, out var current) || !ReferenceEquals(current, ctx))
            return;

        SetStatus(ctx, StreamStatus.Error, message);
        _channels.TryRemove(channelId, out _);
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
