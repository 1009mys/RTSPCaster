using System;
using System.IO;

namespace RTSPCaster.Services;

internal static class AppStoragePaths
{
    internal static string DatabasePath => Path.Combine(DataDirectory, "rtspcaster.db");

    internal static string ConversionCacheDirectory => OperatingSystem.IsLinux()
        ? Path.Combine(XdgDirectory("XDG_CACHE_HOME", ".cache"), "RTSPCaster", "converted")
        : Path.Combine(AppContext.BaseDirectory, "converted");

    private static string DataDirectory => OperatingSystem.IsLinux()
        ? Path.Combine(XdgDirectory("XDG_DATA_HOME", Path.Combine(".local", "share")), "RTSPCaster")
        : AppContext.BaseDirectory;

    private static string XdgDirectory(string variable, string fallback)
    {
        var configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured) && Path.IsPathFullyQualified(configured))
            return configured;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), fallback);
    }
}