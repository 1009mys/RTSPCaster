using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using RTSPCaster.Backend.Contracts;
using RTSPCaster.Models;
using BackendProgram = RTSPCaster.Backend.Program;

if (args.Contains("-show_streams"))
{
    var path = args[^1];
    var content = File.ReadAllText(path);
    if (content == "invalid") { Console.Error.WriteLine("invalid media"); Environment.ExitCode = 1; return; }
    if (content == "slow-probe" && File.Exists(path + ".probed"))
    {
        File.WriteAllText(path + ".probing", Environment.ProcessId.ToString());
        Thread.Sleep(30000);
    }
    File.WriteAllText(path + ".probed", "1");
    var codec = content.Contains("conversion") ? "vp9" : "h264";
    Console.WriteLine($"{{\"streams\":[{{\"codec_type\":\"video\",\"codec_name\":\"{codec}\",\"width\":1920,\"height\":1080}}],\"format\":{{\"duration\":\"10\"}}}}");
    return;
}
if (args.Contains("-movflags"))
{
    var source = args[Array.IndexOf(args, "-i") + 1];
    var output = args[^1];
    File.WriteAllText(output, "converted");
    Console.Error.WriteLine("time=00:00:05.00");
    if (File.ReadAllText(source) == "slow-conversion")
    {
        File.WriteAllText(source + ".converting", Environment.ProcessId.ToString());
        Thread.Sleep(30000);
    }
    return;
}
if (args.Contains("-progress"))
{
    var source = args[Array.IndexOf(args, "-i") + 1];
    File.WriteAllText(source + ".streaming", Environment.ProcessId.ToString());
    if (File.ReadAllText(source) == "restart" && !File.Exists(source + ".restarted"))
    {
        File.WriteAllText(source + ".restarted", "1");
        Console.Error.WriteLine("Connection refused");
        Environment.ExitCode = 1;
        return;
    }
    while (true)
    {
        Console.WriteLine("fps=30\nbitrate=1000kbits/s\nspeed=1.0x\nout_time=00:00:01.000000\nprogress=continue");
        Console.Out.Flush();
        Thread.Sleep(100);
    }
}

var root = Path.Combine(Path.GetTempPath(), "RTSPCaster.Backend.Tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Apphost required");
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
var checks = 0;
using var endpoint = new TcpListener(IPAddress.Loopback, 0);
endpoint.Start();
var port = ((IPEndPoint)endpoint.LocalEndpoint).Port;
using var acceptCancellation = new CancellationTokenSource();
var accepting = Task.Run(async () =>
{
    try { while (true) { using var connection = await endpoint.AcceptTcpClientAsync(acceptCancellation.Token); } }
    catch (OperationCanceledException) { }
});
var appArgs = new[] { "--urls", "http://127.0.0.1:0", "--Backend:DataDirectory", root,
    "--Backend:FfmpegPath", executable, "--Backend:FfprobePath", executable,
    "--Backend:MaxUploadBytes", "1048576", "--Backend:AllowedOrigins:0", "http://web-client.example:5173",
    "--Logging:LogLevel:Default", "Warning" };

void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + message);
    checks++;
    Console.WriteLine("PASS: " + message);
}

async Task<T> Get<T>(HttpClient client, string path) =>
    await client.GetFromJsonAsync<T>(path, json) ?? throw new InvalidOperationException("Empty response: " + path);

async Task Expect(HttpClient client, HttpMethod method, string path, HttpStatusCode status, object? body = null)
{
    using var request = new HttpRequestMessage(method, path);
    if (body != null) request.Content = JsonContent.Create(body, options: json);
    using var response = await client.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    Assert(response.StatusCode == status, $"{method} {path}: {(int)status} (actual {(int)response.StatusCode}: {text[..Math.Min(text.Length, 180)]})");
    if ((int)status >= 400) Assert(response.Content.Headers.ContentType?.MediaType == "application/problem+json", "ProblemDetails content type");
}

async Task<ChannelSnapshot> Upload(HttpClient client, string name, string content)
{
    using var form = new MultipartFormDataContent();
    form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(content)), "files", name);
    using var response = await client.PostAsync("/api/channels/upload", form);
    response.EnsureSuccessStatusCode();
    var result = (await response.Content.ReadFromJsonAsync<UploadResult[]>(json))![0];
    Assert(result.Channel != null && result.Error == null, "upload " + name);
    return result.Channel!;
}

async Task<ChannelSnapshot> WaitState(HttpClient client, int id, StreamStatus status)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (true)
    {
        var channel = await Get<ChannelSnapshot>(client, $"/api/channels/{id}");
        if (channel.Status == status && (status != StreamStatus.Idle || !channel.OperationInProgress)) return channel;
        await Task.Delay(50, timeout.Token);
    }
}

async Task<int> WaitMarker(string suffix)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    while (true)
    {
        var path = Directory.GetFiles(Path.Combine(root, "media"), "*" + suffix).FirstOrDefault();
        if (path != null && int.TryParse(await File.ReadAllTextAsync(path), out var pid)) return pid;
        await Task.Delay(50, timeout.Token);
    }
}

bool Exited(int pid)
{
    try { using var process = Process.GetProcessById(pid); return process.HasExited; }
    catch (ArgumentException) { return true; }
}

try
{
    int persistedId;
    await using (var app = BackendProgram.CreateApplication(appArgs))
    {
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(30) };
        await Expect(client, HttpMethod.Post, "/api/channels/stop-all", HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Add("X-RTSPCaster-Client", "web");
        await Expect(client, HttpMethod.Get, "/api/channels/999", HttpStatusCode.NotFound);
        client.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        await Expect(client, HttpMethod.Get, "/api/status", HttpStatusCode.Forbidden);
        client.DefaultRequestHeaders.Remove("Origin");
        client.DefaultRequestHeaders.Add("Origin", "http://web-client.example:5173");
        using (var cors = await client.GetAsync("/api/status"))
            Assert(cors.Headers.GetValues("Access-Control-Allow-Origin").Single() == "http://web-client.example:5173", "remote web client CORS origin");
        client.DefaultRequestHeaders.Remove("Origin");
        using (var request = new HttpRequestMessage(HttpMethod.Get, "/api/status"))
        {
            request.Headers.Host = "rtsp-server.example:5058";
            using var response = await client.SendAsync(request);
            Assert(response.StatusCode == HttpStatusCode.OK, "remote server hostname allowed without authentication");
        }
        Assert((await Get<ChannelSnapshot[]>(client, "/api/channels")).Length == 0, "isolated empty channel store");
        Assert((await Get<LogFileList>(client, "/api/log-files")).Files.Length == 0, "missing log folder returns empty list");
        Assert(!Directory.Exists(Path.Combine(root, "logs")), "listing does not create log directory");
        await VerifyLogFileApi(client);
        var uploadLimits = await Get<JsonElement>(client, "/api/uploads/limits");
        Assert(uploadLimits.GetProperty("maxUploadBytes").GetInt64() == 1048576, "upload limits reflect configured maximum");
        var settings = new CasterSettings { MediaMtxPort = port, AutoRestartBaseDelaySeconds = 1 };
        await Expect(client, HttpMethod.Put, "/api/settings", HttpStatusCode.OK, settings);
        await Expect(client, HttpMethod.Put, "/api/settings", HttpStatusCode.BadRequest, settings with { MediaMtxPort = 0 });
        await Expect(client, HttpMethod.Put, "/api/settings", HttpStatusCode.BadRequest, settings with { MediaMtxHost = "http://localhost" });
        await Expect(client, HttpMethod.Post, "/api/mediamtx/check", HttpStatusCode.OK);
        Assert((await Get<MediaMtxConnection>(client, "/api/mediamtx")).Reachable == true, "MediaMTX connection check");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("invalid")), "files", "invalid.mp4");
            using var response = await client.PostAsync("/api/channels/upload", form);
            var results = await response.Content.ReadFromJsonAsync<UploadResult[]>(json);
            Assert(results![0].Channel == null && results[0].Error != null, "invalid media reported per file");
            Assert(Directory.GetFiles(Path.Combine(root, "media"), "*.mp4").Length == 0, "failed upload cleaned up");
        }
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(new byte[1048577]), "files", "large.mp4");
            using var response = await client.PostAsync("/api/channels/upload", form);
            Assert(!response.IsSuccessStatusCode, "oversized upload rejected");
        }
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(new byte[600 * 1024]), "files", "first.mp4");
            form.Add(new ByteArrayContent(new byte[600 * 1024]), "files", "second.mp4");
            using var response = await client.PostAsync("/api/channels/upload", form);
            Assert(response.StatusCode == HttpStatusCode.RequestEntityTooLarge, "combined files exceeding request limit rejected");
        }
        var first = await Upload(client, "../sample.mp4", new string('v', 600 * 1024));
        var second = await Upload(client, "sample.mp4", new string('v', 600 * 1024));
        Assert(first.Video.FileSize + second.Video.FileSize > 1048576, "separate uploads succeed when combined size exceeds request limit");
        persistedId = first.Id;
        Assert(first.Video.FileName == "sample.mp4" && first.RtspPath != second.RtspPath, "safe filename and unique generated RTSP paths");
        await Expect(client, HttpMethod.Put, $"/api/channels/{second.Id}/endpoint", HttpStatusCode.Conflict,
            new ChannelEndpointRequest(first.RtspPath, first.MediaMtxHost, port));
        await Expect(client, HttpMethod.Post, "/api/rtsp-template/apply", HttpStatusCode.BadRequest, new TemplateRequest("rtsp://{unknown}/x"));
        await Expect(client, HttpMethod.Post, "/api/rtsp-template/apply", HttpStatusCode.Conflict, new TemplateRequest("same"));
        await Expect(client, HttpMethod.Post, "/api/rtsp-template/apply", HttpStatusCode.OK, new TemplateRequest("web_{index:D3}"));
        Assert((await Get<ChannelSnapshot>(client, $"/api/channels/{first.Id}")).RtspPath == "web_001", "numbered path template");
        await Expect(client, HttpMethod.Post, "/api/channels/start-all", HttpStatusCode.Accepted);
        await WaitState(client, first.Id, StreamStatus.Streaming);
        await WaitState(client, second.Id, StreamStatus.Streaming);
        await Expect(client, HttpMethod.Post, $"/api/channels/{first.Id}/start", HttpStatusCode.Conflict);
        await Expect(client, HttpMethod.Put, $"/api/channels/{first.Id}/endpoint", HttpStatusCode.Conflict,
            new ChannelEndpointRequest("changed", "127.0.0.1", port));
        await Expect(client, HttpMethod.Post, "/api/rtsp-template/apply", HttpStatusCode.OK, new TemplateRequest("busy_{index}"));
        Assert((await Get<ChannelSnapshot>(client, $"/api/channels/{first.Id}")).RtspPath == "web_001", "template skips active channels");
        using (var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var response = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, sseCts.Token))
        using (var reader = new StreamReader(await response.Content.ReadAsStreamAsync(sseCts.Token)))
        {
            Assert(response.Content.Headers.ContentType?.MediaType == "text/event-stream", "SSE content type");
            Assert(await reader.ReadLineAsync(sseCts.Token) == "event: snapshot", "SSE snapshot event");
            var data = await reader.ReadLineAsync(sseCts.Token);
            var snapshot = JsonSerializer.Deserialize<CasterSnapshot>(data![6..], json)!;
            Assert(snapshot.Channels.Length == 2 && snapshot.Logs.Entries.Length > 0, "SSE state and logs payload");
            sseCts.Cancel();
        }
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        {
            while ((await Get<ChannelSnapshot>(client, $"/api/channels/{first.Id}")).HealthSamples.Length == 0)
                await Task.Delay(50, timeout.Token);
        }
        Assert((await Get<ChannelSnapshot>(client, $"/api/channels/{first.Id}")).HealthSamples[^1].Fps == 30, "stream health samples");
        await Expect(client, HttpMethod.Put, "/api/settings", HttpStatusCode.OK, settings with { FileLoggingEnabled = false });
        var recordedFiles = (await Get<LogFileList>(client, "/api/log-files")).Files;
        Assert(recordedFiles.Length > 0, "enabled streams create backend log files");
        var recordedSize = recordedFiles.Sum(file => file.Size);
        var snapshotBefore = await Get<CasterSnapshot>(client, "/api/status");
        await Task.Delay(350);
        Assert((await Get<LogFileList>(client, "/api/log-files")).Files.Sum(file => file.Size) == recordedSize, "disable stops appending during active streaming");
        var snapshotAfter = await Get<CasterSnapshot>(client, "/api/status");
        Assert(snapshotAfter.Channels.Single(channel => channel.Id == first.Id).HealthSamples[^1].Timestamp > snapshotBefore.Channels.Single(channel => channel.Id == first.Id).HealthSamples[^1].Timestamp,
            "health collection continues with file logging disabled");
        var storedLog = await Get<LogFileContent>(client, $"/api/log-files/content?name={recordedFiles[0].Name}");
        Assert(storedLog.Content.Length > 0, "existing files remain readable when logging disabled");
        await Expect(client, HttpMethod.Put, "/api/settings", HttpStatusCode.OK, settings);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            while ((await Get<LogFileList>(client, "/api/log-files")).Files.Sum(file => file.Size) <= recordedSize)
                await Task.Delay(50, timeout.Token);
        await Expect(client, HttpMethod.Get, $"/api/channels/{first.Id}/url", HttpStatusCode.OK);
        await Expect(client, HttpMethod.Post, "/api/channels/stop-all", HttpStatusCode.NoContent);
        await WaitState(client, first.Id, StreamStatus.Idle);
        await Expect(client, HttpMethod.Post, $"/api/channels/{first.Id}/stop", HttpStatusCode.OK);
        var converted = await Upload(client, "convert.webm", "conversion");
        await Expect(client, HttpMethod.Post, $"/api/channels/{converted.Id}/start", HttpStatusCode.Accepted);
        Assert((await WaitState(client, converted.Id, StreamStatus.Streaming)).ConversionProgress == 100, "automatic conversion and result probe");
        Assert(Directory.GetFiles(Path.Combine(root, "converted"), "*.mp4").Length > 0, "Backend conversion cache");
        await Expect(client, HttpMethod.Delete, $"/api/channels/{converted.Id}", HttpStatusCode.NoContent);
        await Expect(client, HttpMethod.Get, $"/api/channels/{converted.Id}", HttpStatusCode.NotFound);
        var slowProbe = await Upload(client, "slow-probe.mp4", "slow-probe");
        await Expect(client, HttpMethod.Post, $"/api/channels/{slowProbe.Id}/start", HttpStatusCode.Accepted);
        var probePid = await WaitMarker(".probing");
        await Expect(client, HttpMethod.Post, $"/api/channels/{slowProbe.Id}/stop", HttpStatusCode.OK);
        Assert(Exited(probePid), "stopping preparation kills ffprobe");
        await Expect(client, HttpMethod.Delete, $"/api/channels/{slowProbe.Id}", HttpStatusCode.NoContent);
        var slowConversion = await Upload(client, "slow-conversion.webm", "slow-conversion");
        await Expect(client, HttpMethod.Post, $"/api/channels/{slowConversion.Id}/start", HttpStatusCode.Accepted);
        var conversionPid = await WaitMarker(".converting");
        await Expect(client, HttpMethod.Delete, $"/api/channels/{slowConversion.Id}", HttpStatusCode.NoContent);
        Assert(Exited(conversionPid), "delete cancels active conversion");
        var restart = await Upload(client, "restart.mp4", "restart");
        await Expect(client, HttpMethod.Post, $"/api/channels/{restart.Id}/start", HttpStatusCode.Accepted);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
        {
            while ((await Get<ChannelSnapshot>(client, $"/api/channels/{restart.Id}")).HealthSamples.Length == 0)
                await Task.Delay(50, timeout.Token);
        }
        Assert((await Get<LogPage>(client, "/api/logs")).Entries.Any(entry => entry.Message.Contains("restart", StringComparison.OrdinalIgnoreCase)), "automatic restart logs");
        var logs = await Get<LogPage>(client, "/api/logs");
        Assert((await Get<LogPage>(client, $"/api/logs?after={logs.LastId}")).Entries.Length == 0, "log cursor");
        await Expect(client, HttpMethod.Get, "/openapi/v1.json", HttpStatusCode.OK);
        await Expect(client, HttpMethod.Put, "/api/settings", HttpStatusCode.OK, settings with { FileLoggingEnabled = false });
        await app.StopAsync();
        foreach (var marker in Directory.GetFiles(root, "*.streaming", SearchOption.AllDirectories))
            Assert(Exited(int.Parse(await File.ReadAllTextAsync(marker))), "shutdown stops tracked stream process");
    }
    await using (var app = BackendProgram.CreateApplication(appArgs))
    {
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        Assert((await Get<CasterSettings>(client, "/api/settings")).MediaMtxPort == port, "settings persist across restart");
        Assert(!(await Get<CasterSettings>(client, "/api/settings")).FileLoggingEnabled, "file logging disabled setting persists across restart");
        var previousSize = (await Get<LogFileList>(client, "/api/log-files")).Files.Sum(file => file.Size);
        client.DefaultRequestHeaders.Add("X-RTSPCaster-Client", "web");
        await Expect(client, HttpMethod.Post, $"/api/channels/{persistedId}/start", HttpStatusCode.Accepted);
        await WaitState(client, persistedId, StreamStatus.Streaming);
        await Task.Delay(350);
        Assert((await Get<LogFileList>(client, "/api/log-files")).Files.Sum(file => file.Size) == previousSize, "persisted disabled policy applies to streams after restart");
        await Expect(client, HttpMethod.Post, $"/api/channels/{persistedId}/stop", HttpStatusCode.OK);
        var channel = await Get<ChannelSnapshot>(client, $"/api/channels/{persistedId}");
        Assert(channel.Status == StreamStatus.Idle && channel.RtspPath == "web_001", "channels persist without automatic startup");
        await app.StopAsync();
    }
    Console.WriteLine($"All {checks} Backend smoke checks passed.");
}
finally
{
    acceptCancellation.Cancel();
    await accepting;
    SqliteConnection.ClearAllPools();
    Directory.Delete(root, recursive: true);
}

async Task VerifyLogFileApi(HttpClient client)
{
    var directory = Path.Combine(root, "logs");
    Directory.CreateDirectory(directory);
    var name = "ch999_20260320.log";
    var text = new string('a', 65535) + string.Concat(Enumerable.Repeat("한글🙂 <script>test</script>\n", 4000));
    await File.WriteAllTextAsync(Path.Combine(directory, name), text, new UTF8Encoding(false));
    await File.WriteAllTextAsync(Path.Combine(root, "ch777_20260320.log"), "Windows/private file");
    await File.WriteAllTextAsync(Path.Combine(directory, "private.txt"), "not a log");
    var reconstructed = new StringBuilder();
    long cursor = 0;
    LogFileContent part;
    do
    {
        part = await Get<LogFileContent>(client, $"/api/log-files/content?name={name}&offset={cursor}");
        Assert(Encoding.UTF8.GetByteCount(part.Content) <= 65536 && !part.Content.Contains('\ufffd'), "bounded UTF-8 page without split characters");
        Assert(part.NextOffset > cursor, "file page cursor advances");
        cursor = part.NextOffset;
        reconstructed.Append(part.Content);
    } while (part.HasMore);
    Assert(reconstructed.ToString() == text, "file pages reconstruct complete Unicode content");
    var tail = await Get<LogFileContent>(client, $"/api/log-files/content?name={name}");
    Assert(tail.Offset > 0 && !tail.Content.Contains('\ufffd') && text.EndsWith(tail.Content, StringComparison.Ordinal), "latest page begins on a UTF-8 boundary");
    await Expect(client, HttpMethod.Get, $"/api/log-files/content?name={name}&offset=-1", HttpStatusCode.BadRequest);
    await Expect(client, HttpMethod.Get, $"/api/log-files/content?name={name}&offset=65536", HttpStatusCode.BadRequest);
    await Expect(client, HttpMethod.Get, $"/api/log-files/content?name={name}&offset=99999999", HttpStatusCode.Conflict);
    await Expect(client, HttpMethod.Get, "/api/log-files/content?name=..%2Fch777_20260320.log", HttpStatusCode.BadRequest);
    await Expect(client, HttpMethod.Get, "/api/log-files/content?name=ch777_20260320.log", HttpStatusCode.NotFound);
    await Expect(client, HttpMethod.Get, "/api/log-files/content?name=ch999_20260320.log%3Asecret", HttpStatusCode.BadRequest);
    await Expect(client, HttpMethod.Get, "/api/log-files?skip=-1", HttpStatusCode.BadRequest);
    for (var i = 0; i < 101; i++) await File.WriteAllTextAsync(Path.Combine(directory, $"ch{i}_20260101.log"), "");
    var firstPage = await Get<LogFileList>(client, "/api/log-files");
    Assert(firstPage.Files.Length == 100 && firstPage.HasMore && firstPage.Files.All(file => file.Name.EndsWith(".log")), "file list has bounded pages and excludes unrelated files");
    var secondPage = await Get<LogFileList>(client, "/api/log-files?skip=100");
    Assert(secondPage.Files.Length == 2 && !secondPage.HasMore, "remaining file list page");
    var empty = await Get<LogFileContent>(client, "/api/log-files/content?name=ch0_20260101.log&offset=0");
    Assert(empty.Content == "" && !empty.HasMore, "empty file is readable");
    var link = Path.Combine(directory, "ch888_20260320.log");
    var linked = false;
    try { File.CreateSymbolicLink(link, Path.Combine(root, "ch777_20260320.log")); linked = true; }
    catch (UnauthorizedAccessException) { Console.WriteLine("SKIP: symlink creation requires OS permission"); }
    catch (IOException) { Console.WriteLine("SKIP: symlink creation is unavailable on this filesystem"); }
    if (linked)
    {
        await Expect(client, HttpMethod.Get, "/api/log-files/content?name=ch888_20260320.log", HttpStatusCode.Forbidden);
        Assert(!(await Get<LogFileList>(client, "/api/log-files")).Files.Any(file => file.Name == "ch888_20260320.log"), "symbolic link omitted from file list");
        File.Delete(link);
    }
    using (var response = await client.GetAsync($"/api/log-files/content?name={name}"))
        Assert(response.Headers.CacheControl?.NoStore == true, "file responses are not cached");
    Directory.Delete(directory, recursive: true);
    File.Delete(Path.Combine(root, "ch777_20260320.log"));
}
