using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using RTSPCaster.Models;

namespace RTSPCaster.Services;

public class ConversionProgressEventArgs : EventArgs
{
    public double Percent { get; init; }
    public string? Line { get; init; }
}

public class ConversionService
{
    private readonly SqliteService _db;
    private readonly ChildProcessTracker? _tracker;
    public string FfmpegPath { get; set; } = ToolLocator.Find("ffmpeg.exe") ?? "ffmpeg.exe";
    public string CacheDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "converted");

    public ConversionService(SqliteService db, ChildProcessTracker? tracker = null)
    {
        _db = db;
        _tracker = tracker;
        Directory.CreateDirectory(CacheDirectory);
    }

    public event EventHandler<ConversionProgressEventArgs>? Progress;
    public event EventHandler<string>? Log;

    public async Task<string> EnsureCompatibleAsync(VideoFile file, CancellationToken ct)
    {
        if (file.StreamCopyCompatible)
            return file.FilePath;

        var hash = ComputeQuickHash(file.FilePath);
        var cached = _db.FindCache(file.Id, hash);
        if (cached != null && File.Exists(cached.ConvertedPath))
        {
            Log?.Invoke(this, $"[cache] using converted file: {cached.ConvertedPath}");
            return cached.ConvertedPath;
        }

        var outPath = Path.Combine(CacheDirectory, Path.GetFileNameWithoutExtension(file.FilePath) + $"_{hash[..8]}.mp4");
        await RunConversionAsync(file, outPath, ct).ConfigureAwait(false);

        _db.SaveCache(new ConversionCacheEntry
        {
            SourceVideoFileId = file.Id,
            SourcePath = file.FilePath,
            ConvertedPath = outPath,
            SourceHash = hash,
            ConvertedAt = DateTime.UtcNow
        });
        return outPath;
    }

    private async Task RunConversionAsync(VideoFile file, string outPath, CancellationToken ct)
    {
        if (File.Exists(outPath)) File.Delete(outPath);

        var psi = new ProcessStartInfo
        {
            FileName = FfmpegPath,
            Arguments = $"-y -i \"{file.FilePath}\" -c:v libx264 -preset veryfast -pix_fmt yuv420p -c:a aac -b:a 128k -movflags +faststart \"{outPath}\"",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            Log?.Invoke(this, e.Data);
            var pct = ParseProgress(e.Data, file.DurationSeconds);
            if (pct.HasValue)
                Progress?.Invoke(this, new ConversionProgressEventArgs { Percent = pct.Value, Line = e.Data });
        };

        proc.Start();
        _tracker?.Track(proc);
        proc.BeginErrorReadLine();

        using (ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } }))
        {
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"Conversion failed. Exit code {proc.ExitCode}");

        if (!File.Exists(outPath) || new FileInfo(outPath).Length == 0)
            throw new InvalidOperationException("Conversion produced no output.");

        Progress?.Invoke(this, new ConversionProgressEventArgs { Percent = 100 });
    }

    private static readonly Regex TimeRegex = new(@"time=(\d+):(\d+):(\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private static double? ParseProgress(string line, double duration)
    {
        if (duration <= 0) return null;
        var m = TimeRegex.Match(line);
        if (!m.Success) return null;
        var h = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var mi = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        var s = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
        var t = h * 3600 + mi * 60 + s;
        return Math.Clamp(t / duration * 100.0, 0, 100);
    }

    private static string ComputeQuickHash(string filePath)
    {
        var fi = new FileInfo(filePath);
        using var md5 = MD5.Create();
        var input = System.Text.Encoding.UTF8.GetBytes($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
        return Convert.ToHexString(md5.ComputeHash(input));
    }
}
