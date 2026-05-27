# TradingCsvProcessor.Infrastructure

All I/O and framework implementations. References Domain + Application. Never referenced by API.

## Directory structure

```
TradingCsvProcessor.Infrastructure/
├── Extensions/
│   └── InfrastructureServiceExtensions.cs  # AddInfrastructure(IServiceCollection, IConfiguration)
├── Messaging/
│   ├── ProcessingChannel.cs      # System.Threading.Channels bounded queue (implements IJobQueue)
│   └── JobCancellationRegistry.cs # ConcurrentDictionary<Guid, CTS> (implements IJobCancellationRegistry)
├── Models/
│   └── TradeCsvRow.cs            # CsvHelper mapping target for CSV rows
├── Persistence/
│   ├── AppDbContext.cs           # EF Core DbContext; configures backing fields + column mappings
│   └── UnitOfWork.cs             # Wraps AppDbContext.SaveChangesAsync — internal sealed
├── Processing/
│   ├── ICsvStreamReader.cs       # StreamChunksAsync(IAsyncEnumerable<ChunkPayload>) + CountRowsAsync
│   ├── CsvStreamReaderService.cs # CsvHelper implementation; 64 KB read buffer
│   ├── IChunkProcessor.cs        # ProcessAsync(Guid jobId, ChunkPayload, ct) → ChunkResult
│   ├── ChunkProcessorService.cs  # Dedup + bulk insert + unique-violation retry
│   ├── IJobOrchestrator.cs       # RequeueInterruptedJobsAsync + OrchestrateAsync
│   ├── JobOrchestrator.cs        # Full job lifecycle; Parallel.ForEachAsync over chunks
│   └── RowHasher.cs              # SHA-256 hash of TradeId|Symbol|TradeDate|Qty|Price|Side
├── Repositories/
│   ├── UploadJobRepository.cs
│   ├── UploadJobChunkRepository.cs
│   ├── TradeRecordRepository.cs
│   └── JobStageLogRepository.cs
├── Storage/
│   └── FileStorageService.cs     # Day-bucketed storage; path-traversal protection
└── Workers/
    └── CsvProcessingWorker.cs    # BackgroundService — reads channel, delegates to IJobOrchestrator
```

## Processing pipeline

```
CsvProcessingWorker  (channel loop)
  └─ JobOrchestrator.OrchestrateAsync
       ├─ CsvStreamReaderService.CountRowsAsync   (single-pass row count, O(1) memory)
       └─ Parallel.ForEachAsync (DOP from ProcessingOptions)
            └─ ChunkProcessorService.ProcessAsync (scoped per chunk)
                 ├─ RowHasher.Compute              (SHA-256 dedup key)
                 ├─ TradeRecordRepository.GetExistingHashesAsync
                 ├─ TradeRecordRepository.BulkInsertAsync (EFCore.BulkExtensions)
                 └─ unique-violation retry/refilter
```

Each chunk gets its own `IServiceScope` (fresh `AppDbContext` + `IUnitOfWork`) to avoid EF Core concurrency issues under parallel execution.

## Cancellation design

Two cancellation tokens per job:
- **`stoppingToken`** — host shutdown; job stays in DB as-is and is re-queued on restart.
- **`jobToken`** — user cancel via API; `JobCancellationRegistry.TryCancel(jobId)` triggers it.

`JobOrchestrator` links both into `linked.Token`. The `catch (OperationCanceledException)` block distinguishes which token fired to decide whether to mark the job `Cancelled` or let the exception propagate (re-throw on app shutdown).

## UnitOfWork

`UnitOfWork` is `internal sealed` — only the DI container uses it. Repositories call `IUnitOfWork.SaveChangesAsync(ct)`; they **never** call `dbContext.SaveChangesAsync` directly. This ensures all writes in a unit of work are transactional.

## FileStorageService — path traversal protection

```csharp
var safeName   = Path.GetFileName(file.FileName);        // strip any directory components
var fullPath   = Path.GetFullPath(Path.Combine(...));    // resolve to absolute
var baseResolved = Path.GetFullPath(_basePath);
if (!fullPath.StartsWith(baseResolved, ...))
    throw new InvalidOperationException("Resolved file path escapes the storage root.");
```

Files are stored under `{basePath}/{yyyy-MM-dd}/{guid}_{filename}`.

## AppDbContext — EF Core notes

- Navigation backing fields configured explicitly:
  ```csharp
  e.Navigation(x => x.Chunks).HasField("_chunks").UsePropertyAccessMode(PropertyAccessMode.Field);
  e.Navigation(x => x.StageLogs).HasField("_stageLogs").UsePropertyAccessMode(PropertyAccessMode.Field);
  ```
- Enums stored as strings (`HasConversion<string>()`).
- `TradeRecord.RecordHash` has a unique index — enforces deduplication at the DB level.
- SQL Server retry: 5 retries, 30 s max delay, `CommandTimeout` 120 s.

## ProcessingChannel

`System.Threading.Channels` bounded channel. `SingleReader = true` (only `CsvProcessingWorker` reads). `FullMode = Wait` — upload endpoint blocks (backpressure) when the channel is full. Capacity is configurable via `Processing:ChannelCapacity`.

## Registration (AddInfrastructure)

| Interface | Implementation | Lifetime |
|---|---|---|
| `IUnitOfWork` | `UnitOfWork` | Scoped |
| `IJobQueue` | `ProcessingChannel` (singleton alias) | Singleton |
| `IJobCancellationRegistry` | `JobCancellationRegistry` (singleton alias) | Singleton |
| `IUploadJobRepository` | `UploadJobRepository` | Scoped |
| `IUploadJobChunkRepository` | `UploadJobChunkRepository` | Scoped |
| `ITradeRecordRepository` | `TradeRecordRepository` | Scoped |
| `IJobStageLogRepository` | `JobStageLogRepository` | Scoped |
| `IFileStorageService` | `FileStorageService` | Scoped |
| `ICsvStreamReader` | `CsvStreamReaderService` | Scoped |
| `IChunkProcessor` | `ChunkProcessorService` | Scoped |
| `IJobOrchestrator` | `JobOrchestrator` | Scoped |
| `CsvProcessingWorker` | — | Hosted service |
