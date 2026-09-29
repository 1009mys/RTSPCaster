using System.Net.Sockets;
using System.Text.Json;
using RTSPCaster.Backend.Contracts;
using RTSPCaster.Models;
using RTSPCaster.Services;

namespace RTSPCaster.Backend.Services;

public sealed partial class CasterService : BackgroundService
{
    private sealed class RuntimeChannel(Channel channel, VideoFile video)
    {
        public Channel Channel { get; } = channel;
        public VideoFile Video { get; } = video;
        public StreamStatus Status { get; set; }
        public string Message { get; set; } = string.Empty;
        public double Progress { get; set; }
        public DateTime StartedAt { get; set; }
        public Queue<HealthSample> Samples { get; } = new();
        public CancellationTokenSource? Cancellation { get; set; }
        public Task Operation { get; set; } = Task.CompletedTask;
        public bool StopRequested { get; set; }
        public bool Busy => !Operation.IsCompleted || Status is StreamStatus.Probing or StreamStatus.Converting
            or StreamStatus.Streaming or StreamStatus.Stopping or StreamStatus.Ready;
    }

    private readonly object _sync = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly SemaphoreSlim _conversionGate = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<int, RuntimeChannel> _channels = new();
    private readonly Queue<LogEntry> _logs = new();
    private readonly SqliteService _db;
    private readonly BackendStorage _storage;
    private readonly BackendOptions _options;
    private readonly ChildProcessTracker _tracker;
    private readonly StreamingService _streaming;
    private readonly FfprobeService _probe;
    private readonly ILogger<CasterService> _logger;
    private CasterSettings _settings;
    private MediaMtxConnection _connection;
    private long _logId;

    public CasterService(SqliteService db, BackendStorage storage, BackendOptions options,
        ChildProcessTracker tracker, StreamingService streaming, ILogger<CasterService> logger)
    {
        _db = db;
        _storage = storage;
        _options = options;
        _tracker = tracker;
        _streaming = streaming;
        _logger = logger;
        _probe = new FfprobeService(tracker) { FfprobePath = BackendStorage.ResolveTool(options.FfprobePath, "ffprobe") };
        _settings = JsonSerializer.Deserialize<CasterSettings>(_db.GetSetting("BackendSettings") ?? "{}")!;
        ValidateSettings(_settings);
        _connection = new(_settings.MediaMtxHost, _settings.MediaMtxPort, null, null);
        ApplyRestartPolicy();
        foreach (var (channel, file) in _db.LoadChannels())
            _channels.Add(channel.Id, new RuntimeChannel(channel, file));
        _streaming.StatusChanged += OnStatusChanged;
        _streaming.Log += OnStreamLog;
    }

    public CasterSnapshot Snapshot(long afterLogId = 0)
    {
        lock (_sync)
            return new(_settings, _connection, _channels.Values.Select(Snapshot).ToArray(), LogsCore(afterLogId));
    }

    public ChannelSnapshot GetChannel(int id)
    {
        lock (_sync) return Snapshot(Find(id));
    }

    public LogPage Logs(long after = 0)
    {
        if (after < 0) throw new ApiException(400, "로그 순번은 0 이상이어야 합니다.");
        lock (_sync) return LogsCore(after);
    }

    private LogPage LogsCore(long after) => new(_logId, _logs.Where(entry => entry.Id > after).ToArray());

    private static ChannelSnapshot Snapshot(RuntimeChannel item)
    {
        var c = item.Channel;
        var v = item.Video;
        return new(c.Id, c.Name, c.RtspPath, c.MediaMtxHost, c.MediaMtxPort, c.RtspUrl,
            new(v.Id, v.FileName, v.FileSize, v.VideoCodec, v.AudioCodec, v.VideoWidth, v.VideoHeight,
                v.DurationSeconds, v.StreamCopyCompatible, v.IncompatibleReason),
            item.Status, item.Message, item.Progress, !item.Operation.IsCompleted, item.Samples.ToArray());
    }

    private RuntimeChannel Find(int id) => _channels.TryGetValue(id, out var item)
        ? item : throw new ApiException(404, "채널을 찾을 수 없습니다.");

    private void Log(string message, int? channelId = null)
    {
        lock (_sync)
        {
            _logs.Enqueue(new(++_logId, DateTime.UtcNow, channelId, message.Length > 4096 ? message[..4096] : message));
            while (_logs.Count > 500) _logs.Dequeue();
        }
    }

    private void State(RuntimeChannel item, StreamStatus status, string message)
    {
        lock (_sync)
        {
            if (item.StopRequested && status is not (StreamStatus.Idle or StreamStatus.Stopping)) return;
            item.Status = status;
            item.Message = message;
            if (status == StreamStatus.Streaming) item.StartedAt = DateTime.UtcNow;
            if (status is StreamStatus.Streaming or StreamStatus.Idle or StreamStatus.Stopping or StreamStatus.Error)
                item.Samples.Clear();
            Log($"{status}: {message}", item.Channel.Id);
        }
    }

    private void OnStatusChanged(object? sender, StreamEventArgs args)
    {
        lock (_sync)
        {
            if (!_channels.TryGetValue(args.ChannelId, out var item)) return;
            State(item, args.Status, args.Message ?? string.Empty);
        }
    }

    private void OnStreamLog(object? sender, (int ChannelId, string Line) args)
    {
        lock (_sync)
        {
            if (!_channels.TryGetValue(args.ChannelId, out var item)) return;
            if (item.Status == StreamStatus.Streaming && StreamHealth.Parse(args.Line, item.StartedAt) is { } sample)
            {
                item.Samples.Enqueue(sample);
                while (item.Samples.Count > 30) item.Samples.Dequeue();
            }
            if (StreamHealth.IsInteresting(args.Line)) Log(args.Line, args.ChannelId);
        }
    }

    public async Task<ChannelSnapshot> StartChannelAsync(int id, CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                var item = Find(id);
                StartCore(item);
                return Snapshot(item);
            }
        }
        finally { _commands.Release(); }
    }

    private void StartCore(RuntimeChannel item)
    {
        if (_shutdown.IsCancellationRequested) throw new ApiException(503, "Backend 종료 중입니다.");
        if (item.Busy || item.StopRequested) throw new ApiException(409, "이미 송출 또는 처리 중인 채널입니다.");
        EnsureUnique(item.Channel.Id, item.Channel.MediaMtxHost, item.Channel.MediaMtxPort, item.Channel.RtspPath);
        item.Cancellation?.Dispose();
        item.Cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        item.Progress = 0;
        State(item, StreamStatus.Probing, "사전 검증 중");
        var token = item.Cancellation.Token;
        item.Operation = Task.Run(() => PrepareAndStartAsync(item, token), CancellationToken.None);
    }

    private async Task PrepareAndStartAsync(RuntimeChannel item, CancellationToken ct)
    {
        try
        {
            if (!await ReachableAsync(item.Channel.MediaMtxHost, item.Channel.MediaMtxPort, ct))
                throw new InvalidOperationException("MediaMTX 연결 불가");
            ct.ThrowIfCancellationRequested();
            var metadata = await ProbeAsync(item.Video.FilePath, ct);
            lock (_sync)
            {
                ApplyMetadata(item.Video, metadata);
                _db.UpsertVideoFile(item.Video);
            }
            var source = item.Video.FilePath;
            if (!metadata.StreamCopyCompatible)
            {
                State(item, StreamStatus.Converting, "변환 대기 중");
                await _conversionGate.WaitAsync(ct);
                try
                {
                    ct.ThrowIfCancellationRequested();
                    State(item, StreamStatus.Converting, "변환 중");
                    var conversion = new ConversionService(_db, _tracker, _storage.ConversionDirectory)
                    {
                        FfmpegPath = BackendStorage.ResolveTool(_options.FfmpegPath, "ffmpeg")
                    };
                    conversion.Progress += (_, progress) =>
                    {
                        lock (_sync)
                            if (!item.StopRequested) item.Progress = progress.Percent;
                    };
                    conversion.Log += (_, line) =>
                    {
                        if (StreamHealth.IsInteresting(line)) Log(line, item.Channel.Id);
                    };
                    source = await conversion.EnsureCompatibleAsync(item.Video, ct);
                    ct.ThrowIfCancellationRequested();
                }
                finally { _conversionGate.Release(); }
                await WaitForFileAsync(source, ct);
                State(item, StreamStatus.Probing, "변환 결과 검증 중");
                var converted = await ProbeAsync(source, ct);
                if (!converted.StreamCopyCompatible)
                    throw new InvalidOperationException($"변환 결과가 RTSP copy 호환이 아닙니다: {converted.IncompatibleReason}");
            }
            Task start;
            lock (_sync)
            {
                ct.ThrowIfCancellationRequested();
                start = _streaming.StartAsync(item.Channel, source);
            }
            await start;
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            State(item, StreamStatus.Idle, "준비 작업 취소됨");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Channel {ChannelId} failed to start", item.Channel.Id);
            State(item, StreamStatus.Error, exception.Message);
        }
    }

    public async Task<ChannelActionResult[]> StartAllAsync(CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                var results = new List<ChannelActionResult>();
                foreach (var item in _channels.Values)
                {
                    try
                    {
                        StartCore(item);
                        results.Add(new(item.Channel.Id, true, null));
                    }
                    catch (ApiException exception) { results.Add(new(item.Channel.Id, false, exception.Message)); }
                }
                return results.ToArray();
            }
        }
        finally { _commands.Release(); }
    }

    public async Task StopChannelAsync(int id, CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            RuntimeChannel item;
            lock (_sync) item = Find(id);
            await StopCoreAsync(item);
        }
        finally { _commands.Release(); }
    }

    private async Task StopCoreAsync(RuntimeChannel item)
    {
        lock (_sync)
        {
            item.StopRequested = true;
            State(item, StreamStatus.Stopping, "중지 중");
            item.Cancellation?.Cancel();
        }
        try
        {
            await item.Operation;
            _streaming.Stop(item.Channel.Id);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (_streaming.IsStreaming(item.Channel.Id))
                await Task.Delay(50, timeout.Token);
            State(item, StreamStatus.Idle, "중지됨");
        }
        catch (OperationCanceledException)
        {
            throw new ApiException(503, "중지 처리가 지연되고 있습니다. 잠시 후 다시 시도하세요.");
        }
        finally
        {
            lock (_sync) item.StopRequested = false;
        }
    }

    public async Task StopAllChannelsAsync(CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            RuntimeChannel[] items;
            lock (_sync) items = _channels.Values.ToArray();
            await Task.WhenAll(items.Select(StopCoreAsync));
        }
        finally { _commands.Release(); }
    }

    public async Task RemoveChannelAsync(int id, CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            RuntimeChannel item;
            lock (_sync) item = Find(id);
            await StopCoreAsync(item);
            lock (_sync)
            {
                _db.DeleteChannel(id);
                _channels.Remove(id);
                item.Cancellation?.Dispose();
                Log($"채널 삭제: {item.Channel.Name}", id);
            }
        }
        finally { _commands.Release(); }
    }

    private async Task<ProbeResult> ProbeAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var result = await _probe.ProbeAsync(path, timeout.Token);
            if (string.IsNullOrEmpty(result.VideoCodec))
                throw new ApiException(422, "영상 스트림이 없는 파일입니다.");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_shutdown.IsCancellationRequested)
        {
            throw new ApiException(422, "영상 검사가 60초 안에 완료되지 않았습니다.");
        }
    }

    private static async Task WaitForFileAsync(string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                if (await file.ReadAsync(new byte[1], ct) > 0) return;
            }
            catch (IOException) { }
            await Task.Delay(100, ct);
        }
        throw new IOException("변환된 파일을 읽을 수 없습니다.");
    }

    private static void ApplyMetadata(VideoFile file, ProbeResult result)
    {
        file.VideoCodec = result.VideoCodec;
        file.AudioCodec = result.AudioCodec;
        file.VideoWidth = result.VideoWidth;
        file.VideoHeight = result.VideoHeight;
        file.DurationSeconds = result.DurationSeconds;
        file.StreamCopyCompatible = result.StreamCopyCompatible;
        file.IncompatibleReason = result.IncompatibleReason;
    }

    public async Task<MediaMtxConnection> CheckMediaMtxAsync(CancellationToken ct)
    {
        CasterSettings settings;
        lock (_sync) settings = _settings;
        var reachable = await ReachableAsync(settings.MediaMtxHost, settings.MediaMtxPort, ct);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_settings.MediaMtxHost != settings.MediaMtxHost || _settings.MediaMtxPort != settings.MediaMtxPort)
                return _connection;
            if (_connection.Reachable != reachable)
                Log($"MediaMTX {(reachable ? "연결됨" : "연결 안됨")}: {settings.MediaMtxHost}:{settings.MediaMtxPort}");
            return _connection = new(settings.MediaMtxHost, settings.MediaMtxPort, reachable, DateTime.UtcNow);
        }
    }

    private static async Task<bool> ReachableAsync(string host, int port, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(800));
        using var client = new TcpClient();
        try { await client.ConnectAsync(host, port, timeout.Token); return true; }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await CheckMediaMtxAsync(stoppingToken);
                await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _shutdown.Cancel();
        try { await StopAllChannelsAsync(CancellationToken.None); }
        finally
        {
            _streaming.StopAll();
            await base.StopAsync(cancellationToken);
        }
    }

    public override void Dispose()
    {
        _shutdown.Cancel();
        _streaming.StatusChanged -= OnStatusChanged;
        _streaming.Log -= OnStreamLog;
        base.Dispose();
    }
}
