
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using RTSPCaster.Backend.Infrastructure;
using RTSPCaster.Backend.Services;
using RTSPCaster.Services;

namespace RTSPCaster.Backend;

public class Program
{
    public static void Main(string[] args) => CreateApplication(args).Run();

    public static WebApplication CreateApplication(string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ApplicationName = typeof(Program).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory
        });
        foreach (var source in builder.Configuration.Sources.OfType<EnvironmentVariablesConfigurationSource>().ToArray())
            builder.Configuration.Sources.Remove(source);
        builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5058");
        var options = builder.Configuration.GetSection("Backend").Get<BackendOptions>() ?? new();
        try { ApiValidation.Host(options.MediaMtxHost); }
        catch (ApiException exception) { throw new InvalidOperationException("Backend:MediaMtxHost is invalid.", exception); }
        if (options.MediaMtxPort is < 1 or > 65535)
            throw new InvalidOperationException("Backend:MediaMtxPort must be between 1 and 65535.");
        if (options.MaxUploadBytes < 1 || options.MaxUploadBytes > 512L * 1024 * 1024 * 1024)
            throw new InvalidOperationException("Backend:MaxUploadBytes must be between 1 byte and 512 GiB.");
        foreach (var origin in options.AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || uri.UserInfo.Length > 0 || uri.GetLeftPart(UriPartial.Authority) != origin)
                throw new InvalidOperationException("Backend:AllowedOrigins must contain HTTP origins without a trailing slash.");
        }
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = options.MaxUploadBytes + 1024 * 1024);
        builder.Services.Configure<FormOptions>(form => form.MultipartBodyLengthLimit = options.MaxUploadBytes);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<BackendStorage>();
        builder.Services.AddSingleton<BackendLogFiles>();
        builder.Services.AddSingleton(provider => new SqliteService(provider.GetRequiredService<BackendStorage>().DatabasePath));
        builder.Services.AddSingleton<ChildProcessTracker>();
        builder.Services.AddSingleton(provider =>
        {
            var storage = provider.GetRequiredService<BackendStorage>();
            var tracker = provider.GetRequiredService<ChildProcessTracker>();
            return new StreamingService(provider.GetRequiredService<SqliteService>(), tracker, storage.LogDirectory,
                new StreamCopyTimelineService(tracker, storage.TimelineDirectory),
                new StreamKeyframeService(tracker) { FfprobePath = BackendStorage.ResolveTool(options.FfprobePath, "ffprobe") })
            {
                FfmpegPath = BackendStorage.ResolveTool(options.FfmpegPath, "ffmpeg")
            };
        });
        builder.Services.AddSingleton<CasterService>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<CasterService>());
        builder.Services.AddControllers().AddJsonOptions(json => json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddOpenApi();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy =>
        {
            if (options.AllowedOrigins.Length > 0)
                policy.WithOrigins(options.AllowedOrigins).AllowAnyHeader().AllowAnyMethod();
        }));

        var app = builder.Build();
        app.UseExceptionHandler();
        app.UseMiddleware<BrowserAccessMiddleware>();
        app.UseCors();
        app.UseStatusCodePages(async context =>
            await Results.Problem(statusCode: context.HttpContext.Response.StatusCode).ExecuteAsync(context.HttpContext));
        app.MapOpenApi();
        app.MapControllers();
        return app;
    }
}
