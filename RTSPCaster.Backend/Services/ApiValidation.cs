using System.Globalization;
using System.Text.RegularExpressions;
using RTSPCaster.Backend.Contracts;

namespace RTSPCaster.Backend.Services;

public sealed class ApiException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class ApiValidation
{
    public static void Host(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 || host != host.Trim()
            || Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains(':'))
            throw new ApiException(400, "유효한 IPv4 주소 또는 호스트 이름을 지정하세요. IPv6 RTSP 주소는 지원하지 않습니다.");
    }

    public static string Path(string path)
    {
        var sanitized = Regex.Replace(path ?? string.Empty, @"[^A-Za-z0-9_\-/]", "_").Trim('/');
        if (sanitized.Length is 0 or > 512)
            throw new ApiException(400, "RTSP 경로는 1~512자여야 합니다.");
        return sanitized;
    }

    public static ChannelEndpointRequest ExpandTemplate(string template, int index, string name,
        string currentHost, int currentPort, string currentPath, CasterSettings settings)
    {
        if (string.IsNullOrWhiteSpace(template) || template.Length > 1024)
            throw new ApiException(400, "RTSP 템플릿은 1~1024자여야 합니다.");
        var raw = Regex.Replace(template.Trim(), @"\{index(?::([^}]+))?\}", match =>
        {
            var format = match.Groups[1].Success ? match.Groups[1].Value : null;
            if (format != null && !Regex.IsMatch(format, @"^[dD][1-9]?[0-9]$"))
                throw new ApiException(400, "순번 형식은 {index} 또는 {index:D3}처럼 지정하세요 (최대 99자리).");
            return index.ToString(format, CultureInfo.InvariantCulture);
        }, RegexOptions.IgnoreCase);
        raw = raw.Replace("{name}", Regex.Replace(name, @"[^A-Za-z0-9_\-]", "_"), StringComparison.OrdinalIgnoreCase)
            .Replace("{host}", settings.MediaMtxHost, StringComparison.OrdinalIgnoreCase)
            .Replace("{port}", settings.MediaMtxPort.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        if (raw.Contains('{') || raw.Contains('}'))
            throw new ApiException(400, "지원하지 않는 템플릿 변수가 있습니다.");
        var candidate = raw.Contains("://", StringComparison.Ordinal) ? raw
            : raw.Contains(':') ? $"rtsp://{raw}{(raw.Contains('/') ? string.Empty : "/" + currentPath)}"
            : $"rtsp://{currentHost}:{currentPort}/{raw}";
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme != "rtsp"
            || uri.Port is < 1 or > 65535 || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ApiException(400, "템플릿 결과가 올바른 RTSP URL이 아닙니다.");
        Host(uri.Host);
        return new ChannelEndpointRequest(Path(uri.AbsolutePath), uri.Host, uri.Port);
    }
}
