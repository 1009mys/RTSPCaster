using System.ComponentModel;
using Microsoft.AspNetCore.Diagnostics;
using RTSPCaster.Backend.Services;

namespace RTSPCaster.Backend.Infrastructure;

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested)
        {
            context.Response.StatusCode = 499;
            return true;
        }
        var (status, message) = exception switch
        {
            ApiException api => (api.StatusCode, api.Message),
            BadHttpRequestException bad => (bad.StatusCode, "잘못되었거나 크기 제한을 초과한 요청입니다."),
            Win32Exception => (503, "FFmpeg/ffprobe를 실행할 수 없습니다. Backend 실행 파일 경로 설정을 확인하세요."),
            _ => (500, "요청 처리 중 오류가 발생했습니다. Backend 서버 로그를 확인하세요.")
        };
        if (status >= 500) logger.LogError(exception, "API request failed: {Path}", context.Request.Path);
        await Results.Problem(statusCode: status, title: "RTSPCaster API 오류", detail: message).ExecuteAsync(context);
        return true;
    }
}
