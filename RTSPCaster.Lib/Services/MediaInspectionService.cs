using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RTSPCaster.Models;

namespace RTSPCaster.Services;

public sealed class MediaInspectionResult
{
    public ProbeResult Metadata { get; init; } = new();
    public List<string> Issues { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool VideoNeedsRepair { get; internal set; }
    public bool AudioNeedsRepair { get; internal set; }
    public bool CanCopyVideo => !VideoNeedsRepair && FfprobeService.IsVideoCodecCompatible(Metadata.VideoCodec);
    public bool CanCopyAudio => !AudioNeedsRepair && FfprobeService.IsAudioCodecCompatible(Metadata.AudioCodec);
    public long VideoPackets { get; internal set; }
    public long AudioPackets { get; internal set; }
    public MediaContentFingerprint? VideoContent { get; internal set; }
    public MediaContentFingerprint? AudioContent { get; internal set; }
    public double DurationSeconds { get; internal set; }
    public bool IsHealthy => Metadata.StreamCopyCompatible && Issues.Count == 0;

    public string? GetContentMismatch(MediaInspectionResult source, bool videoEncoded = false, bool audioEncoded = false)
    {
        var videoMismatch = CompareContent("영상", source.VideoContent, VideoContent, videoEncoded, audio: false);
        if (videoMismatch != null) return videoMismatch;
        var hasAudio = source.AudioPackets > 0 || !string.IsNullOrEmpty(source.Metadata.AudioCodec);
        return hasAudio ? CompareContent("음성", source.AudioContent, AudioContent, audioEncoded, audio: true) : null;
    }

    private static string? CompareContent(string name, MediaContentFingerprint? source, MediaContentFingerprint? output, bool encoded, bool audio)
    {
        if (source == null || output == null)
            return $"{name}: 전체 디코딩 내용 검증 불가 (원본={source != null}, 결과={output != null})";
        if (!encoded)
        {
            if (source.Hash == output.Hash && source.Frames == output.Frames && source.Samples == output.Samples)
                return null;
            return $"{name}: 디코딩 내용 불일치 (프레임/블록 {source.Frames}→{output.Frames}, 샘플 {source.Samples}→{output.Samples})";
        }
        if (!audio && output.Frames < source.Frames)
            return $"영상: 디코딩 프레임 누락 ({source.Frames}→{output.Frames})";
        if (audio && output.AudioSeconds + 0.05 < source.AudioSeconds)
            return $"음성: 디코딩 샘플 길이 감소 ({source.AudioSeconds:0.###}초→{output.AudioSeconds:0.###}초)";
        return null;
    }
}

public sealed class MediaInspectionService
{
    private readonly ChildProcessTracker? _tracker;
    public string FfmpegPath { get; set; } = ToolLocator.Find(ToolLocator.ExecutableName("ffmpeg")) ?? ToolLocator.ExecutableName("ffmpeg");
    public string FfprobePath { get; set; } = ToolLocator.Find(ToolLocator.ExecutableName("ffprobe")) ?? ToolLocator.ExecutableName("ffprobe");

    public MediaInspectionService(ChildProcessTracker? tracker = null) => _tracker = tracker;

    public async Task<MediaInspectionResult> InspectAsync(string path, CancellationToken ct = default)
    {
        var metadata = await new FfprobeService { FfprobePath = FfprobePath }.ProbeAsync(path, ct).ConfigureAwait(false);
        var result = new MediaInspectionResult { Metadata = metadata };
        using var document = JsonDocument.Parse(metadata.RawJson!);
        var states = new Dictionary<int, PacketTimeline>();
        foreach (var stream in document.RootElement.GetProperty("streams").EnumerateArray())
        {
            var type = stream.GetProperty("codec_type").GetString();
            if (type is not ("video" or "audio") || states.Values.Any(s => s.Type == type)) continue;
            states.Add(stream.GetProperty("index").GetInt32(), new PacketTimeline(type));
        }

        void AddIssue(string issue, string? type = null)
        {
            lock (result.Issues)
            {
                if (!result.Issues.Contains(issue)) result.Issues.Add(issue);
                if (type == "video") result.VideoNeedsRepair = true;
                if (type == "audio") result.AudioNeedsRepair = true;
            }
        }

        void AddWarning(string warning)
        {
            if (!result.Warnings.Contains(warning)) result.Warnings.Add(warning);
        }

        var packetExit = await MediaProcess.RunAsync(FfprobePath,
            ["-v", "error", "-show_packets", "-show_entries", "packet=stream_index,pts_time,dts_time,duration_time,flags",
             "-of", "compact=p=0:nk=0", path], _tracker, ct,
            line =>
            {
                var fields = new Dictionary<string, string>();
                foreach (var part in line.Split('|'))
                {
                    var pair = part.Split('=', 2);
                    if (pair.Length == 2 && pair[0] is "stream_index" or "pts_time" or "dts_time" or "duration_time" or "flags")
                        fields[pair[0]] = pair[1];
                }
                if (!fields.TryGetValue("stream_index", out var index) || !int.TryParse(index, out var id) || !states.TryGetValue(id, out var state)) return;
                state.Read(fields, issue => AddIssue(issue, state.Type), AddWarning);
            }, _ => AddIssue("패킷 읽기 오류")).ConfigureAwait(false);
        if (packetExit != 0) AddIssue("패킷 검사 실패");

        foreach (var state in states.Values)
        {
            if (state.Count == 0) AddIssue($"{state.Type}: 패킷 없음", state.Type);
            if (state.FirstPts is double start && Math.Abs(start) > 1) AddIssue($"{state.Type}: 시작 타임스탬프가 0에서 1초 초과 이탈", state.Type);
            if (state.Type == "video")
            {
                result.VideoPackets = state.Count;
                if (state.LastKeyPts == null) AddIssue("video: 키프레임 없음", "video");
                else if (state.EndPts - state.LastKeyPts > 5) AddWarning("키프레임 간격 5초 초과: 재생 시작이 지연될 수 있음 (재인코딩 생략)");
            }
            else result.AudioPackets = state.Count;
        }
        var video = states.Values.FirstOrDefault(s => s.Type == "video");
        var audio = states.Values.FirstOrDefault(s => s.Type == "audio");
        var timelines = states.Values.Where(s => s.FirstPts.HasValue && s.EndPts.HasValue).ToArray();
        if (timelines.Length > 0)
            result.DurationSeconds = timelines.Max(s => s.EndPts!.Value) - timelines.Min(s => s.FirstPts!.Value);
        if (video == null) AddIssue("영상 스트림 없음", "video");
        if (video?.FirstPts is double vStart && audio?.FirstPts is double aStart && Math.Abs(vStart - aStart) > 1)
            AddIssue("영상·음성 시작 시각 차이 1초 초과", "audio");

        foreach (var state in states.Values)
        {
            var audioStream = state.Type == "audio";
            var map = audioStream ? "0:a:0" : "0:v:0";
            using var fingerprint = new MediaContentFingerprintBuilder(audioStream);
            var decodeErrors = false;
            var arguments = new List<string>
            {
                "-hide_banner", "-nostdin", "-v", "error", "-xerror", "-err_detect", "explode",
                "-threads", "1", "-i", path, "-map", map
            };
            if (audioStream)
                arguments.AddRange(["-af", "asetnsamples=n=4096:p=0,asetpts=N/SR/TB", "-c:a", "pcm_f64le"]);
            else
                arguments.AddRange(["-vf", "settb=expr=1/1000,setpts=N", "-enc_time_base:v", "1:1000",
                    "-fps_mode:v", "passthrough", "-c:v", "rawvideo"]);
            arguments.AddRange(["-threads", "1", "-flags", "+bitexact", "-f", "framehash", "-hash", "sha256", "-"]);
            var decodeExit = await MediaProcess.RunAsync(FfmpegPath,
                arguments, _tracker, ct, fingerprint.Read, _ =>
                {
                    decodeErrors = true;
                    AddIssue($"{state.Type}: 디코딩 오류", state.Type);
                }).ConfigureAwait(false);
            if (decodeExit != 0) AddIssue($"{state.Type}: 전체 디코딩 검사 실패", state.Type);
            var content = decodeExit == 0 && !decodeErrors ? fingerprint.Complete() : null;
            if (content == null) AddIssue($"{state.Type}: 디코딩 내용 지문 생성 실패", state.Type);
            if (audioStream) result.AudioContent = content;
            else result.VideoContent = content;
        }
        return result;
    }

    private sealed class PacketTimeline(string type)
    {
        public string Type { get; } = type;
        public long Count { get; private set; }
        public double? FirstPts { get; private set; }
        public double? LastPts { get; private set; }
        public double? EndPts { get; private set; }
        public double? LastKeyPts { get; private set; }
        private double? _lastDts;
        private double _lastDuration;

        public void Read(Dictionary<string, string> fields, Action<string> issue, Action<string> warning)
        {
            static double? Number(Dictionary<string, string> values, string name) =>
                values.TryGetValue(name, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value) ? value : null;

            var pts = Number(fields, "pts_time");
            var dts = Number(fields, "dts_time");
            var duration = Number(fields, "duration_time") ?? 0;
            fields.TryGetValue("flags", out var flags);
            if (pts == null || dts == null) issue($"{Type}: 타임스탬프 누락");
            if (flags?.Contains('C') == true) issue($"{Type}: 손상된 패킷");
            if (dts is double current && _lastDts is double previous)
            {
                if (current <= previous) issue($"{Type}: DTS 역행 또는 중복");
                if (current - previous - _lastDuration > 2) issue($"{Type}: 패킷 공백 2초 초과");
            }
            if (duration > 2) issue($"{Type}: 패킷 지속 시간 2초 초과");
            if (Count == 0)
            {
                FirstPts = pts;
                if (Type == "video" && flags?.Contains('K') != true) issue("video: 첫 패킷이 키프레임이 아님");
            }
            if (Type == "video" && flags?.Contains('K') == true)
            {
                if (pts - LastKeyPts > 5) warning("키프레임 간격 5초 초과: 재생 시작이 지연될 수 있음 (재인코딩 생략)");
                LastKeyPts = pts;
            }
            Count++;
            LastPts = pts;
            if (pts is double presentationTime)
                EndPts = Math.Max(EndPts ?? double.MinValue, presentationTime + Math.Max(0, duration));
            _lastDts = dts;
            _lastDuration = duration;
        }
    }
}

internal static class MediaProcess
{
    internal static async Task<int> RunAsync(string executable, IEnumerable<string> arguments,
        ChildProcessTracker? tracker, CancellationToken ct, Action<string>? output, Action<string>? error)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = psi };
        process.Start();
        tracker?.Track(process);
        void StopProcess()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        using var registration = ct.Register(StopProcess);

        async Task ReadAsync(StreamReader reader, Action<string>? receive)
        {
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is string line)
                    receive?.Invoke(line);
            }
            catch
            {
                StopProcess();
                throw;
            }
        }

        var stdout = ReadAsync(process.StandardOutput, output);
        var stderr = ReadAsync(process.StandardError, error);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(CancellationToken.None)).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
    }
}
