using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using RTSPCaster.Backend.Contracts;

namespace RTSPCaster.Backend.Services;

public sealed partial class BackendLogFiles(BackendStorage storage)
{
    public const int PageBytes = 64 * 1024;
    public const int ListPageSize = 100;

    [GeneratedRegex(@"\Ach[0-9]{1,10}_[0-9]{8}\.log\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();

    private bool CheckDirectory()
    {
        var directory = new DirectoryInfo(storage.LogDirectory);
        if (directory.LinkTarget != null || (directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new ApiException(403, "링크된 로그 폴더는 조회할 수 없습니다.");
        return directory.Exists;
    }

    public LogFileList List(int skip, CancellationToken ct)
    {
        if (skip < 0 || skip > 1_000_000) throw new ApiException(400, "파일 목록 위치가 범위를 벗어났습니다.");
        if (!CheckDirectory()) return new([], false);
        var files = new List<LogFileEntry>();
        foreach (var path in Directory.EnumerateFiles(storage.LogDirectory, "*.log", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            var file = new FileInfo(path);
            if (!FileNamePattern().IsMatch(file.Name) || !file.Exists || file.LinkTarget != null
                || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            try { files.Add(new(file.Name, file.Length, file.LastWriteTimeUtc)); }
            catch (FileNotFoundException) { /* A rotated/deleted file is omitted from this snapshot. */ }
        }
        var page = files.OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenBy(file => file.Name, StringComparer.Ordinal).Skip(skip).Take(ListPageSize + 1).ToArray();
        return new(page.Take(ListPageSize).ToArray(), page.Length > ListPageSize);
    }

    public async Task<LogFileContent> ReadAsync(string name, long? offset, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(name) || !FileNamePattern().IsMatch(name))
            throw new ApiException(400, "허용되지 않는 로그 파일명입니다.");
        if (offset < 0) throw new ApiException(400, "파일 위치는 0 이상이어야 합니다.");
        if (!CheckDirectory()) throw new ApiException(404, "로그 파일을 찾을 수 없습니다.");
        var file = new FileInfo(Path.Combine(storage.LogDirectory, name));
        if (file.LinkTarget != null || (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new ApiException(403, "링크된 로그 파일은 조회할 수 없습니다.");
        try
        {
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var size = stream.Length;
            var start = offset ?? Math.Max(0, size - PageBytes);
            if (start > size) throw new ApiException(409, "로그 파일 크기가 변경되었습니다. 처음 또는 최신 내용부터 다시 조회하세요.");
            stream.Position = start;
            var buffer = new byte[(int)Math.Min(PageBytes + 4L, size - start)];
            var count = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken: ct);
            if (count < buffer.Length) throw new ApiException(409, "로그 파일이 변경되었습니다. 다시 조회하세요.");
            var leading = 0;
            while (leading < count && (buffer[leading] & 0xc0) == 0x80) leading++;
            if (offset.HasValue && leading != 0) throw new ApiException(400, "UTF-8 문자 경계가 아닌 파일 위치입니다.");
            start += leading;
            var length = CompleteUtf8Length(buffer.AsSpan(leading, count - leading));
            var next = start + length;
            return new(name, size, file.LastWriteTimeUtc, start, next, length > 0 && next < size,
                Encoding.UTF8.GetString(buffer, leading, length));
        }
        catch (FileNotFoundException) { throw new ApiException(404, "로그 파일을 찾을 수 없습니다."); }
        catch (DirectoryNotFoundException) { throw new ApiException(404, "로그 파일을 찾을 수 없습니다."); }
        catch (UnauthorizedAccessException) { throw new ApiException(403, "로그 파일을 읽을 권한이 없습니다."); }
    }

    private static int CompleteUtf8Length(ReadOnlySpan<byte> bytes)
    {
        var length = 0;
        while (length < bytes.Length && length < PageBytes)
        {
            var status = Rune.DecodeFromUtf8(bytes[length..], out _, out var consumed);
            if (status == OperationStatus.NeedMoreData || length + consumed > PageBytes) break;
            length += Math.Max(1, consumed);
        }
        return length;
    }
}
