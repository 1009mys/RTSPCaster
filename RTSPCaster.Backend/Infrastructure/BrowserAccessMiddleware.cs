using RTSPCaster.Backend.Services;

namespace RTSPCaster.Backend.Infrastructure;

public sealed class BrowserAccessMiddleware(RequestDelegate next, BackendOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var origin = request.Headers.Origin.ToString();
        if (origin.Length > 0 && (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || (!origin.Equals($"{request.Scheme}://{request.Host}", StringComparison.OrdinalIgnoreCase)
                && !options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))))
        {
            await Reject(context, "허용하지 않은 웹 Origin입니다. Backend:AllowedOrigins 설정을 확인하세요.");
            return;
        }
        if (origin.Length == 0 && request.Headers["Sec-Fetch-Site"] == "cross-site")
        {
            await Reject(context, "Origin이 없는 교차 사이트 요청은 허용하지 않습니다.");
            return;
        }
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method) && !HttpMethods.IsOptions(request.Method)
            && request.Headers["X-RTSPCaster-Client"] != "web")
        {
            await Reject(context, "변경 요청에는 X-RTSPCaster-Client: web 헤더가 필요합니다.");
            return;
        }
        await next(context);
    }

    private static Task Reject(HttpContext context, string message) =>
        Results.Problem(statusCode: 403, title: "브라우저 요청 정책", detail: message).ExecuteAsync(context);
}
