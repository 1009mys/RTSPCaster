using System;
using System.IO;

namespace RTSPCaster.Services;

public static class ToolLocator
{
    public static string? Find(string exeName)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, exeName),
            Path.Combine(baseDir, "tools", exeName),
            Path.Combine(baseDir, "ffmpeg", exeName),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                try
                {
                    var p = Path.Combine(dir, exeName);
                    if (File.Exists(p)) return p;
                }
                catch { }
            }
        }
        return null;
    }
}
