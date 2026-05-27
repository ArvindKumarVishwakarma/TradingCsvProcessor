namespace TradingCsvProcessor.API.Middleware;

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.Append("X-Content-Type-Options", "nosniff");
        headers.Append("X-Frame-Options", "DENY");
        headers.Append("X-XSS-Protection", "1; mode=block");
        headers.Append("Referrer-Policy", "no-referrer");
        headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=()");

        await next(context);
    }
}
