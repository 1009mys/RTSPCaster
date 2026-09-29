using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using RTSPCaster.Backend.Contracts;
using RTSPCaster.Backend.Services;

namespace RTSPCaster.Backend.Controllers;

[ApiController]
[Route("api")]
public sealed class CasterController(CasterService caster, BackendOptions options) : ControllerBase
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [HttpGet("status")]
    public CasterSnapshot Status() => caster.Snapshot();

    [HttpGet("channels")]
    public ChannelSnapshot[] Channels() => caster.Snapshot().Channels;

    [HttpGet("channels/{id:int}")]
    public ChannelSnapshot Channel(int id) => caster.GetChannel(id);

    [HttpGet("channels/{id:int}/url")]
    public IActionResult GetRtspUrl(int id) => Ok(new { rtspUrl = caster.GetChannel(id).RtspUrl });

    [HttpPost("channels/upload")]
    [Consumes("multipart/form-data")]
    public async Task<ActionResult<UploadResult[]>> Upload([FromForm] List<IFormFile> files, CancellationToken ct)
    {
        if (files.Count == 0 || files.Count > 50) throw new ApiException(400, "files 필드에 1~50개 파일을 첨부하세요.");
        if (files.Sum(file => file.Length) > options.MaxUploadBytes)
            throw new ApiException(413, "요청 전체의 업로드 크기 제한을 초과했습니다.");
        var results = new List<UploadResult>();
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            try { results.Add(new(file.FileName, await caster.UploadAsync(file, ct), null)); }
            catch (ApiException exception) { results.Add(new(file.FileName, null, exception.Message)); }
            catch (InvalidOperationException exception) { results.Add(new(file.FileName, null, exception.Message)); }
        }
        return Ok(results.ToArray());
    }

    [HttpPut("channels/{id:int}/endpoint")]
    public Task<ChannelSnapshot> Endpoint(int id, ChannelEndpointRequest request, CancellationToken ct) =>
        caster.UpdateEndpointAsync(id, request, ct);

    [HttpPost("channels/{id:int}/start")]
    public async Task<IActionResult> Start(int id, CancellationToken ct)
    {
        var channel = await caster.StartChannelAsync(id, ct);
        return Accepted($"/api/channels/{id}", channel);
    }

    [HttpPost("channels/{id:int}/stop")]
    public async Task<IActionResult> Stop(int id, CancellationToken ct)
    {
        await caster.StopChannelAsync(id, ct);
        return Ok(caster.GetChannel(id));
    }

    [HttpDelete("channels/{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await caster.RemoveChannelAsync(id, ct);
        return NoContent();
    }

    [HttpPost("channels/start-all")]
    public async Task<IActionResult> StartAll(CancellationToken ct) => Accepted(await caster.StartAllAsync(ct));

    [HttpPost("channels/stop-all")]
    public async Task<IActionResult> StopAll(CancellationToken ct)
    {
        await caster.StopAllChannelsAsync(ct);
        return NoContent();
    }

    [HttpGet("settings")]
    public CasterSettings Settings() => caster.Snapshot().Settings;

    [HttpPut("settings")]
    public Task<CasterSettings> Settings(CasterSettings settings, CancellationToken ct) => caster.UpdateSettingsAsync(settings, ct);

    [HttpGet("rtsp-template/help")]
    public IActionResult TemplateHelp() => Ok(new
    {
        placeholders = new[] { "{host}", "{port}", "{index}", "{index:D3}", "{name}" },
        examples = new[] { "rtsp://{host}:{port}/stream_{index}", "rtsp://10.0.0.{index}:8554/cam_{index:D2}", "stream_{index}" },
        notes = new[] { "순번은 채널 등록 순서이며 1부터 시작합니다.", "경로만 지정하면 기존 호스트/포트를 유지합니다.",
            "준비/변환/송출/중지 중인 채널은 제외됩니다.", "잘못된 URL 또는 중복 URL이 있으면 적용하지 않습니다." }
    });

    [HttpPost("rtsp-template/apply")]
    public Task<TemplateResult> Template(TemplateRequest request, CancellationToken ct) => caster.ApplyTemplateAsync(request.Template, ct);

    [HttpGet("mediamtx")]
    public MediaMtxConnection MediaMtx() => caster.Snapshot().MediaMtx;

    [HttpPost("mediamtx/check")]
    public Task<MediaMtxConnection> CheckMediaMtx(CancellationToken ct) => caster.CheckMediaMtxAsync(ct);

    [HttpGet("logs")]
    public LogPage Logs([FromQuery] long after = 0) => caster.Logs(after);

    [HttpGet("events")]
    [Produces("text/event-stream")]
    public async Task Events(CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                var json = JsonSerializer.Serialize(caster.Snapshot(), EventJson);
                await Response.WriteAsync($"event: snapshot\ndata: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
