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
    internal string ActiveSourceFilePath { get; set; } = string.Empty;
    internal int TimestampDiscontinuity;
    internal bool TimestampRecoveryAttempted { get; set; }
    internal bool InputFailure { get; set; }
    internal bool OutputFailure { get; set; }
    internal double SeekSeconds { get; set; }
    internal double? OutputSeconds { get; set; }
    public Process? Process { get; set; }
    public CancellationTokenSource Cts { get; } = new();
    public StreamStatus Status { get; set; } = StreamStatus.Idle;
    public int HistoryId { get; set; }
    public int AutoRestartAttempts { get; set; }
    public bool StreamCopyFailedEarly { get; set; }
    public bool RtspBadRequestOnHeader { get; set; }
    public DateTime StartedAt { get; set; }
    public double? ProgressFps { get; set; }
    public double? ProgressBitrateKbps { get; set; }
    public double? ProgressSpeed { get; set; }
    public string? ProgressOutTime { get; set; }
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
    private readonly StreamLogWriter _logWriter;
    private readonly StreamCopyTimelineService _timeline;
    private readonly StreamKeyframeService _keyframes;
    private readonly ConcurrentDictionary<int, byte> _logWriteWarnings = new();

    public string FfmpegPath { get; set; } = ToolLocator.Find(ToolLocator.ExecutableName("ffmpeg")) ?? ToolLocator.ExecutableName("ffmpeg");
    public bool AutoRestartEnabled { get; set; } = true;
    public int MaxAutoRestartAttempts { get; set; } = 3;
    public TimeSpan AutoRestartBaseDelay { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan AutoRestartAttemptResetThreshold { get; set; } = TimeSpan.FromSeconds(30);

    public event EventHandler<StreamEventArgs>? StatusChanged;
    public event EventHandler<(int ChannelId, string Line)>? Log;

    public StreamingService(SqliteService db, ChildProcessTracker tracker, string? logDirectory = null,
        StreamCopyTimelineService? timelineService = null, StreamKeyframeService? keyframeService = null)
    {
        _db = db;
        _tracker = tracker;
        _logWriter = new StreamLogWriter(logDirectory);
        _timeline = timelineService ?? new StreamCopyTimelineService(tracker);
        _keyframes = keyframeService ?? new StreamKeyframeService(tracker);
    }

    public bool IsStreaming(int channelId) =>
        _channels.TryGetValue(channelId, out var ctx) &&
        (ctx.Status == StreamStatus.Streaming || ctx.Status == StreamStatus.Stopping || ctx.Status == StreamStatus.Ready);

    public Task StartAsync(Channel channel, string sourceFilePath)
    {
        if (_channels.ContainsKey(channel.Id))
            throw new InvalidOperationException("Channel already streaming.");

        var cachedSource = System.IO.File.Exists(sourceFilePath) ? _timeline.FindCached(sourceFilePath) : null;
        var ctx = new ChannelStreamContext
        {
            ChannelId = channel.Id,
            SourceFilePath = sourceFilePath,
            ActiveSourceFilePath = cachedSource ?? sourceFilePath,
            TimestampRecoveryAttempted = cachedSource != null,
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
        ctx.InputFailure = false;
        ctx.OutputFailure = false;
        ctx.OutputSeconds = null;
        Interlocked.Exchange(ref ctx.TimestampDiscontinuity, 0);
        ctx.ProgressFps = null;
        ctx.ProgressBitrateKbps = null;
        ctx.ProgressSpeed = null;
        ctx.ProgressOutTime = null;
        SetStatus(ctx, StreamStatus.Streaming, restartMessage);

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-nostdin", "-loglevel", "verbose", "-nostats", "-stats_period", "0.5", "-progress", "pipe:1", "-fflags", "+genpts", "-re" })
            psi.ArgumentList.Add(arg);
        if (ctx.SeekSeconds > 0)
        {
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add(ctx.SeekSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            psi.ArgumentList.Add("-stream_loop");
            psi.ArgumentList.Add("-1");
        }
        foreach (var arg in new[] { "-i", ctx.ActiveSourceFilePath, "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-avoid_negative_ts", "make_zero", "-f", "rtsp", "-rtsp_transport", "tcp", channel.RtspUrl })
            psi.ArgumentList.Add(arg);

        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        ctx.Process = proc;
        ctx.HistoryId = _db.StartHistory(channel.Id);
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        void HandleErrorLine(string line)
        {
            WriteLog(channel.Id, "stderr", line);
            if (!ReferenceEquals(ctx.Process, proc) || ctx.Cts.IsCancellationRequested) return;
            if (DetectStreamCopyFailure(line))
                ctx.StreamCopyFailedEarly = true;
            if (DetectRtspBadRequest(line))
                ctx.RtspBadRequestOnHeader = true;
            ctx.InputFailure |= DetectInputFailure(line);
            ctx.OutputFailure |= DetectOutputFailure(line);
            Log?.Invoke(this, (channel.Id, line));
            if (StreamCopyTimelineService.IsTimestampDiscontinuity(line))
                Interlocked.Exchange(ref ctx.TimestampDiscontinuity, 1);
        }
        void HandleOutputLine(string line)
        {
            WriteLog(channel.Id, "stdout", line);
            if (!ReferenceEquals(ctx.Process, proc) || ctx.Cts.IsCancellationRequested) return;
            if (TryHandleProgressLine(ctx, line, out var progressSummary))
            {
                if (progressSummary != null)
                    Log?.Invoke(this, (channel.Id, progressSummary));
                return;
            }
            Log?.Invoke(this, (channel.Id, line));
        }
        proc.Exited += (_, _) => _ = HandleExitAfterOutputAsync(channel, ctx, proc, started.Task, stderrDone.Task, stdoutDone.Task);

        try
        {
            WriteLog(channel.Id, "session", $"Channel: {channel.Name}; source: {ctx.ActiveSourceFilePath}; target: {channel.RtspUrl}");
            WriteLog(channel.Id, "command", $"\"{psi.FileName}\" " + string.Join(" ", psi.ArgumentList.Select(arg => $"\"{arg}\"")));
            ctx.Cts.Token.ThrowIfCancellationRequested();
            proc.Start();
            WriteLog(channel.Id, "session", $"Process started: PID {proc.Id}");
            _tracker.Track(proc);
            StartOutputReader(proc.StandardError, HandleErrorLine, $"ffmpeg-{channel.Id}-stderr", stderrDone);
            StartOutputReader(proc.StandardOutput, HandleOutputLine, $"ffmpeg-{channel.Id}-stdout", stdoutDone);
            if (ctx.Cts.IsCancellationRequested && !proc.HasExited)
                proc.Kill(entireProcessTree: true);
            started.TrySetResult(true);
            return true;
        }
        catch (Exception ex)
        {
            started.TrySetResult(false);
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            error = ex.Message;
            _db.EndHistory(ctx.HistoryId, "error", $"failed to start ffmpeg: {ex.Message}");
            SetStatus(ctx, StreamStatus.Error, $"failed to start ffmpeg: {ex.Message}");
            return false;
        }
    }

    private static void StartOutputReader(System.IO.StreamReader reader, Action<string> handleLine, string name, TaskCompletionSource completed)
    {
        // Windows의 동기 프로세스 파이프 읽기가 공유 ThreadPool을 점유하지 않도록 분리한다.
        var thread = new Thread(() =>
        {
            try
            {
                using (reader)
                {
                    while (reader.ReadLine() is { } line)
                        handleLine(line);
                }
            }
            catch (System.IO.IOException) { } // 프로세스 종료 시 파이프가 닫힐 수 있다.
            catch (ObjectDisposedException) { }
            finally { completed.TrySetResult(); }
        })
        {
            IsBackground = true,
            Name = name
        };
        thread.Start();
    }

    private static bool TryHandleProgressLine(ChannelStreamContext ctx, string line, out string? progressSummary)
    {
        progressSummary = null;

        var separatorIndex = line.IndexOf('=');
        if (separatorIndex <= 0)
            return false;

        var key = line[..separatorIndex].Trim();
        var value = line[(separatorIndex + 1)..].Trim();

        switch (key)
        {
            case "frame":
            case "stream_0_0_q":
            case "total_size":
            case "out_time_ms":
            case "dup_frames":
            case "drop_frames":
                return true;
            case "out_time_us":
                if (TryParseProgressDouble(value, out var microseconds) && double.IsFinite(microseconds) && microseconds >= 0)
                    ctx.OutputSeconds = microseconds / 1000_000d;
                return true;
            case "fps":
                ctx.ProgressFps = TryParseProgressDouble(value, out var fps) ? fps : 0d;
                return true;
            case "bitrate":
                ctx.ProgressBitrateKbps = TryParseProgressBitrateKbps(value, out var bitrateKbps) ? bitrateKbps : 0d;
                return true;
            case "speed":
                ctx.ProgressSpeed = TryParseProgressSpeed(value, out var speed) ? speed : 1d;
                return true;
            case "out_time":
                ctx.ProgressOutTime = value;
                if (TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var outputTime) && outputTime >= TimeSpan.Zero)
                    ctx.OutputSeconds = outputTime.TotalSeconds;
                return true;
            case "progress":
                progressSummary = BuildProgressSummary(ctx);
                return true;
            default:
                return false;
        }
    }

    private static string? BuildProgressSummary(ChannelStreamContext ctx)
    {
        var fps = ctx.ProgressFps ?? 0d;
        var bitrateKbps = ctx.ProgressBitrateKbps ?? 0d;
        var speed = ctx.ProgressSpeed ?? 1d;
        var outTime = ctx.ProgressOutTime;

        if (fps <= 0d && bitrateKbps <= 0d && string.IsNullOrWhiteSpace(outTime))
            return null;

        var timePart = string.IsNullOrWhiteSpace(outTime)
            ? string.Empty
            : $" time={outTime}";

        return FormattableString.Invariant($"fps={fps:0.###} bitrate={bitrateKbps:0.###}kbits/s speed={speed:0.###}x{timePart}");
    }

    private static bool TryParseProgressDouble(string raw, out double value)
    {
        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseProgressBitrateKbps(string raw, out double value)
    {
        value = 0d;
        if (string.Equals(raw, "N/A", StringComparison.OrdinalIgnoreCase))
            return true;

        var unitIndex = raw.IndexOf("bits/s", StringComparison.OrdinalIgnoreCase);
        if (unitIndex <= 0)
            return false;

        var numberPart = raw[..unitIndex].Trim();
        var unitPrefix = numberPart.Length > 0 ? numberPart[^1] : '\0';
        var factor = 1d;

        if (char.IsLetter(unitPrefix))
        {
            numberPart = numberPart[..^1].TrimEnd();
            factor = char.ToLowerInvariant(unitPrefix) switch
            {
                'k' => 1d,
                'm' => 1000d,
                'g' => 1000_000d,
                _ => 1d / 1000d
            };
        }
        else
        {
            factor = 1d / 1000d;
        }

        if (!double.TryParse(numberPart, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return false;

        value = parsed * factor;
        return true;
    }

    private static bool TryParseProgressSpeed(string raw, out double value)
    {
        value = 1d;
        if (string.Equals(raw, "N/A", StringComparison.OrdinalIgnoreCase))
            return true;

        if (raw.EndsWith('x'))
            raw = raw[..^1];

        return double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
    }

    private async Task HandleExitAfterOutputAsync(Channel channel, ChannelStreamContext ctx, Process process,
        Task<bool> started, Task stderrDone, Task stdoutDone)
    {
        try
        {
            if (!await started.ConfigureAwait(false)) return;
            await Task.WhenAll(stderrDone, stdoutDone).ConfigureAwait(false);
            HandleExit(channel, ctx, process);
        }
        catch (Exception ex)
        {
            FinalizeErrorChannel(channel.Id, ctx, $"Process exit handling failed: {ex.Message}");
        }
    }

    private void HandleExit(Channel channel, ChannelStreamContext ctx, Process process)
    {
        if (!ReferenceEquals(ctx.Process, process)) return;
        var exitCode = process.ExitCode;
        var wasCancelled = ctx.Cts.IsCancellationRequested;
        var runtime = DateTime.UtcNow - ctx.StartedAt;
        WriteLog(channel.Id, "session", FormattableString.Invariant($"Process exited: code={exitCode}, cancelled={wasCancelled}, runtime={runtime.TotalSeconds:0.###}s"));
        var earlyFail = ctx.StreamCopyFailedEarly && runtime.TotalSeconds < 5;
        var inputFailed = exitCode != 0 && !ctx.OutputFailure && !ctx.RtspBadRequestOnHeader
            && (ctx.InputFailure || Volatile.Read(ref ctx.TimestampDiscontinuity) != 0);

        // 자동 재시도 횟수는 "연속 실패" 기준으로 관리한다.
        // 일정 시간 이상 정상 송출된 뒤 발생한 오류는 새 장애로 보고 재시도 카운트를 초기화한다.
        if (!wasCancelled && !inputFailed && runtime >= AutoRestartAttemptResetThreshold)
            ctx.AutoRestartAttempts = 0;

        string result;
        string? message;
        if (wasCancelled)
        {
            result = "stopped";
            message = "User stopped";
        }
        else if (inputFailed)
        {
            if (ShouldAutoRestart(ctx, earlyFail: false))
            {
                ScheduleInputRecovery(channel, ctx);
                return;
            }

            result = "error";
            message = AutoRestartEnabled
                ? "영상 입력 오류: 자동 복구 횟수 제한에 도달했습니다."
                : "영상 입력 오류: 자동 재시작 설정이 꺼져 있습니다.";
            SetStatus(ctx, StreamStatus.Error, message);
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
            message = $"Stream-copy output failed (exit {exitCode}). Re-encoding was not attempted.";
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
            if (AutoRestartEnabled && MaxAutoRestartAttempts > 0 && ctx.AutoRestartAttempts >= MaxAutoRestartAttempts)
                message = $"streaming failed (exit {exitCode}). auto-restart limit reached ({ctx.AutoRestartAttempts}/{MaxAutoRestartAttempts}).";
            else
                message = $"streaming failed (exit {exitCode}).";
            SetStatus(ctx, StreamStatus.Error, message);
        }
        else if (ctx.SeekSeconds > 0)
        {
            _db.EndHistory(ctx.HistoryId, "ended", "Skipped segment remainder completed");
            ctx.SeekSeconds = 0;
            SetStatus(ctx, StreamStatus.Ready, "남은 구간 재생 완료: 전체 반복 재개");
            RestartPreparedChannel(channel, ctx, "전체 반복 송출 재개");
            return;
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

    private void ScheduleInputRecovery(Channel channel, ChannelStreamContext ctx)
    {
        ctx.AutoRestartAttempts++;
        const string message = "입력 오류 복구 중: 후속 키프레임 탐색 (재인코딩 없음)";
        _db.EndHistory(ctx.HistoryId, "repair", message);
        SetStatus(ctx, StreamStatus.Ready, message);
        Log?.Invoke(this, (channel.Id, $"[warning] {message}"));

        _ = Task.Run(async () =>
        {
            try
            {
                var delay = TimeSpan.FromSeconds(Math.Clamp(AutoRestartBaseDelay.TotalSeconds, 0, 30));
                await Task.Delay(delay, ctx.Cts.Token).ConfigureAwait(false);
                if (!ctx.OutputSeconds.HasValue && ctx.SeekSeconds == 0)
                    throw new InvalidOperationException("송출 위치를 확인할 수 없어 오류 구간을 건너뛸 수 없습니다.");
                var resume = await _keyframes.FindNextAsync(ctx.ActiveSourceFilePath, ctx.SeekSeconds,
                    ctx.OutputSeconds ?? 0, looping: ctx.SeekSeconds == 0,
                    line => WriteLog(channel.Id, "recovery", line), ctx.Cts.Token).ConfigureAwait(false);
                ctx.Cts.Token.ThrowIfCancellationRequested();
                if (resume.NextKeyframeSeconds is double next)
                {
                    ctx.SeekSeconds = next;
                    var skipped = FormattableString.Invariant($"[warning] 입력 오류 구간 건너뛰기: {resume.FailedPositionSeconds:0.###}s → {next:0.###}s (stream copy)");
                    WriteLog(channel.Id, "recovery", skipped);
                    Log?.Invoke(this, (channel.Id, skipped));
                }
                else
                {
                    if (ctx.TimestampRecoveryAttempted)
                        throw new InvalidOperationException("후속 키프레임이 없고 리먹싱 복구를 이미 시도했습니다.");
                    ctx.TimestampRecoveryAttempted = true;
                    ctx.ActiveSourceFilePath = await _timeline.PrepareAsync(ctx.SourceFilePath, FfmpegPath,
                        line => WriteLog(channel.Id, "repair", line), ctx.Cts.Token).ConfigureAwait(false);
                    ctx.SeekSeconds = 0;
                }
                RestartPreparedChannel(channel, ctx, "입력 오류 복구 완료: stream copy 송출 재개");
            }
            catch (OperationCanceledException)
            {
                FinalizeStoppedChannel(channel.Id, ctx, "User stopped");
            }
            catch (Exception ex)
            {
                var error = $"입력 오류 복구 실패 (재인코딩하지 않음): {ex.Message}";
                WriteLog(channel.Id, "repair", $"[error] {error}");
                Log?.Invoke(this, (channel.Id, $"[error] {error}"));
                FinalizeErrorChannel(channel.Id, ctx, error);
            }
        });
    }

    private void RestartPreparedChannel(Channel channel, ChannelStreamContext ctx, string message)
    {
        if (ctx.Cts.IsCancellationRequested)
        {
            FinalizeStoppedChannel(channel.Id, ctx, "User stopped");
            return;
        }
        if (!_channels.TryGetValue(channel.Id, out var current) || !ReferenceEquals(current, ctx)) return;
        if (!TryStartProcess(channel, ctx, message, out var error))
        {
            if (ctx.Cts.IsCancellationRequested)
                FinalizeStoppedChannel(channel.Id, ctx, "User stopped");
            else
                FinalizeErrorChannel(channel.Id, ctx, error ?? "Stream-copy resume failed");
        }
    }

    private bool ShouldAutoRestart(ChannelStreamContext ctx, bool earlyFail)
    {
        if (!AutoRestartEnabled) return false;
        if (ctx.Cts.IsCancellationRequested) return false;
        if (ctx.RtspBadRequestOnHeader) return false;
        if (earlyFail) return false;
        if (MaxAutoRestartAttempts <= 0) return true;
        return ctx.AutoRestartAttempts < MaxAutoRestartAttempts;
    }

    private void ScheduleRestart(Channel channel, ChannelStreamContext ctx, int exitCode)
    {
        ctx.AutoRestartAttempts++;
        var delaySeconds = AutoRestartBaseDelay.TotalSeconds * Math.Pow(2, Math.Max(0, ctx.AutoRestartAttempts - 1));
        var delay = TimeSpan.FromSeconds(Math.Clamp(delaySeconds, 1, 30));

        var attemptText = MaxAutoRestartAttempts <= 0
            ? $"{ctx.AutoRestartAttempts}/∞"
            : $"{ctx.AutoRestartAttempts}/{MaxAutoRestartAttempts}";
        var message = $"streaming failed (exit {exitCode}). auto-restart {attemptText} in {delay.TotalSeconds:0}s";
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

    private static bool DetectInputFailure(string line) =>
        (line.Contains("[in#", StringComparison.OrdinalIgnoreCase)
            && (line.Contains("error code:", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Error during", StringComparison.OrdinalIgnoreCase)))
        || line.Contains("Error during demuxing", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Error demuxing input", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Seek to start failed", StringComparison.OrdinalIgnoreCase);

    private static bool DetectOutputFailure(string line) =>
        line.Contains("Error muxing a packet", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Error writing", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Could not write header", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Broken pipe", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
        || (line.Contains("[out#", StringComparison.OrdinalIgnoreCase)
            && line.Contains("error code:", StringComparison.OrdinalIgnoreCase));

    private static bool DetectStreamCopyFailure(string line)
    {
        // Common ffmpeg stream-copy failure signals
        if (line.Contains("Could not find tag for codec", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains("codec not currently supported in container", StringComparison.OrdinalIgnoreCase)) return true;
        if (StreamCopyTimelineService.IsTimestampDiscontinuity(line)) return false;
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
        if (ctx.Status is StreamStatus.Idle or StreamStatus.Error) return;
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
        WriteLog(ctx.ChannelId, "status", $"{status}: {message}");
        StatusChanged?.Invoke(this, new StreamEventArgs { ChannelId = ctx.ChannelId, Status = status, Message = message });
    }

    private void WriteLog(int channelId, string source, string line)
    {
        try
        {
            _logWriter.Write(channelId, source, line);
            _logWriteWarnings.TryRemove(channelId, out _);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            if (_logWriteWarnings.TryAdd(channelId, 0))
                Log?.Invoke(this, (channelId, $"[warning] FFmpeg log write failed ({_logWriter.DirectoryPath}): {ex.Message}"));
        }
    }

    public void Dispose() => StopAll();
}
