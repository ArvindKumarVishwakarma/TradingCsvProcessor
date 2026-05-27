# TradingCsvProcessor — Codebase Guide

## Solution layout

```
Trading.sln
├── TradingCsvProcessor.Domain/          # Pure domain — no framework deps
├── TradingCsvProcessor.Application/     # Use-case orchestration, CQRS, options
├── TradingCsvProcessor.Infrastructure/  # EF Core, file I/O, CSV, background worker
├── TradingCsvProcessor.API/             # ASP.NET Core host, controllers, middleware
├── Dockerfile                           # Multi-stage build → non-root runtime image
└── docker-compose.yml                   # API + SQL Server 2022, health-gated startup
```

Dependency direction is strictly inward: `API → Application → Domain ← Infrastructure`.
Infrastructure and API both reference Application but **not each other**.

## Build & run

```bash
dotnet build Trading.sln
dotnet run --project TradingCsvProcessor.API

# Docker
docker compose up --build
```

## Key configuration sections (`appsettings.json`)

| Section | What it controls |
|---|---|
| `ConnectionStrings:DefaultConnection` | SQL Server connection string |
| `FileStorage:Path` | Upload directory root (default `uploads`) |
| `FileStorage:MaxFileSizeBytes` | Per-upload size cap (default 100 MB) |
| `Processing:ChunkSize` | Rows per chunk (100–100,000; default 5,000) |
| `Processing:DegreeOfParallelism` | Parallel chunk workers (1–64; default 4) |
| `Processing:ChannelCapacity` | In-memory channel backpressure limit (default 100) |
| `RateLimit:UploadPermitLimit` | Upload endpoint sliding-window permit limit |
| `RateLimit:ApiPermitLimit` | General API fixed-window permit limit |
| `Cors:AllowedOrigins` | Array of allowed origins; empty = allow any |
| `Serilog` | Full Serilog configuration (console + rolling file) |

All `ProcessingOptions` and `FileStorageOptions` are validated with `DataAnnotations` at startup (`ValidateOnStart`). A bad config aborts the host before it accepts traffic.

## HTTP API endpoints

| Method | Route | Description |
|---|---|---|
| `POST` | `/api/csv/upload` | Upload CSV, returns `UploadResponse` with `jobId` |
| `GET` | `/api/csv/jobs/{jobId}` | Full job status + chunks + stage log |
| `GET` | `/api/csv/jobs` | List all jobs, newest first |
| `POST` | `/api/csv/jobs/{jobId}/cancel` | Cancel running or queued job |
| `GET` | `/health` | All health checks |
| `GET` | `/health/ready` | Readiness (DB + file storage) |
| `GET` | `/health/live` | Liveness (always healthy if process is up) |

## Middleware pipeline (in order)

1. `ExceptionHandlingMiddleware` — maps domain exceptions to RFC 7807 Problem Details
2. `CorrelationIdMiddleware` — reads/generates `X-Correlation-Id`, pushes to Serilog LogContext
3. `SecurityHeadersMiddleware` — adds `X-Content-Type-Options`, `X-Frame-Options`, etc.
4. Serilog request logging
5. Response compression (Brotli → Gzip)
6. CORS
7. Rate limiter (`upload` sliding-window on POST /upload; `api` fixed-window on all routes)
8. Request timeouts (30 s default; 10 min on POST /upload)
9. Output cache (3 s TTL on GET jobs endpoints)
10. Authorization (placeholder)
11. Controllers

## Job lifecycle (status flow)

```
Pending → Queued → Reading → Processing → Completed
                                       ↘ PartiallyCompleted
                                       ↘ Failed
         → Cancelling → Cancelled
```

A job in `Reading` or `Processing` when the process shuts down stays in that state in the DB. On the next startup `CsvProcessingWorker` calls `RequeueInterruptedJobsAsync` and re-enqueues those jobs — chunk-level deduplication ensures no rows are double-inserted.

## Deduplication

Each row is SHA-256 hashed over `TradeId|Symbol|TradeDate|Quantity|Price|Side`. The hash is stored in `TradeRecord.RecordHash` with a unique index. Duplicate rows within the same or a re-uploaded file are silently skipped (`SkippedRows` counter). Parallel-chunk unique-constraint races are handled with a retry/refilter pass in `ChunkProcessorService`.

## Adding a new feature

1. **Domain** — add entity/value-object or domain method; update repository interface if needed.
2. **Application** — add a `record XyzCommand/Query` + `XyzCommandHandler/QueryHandler` in `Features/`.  Register in `ApplicationServiceExtensions.AddApplication()`.
3. **Infrastructure** — implement any new repository methods; register in `InfrastructureServiceExtensions.AddInfrastructure()`.
4. **API** — inject the new handler into the controller; add a new action method.

Do **not** call `DbContext.SaveChangesAsync` in repositories — always go through `IUnitOfWork`.
