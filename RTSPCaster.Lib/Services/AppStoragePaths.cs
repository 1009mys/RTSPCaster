using System;
using System.IO;

namespace RTSPCaster.Services;

internal static class AppStoragePaths
{
    internal static string DatabasePath => Path.Combine(AppContext.BaseDirectory, "rtspcaster.db");

    internal static string ConversionCacheDirectory => Path.Combine(AppContext.BaseDirectory, "converted");
}