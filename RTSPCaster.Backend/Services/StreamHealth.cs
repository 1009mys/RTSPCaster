using System.Globalization;
using System.Text.RegularExpressions;
using RTSPCaster.Backend.Contracts;

namespace RTSPCaster.Backend.Services;

internal static class StreamHealth
{
    private static readonly Regex Fps = new(@"fps=\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Bitrate = new(@"bitrate=\s*([0-9]+(?:\.[0-9]+)?)\s*(bits/s|kbits/s|mbits/s)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Speed = new(@"speed=\s*([0-9]+(?:\.[0-9]+)?)x", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Time = new(@"time=\s*(\d+:\d+:\d+(?:\.\d+)?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static HealthSample? Parse(string line, DateTime startedAt)
    {
        if (!line.Contains("fps=", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("bitrate=", StringComparison.OrdinalIgnoreCase)) return null;
        var bitrateMatch = Bitrate.Match(line);
        var bitrate = Number(bitrateMatch);
        bitrate *= bitrateMatch.Groups[2].Value.ToLowerInvariant() switch
        {
            "bits/s" => 0.001,
            "mbits/s" => 1000,
            _ => 1
        };
        var now = DateTime.UtcNow;
        double? latency = TimeSpan.TryParse(Time.Match(line).Groups[1].Value, CultureInfo.InvariantCulture, out var elapsed)
            ? Math.Max(0, (now - startedAt - elapsed).TotalMilliseconds) : null;
        return new HealthSample(now, Number(Fps.Match(line)), bitrate, Number(Speed.Match(line)), latency);
    }

    public static bool IsInteresting(string line) => new[]
    {
        "error", "failed", "invalid", "could not", "permission denied", "connection refused",
        "unable to", "no such", "warning", "[cache]", "[repair]"
    }.Any(word => line.Contains(word, StringComparison.OrdinalIgnoreCase));

    private static double Number(Match match) =>
        double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
        && double.IsFinite(value) ? value : 0;
}
