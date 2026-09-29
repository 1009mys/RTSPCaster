using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace RTSPCaster.Services;

public sealed class StreamCopyTimelineService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheLocks = new(StringComparer.Ordinal);
    private readonly ChildProcessTracker? _tracker;

    public string CacheDirectory { get; }

    public StreamCopyTimelineService(ChildProcessTracker? tracker = null, string? cacheDirectory = null)
    {
        _tracker = tracker;
        CacheDirectory = cacheDirectory ?? Path.Combine(AppStoragePaths.ConversionCacheDirectory, "copy-timeline-v1");
    }

    public static bool IsTimestampDiscontinuity(string line) =>
        line.Contains("Non-monotonic DTS", StringComparison.OrdinalIgnoreCase)
        || line.Contains("Non-monotonous DTS", StringComparison.OrdinalIgnoreCase)
        || line.Contains("non monotonically increasing dts", StringComparison.OrdinalIgnoreCase)
        || line.Contains("non-monotonically increasing dts", StringComparison.OrdinalIgnoreCase)
        || (line.Contains("DTS", StringComparison.OrdinalIgnoreCase)
            && line.Contains("out of order", StringComparison.OrdinalIgnoreCase));

    public string? FindCached(string sourcePath)
    {
        var path = GetCachePath(sourcePath);
        return File.Exists(path) && new FileInfo(path).Length > 0 ? path : null;
    }

    public async Task<string> PrepareAsync(string sourcePath, string ffmpegPath, Action<string> log, CancellationToken ct)
    {
        var destination = GetCachePath(sourcePath);
        var gate = CacheLocks.GetOrAdd(destination, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                return destination;

            Directory.CreateDirectory(CacheDirectory);
            temporary = Path.Combine(CacheDirectory, $"{Guid.NewGuid():N}.tmp.mp4");
            log("[repair] Rebuilding container timestamps with stream copy; no re-encoding.");
            await RunCheckedAsync(ffmpegPath,
                ["-nostdin", "-y", "-v", "warning", "-nostats", "-fflags", "+genpts", "-i", sourcePath,
                 "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-avoid_negative_ts", "make_zero",
                 "-movflags", "+faststart", temporary], log, ct).ConfigureAwait(false);

            if (!File.Exists(temporary) || new FileInfo(temporary).Length == 0)
                throw new InvalidOperationException("DTS 복구 실패: 리먹싱 결과가 없습니다.");

            log("[repair] Checking timestamps across two playback iterations.");
            await RunCheckedAsync(ffmpegPath,
                ["-nostdin", "-v", "warning", "-nostats", "-fflags", "+genpts", "-stream_loop", "1", "-i", temporary,
                 "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-f", "null", "-"], log, ct).ConfigureAwait(false);

            ct.ThrowIfCancellationRequested();
            if (GetCachePath(sourcePath) != destination)
                throw new InvalidOperationException("DTS 복구 중 원본 파일이 변경되었습니다.");

            File.Move(temporary, destination, overwrite: true);
            temporary = null;
            log($"[repair] Verified stream-copy cache: {destination}");
            return destination;
        }
        finally
        {
            try
            {
                if (temporary != null && File.Exists(temporary)) File.Delete(temporary);
            }
            finally
            {
                gate.Release();
            }
        }
    }

    private async Task RunCheckedAsync(string executable, string[] arguments, Action<string> log, CancellationToken ct)
    {
        string? lastError = null;
        var exitCode = await MediaProcess.RunAsync(executable, arguments, _tracker, ct, null, line =>
        {
            lastError = line;
            log(line);
        }).ConfigureAwait(false);

        if (exitCode != 0)
            throw new InvalidOperationException($"복사 리먹싱/반복 검사 중 FFmpeg가 비정상 종료되었습니다. (exit {exitCode}) {lastError}");
    }

    private string GetCachePath(string sourcePath)
    {
        var file = new FileInfo(sourcePath);
        if (!file.Exists) throw new FileNotFoundException("Video file not found", sourcePath);
        var identity = FormattableString.Invariant($"copy-timeline-v1|{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(CacheDirectory, $"{hash}.mp4");
    }
}
