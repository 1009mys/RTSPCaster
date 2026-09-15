using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RTSPCaster.Models;
using RTSPCaster.Services;

namespace RTSPCaster.ViewModels;

public partial class ChannelViewModel : ObservableObject
{
    private readonly MainViewModel _owner;

    public Channel Channel { get; }
    public VideoFile VideoFile { get; }

    [ObservableProperty] private StreamStatus status = StreamStatus.Idle;
    [ObservableProperty] private double conversionProgress;
    [ObservableProperty] private string statusMessage = string.Empty;

    public string Name => Channel.Name;
    public string FileName => VideoFile.FileName;
    public string Codecs => $"{VideoFile.VideoCodec ?? "-"}/{VideoFile.AudioCodec ?? "-"}";
    public string CompatibilityText => VideoFile.StreamCopyCompatible ? "OK (copy)" : $"필요: 변환 ({VideoFile.IncompatibleReason})";
    public string RtspUrlPrefix => $"rtsp://{Channel.MediaMtxHost}:{Channel.MediaMtxPort}/";
    public string RtspUrl => Channel.RtspUrl;
    public bool NeedsConversion => !VideoFile.StreamCopyCompatible;

    public string RtspPath
    {
        get => Channel.RtspPath;
        set
        {
            var sanitized = System.Text.RegularExpressions.Regex.Replace(value ?? string.Empty, @"[^A-Za-z0-9_\-/]", "_").Trim('/');
            if (string.IsNullOrEmpty(sanitized) || sanitized == Channel.RtspPath) return;
            if (Status == StreamStatus.Streaming || Status == StreamStatus.Stopping)
            {
                OnPropertyChanged(nameof(RtspPath)); // revert UI
                _owner.WarnPathEditWhileStreaming(this);
                return;
            }
            Channel.RtspPath = sanitized;
            _owner.PersistChannelPath(this);
            OnPropertyChanged(nameof(RtspPath));
            OnPropertyChanged(nameof(RtspUrl));
        }
    }

    public ChannelViewModel(MainViewModel owner, Channel channel, VideoFile file)
    {
        _owner = owner;
        Channel = channel;
        VideoFile = file;
    }

    public IAsyncRelayCommand StartCommand => new AsyncRelayCommand(() => _owner.StartChannelAsync(this));
    public IRelayCommand StopCommand => new RelayCommand(() => _owner.StopChannel(this));
    public IRelayCommand RemoveCommand => new RelayCommand(() => _owner.RemoveChannel(this));
    public IRelayCommand CopyUrlCommand => new RelayCommand(() => _owner.CopyChannelUrl(this));

    public void NotifyRtspPathChanged()
    {
        OnPropertyChanged(nameof(RtspUrlPrefix));
        OnPropertyChanged(nameof(RtspPath));
        OnPropertyChanged(nameof(RtspUrl));
    }
}

public partial class MainViewModel : ObservableObject
{
    private readonly SqliteService _db;
    private readonly FfprobeService _probe;
    private readonly ConversionService _conversion;
    private readonly StreamingService _streaming;
    private readonly CancellationTokenSource _mediaMtxMonitorCts = new();
    private bool? _lastMediaMtxReachable;
    private string _lastMediaMtxTarget = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<ChannelViewModel> Channels { get; } = new();
    public System.Collections.ObjectModel.ObservableCollection<string> Logs { get; } = new();

    [ObservableProperty] private string mediaMtxHost = "127.0.0.1";
    [ObservableProperty] private int mediaMtxPort = 8554;
    [ObservableProperty] private string? mediaMtxStatus = "확인 안됨";
    [ObservableProperty] private string bulkRtspTemplate = "rtsp://{host}:{port}/stream_{index}";

    public MainViewModel(SqliteService db, FfprobeService probe, ConversionService conversion,
        StreamingService streaming)
    {
        _db = db;
        _probe = probe;
        _conversion = conversion;
        _streaming = streaming;

        _streaming.StatusChanged += OnStreamStatusChanged;
        _streaming.Log += (_, t) =>
        {
            if (IsInterestingFfmpegLine(t.Line))
                AppendLog($"[ch{t.ChannelId}] {t.Line}");
        };
        _conversion.Log += (_, l) =>
        {
            if (IsInterestingFfmpegLine(l))
                AppendLog("[conv] " + l);
        };
        _conversion.Progress += OnConversionProgress;

        LoadChannels();
        StartMediaMtxMonitor();
    }

    // ffmpeg stderr는 매 프레임마다 진행 상황을 출력하므로, 오류·경고·중요한 상태만 남긴다.
    private static bool IsInterestingFfmpegLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        // 프레임 통계 라인 무시 (예: "frame= 123 fps= 30 ...")
        if (line.StartsWith("frame=", StringComparison.OrdinalIgnoreCase)) return false;
        if (line.StartsWith("size=", StringComparison.OrdinalIgnoreCase)) return false;
        var lower = line.ToLowerInvariant();
        return lower.Contains("error")
            || lower.Contains("failed")
            || lower.Contains("invalid")
            || lower.Contains("could not")
            || lower.Contains("permission denied")
            || lower.Contains("connection refused")
            || lower.Contains("unable to")
            || lower.Contains("no such")
            || lower.Contains("warning");
    }

    public IAsyncRelayCommand AddFileCommand => new AsyncRelayCommand(AddFileAsync);
    public IRelayCommand CheckMediaMtxCommand => new RelayCommand(DetectExternalMediaMtx);
    public IRelayCommand ShowRtspTemplateHelpCommand => new RelayCommand(ShowRtspTemplateHelp);
    public IRelayCommand ApplyRtspTemplateToAllCommand => new RelayCommand(ApplyRtspTemplateToAll);

    private static void ShowRtspTemplateHelp()
    {
        const string message = "RTSP 템플릿은 모든 채널의 URL(호스트/포트/경로)을 한 번에 바꿀 때 사용합니다.\n\n"
            + "사용 가능한 값:\n"
            + "  {host}       현재 MediaMTX Host 값\n"
            + "  {port}       현재 MediaMTX Port 값\n"
            + "  {index}      채널 순번(1, 2, 3...)\n"
            + "  {index:D3}   3자리 0 채움 순번(001, 002...)\n"
            + "  {name}       채널명(경로에 쓸 수 없는 문자는 _로 변경)\n\n"
            + "예시:\n"
            + "  rtsp://{host}:{port}/stream_{index}\n"
            + "  rtsp://10.0.0.{index}:8554/cam_{index:D2}\n"
            + "  stream_{index} (경로만 지정 시 host/port는 채널 기존값 유지)\n\n"
            + "송출 중인 채널은 전체 적용에서 제외됩니다.";

        MessageBox.Show(message, "RTSP 템플릿 도움말", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ApplyRtspTemplateToAll()
    {
        var template = (BulkRtspTemplate ?? string.Empty).Trim();
        if (template.Length == 0)
        {
            AppendLog("[warn] RTSP 템플릿이 비어 있음");
            return;
        }

        var indexRegex = new System.Text.RegularExpressions.Regex(@"\{index(?::([^}]+))?\}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        int updated = 0, skipped = 0, invalid = 0, i = 0;

        foreach (var vm in Channels)
        {
            i++;
            if (vm.Status == StreamStatus.Streaming || vm.Status == StreamStatus.Stopping)
            {
                skipped++;
                continue;
            }

            var sanitizedName = System.Text.RegularExpressions.Regex.Replace(vm.Name ?? string.Empty, @"[^A-Za-z0-9_\-]", "_");
            int currentIndex = i;
            var raw = indexRegex.Replace(template, m =>
            {
                var fmt = m.Groups[1].Success ? m.Groups[1].Value : null;
                try
                {
                    return string.IsNullOrEmpty(fmt) ? currentIndex.ToString() : currentIndex.ToString(fmt);
                }
                catch (FormatException)
                {
                    return currentIndex.ToString();
                }
            });
            raw = raw.Replace("{name}", sanitizedName, StringComparison.OrdinalIgnoreCase)
                     .Replace("{host}", MediaMtxHost ?? string.Empty, StringComparison.OrdinalIgnoreCase)
                     .Replace("{port}", MediaMtxPort.ToString(), StringComparison.OrdinalIgnoreCase)
                     .Trim();

            string candidate;
            if (raw.Contains("://", StringComparison.Ordinal))
            {
                candidate = raw;
            }
            else if (raw.Contains(':', StringComparison.Ordinal) && raw.Contains('/', StringComparison.Ordinal))
            {
                candidate = $"rtsp://{raw}";
            }
            else if (raw.Contains(':', StringComparison.Ordinal))
            {
                candidate = $"rtsp://{raw}/{vm.Channel.RtspPath}";
            }
            else
            {
                candidate = $"rtsp://{vm.Channel.MediaMtxHost}:{vm.Channel.MediaMtxPort}/{raw}";
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || !uri.Scheme.Equals("rtsp", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(uri.Host)
                || uri.Port <= 0)
            {
                invalid++;
                continue;
            }

            var newPath = System.Text.RegularExpressions.Regex.Replace(uri.AbsolutePath.Trim('/'), @"[^A-Za-z0-9_\-/]", "_").Trim('/');
            if (string.IsNullOrEmpty(newPath))
            {
                invalid++;
                continue;
            }

            bool changed = false;
            if (!string.Equals(vm.Channel.MediaMtxHost, uri.Host, StringComparison.OrdinalIgnoreCase)
                || vm.Channel.MediaMtxPort != uri.Port)
            {
                vm.Channel.MediaMtxHost = uri.Host;
                vm.Channel.MediaMtxPort = uri.Port;
                _db.UpdateChannelRtspEndpoint(vm.Channel.Id, uri.Host, uri.Port);
                changed = true;
            }

            if (!string.Equals(vm.Channel.RtspPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                vm.Channel.RtspPath = newPath;
                _db.UpdateChannelRtspPath(vm.Channel.Id, newPath);
                changed = true;
            }

            if (!changed) continue;
            vm.NotifyRtspPathChanged();
            updated++;
        }
        AppendLog($"[bulk] RTSP 템플릿 일괄 적용 (템플릿='{template}', 변경 {updated}, 송출 중 제외 {skipped}, 실패 {invalid})");
    }
    public IAsyncRelayCommand StartAllCommand => new AsyncRelayCommand(StartAllAsync);
    public IRelayCommand StopAllCommand => new RelayCommand(StopAll);

    public void DetectExternalMediaMtx()
    {
        _ = RefreshMediaMtxStatusAsync(logOnChange: true, CancellationToken.None);
    }

    private void StartMediaMtxMonitor()
    {
        _ = Task.Run(async () =>
        {
            while (!_mediaMtxMonitorCts.IsCancellationRequested)
            {
                await RefreshMediaMtxStatusAsync(logOnChange: true, _mediaMtxMonitorCts.Token).ConfigureAwait(false);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), _mediaMtxMonitorCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    private async Task RefreshMediaMtxStatusAsync(bool logOnChange, CancellationToken ct)
    {
        var host = MediaMtxHost;
        var port = MediaMtxPort;
        var reachable = await IsEndpointReachableAsync(host, port, 500, ct).ConfigureAwait(false);

        var status = reachable
            ? $"연결됨 ({host}:{port})"
            : $"연결 안됨 ({host}:{port})";

        Application.Current?.Dispatcher.Invoke(() => { MediaMtxStatus = status; });

        if (!logOnChange) return;

        var target = $"{host}:{port}";
        bool changed = _lastMediaMtxReachable != reachable || !string.Equals(_lastMediaMtxTarget, target, StringComparison.OrdinalIgnoreCase);
        if (changed)
        {
            if (reachable)
                AppendLog($"[mediamtx] 연결됨: {target}");
            else
                AppendLog($"[mediamtx] 연결 안됨: {target}");
            _lastMediaMtxReachable = reachable;
            _lastMediaMtxTarget = target;
        }
    }

    private void LoadChannels()
    {
        foreach (var (ch, vf) in _db.LoadChannels())
            Channels.Add(new ChannelViewModel(this, ch, vf));
    }

    private async Task AddFileAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "동영상 파일|*.mp4;*.mov;*.mkv;*.avi;*.ts;*.flv;*.webm|모든 파일|*.*",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        var copyResult = MessageBox.Show(
            "선택한 동영상 파일을 앱 내부 폴더에 복사해서 저장할까요?\n\n예: 원본을 옮기거나 삭제해도 계속 사용할 수 있습니다.\n아니오: 원본 파일 경로를 그대로 사용합니다.",
            "동영상 파일 복사",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);
        if (copyResult == MessageBoxResult.Cancel) return;

        var copyOriginal = copyResult == MessageBoxResult.Yes;

        foreach (var path in dlg.FileNames)
        {
            try
            {
                await AddSingleFileAsync(path, copyOriginal).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AppendLog($"[error] {Path.GetFileName(path)}: {ex.Message}");
            }
        }
    }

    private async Task AddSingleFileAsync(string filePath, bool copyOriginal)
    {
        var storedPath = copyOriginal
            ? await CopyToMediaLibraryAsync(filePath).ConfigureAwait(true)
            : filePath;

        try
        {
            var probeResult = await _probe.ProbeAsync(storedPath).ConfigureAwait(true);

            var fi = new FileInfo(storedPath);
            var vf = new VideoFile
            {
                FilePath = storedPath,
                FileName = Path.GetFileName(filePath),
                FileSize = fi.Length,
                VideoCodec = probeResult.VideoCodec,
                AudioCodec = probeResult.AudioCodec,
                DurationSeconds = probeResult.DurationSeconds,
                StreamCopyCompatible = probeResult.StreamCopyCompatible,
                IncompatibleReason = probeResult.IncompatibleReason,
                CreatedAt = DateTime.UtcNow
            };
            _db.UpsertVideoFile(vf);

            var rtspPath = System.Text.RegularExpressions.Regex.Replace(
                Path.GetFileNameWithoutExtension(vf.FileName), @"[^A-Za-z0-9_-]", "_");
            if (string.IsNullOrEmpty(rtspPath)) rtspPath = $"ch{DateTime.UtcNow.Ticks}";

            var channel = new Channel
            {
                Name = Path.GetFileNameWithoutExtension(vf.FileName),
                VideoFileId = vf.Id,
                RtspPath = rtspPath,
                MediaMtxHost = MediaMtxHost,
                MediaMtxPort = MediaMtxPort,
            };
            _db.InsertChannel(channel);

            var vm = new ChannelViewModel(this, channel, vf);
            Channels.Add(vm);
            AppendLog($"[add] {vf.FileName} → {channel.RtspUrl} ({(vf.StreamCopyCompatible ? "copy 가능" : "변환 필요")})");
        }
        catch
        {
            if (copyOriginal) TryDeleteFile(storedPath);
            throw;
        }
    }

    private static async Task<string> CopyToMediaLibraryAsync(string sourcePath)
    {
        var libraryDirectory = Path.Combine(AppContext.BaseDirectory, "media");
        Directory.CreateDirectory(libraryDirectory);

        var extension = Path.GetExtension(sourcePath);
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var uniqueName = $"{baseName}_{DateTime.UtcNow:yyyyMMddHHmmssfff}_{Guid.NewGuid():N}{extension}";
        var destinationPath = Path.Combine(libraryDirectory, uniqueName);

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true);
        await source.CopyToAsync(destination).ConfigureAwait(true);
        return destinationPath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    public async Task StartChannelAsync(ChannelViewModel vm)
    {
        try
        {
            if (_streaming.IsStreaming(vm.Channel.Id))
            {
                AppendLog($"[skip] {vm.Name} 이미 송출 중");
                return;
            }

            if (!await IsEndpointReachableAsync(vm.Channel.MediaMtxHost, vm.Channel.MediaMtxPort, 800).ConfigureAwait(true))
            {
                vm.Status = StreamStatus.Error;
                vm.StatusMessage = "MediaMTX 연결 불가";
                AppendLog($"[error] {vm.Name} MediaMTX 연결 실패: {vm.Channel.MediaMtxHost}:{vm.Channel.MediaMtxPort}");
                return;
            }

            var conflict = FindRtspPathConflict(vm.Channel);
            if (conflict != null)
            {
                vm.Status = StreamStatus.Error;
                vm.StatusMessage = "RTSP 경로 충돌";
                AppendLog($"[error] {vm.Name} RTSP 경로 충돌: '{vm.Channel.RtspPath}' 경로를 '{conflict.Name}' 채널이 이미 사용 중");
                return;
            }

            string sourcePath = vm.VideoFile.FilePath;
            if (!vm.VideoFile.StreamCopyCompatible)
            {
                vm.Status = StreamStatus.Converting;
                vm.StatusMessage = "변환 중";
                AppendLog($"[start] {vm.Name} 변환 시작");
                sourcePath = await _conversion.EnsureCompatibleAsync(vm.VideoFile, CancellationToken.None).ConfigureAwait(true);
                vm.ConversionProgress = 100;
                vm.Status = StreamStatus.Ready;
                AppendLog($"[start] {vm.Name} 변환 완료");

                // 갓 변환된 파일은 OS 버퍼 flush와 파일 핸들 해제가 완료되기까지 잠깐의 여유가 필요하다.
                // 이 대기 없이 즉시 스트리밍을 시작하면 ffmpeg가 파일을 잘못 읽어 초기 오류로 종료되는 사례가 있음.
                await WaitForFileReadyAsync(sourcePath).ConfigureAwait(true);
            }

            AppendLog($"[start] {vm.Name} 송출 시작 → {vm.Channel.RtspUrl}");
            vm.StatusMessage = "송출 시작";
            await _streaming.StartAsync(vm.Channel, sourcePath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            vm.Status = StreamStatus.Error;
            vm.StatusMessage = ex.Message;
            AppendLog($"[error] {vm.Name} {ex.Message}");
        }
    }

    private ChannelViewModel? FindRtspPathConflict(Channel current)
    {
        return Channels.FirstOrDefault(c =>
            c.Channel.Id != current.Id
            && c.Channel.MediaMtxPort == current.MediaMtxPort
            && string.Equals(c.Channel.MediaMtxHost, current.MediaMtxHost, StringComparison.OrdinalIgnoreCase)
            && string.Equals(c.Channel.RtspPath, current.RtspPath, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> IsEndpointReachableAsync(string host, int port, int timeoutMs, CancellationToken ct = default)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port, ct).AsTask();
            var timeoutTask = Task.Delay(timeoutMs, ct);
            var completed = await Task.WhenAny(connectTask, timeoutTask).ConfigureAwait(false);
            if (completed != connectTask) return false;
            await connectTask.ConfigureAwait(false);
            return client.Connected;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    private static async Task WaitForFileReadyAsync(string path)
    {
        for (int i = 0; i < 10; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (fs.Length > 0)
                {
                    var buffer = new byte[Math.Min(4096, fs.Length)];
                    _ = await fs.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                    return;
                }
            }
            catch (IOException) { }
            await Task.Delay(100).ConfigureAwait(false);
        }
    }

    public void StopChannel(ChannelViewModel vm)
    {
        if (_streaming.IsStreaming(vm.Channel.Id))
            AppendLog($"[stop] {vm.Name} 송출 중지");
        _streaming.Stop(vm.Channel.Id);
    }

    public void RemoveChannel(ChannelViewModel vm)
    {
        _streaming.Stop(vm.Channel.Id);
        _db.DeleteChannel(vm.Channel.Id);
        Channels.Remove(vm);
        AppendLog($"[remove] {vm.Name}");
    }

    private async Task StartAllAsync()
    {
        AppendLog($"[bulk] 전체 시작 ({Channels.Count}개)");
        // 순차 실행: 변환이 동시에 진행되면 CPU 부하가 심하고, 각 채널 상태를 명확히 파악할 수 있다.
        foreach (var vm in Channels.ToList())
        {
            if (_streaming.IsStreaming(vm.Channel.Id)) continue;
            await StartChannelAsync(vm).ConfigureAwait(true);
        }
    }

    private void StopAll()
    {
        AppendLog("[bulk] 전체 중지");
        foreach (var vm in Channels.ToList())
        {
            _streaming.Stop(vm.Channel.Id);
        }
    }

    private void OnStreamStatusChanged(object? sender, StreamEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var c in Channels)
            {
                if (c.Channel.Id == e.ChannelId)
                {
                    c.Status = e.Status;
                    c.StatusMessage = e.Message ?? string.Empty;
                    if (e.Status == StreamStatus.Error && e.Message != null &&
                        e.Message.Contains("stream copy failed", StringComparison.OrdinalIgnoreCase))
                    {
                        // Force pre-conversion path for next start.
                        c.VideoFile.StreamCopyCompatible = false;
                        c.VideoFile.IncompatibleReason = "runtime stream-copy failure";
                        _db.UpsertVideoFile(c.VideoFile);
                    }
                    break;
                }
            }
        });
    }

    private void OnConversionProgress(object? sender, ConversionProgressEventArgs e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var c in Channels)
                if (c.Status == StreamStatus.Converting)
                    c.ConversionProgress = e.Percent;
        });
    }

    private void AppendLog(string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Logs.Add($"{DateTime.Now:HH:mm:ss} {message}");
            while (Logs.Count > 500) Logs.RemoveAt(0);
        });
    }

    public void ShutdownAll()
    {
        try { _mediaMtxMonitorCts.Cancel(); } catch { }
        _streaming.StopAll();
    }

    public void PersistChannelPath(ChannelViewModel vm)
    {
        _db.UpdateChannelRtspPath(vm.Channel.Id, vm.Channel.RtspPath);
        AppendLog($"[edit] {vm.Name} RTSP 경로 변경 → {vm.RtspUrl}");
    }

    public void WarnPathEditWhileStreaming(ChannelViewModel vm)
    {
        AppendLog($"[warn] {vm.Name} 송출 중에는 RTSP 경로를 변경할 수 없음");
    }

    public void CopyChannelUrl(ChannelViewModel vm)
    {
        try
        {
            Clipboard.SetText(vm.RtspUrl);
            AppendLog($"[copy] {vm.Name} URL 복사됨 → {vm.RtspUrl}");
        }
        catch (Exception ex)
        {
            AppendLog($"[error] URL 복사 실패: {ex.Message}");
        }
    }
}
