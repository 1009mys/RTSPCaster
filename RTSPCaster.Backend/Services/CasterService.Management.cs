using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using RTSPCaster.Backend.Contracts;

namespace RTSPCaster.Backend.Services;

public sealed partial class CasterService
{
    private static void ValidateSettings(CasterSettings settings)
    {
        var results = new List<ValidationResult>();
        if (!Validator.TryValidateObject(settings, new ValidationContext(settings), results, true))
            throw new ApiException(400, string.Join(" ", results.Select(result => result.ErrorMessage)));
        ApiValidation.Host(settings.MediaMtxHost);
    }

    private void ApplyRestartPolicy()
    {
        _streaming.AutoRestartEnabled = _settings.AutoRestartEnabled;
        _streaming.MaxAutoRestartAttempts = _settings.MaxAutoRestartAttempts;
        _streaming.AutoRestartBaseDelay = TimeSpan.FromSeconds(_settings.AutoRestartBaseDelaySeconds);
        _streaming.AutoRestartAttemptResetThreshold = TimeSpan.FromSeconds(_settings.AutoRestartResetThresholdSeconds);
    }

    public async Task<CasterSettings> UpdateSettingsAsync(CasterSettings settings, CancellationToken ct)
    {
        ValidateSettings(settings);
        await _commands.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                _db.SetSetting("BackendSettings", JsonSerializer.Serialize(settings));
                if (_settings.MediaMtxHost != settings.MediaMtxHost || _settings.MediaMtxPort != settings.MediaMtxPort)
                    _connection = new(settings.MediaMtxHost, settings.MediaMtxPort, null, null);
                _settings = settings;
                ApplyRestartPolicy();
                Log("설정 및 자동 재시작 정책 저장됨");
                return _settings;
            }
        }
        finally { _commands.Release(); }
    }

    private void EnsureUnique(int id, string host, int port, string path)
    {
        if (_channels.Values.Any(item => item.Channel.Id != id
            && item.Channel.MediaMtxPort == port
            && string.Equals(item.Channel.MediaMtxHost, host, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.Channel.RtspPath, path, StringComparison.OrdinalIgnoreCase)))
            throw new ApiException(409, "다른 채널에서 같은 RTSP URL을 사용하고 있습니다.");
    }

    public async Task<ChannelSnapshot> UpdateEndpointAsync(int id, ChannelEndpointRequest request, CancellationToken ct)
    {
        ApiValidation.Host(request.MediaMtxHost);
        if (request.MediaMtxPort is < 1 or > 65535) throw new ApiException(400, "포트 범위는 1~65535입니다.");
        var path = ApiValidation.Path(request.RtspPath);
        await _commands.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                var item = Find(id);
                if (item.Busy) throw new ApiException(409, "준비/송출/중지 중에는 URL을 변경할 수 없습니다.");
                EnsureUnique(id, request.MediaMtxHost, request.MediaMtxPort, path);
                SaveEndpoint(item, request with { RtspPath = path });
                Log($"RTSP URL 변경: {item.Channel.RtspUrl}", id);
                return Snapshot(item);
            }
        }
        finally { _commands.Release(); }
    }

    private void SaveEndpoint(RuntimeChannel item, ChannelEndpointRequest endpoint)
    {
        _db.UpdateChannelRtspEndpoint(item.Channel.Id, endpoint.MediaMtxHost, endpoint.MediaMtxPort);
        _db.UpdateChannelRtspPath(item.Channel.Id, endpoint.RtspPath);
        item.Channel.MediaMtxHost = endpoint.MediaMtxHost;
        item.Channel.MediaMtxPort = endpoint.MediaMtxPort;
        item.Channel.RtspPath = endpoint.RtspPath;
    }

    public async Task<TemplateResult> ApplyTemplateAsync(string template, CancellationToken ct)
    {
        await _commands.WaitAsync(ct);
        try
        {
            lock (_sync)
            {
                // Validate even when there are no channels or every channel is busy.
                ApiValidation.ExpandTemplate(template, 1, "channel", _settings.MediaMtxHost,
                    _settings.MediaMtxPort, "stream", _settings);
                var proposed = new Dictionary<int, ChannelEndpointRequest>();
                var skipped = new List<int>();
                var index = 0;
                foreach (var item in _channels.Values)
                {
                    index++;
                    if (item.Busy) { skipped.Add(item.Channel.Id); continue; }
                    proposed.Add(item.Channel.Id, ApiValidation.ExpandTemplate(template, index, item.Channel.Name,
                        item.Channel.MediaMtxHost, item.Channel.MediaMtxPort, item.Channel.RtspPath, _settings));
                }
                var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in _channels.Values)
                {
                    var c = item.Channel;
                    var endpoint = proposed.GetValueOrDefault(c.Id) ?? new(c.RtspPath, c.MediaMtxHost, c.MediaMtxPort);
                    if (!urls.Add($"{endpoint.MediaMtxHost}:{endpoint.MediaMtxPort}/{endpoint.RtspPath}"))
                        throw new ApiException(409, "템플릿 결과에 중복 RTSP URL이 있습니다. 변경하지 않았습니다.");
                }
                var updated = new List<int>();
                foreach (var (id, endpoint) in proposed)
                {
                    var item = _channels[id];
                    if (item.Channel.RtspPath == endpoint.RtspPath && item.Channel.MediaMtxHost == endpoint.MediaMtxHost
                        && item.Channel.MediaMtxPort == endpoint.MediaMtxPort) continue;
                    SaveEndpoint(item, endpoint);
                    updated.Add(id);
                }
                var settings = _settings with { BulkRtspTemplate = template.Trim() };
                _db.SetSetting("BackendSettings", JsonSerializer.Serialize(settings));
                _settings = settings;
                Log($"RTSP 템플릿 적용: 변경 {updated.Count}, 처리 중 제외 {skipped.Count}");
                return new(updated.ToArray(), skipped.ToArray());
            }
        }
        finally { _commands.Release(); }
    }
}
