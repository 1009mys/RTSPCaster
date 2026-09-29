using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RTSPCaster.Models;

namespace RTSPCaster.Services;

public class FfprobeService
{
    private static readonly string[] CompatibleVideoCodecs = { "h264", "hevc", "h265" };
    private static readonly string[] CompatibleAudioCodecs = { "aac", "mp3", "opus" };

    public string FfprobePath { get; set; } = ToolLocator.Find("ffprobe.exe") ?? "ffprobe.exe";

    public async Task<ProbeResult> ProbeAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("Video file not found", filePath);

        var psi = new ProcessStartInfo
        {
            FileName = FfprobePath,
            Arguments = $"-v error -print_format json -show_streams -show_format \"{filePath}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"ffprobe failed: {stderr}");

        return Parse(stdout);
    }

    private static ProbeResult Parse(string json)
    {
        var result = new ProbeResult { RawJson = json };
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("format", out var format) &&
            format.TryGetProperty("duration", out var dur) &&
            double.TryParse(dur.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            result.DurationSeconds = d;
        }

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                if (type == "video" && result.VideoCodec == null)
                {
                    result.VideoCodec = codec;
                    if (s.TryGetProperty("width", out var w) && w.TryGetInt32(out var width))
                        result.VideoWidth = width;
                    if (s.TryGetProperty("height", out var h) && h.TryGetInt32(out var height))
                        result.VideoHeight = height;
                }
                else if (type == "audio" && result.AudioCodec == null) result.AudioCodec = codec;
            }
        }

        var reasons = new StringBuilder();
        if (string.IsNullOrEmpty(result.VideoCodec))
        {
            reasons.Append("video stream missing; ");
        }
        else if (Array.IndexOf(CompatibleVideoCodecs, result.VideoCodec.ToLowerInvariant()) < 0)
        {
            reasons.Append($"video codec '{result.VideoCodec}' not RTSP-compatible; ");
        }

        if (!string.IsNullOrEmpty(result.AudioCodec) &&
            Array.IndexOf(CompatibleAudioCodecs, result.AudioCodec.ToLowerInvariant()) < 0)
        {
            reasons.Append($"audio codec '{result.AudioCodec}' not RTSP-compatible; ");
        }

        result.StreamCopyCompatible = reasons.Length == 0;
        result.IncompatibleReason = reasons.Length == 0 ? null : reasons.ToString().TrimEnd(' ', ';');
        return result;
    }
}
