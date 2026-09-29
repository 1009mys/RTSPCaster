using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RTSPCaster.Services;

public sealed record MediaContentFingerprint(string Hash, long Frames, long Samples, int SampleRate)
{
    public double AudioSeconds => SampleRate > 0 ? (double)Samples / SampleRate : 0;
}

internal sealed class MediaContentFingerprintBuilder(bool audio) : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private long _frames;
    private long _samples;
    private int _sampleRate;
    private bool _invalid;

    public void Read(string line)
    {
        if (line.StartsWith('#'))
        {
            if (line.StartsWith("#sample_rate", StringComparison.Ordinal))
            {
                var separator = line.IndexOf(':');
                if (separator < 0 || !int.TryParse(line[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _sampleRate))
                    _invalid = true;
            }
            if (line.StartsWith("#dimensions", StringComparison.Ordinal) ||
                line.StartsWith("#sar", StringComparison.Ordinal) ||
                line.StartsWith("#sample_rate", StringComparison.Ordinal) ||
                line.StartsWith("#channel_layout", StringComparison.Ordinal))
                Append(line.Trim());
            return;
        }
        if (string.IsNullOrWhiteSpace(line)) return;
        var fields = line.Split(',');
        if (fields.Length < 6 ||
            !long.TryParse(fields[3].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var duration) ||
            !long.TryParse(fields[4].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) || size <= 0 ||
            fields[5].Trim().Length != 64 || !fields[5].Trim().All(Uri.IsHexDigit))
        {
            _invalid = true;
            return;
        }
        // DTS/PTS와 패킷 경계는 보존 기준이 아니다. 음성은 고정 크기 샘플 블록으로 정규화해 비교한다.
        Append($"{size.ToString(CultureInfo.InvariantCulture)}:{fields[5].Trim()}");
        _frames++;
        if (audio)
        {
            if (duration <= 0) _invalid = true;
            else _samples += duration;
        }
    }

    public MediaContentFingerprint? Complete() =>
        _invalid || _frames == 0 || (audio && (_sampleRate <= 0 || _samples <= 0))
            ? null
            : new MediaContentFingerprint(Convert.ToHexString(_hash.GetHashAndReset()), _frames, _samples, _sampleRate);

    private void Append(string value) => _hash.AppendData(Encoding.UTF8.GetBytes(value + "\n"));

    public void Dispose() => _hash.Dispose();
}
