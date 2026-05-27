using Microsoft.AspNetCore.Mvc;
using TradingCsvProcessor.Domain.Exceptions;

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

            await WriteProblemAsync(context, ex);
        }
    }

    private static Task WriteProblemAsync(HttpContext context, Exception ex)
    {
        var (status, title) = ex switch
        {
            NotFoundException      => (StatusCodes.Status404NotFound,            "Not Found"),
            ConflictException      => (StatusCodes.Status409Conflict,            "Conflict"),
            DomainException        => (StatusCodes.Status400BadRequest,          "Bad Request"),
            InvalidOperationException
                                   => (StatusCodes.Status400BadRequest,          "Bad Request"),
            ArgumentException      => (StatusCodes.Status400BadRequest,          "Bad Request"),
            OperationCanceledException when context.RequestAborted.IsCancellationRequested
                                   => (499,                                      "Client Closed Request"),
            _                      => (StatusCodes.Status500InternalServerError, "Internal Server Error")
        };

        context.Response.StatusCode  = status;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status   = status,
            Title    = title,
            Detail   = ex.Message,
            Instance = context.Request.Path
        };
        problem.Extensions["traceId"]       = context.TraceIdentifier;
        problem.Extensions["correlationId"] = context.Items[CorrelationIdMiddleware.HeaderName];

        var env = context.RequestServices.GetService<IWebHostEnvironment>();
        if (env?.IsDevelopment() == true)
            problem.Extensions["exception"] = ex.ToString();

        return context.Response.WriteAsJsonAsync(problem, context.RequestAborted);
    }
}
