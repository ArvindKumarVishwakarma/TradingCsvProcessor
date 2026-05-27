using System.Net.Mime;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using TradingCsvProcessor.Application.Extensions;
using TradingCsvProcessor.API.HealthChecks;
using TradingCsvProcessor.API.Middleware;
using TradingCsvProcessor.Infrastructure.Extensions;
using TradingCsvProcessor.Infrastructure.Persistence;

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ── Serilog ───────────────────────────────────────────────────────────────
    builder.Host.UseSerilog((ctx, services, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext()
        .Enrich.WithMachineName()
        .Enrich.WithThreadId());

    // ── Controllers & Problem Details ─────────────────────────────────────────
    builder.Services.AddControllers();
    builder.Services.AddProblemDetails();

    // ── Swagger ───────────────────────────────────────────────────────────────
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new()
        {
            Title       = "Trading CSV Processor API",
            Version     = "v1",
            Description = "Asynchronous bulk CSV trade upload and processing service."
        });
    });

    // ── Domain & Infrastructure ───────────────────────────────────────────────
    builder.Services.AddApplication(builder.Configuration);
    builder.Services.AddInfrastructure(builder.Configuration);

    // ── CORS ──────────────────────────────────────────────────────────────────
    var allowedOrigins = builder.Configuration
        .GetSection("Cors:AllowedOrigins")
        .Get<string[]>() ?? [];

    builder.Services.AddCors(opts => opts.AddDefaultPolicy(policy =>
    {
        if (allowedOrigins.Length > 0)
            policy.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod();
        else
            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
    }));

    // ── Rate Limiting ─────────────────────────────────────────────────────────
    var rlSection = builder.Configuration.GetSection("RateLimit");

    builder.Services.AddRateLimiter(opts =>
    {
        opts.AddSlidingWindowLimiter("upload", o =>
        {
            o.PermitLimit             = rlSection.GetValue("UploadPermitLimit", 10);
            o.Window                  = TimeSpan.FromMinutes(rlSection.GetValue("UploadWindowMinutes", 1));
            o.SegmentsPerWindow       = 4;
            o.QueueProcessingOrder    = QueueProcessingOrder.OldestFirst;
            o.QueueLimit              = 2;
        });

        opts.AddFixedWindowLimiter("api", o =>
        {
            o.PermitLimit             = rlSection.GetValue("ApiPermitLimit", 200);
            o.Window                  = TimeSpan.FromMinutes(rlSection.GetValue("ApiWindowMinutes", 1));
            o.QueueProcessingOrder    = QueueProcessingOrder.OldestFirst;
            o.QueueLimit              = 10;
        });

        opts.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        opts.OnRejected = async (ctx, ct) =>
        {
            ctx.HttpContext.Response.ContentType = MediaTypeNames.Application.Json;
            await ctx.HttpContext.Response.WriteAsJsonAsync(new
            {
                status = 429,
                title  = "Too Many Requests",
                detail = "Rate limit exceeded. Please reduce your request frequency."
            }, ct);
        };
    });

    // ── Response Compression ──────────────────────────────────────────────────
    builder.Services.AddResponseCompression(opts =>
    {
        opts.EnableForHttps = true;
        opts.Providers.Add<BrotliCompressionProvider>();
        opts.Providers.Add<GzipCompressionProvider>();
        opts.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        [
            "application/json",
            "application/problem+json"
        ]);
    });

    // ── Output Cache ──────────────────────────────────────────────────────────
    builder.Services.AddOutputCache(opts =>
    {
        opts.AddPolicy("job-status", policy =>
            policy.Expire(TimeSpan.FromSeconds(3)).SetVaryByRouteValue("jobId"));
        opts.AddPolicy("jobs-list", policy =>
            policy.Expire(TimeSpan.FromSeconds(3)));
    });

    // ── Request Timeouts ──────────────────────────────────────────────────────
    builder.Services.AddRequestTimeouts(opts =>
    {
        opts.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(30) };
        opts.AddPolicy("upload", TimeSpan.FromMinutes(10));
    });

    // ── Health Checks ─────────────────────────────────────────────────────────
    builder.Services.AddHealthChecks()
        .AddDbContextCheck<AppDbContext>("database", tags: ["ready", "db"])
        .AddCheck<FileStorageHealthCheck>("file-storage", tags: ["ready", "storage"]);

    // ── Memory Cache (for potential future distributed cache) ─────────────────
    builder.Services.AddMemoryCache();

    var app = builder.Build();

    // ── Database Initialization ───────────────────────────────────────────────
    using (var scope = app.Services.CreateScope())
    {
        var db  = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var log = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();
        try
        {
            await db.Database.EnsureCreatedAsync();
            log.LogInformation("Database schema verified.");
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Failed to initialize database — aborting startup.");
            throw;
        }
    }

    // ── Middleware Pipeline ───────────────────────────────────────────────────
    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseMiddleware<SecurityHeadersMiddleware>();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "Trading CSV Processor v1");
            c.RoutePrefix = "swagger";
        });
    }

    app.UseSerilogRequestLogging(opts =>
    {
        opts.EnrichDiagnosticContext = (diag, ctx) =>
        {
            diag.Set("RequestHost", ctx.Request.Host.Value ?? string.Empty);
            diag.Set("UserAgent",   ctx.Request.Headers.UserAgent.ToString());
        };
    });

    app.UseResponseCompression();
    app.UseCors();
    app.UseRateLimiter();
    app.UseRequestTimeouts();
    app.UseOutputCache();
    app.UseAuthorization();

    // ── Health Endpoints ──────────────────────────────────────────────────────
    app.MapHealthChecks("/health", new HealthCheckOptions
    {
        ResponseWriter        = HealthResponseWriter.Write,
        AllowCachingResponses = false
    });

    app.MapHealthChecks("/health/ready", new HealthCheckOptions
    {
        Predicate             = check => check.Tags.Contains("ready"),
        ResponseWriter        = HealthResponseWriter.Write,
        AllowCachingResponses = false
    });

    // Liveness — no checks needed; if the process is alive, it answers.
    app.MapHealthChecks("/health/live", new HealthCheckOptions
    {
        Predicate             = _ => false,
        ResponseWriter        = HealthResponseWriter.Write,
        AllowCachingResponses = false
    });

    app.MapControllers();

    await app.RunAsync();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Host terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}

// ── Health response helper ────────────────────────────────────────────────────
internal static class HealthResponseWriter
{
    public static Task Write(HttpContext ctx, HealthReport report)
    {
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsJsonAsync(new
        {
            status        = report.Status.ToString(),
            totalDurationMs = report.TotalDuration.TotalMilliseconds,
            checks        = report.Entries.Select(e => new
            {
                name        = e.Key,
                status      = e.Value.Status.ToString(),
                description = e.Value.Description,
                durationMs  = e.Value.Duration.TotalMilliseconds,
                tags        = e.Value.Tags
            })
        });
    }
}
