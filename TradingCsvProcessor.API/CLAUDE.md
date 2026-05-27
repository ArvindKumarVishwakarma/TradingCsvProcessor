# TradingCsvProcessor.API

ASP.NET Core 8 host. References Application (for CQRS interfaces + options) and Infrastructure (for `AppDbContext` startup init + `InfrastructureServiceExtensions`).

## Directory structure

```
TradingCsvProcessor.API/
├── Controllers/
│   └── CsvUploadController.cs    # All CSV job endpoints; injects CQRS handlers directly
├── HealthChecks/
│   └── FileStorageHealthCheck.cs # Write-probe against the upload directory
├── Middleware/
│   ├── ExceptionHandlingMiddleware.cs  # RFC 7807 Problem Details for all unhandled exceptions
│   ├── CorrelationIdMiddleware.cs      # X-Correlation-Id propagation + Serilog enrichment
│   └── SecurityHeadersMiddleware.cs   # nosniff, DENY, XSS-protection, referrer-policy
├── appsettings.json
├── appsettings.Development.json
├── appsettings.Production.json
└── Program.cs
```

## Middleware pipeline order

```
ExceptionHandlingMiddleware
CorrelationIdMiddleware          ← X-Correlation-Id header in + out; pushes to Serilog LogContext
SecurityHeadersMiddleware
[Swagger — Development only]
SerilogRequestLogging
ResponseCompression (Brotli, Gzip)
CORS
RateLimiter
RequestTimeouts
OutputCache
Authorization
MapControllers
```

### Why ExceptionHandlingMiddleware is first, not CorrelationIdMiddleware

`ExceptionHandlingMiddleware` wraps the entire pipeline in a try/catch. When an exception is caught it writes a Problem Details response that includes `correlationId` from `HttpContext.Items`. For that value to exist at error-response time, `CorrelationIdMiddleware` must have already run — which it has, because it sits immediately inside the exception handler and executes on the way **in**.

Request flow on error:

```
→ ExceptionHandlingMiddleware  (try { await next })
  → CorrelationIdMiddleware    sets HttpContext.Items["X-Correlation-Id"]
    → ... deeper middleware throws
  ← exception bubbles up
← ExceptionHandlingMiddleware catch block reads HttpContext.Items["X-Correlation-Id"] ✓
```

If the order were reversed, the catch block would fire before the correlation ID was ever set, and every error response would have `"correlationId": null`.

## CsvUploadController

Route prefix: `api/csv`. Rate-limited with `[EnableRateLimiting("api")]` globally; the upload action adds `[EnableRateLimiting("upload")]`.

Constructor injects CQRS handlers directly — no service locator, no MediatR:

```csharp
ICommandHandler<UploadCsvCommand, UploadResponse>
ICommandHandler<CancelJobCommand, CancelJobResponse>
IQueryHandler<GetJobStatusQuery,  JobStatusResponse?>
IQueryHandler<GetAllJobsQuery,    IReadOnlyList<JobStatusResponse>>
IOptions<FileStorageOptions>   // for MaxFileSize guard
```

`NotFoundException` and `ConflictException` thrown by handlers are **not caught in the controller** — they bubble to `ExceptionHandlingMiddleware`.

## ExceptionHandlingMiddleware

Maps exception types to HTTP status + Problem Details:

| Exception | Status |
|---|---|
| `NotFoundException` | 404 |
| `ConflictException` | 409 |
| `DomainException` | 400 |
| `InvalidOperationException` | 400 |
| `ArgumentException` | 400 |
| `OperationCanceledException` (client abort) | 499 |
| anything else | 500 |

Adds `traceId` and `correlationId` to Problem Details extensions. In Development, also adds full `exception` stack trace string.

## CorrelationIdMiddleware

- Reads `X-Correlation-Id` from request headers; generates a new `Guid("N")` if absent.
- Stores it in `HttpContext.Items[CorrelationIdMiddleware.HeaderName]`.
- Echoes it back in the response via `OnStarting`.
- Wraps downstream in `LogContext.PushProperty("CorrelationId", ...)` so every Serilog log line from the request carries the correlation ID.

## Rate limiting

| Policy | Type | Default |
|---|---|---|
| `upload` | Sliding window | 10 req / 1 min, queue 2 |
| `api` | Fixed window | 200 req / 1 min, queue 10 |

429 responses are JSON (not the default plain-text): `{ status, title, detail }`.

## Output caching

| Policy | TTL | Varies by |
|---|---|---|
| `job-status` | 3 s | `jobId` route value |
| `jobs-list` | 3 s | — |

## Health checks

| Endpoint | Checks |
|---|---|
| `/health` | All registered checks |
| `/health/ready` | Tag `ready` — EF Core DB ping + file storage write-probe |
| `/health/live` | No checks (always Healthy while process is up) |

Response is custom JSON (not the default string): status, totalDurationMs, per-check breakdown.

## Program.cs startup sequence

1. Bootstrap Serilog logger (console only, before host builds)
2. Build host — register Application + Infrastructure services, CORS, rate limiter, compression, output cache, timeouts, health checks, memory cache
3. `EnsureCreatedAsync` — creates schema on first run (no migrations)
4. Build middleware pipeline
5. `app.RunAsync()`

Serilog request logging enriches `DiagnosticContext` with `CorrelationId` and `RequestHost`.
