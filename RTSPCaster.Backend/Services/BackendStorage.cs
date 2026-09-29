using RTSPCaster.Services;

namespace RTSPCaster.Backend.Services;

public sealed class BackendOptions
{
    public string? DataDirectory { get; set; }
    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
    public long MaxUploadBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed class BackendStorage : IDisposable
{
    private readonly FileStream _instanceLock;
    public string Root { get; }
    public string DatabasePath => Path.Combine(Root, "rtspcaster.db");
    public string MediaDirectory => Path.Combine(Root, "media");
    public string ConversionDirectory => Path.Combine(Root, "converted");
    public string TimelineDirectory => Path.Combine(ConversionDirectory, "copy-timeline-v1");
    public string LogDirectory => Path.Combine(Root, "logs");

    public BackendStorage(BackendOptions options)
    {
        Root = Path.GetFullPath(string.IsNullOrWhiteSpace(options.DataDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RTSPCaster.Backend")
            : options.DataDirectory);
        Directory.CreateDirectory(Root);
        _instanceLock = new FileStream(Path.Combine(Root, "backend.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Directory.CreateDirectory(MediaDirectory);
        Directory.CreateDirectory(ConversionDirectory);
    }

    public static string ResolveTool(string? configured, string name) =>
        string.IsNullOrWhiteSpace(configured)
            ? ToolLocator.Find(ToolLocator.ExecutableName(name)) ?? ToolLocator.ExecutableName(name)
            : configured;

    public void Dispose() => _instanceLock.Dispose();
}
