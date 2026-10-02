using System.Diagnostics;
using Microsoft.Data.Sqlite;
using RTSPCaster.Models;
using RTSPCaster.Services;

if (args is ["--child"])
{
    Thread.Sleep(TimeSpan.FromSeconds(30));
    return;
}

if (args.Contains("-show_streams") || args.Contains("-show_format"))
{
    var startTime = File.ReadAllText(args[^1]) == "offset-timeline" ? "100" : "0";
    Console.WriteLine("{\"streams\":[{\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1920,\"height\":1080}],\"format\":{\"duration\":\"10.0\",\"start_time\":\"" + startTime + "\"}}");
    return;
}

if (args.Contains("-show_packets"))
{
    var source = args[^1];
    var content = File.ReadAllText(source);
    if (content == "fatal-cancel")
    {
        File.WriteAllText(source + ".scan-started", "started");
        Thread.Sleep(TimeSpan.FromSeconds(30));
    }
    var offset = content == "offset-timeline" ? 100 : 0;
    foreach (var (time, flags) in new[] { (0, "K_"), (1, "__"), (2, "K_"), (3, "KC"), (4, "K_"), (6, "KD"), (8, "K_"), (7, "K_") })
        Console.WriteLine($"pts_time={offset + time}|flags={flags}");
    Console.WriteLine("pts_time=N/A|flags=K_");
    return;
}

if (args.Contains("-movflags") || (args.Contains("-f") && args[Array.IndexOf(args, "-f") + 1] == "null"))
{
    Assert(args.Contains("-c") && args[Array.IndexOf(args, "-c") + 1] == "copy", "repair must not re-encode");
    var input = args[Array.IndexOf(args, "-i") + 1];
    var content = File.ReadAllText(input);
    if (args.Contains("-movflags"))
    {
        if (content == "fatal-eof-cancel")
        {
            File.WriteAllText(input + ".repair-started", "started");
            Thread.Sleep(TimeSpan.FromSeconds(30));
        }
        File.Copy(input, args[^1]);
    }
    else
    {
        Assert(args.Contains("-stream_loop") && args[Array.IndexOf(args, "-stream_loop") + 1] == "1", "validate loop boundary");
        if (content is "bad-timeline" or "warning-timeline")
            Console.Error.WriteLine("Application provided invalid, non monotonically increasing dts to muxer");
        if (content == "bad-timeline") Environment.ExitCode = 1;
    }
    return;
}

if (args.Contains("-progress"))
{
    Assert(args.Contains("-c") && args[Array.IndexOf(args, "-c") + 1] == "copy", "stream must not re-encode");
    var input = args[Array.IndexOf(args, "-i") + 1];
    var content = File.ReadAllText(input);
    var seek = args.Contains("-ss") ? args[Array.IndexOf(args, "-ss") + 1] : "none";
    Assert(seek == "none" || (!args.Contains("-stream_loop") && Array.IndexOf(args, "-ss") < Array.IndexOf(args, "-i")), "resume seeks input and plays remainder once");
    Console.Error.WriteLine("fake ffmpeg started");
    Console.Error.WriteLine("fake seek=" + seek);
    if (content == "warning-only")
        Console.Error.WriteLine("[vost#0:0/copy] Non-monotonic DTS; previous: 7881331, current: 1272000; changing to 7881332.");

    var shouldFail = content.StartsWith("fatal-") && Path.GetFileName(input).StartsWith("fatal-")
        && (!File.Exists(input + ".failed-once") || content is "fatal-repeat" or "fatal-output" or "fatal-dts-only");
    if (shouldFail)
    {
        File.WriteAllText(input + ".failed-once", "failed");
        Console.WriteLine("fps=29.97\nout_time_us=1000000\nout_time=00:00:01.000000\nprogress=continue");
        Console.Out.Flush();
        if (content == "fatal-output")
        {
            Console.Error.WriteLine("Non-monotonic DTS; previous: 50, current: 49");
            Console.Error.WriteLine("[out#0/rtsp] Task finished with error code: -32 (Broken pipe)");
        }
        else if (content == "fatal-dts-only")
        {
            Console.Error.WriteLine("Non-monotonic DTS; previous: 7881331, current: 1272000");
        }
        else
        {
            Console.Error.WriteLine("[in#0/avi] Task finished with error code: -1 (Operation not permitted)");
        }
        var finalSeconds = content.StartsWith("fatal-eof") ? 10d : content == "fatal-final-progress" ? 3.5d : seek != "none" ? 0d : 1.5d;
        Console.WriteLine(FormattableString.Invariant($"out_time_us={finalSeconds * 1000000:0}\nprogress=end"));
        Console.Out.Flush();
        Console.Error.Flush();
        Environment.ExitCode = 1;
        return;
    }
    while (true)
    {
        Console.WriteLine("fps=29.97\nbitrate=N/A\nout_time=00:00:01.000000\nspeed=1.0x\nprogress=continue");
        Console.Out.Flush();
        Thread.Sleep(100);
        if (content == "fatal-once" && seek != "none") return;
    }
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

    VerifyDailyStreamLogs(temp);
    VerifyLogWriteFailure(db, temp);
    VerifyStreamingDoesNotBlockProbe(db, temp);
    VerifyTimestampRepairAsync(db, temp).GetAwaiter().GetResult();

    Console.WriteLine("RTSPCaster.Lib smoke tests passed");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(temp, recursive: true);
}

static void VerifyDailyStreamLogs(string temp)
{
    var defaultWriter = new StreamLogWriter();
    Assert(defaultWriter.DirectoryPath == Path.Combine(AppContext.BaseDirectory, "log"), "default log directory");
    Assert(!defaultWriter.Enabled, "file logging disabled by default");
    var directory = Path.Combine(temp, "daily-logs");
    var writer = new StreamLogWriter(directory);
    var time = new DateTimeOffset(2026, 3, 20, 23, 59, 59, TimeSpan.FromHours(9));
    writer.Enabled = false;
    writer.Write(57, "stderr", "must not create directory", time);
    Assert(!Directory.Exists(directory), "disabled logging does not create directory or file");
    writer.Enabled = true;
    writer.Write(57, "stderr", "첫 번째 상세 로그", time);
    new StreamLogWriter(directory) { Enabled = true }.Write(57, "stdout", "progress=continue", time);
    writer.Write(57, "stderr", "next day", time.AddSeconds(1));
    Parallel.For(0, 100, i => writer.Write(58, "stderr", $"line {i}", time));

    var firstDay = File.ReadAllLines(Path.Combine(directory, "ch57_20260320.log"));
    Assert(firstDay.Length == 2, "same channel and day append across writer instances");
    Assert(firstDay[0] == "[2026-03-20 23:59:59.000 +09:00] [stderr] 첫 번째 상세 로그", "UTF-8 timestamp and source");
    Assert(firstDay[1].Contains("[stdout] progress=continue"), "raw progress saved");
    Assert(File.ReadAllLines(Path.Combine(directory, "ch57_20260321.log")).Length == 1, "midnight log rotation");
    var otherChannel = File.ReadAllLines(Path.Combine(directory, "ch58_20260320.log"));
    Assert(otherChannel.Length == 100 && otherChannel.Distinct().Count() == 100, "concurrent writes and channel isolation");
    var length = new FileInfo(Path.Combine(directory, "ch57_20260320.log")).Length;
    writer.Enabled = false;
    Parallel.For(0, 100, i => writer.Write(57, "stderr", $"disabled {i}", time));
    writer.Write(99, "stderr", "must not create new channel file", time);
    Assert(new FileInfo(Path.Combine(directory, "ch57_20260320.log")).Length == length, "disabled writes do not append existing files");
    Assert(!File.Exists(Path.Combine(directory, "ch99_20260320.log")), "disabled writes do not create new channel files");
    writer.Enabled = true;
    writer.Write(57, "stderr", "resumed", time);
    Assert(File.ReadAllLines(Path.Combine(directory, "ch57_20260320.log")).Length == 3, "reenabling appends without deleting prior logs");
}

static void VerifyLogWriteFailure(SqliteService db, string temp)
{
    var blockedDirectory = Path.Combine(temp, "not-a-directory");
    File.WriteAllText(blockedDirectory, "blocked");
    using var tracker = new ChildProcessTracker();
    using var streaming = new StreamingService(db, tracker, blockedDirectory)
    {
        FfmpegPath = Path.Combine(temp, "missing-ffmpeg"),
        AutoRestartEnabled = false
    };
    var warnings = 0;
    var receivedErrorStatus = false;
    streaming.Log += (_, entry) =>
    {
        if (entry.Line.Contains("FFmpeg log write failed")) warnings++;
    };
    streaming.StatusChanged += (_, entry) => receivedErrorStatus |= entry.Status == StreamStatus.Error;
    try
    {
        streaming.StartAsync(new Channel { Id = 59, RtspPath = "test59" }, "source.mp4").GetAwaiter().GetResult();
        throw new Exception("FAILED: missing ffmpeg must fail to start");
    }
    catch (InvalidOperationException)
    {
        Assert(receivedErrorStatus, "log failure does not prevent process start error handling");
        Assert(warnings == 1, "log write warning is not repeated");
    }
}

static string ReadLiveLog(string path)
{
    if (!File.Exists(path)) return string.Empty;
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    return reader.ReadToEnd();
}

static void VerifyStreamingDoesNotBlockProbe(SqliteService db, string temp)
{
    ThreadPool.GetMinThreads(out var minWorkers, out var minIo);
    ThreadPool.GetMaxThreads(out var maxWorkers, out var maxIo);
    var culture = System.Globalization.CultureInfo.CurrentCulture;
    var logDirectory = Path.Combine(temp, "stream-logs");
    using var tracker = new ChildProcessTracker();
    using var streaming = new StreamingService(db, tracker, logDirectory)
    {
        FfmpegPath = Environment.ProcessPath!,
        AutoRestartEnabled = false
    };
    var summaries = new System.Collections.Concurrent.ConcurrentDictionary<int, string>();
    streaming.Log += (_, entry) =>
    {
        if (entry.Line.StartsWith("fps=")) summaries[entry.ChannelId] = entry.Line;
    };
    var channels = Enumerable.Range(1, 6)
        .Select(id => new Channel { Id = id, Name = $"test{id}", RtspPath = $"test{id}" })
        .ToArray();
    try
    {
        Assert(ThreadPool.SetMinThreads(2, minIo), "set test thread-pool minimum");
        Assert(ThreadPool.SetMaxThreads(4, maxIo), "set test thread-pool maximum");
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

        var sourcePath = Path.Combine(temp, "probe-input.mp4");
        File.WriteAllText(sourcePath, "fake media");
        foreach (var channel in channels)
            streaming.StartAsync(channel, sourcePath).GetAwaiter().GetResult();

        var probe = new FfprobeService { FfprobePath = Environment.ProcessPath! };
        var probeTask = probe.ProbeAsync(sourcePath);
        var probeCompleted = probeTask.Wait(TimeSpan.FromSeconds(10));
        var allStatisticsReceived = SpinWait.SpinUntil(() => summaries.Count == channels.Length, TimeSpan.FromSeconds(10));
        Assert(probeCompleted, "file probe completes while multiple streams are running");
        Assert(probeTask.Result.StreamCopyCompatible, "added file probe metadata");
        Assert(allStatisticsReceived, "later channels receive progress while earlier streams are running");
        Assert(summaries.Values.All(line => line.Contains("fps=29.97 ")), "progress uses invariant decimal formatting");
        Assert(SpinWait.SpinUntil(() => channels.All(channel =>
        {
            var log = ReadLiveLog(Path.Combine(logDirectory, FormattableString.Invariant($"ch{channel.Id}_{DateTimeOffset.Now:yyyyMMdd}.log")));
            return log.Contains("[stderr] fake ffmpeg started")
                && log.Contains("[stdout] progress=continue")
                && log.Contains("[command]")
                && log.Contains("\"-loglevel\" \"verbose\"")
                && log.Contains("Process started: PID");
        }), TimeSpan.FromSeconds(5)), "all channel logs contain verbose command, session and raw output");
    }
    finally
    {
        System.Globalization.CultureInfo.CurrentCulture = culture;
        ThreadPool.SetMaxThreads(maxWorkers, maxIo);
        ThreadPool.SetMinThreads(minWorkers, minIo);
        streaming.StopAll();
        Assert(SpinWait.SpinUntil(() => channels.All(channel => !streaming.IsStreaming(channel.Id)), TimeSpan.FromSeconds(10)),
            "all test streams stop");
    }
}

static async Task VerifyTimestampRepairAsync(SqliteService db, string temp)
{
    Assert(StreamCopyTimelineService.IsTimestampDiscontinuity("Non-monotonic DTS; previous: 7881331, current: 1272000"), "reported DTS warning detected");
    Assert(StreamCopyTimelineService.IsTimestampDiscontinuity("Non-monotonous DTS"), "legacy DTS warning detected");
    Assert(!StreamCopyTimelineService.IsTimestampDiscontinuity("fps=30 bitrate=N/A"), "ordinary progress is not a DTS error");

    using var tracker = new ChildProcessTracker();
    var timeline = new StreamCopyTimelineService(tracker, Path.Combine(temp, "timeline-cache"));
    var source = Path.Combine(temp, "timeline-source.mp4");
    File.WriteAllText(source, "unchanged packets");
    var repaired = await timeline.PrepareAsync(source, Environment.ProcessPath!, _ => { }, CancellationToken.None);
    Assert(File.ReadAllText(source) == "unchanged packets" && File.ReadAllText(repaired) == "unchanged packets", "source and copied data preserved");
    Assert(timeline.FindCached(source) == repaired, "verified timeline cache available");
    Assert(await timeline.PrepareAsync(source, "missing-ffmpeg", _ => { }, CancellationToken.None) == repaired, "verified cache reused without remux");
    File.AppendAllText(source, " changed");
    Assert(timeline.FindCached(source) == null, "source change invalidates cache");

    var invalid = Path.Combine(temp, "bad-timeline.mp4");
    File.WriteAllText(invalid, "bad-timeline");
    try
    {
        await timeline.PrepareAsync(invalid, Environment.ProcessPath!, _ => { }, CancellationToken.None);
        throw new Exception("FAILED: invalid loop timestamps must not be accepted");
    }
    catch (InvalidOperationException)
    {
        Assert(timeline.FindCached(invalid) == null, "invalid timeline not cached");
        Assert(!Directory.EnumerateFiles(timeline.CacheDirectory, "*.tmp.mp4").Any(), "failed repair temporary file removed");
    }

    File.WriteAllText(source, "warning-timeline");
    Assert(File.Exists(await timeline.PrepareAsync(source, Environment.ProcessPath!, _ => { }, CancellationToken.None)), "nonfatal remux warnings accepted");

    var keyframes = new StreamKeyframeService(tracker) { FfprobePath = Environment.ProcessPath! };
    var next = await keyframes.FindNextAsync(source, 0, 11.5, true, _ => { }, CancellationToken.None);
    Assert(next.FailedPositionSeconds == 1.5 && next.NextKeyframeSeconds == 2, "loop progress mapped to file position");
    next = await keyframes.FindNextAsync(source, 2, 1.5, false, _ => { }, CancellationToken.None);
    Assert(next.NextKeyframeSeconds == 4, "resume offset applied and corrupt keyframe ignored");
    next = await keyframes.FindNextAsync(source, 0, 10, true, _ => { }, CancellationToken.None);
    Assert(next.FailedPositionSeconds == 10 && next.NextKeyframeSeconds == null, "EOF does not wrap to an earlier keyframe");
    File.WriteAllText(source, "offset-timeline");
    next = await keyframes.FindNextAsync(source, 0, 1.5, true, _ => { }, CancellationToken.None);
    Assert(next.NextKeyframeSeconds == 2, "container start time removed from seek position");

    foreach (var scenario in new[] { "warning-only", "fatal-once", "fatal-final-progress", "fatal-repeat", "fatal-dts-only", "fatal-output", "fatal-disabled", "fatal-cancel", "fatal-eof", "fatal-eof-cancel" })
        await VerifyInputRecoveryScenarioAsync(db, temp, scenario);
}

static async Task VerifyInputRecoveryScenarioAsync(SqliteService db, string temp, string scenario)
{
    var source = Path.Combine(temp, scenario + ".mp4");
    File.WriteAllText(source, scenario);
    using var tracker = new ChildProcessTracker();
    var timeline = new StreamCopyTimelineService(tracker, Path.Combine(temp, scenario + "-cache"));
    var keyframes = new StreamKeyframeService(tracker) { FfprobePath = Environment.ProcessPath! };
    using var streaming = new StreamingService(db, tracker, Path.Combine(temp, scenario + "-logs"), timeline, keyframes)
    {
        FfmpegPath = Environment.ProcessPath!,
        AutoRestartEnabled = scenario != "fatal-disabled",
        AutoRestartBaseDelay = TimeSpan.Zero,
        AutoRestartAttemptResetThreshold = scenario == "fatal-dts-only" ? TimeSpan.Zero : TimeSpan.FromSeconds(30),
        MaxAutoRestartAttempts = 2
    };
    var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var ended = new TaskCompletionSource<StreamEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
    var seeks = new System.Collections.Concurrent.ConcurrentQueue<string>();
    var starts = 0;
    streaming.StatusChanged += (_, status) =>
    {
        if (status.Status == StreamStatus.Streaming)
            Interlocked.Increment(ref starts);
        if (status.Status is StreamStatus.Error or StreamStatus.Idle)
            ended.TrySetResult(status);
    };
    streaming.Log += (_, entry) =>
    {
        if (entry.Line.StartsWith("fake seek=")) seeks.Enqueue(entry.Line[10..]);
        var expectedStarts = scenario == "warning-only" ? 1 : scenario == "fatal-once" ? 3 : 2;
        if (Volatile.Read(ref starts) >= expectedStarts && entry.Line.StartsWith("fps="))
            resumed.TrySetResult();
    };
    var channel = new Channel { Id = 71, Name = scenario, RtspPath = scenario };
    try
    {
        await streaming.StartAsync(channel, source);
        if (scenario is "warning-only" or "fatal-once" or "fatal-final-progress" or "fatal-eof")
        {
            await resumed.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var expectedStarts = scenario == "warning-only" ? 1 : scenario == "fatal-once" ? 3 : 2;
            Assert(SpinWait.SpinUntil(() => seeks.Count >= expectedStarts, TimeSpan.FromSeconds(5)), "all process seek markers received");
            if (scenario == "warning-only")
            {
                await Task.Delay(300);
                Assert(starts == 1 && streaming.IsStreaming(channel.Id), "DTS warning alone does not terminate or restart stream");
            }
            if (scenario == "fatal-once")
                Assert(seeks.SequenceEqual(new[] { "none", "2", "none" }), "fatal skip then full-file loop resumes");
            if (scenario == "fatal-final-progress")
                Assert(seeks.Contains("4") && !seeks.Contains("2"), "final stdout progress drained before choosing keyframe");
            if (scenario == "fatal-eof")
                Assert(timeline.FindCached(source) != null && seeks.All(s => s == "none"), "EOF failure uses copy remux instead of seek backwards");
            streaming.Stop(channel.Id);
            var stopped = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(stopped.Status == StreamStatus.Idle, "recovered stream stops normally");
        }
        else if (scenario is "fatal-cancel" or "fatal-eof-cancel")
        {
            var marker = source + (scenario == "fatal-cancel" ? ".scan-started" : ".repair-started");
            var timeout = Stopwatch.StartNew();
            while (!File.Exists(marker) && timeout.Elapsed < TimeSpan.FromSeconds(15))
                await Task.Delay(50);
            Assert(File.Exists(marker), "recovery child has started");
            streaming.Stop(channel.Id);
            var stopped = await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(stopped.Status == StreamStatus.Idle && starts == 1, "stop cancels repair without restarting");
            Assert(timeline.FindCached(source) == null, "cancelled repair not cached");
        }
        else
        {
            var error = await ended.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert(error.Status == StreamStatus.Error, "persistent fatal failure is visible");
            Assert(starts == (scenario == "fatal-disabled" ? 1 : 3), "fatal recovery bounded and respects auto-restart setting");
            if (scenario is "fatal-repeat" or "fatal-dts-only")
                Assert(seeks.SequenceEqual(new[] { "none", "2", "4" }), "repeated input failures advance to later keyframes");
            if (scenario == "fatal-output")
                Assert(seeks.All(s => s == "none"), "output failure must not skip video even when DTS warning was seen");
        }
    }
    finally
    {
        if (streaming.IsStreaming(channel.Id)) streaming.Stop(channel.Id);
        Assert(SpinWait.SpinUntil(() => !streaming.IsStreaming(channel.Id), TimeSpan.FromSeconds(10)), "DTS test stream stopped");
    }
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception("FAILED: " + message);
}
