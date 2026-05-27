using Microsoft.AspNetCore.Mvc;

namespace TradingCsvProcessor.API.Middleware;

public sealed class ExceptionHandlingMiddleware(
    RequestDelegate next,
    ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception on {Method} {Path}",
                context.Request.Method, context.Request.Path);

            await WriteProblemDetailsAsync(context, ex);
        }
    }

    private static Task WriteProblemDetailsAsync(HttpContext context, Exception ex)
    {
        var (statusCode, title) = ex switch
        {
            InvalidOperationException => (StatusCodes.Status400BadRequest, "Bad Request"),
            ArgumentException        => (StatusCodes.Status400BadRequest, "Bad Request"),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested
                                     => (499, "Client Closed Request"),
            _                        => (StatusCodes.Status500InternalServerError, "Internal Server Error")
        };

        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status   = statusCode,
            Title    = title,
            Detail   = ex.Message,
            Instance = context.Request.Path
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;

        // Never expose stack traces outside development
        var env = context.RequestServices.GetService<IWebHostEnvironment>();
        if (env?.IsDevelopment() == true)
            problem.Extensions["exception"] = ex.ToString();

        return context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
    }
}
