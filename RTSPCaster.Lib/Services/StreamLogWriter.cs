using System.Globalization;
using System.Text;

namespace RTSPCaster.Services;

public sealed class StreamLogWriter
{
    private readonly object _sync = new();
    private bool _enabled;
    private static readonly Encoding LogEncoding = new UTF8Encoding(false);

    public string DirectoryPath { get; }

    public bool Enabled
    {
        get { lock (_sync) return _enabled; }
        set { lock (_sync) _enabled = value; }
    }

    public StreamLogWriter(string? directoryPath = null)
    {
        DirectoryPath = directoryPath ?? Path.Combine(AppContext.BaseDirectory, "log");
    }

    public void Write(int channelId, string source, string line, DateTimeOffset? timestamp = null)
    {
        lock (_sync)
        {
            if (!_enabled) return;
            var time = timestamp ?? DateTimeOffset.Now;
            var fileName = FormattableString.Invariant($"ch{channelId}_{time:yyyyMMdd}.log");
            Directory.CreateDirectory(DirectoryPath);
            using var file = new FileStream(Path.Combine(DirectoryPath, fileName), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var writer = new StreamWriter(file, LogEncoding);
            writer.WriteLine($"[{time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture)}] [{source}] {line}");
        }
    }
}
