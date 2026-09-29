using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RTSPCaster.Services;

if (args is ["--child"])
{
    Thread.Sleep(TimeSpan.FromSeconds(30));
    return;
}

var temp = Path.Combine(Path.GetTempPath(), "RTSPCaster-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    var ffmpegName = ToolLocator.ExecutableName("ffmpeg");
    var ffprobeName = ToolLocator.ExecutableName("ffprobe");
    Assert(ffmpegName == (OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg"), "ffmpeg executable name");
    Assert(ffprobeName == (OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe"), "ffprobe executable name");
    Assert(new FfprobeService().FfprobePath.EndsWith(ffprobeName, StringComparison.Ordinal), "ffprobe default");

    var db = new SqliteService(Path.Combine(temp, "db;test.sqlite"));
    db.SetSetting("smoke", "ok");
    Assert(db.GetSetting("smoke") == "ok", "SQLite path with semicolon");

    if (OperatingSystem.IsLinux())
    {
        var data = Path.Combine(temp, "data");
        var cache = Path.Combine(temp, "cache");
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", data);
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", cache);
        var linuxDb = new SqliteService();
        linuxDb.SetSetting("smoke", "linux");
        Assert(File.Exists(Path.Combine(data, "RTSPCaster", "rtspcaster.db")), "XDG data path");
        Assert(new ConversionService(linuxDb).CacheDirectory == Path.Combine(cache, "RTSPCaster", "converted"), "XDG cache path");
        Assert(Directory.Exists(Path.Combine(cache, "RTSPCaster", "converted")), "cache directory created");
        Assert(ToolLocator.Find(ffmpegName) != null, "Linux PATH ffmpeg lookup");
        Assert(ToolLocator.Find(ffprobeName) != null, "Linux PATH ffprobe lookup");
    }

    var start = new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false };
    start.ArgumentList.Add("--child");
    using var sleepingChild = Process.Start(start) ?? throw new InvalidOperationException("Child failed to start");
    try
    {
        using (var tracker = new ChildProcessTracker()) tracker.Track(sleepingChild);
        Assert(sleepingChild.WaitForExit(5000), "tracked child stopped on dispose");
    }
    finally
    {
        if (!sleepingChild.HasExited) sleepingChild.Kill(entireProcessTree: true);
    }

    Console.WriteLine("RTSPCaster.Lib smoke tests passed");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(temp, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}
