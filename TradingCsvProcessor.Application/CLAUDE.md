# TradingCsvProcessor.Application

Use-case orchestration layer. References Domain only. No EF Core, no ASP.NET Core, no file I/O.

## Directory structure

```
TradingCsvProcessor.Application/
├── Abstractions/
│   ├── ICommandHandler.cs    # ICommandHandler<TCommand, TResult>
│   └── IQueryHandler.cs      # IQueryHandler<TQuery, TResult>
├── DTOs/
│   ├── UploadResponse.cs     # Response records (all C# records)
│   ├── JobStatusResponse.cs  # + ChunkStatusDto, StageLogDto, CancelJobResponse
├── Extensions/
│   └── ApplicationServiceExtensions.cs   # AddApplication(IServiceCollection, IConfiguration)
├── Features/
│   └── Jobs/
│       ├── Commands/
│       │   ├── UploadCsv/
│       │   │   ├── UploadCsvCommand.cs          # record UploadCsvCommand(IFormFile File)
│       │   │   └── UploadCsvCommandHandler.cs
│       │   └── CancelJob/
│       │       ├── CancelJobCommand.cs          # record CancelJobCommand(Guid JobId)
│       │       └── CancelJobCommandHandler.cs
│       └── Queries/
│           ├── GetJobStatus/
│           │   ├── GetJobStatusQuery.cs         # record GetJobStatusQuery(Guid JobId)
│           │   └── GetJobStatusQueryHandler.cs
│           └── GetAllJobs/
│               ├── GetAllJobsQuery.cs           # record GetAllJobsQuery()
│               └── GetAllJobsQueryHandler.cs
├── Interfaces/
│   ├── IFileStorageService.cs     # StoreAsync / DeleteAsync
│   ├── IJobQueue.cs               # EnqueueAsync(Guid jobId)
│   └── IJobCancellationRegistry.cs # TryCancel / IsRunning
├── Mappings/
│   └── JobMappings.cs             # ToStatusResponse(this UploadJob, bool chunks, bool logs)
└── Options/
    ├── ProcessingOptions.cs       # Section = "Processing"
    └── FileStorageOptions.cs      # Section = "FileStorage"
```

## CQRS pattern

No MediatR. Handlers are registered directly as their interface:

```csharp
services.AddScoped<ICommandHandler<UploadCsvCommand, UploadResponse>, UploadCsvCommandHandler>();
```

Controllers inject `ICommandHandler<,>` / `IQueryHandler<,>` directly. Adding a new operation = new `record` + new `Handler` class + one line in `ApplicationServiceExtensions`.

## UploadCsvCommandHandler flow

1. `IFileStorageService.StoreAsync` — writes file to disk, returns path
2. `UploadJob.Create(...)` — domain factory
3. `jobRepo.Add(job)` + initial `JobStageLog` entries added
4. `job.MarkAsQueued()` + `unitOfWork.SaveChangesAsync()` — persisted **before** enqueue (safe restart)
5. `IJobQueue.EnqueueAsync(job.Id)` — puts job ID in the in-process channel
6. Final stage log + `unitOfWork.SaveChangesAsync()`

## CancelJobCommandHandler flow

1. `jobRepo.GetByIdAsync` — throws `NotFoundException` if missing
2. Guard against terminal statuses — throws `ConflictException`
3. `job.BeginCancelling()` + stage log + `unitOfWork.SaveChangesAsync()`
4. `IJobCancellationRegistry.TryCancel(jobId)` — signals in-memory CTS if job is running

## Options (validated at startup)

| Class | Section | Key fields |
|---|---|---|
| `ProcessingOptions` | `Processing` | `ChunkSize` [100–100k], `DegreeOfParallelism` [1–64], `ChannelCapacity` [1–10k] |
| `FileStorageOptions` | `FileStorage` | `Path` (required string), `MaxFileSizeBytes` [1–long.MaxValue] |

Both use `ValidateDataAnnotations().ValidateOnStart()` — invalid config aborts startup.

## JobMappings

`ToStatusResponse(this UploadJob job, bool includeChunks, bool includeLogs)` — static extension in `JobMappings`. Calculates `ProgressPercent` from total/processed/skipped/failed row counts. Called by both query handlers. Do not inline this mapping into handlers.
