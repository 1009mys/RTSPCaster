using System.Text.RegularExpressions;
using RTSPCaster.Backend.Contracts;
using RTSPCaster.Models;

namespace RTSPCaster.Backend.Services;

public sealed partial class CasterService
{
    public async Task<ChannelSnapshot> UploadAsync(IFormFile upload, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        ct = linked.Token;
        if (upload.Length <= 0) throw new ApiException(400, "빈 파일은 등록할 수 없습니다.");
        if (upload.Length > _options.MaxUploadBytes) throw new ApiException(413, "업로드 크기 제한을 초과했습니다.");
        var name = Path.GetFileName(upload.FileName.Replace('\\', '/'));
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255)
            throw new ApiException(400, "파일 이름은 1~255자여야 합니다.");
        var extension = Path.GetExtension(name);
        if (!Regex.IsMatch(extension, @"^\.[A-Za-z0-9]{1,16}$")) extension = ".media";
        var path = Path.Combine(_storage.MediaDirectory, $"{Guid.NewGuid():N}{extension}");
        var committed = false;
        try
        {
            long size = 0;
            await using (var input = upload.OpenReadStream())
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    size += read;
                    if (size > _options.MaxUploadBytes) throw new ApiException(413, "업로드 크기 제한을 초과했습니다.");
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            if (size == 0) throw new ApiException(400, "빈 파일은 등록할 수 없습니다.");
            var metadata = await ProbeAsync(path, ct);
            await _commands.WaitAsync(ct);
            try
            {
                lock (_sync)
                {
                    ct.ThrowIfCancellationRequested();
                    var video = new VideoFile { FilePath = path, FileName = name, FileSize = size };
                    ApplyMetadata(video, metadata);
                    _db.UpsertVideoFile(video);
                    var stem = Regex.Replace(Path.GetFileNameWithoutExtension(name), @"[^A-Za-z0-9_\-]", "_");
                    if (stem.Length == 0) stem = "stream";
                    var rtspPath = stem;
                    var suffix = 2;
                    while (_channels.Values.Any(item => item.Channel.MediaMtxHost == _settings.MediaMtxHost
                        && item.Channel.MediaMtxPort == _settings.MediaMtxPort
                        && item.Channel.RtspPath.Equals(rtspPath, StringComparison.OrdinalIgnoreCase)))
                        rtspPath = $"{stem}_{suffix++}";
                    var channel = new Channel
                    {
                        Name = Path.GetFileNameWithoutExtension(name), VideoFileId = video.Id, RtspPath = rtspPath,
                        MediaMtxHost = _settings.MediaMtxHost, MediaMtxPort = _settings.MediaMtxPort
                    };
                    _db.InsertChannel(channel);
                    var item = new RuntimeChannel(channel, video);
                    _channels.Add(channel.Id, item);
                    committed = true;
                    Log($"파일 등록: {name} → {channel.RtspUrl}", channel.Id);
                    return Snapshot(item);
                }
            }
            finally { _commands.Release(); }
        }
        finally
        {
            if (!committed)
            {
                try { File.Delete(path); }
                catch (IOException exception) { _logger.LogWarning(exception, "Failed upload cleanup: {Path}", path); }
                catch (UnauthorizedAccessException exception) { _logger.LogWarning(exception, "Failed upload cleanup: {Path}", path); }
            }
        }
    }
}
