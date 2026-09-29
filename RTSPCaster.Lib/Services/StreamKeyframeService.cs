using System.Globalization;
using System.Text.Json;

namespace RTSPCaster.Services;

public readonly record struct StreamResumePoint(double FailedPositionSeconds, double? NextKeyframeSeconds);

public sealed class StreamKeyframeService
{
    private readonly ChildProcessTracker? _tracker;
    public string FfprobePath { get; set; } = ToolLocator.Find(ToolLocator.ExecutableName("ffprobe")) ?? ToolLocator.ExecutableName("ffprobe");

    public StreamKeyframeService(ChildProcessTracker? tracker = null) => _tracker = tracker;

    public async Task<StreamResumePoint> FindNextAsync(string sourcePath, double seekSeconds,
        double outputSeconds, bool looping, Action<string> log, CancellationToken ct)
    {
        if (!double.IsFinite(seekSeconds) || seekSeconds < 0 || !double.IsFinite(outputSeconds) || outputSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(outputSeconds));

        var json = new System.Text.StringBuilder();
        var metadataExit = await MediaProcess.RunAsync(FfprobePath,
            ["-v", "error", "-show_format", "-of", "json", sourcePath], _tracker, ct,
            line => json.AppendLine(line), log).ConfigureAwait(false);
        if (metadataExit != 0)
            throw new InvalidOperationException($"영상 길이 검사 실패 (ffprobe exit {metadataExit}).");
        using var document = JsonDocument.Parse(json.ToString());
        if (!document.RootElement.TryGetProperty("format", out var format)
            || !format.TryGetProperty("duration", out var durationElement)
            || !TryNumber(durationElement.GetString(), out var duration) || duration <= 0)
            throw new InvalidOperationException("영상 길이를 확인할 수 없어 안전한 재개 위치를 계산할 수 없습니다.");

        var position = seekSeconds + outputSeconds;
        if (looping)
        {
            position %= duration;
            // EOF의 시각을 다음 반복의 0초로 오인해 같은 오류 구간으로 돌아가지 않는다.
            if (outputSeconds > 0 && (position < 0.001 || duration - position < 0.001))
                position = duration;
        }
        else
        {
            position = Math.Min(position, duration);
        }

        var startTime = 0d;
        if (format.TryGetProperty("start_time", out var start) && TryNumber(start.GetString(), out var value))
            startTime = value;

        double? next = null;
        var exitCode = await MediaProcess.RunAsync(FfprobePath,
            ["-v", "error", "-select_streams", "v:0", "-show_packets", "-show_entries", "packet=pts_time,flags",
             "-of", "compact=p=0:nk=0", sourcePath], _tracker, ct,
            line =>
            {
                string? pts = null;
                string? flags = null;
                foreach (var part in line.Split('|'))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length != 2) continue;
                    if (pair[0] == "pts_time") pts = pair[1];
                    if (pair[0] == "flags") flags = pair[1];
                }
                if (flags == null || !flags.Contains('K') || flags.Contains('C') || flags.Contains('D')
                    || !TryNumber(pts, out var timestamp)) return;
                var candidate = timestamp - startTime;
                if (candidate > position + 0.001 && candidate < duration
                    && (!next.HasValue || candidate < next.Value))
                    next = candidate;
            }, log).ConfigureAwait(false);

        if (exitCode != 0)
            throw new InvalidOperationException($"키프레임 검사 실패 (ffprobe exit {exitCode}).");

        return new StreamResumePoint(position, next);
    }

    private static bool TryNumber(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}
