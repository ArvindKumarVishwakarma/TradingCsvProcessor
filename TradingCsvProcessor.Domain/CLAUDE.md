# TradingCsvProcessor.Domain

Zero external dependencies. No framework code. References nothing else in the solution.

## Directory structure

```
TradingCsvProcessor.Domain/
├── Entities/
│   ├── UploadJob.cs          # Aggregate root — owns chunks + stage logs
│   ├── UploadJobChunk.cs     # One chunk = one slice of the CSV file
│   ├── JobStageLog.cs        # Immutable audit entry per processing stage
│   └── TradeRecord.cs        # Persisted trade row
├── Enums/
│   └── Enums.cs              # JobStatus, ChunkStatus, ProcessingStage
├── Exceptions/
│   ├── DomainException.cs    # Base — caught by ExceptionHandlingMiddleware → 400
│   ├── NotFoundException.cs  # → 404
│   └── ConflictException.cs  # → 409
├── Interfaces/
│   └── IUnitOfWork.cs        # SaveChangesAsync — implemented by Infrastructure
└── Repositories/
    ├── IUploadJobRepository.cs
    ├── IUploadJobChunkRepository.cs
    ├── IJobStageLogRepository.cs
    └── ITradeRecordRepository.cs
```

## Entity design rules

All entities use **private setters** and are mutated only through named domain methods. EF Core materialises them via the private parameterless constructor.

Navigation collections (`_chunks`, `_stageLogs`) are `private readonly List<T>` backing fields exposed as `IReadOnlyCollection<T>`. EF Core is told about them via `HasField` + `UsePropertyAccessMode(PropertyAccessMode.Field)` in `AppDbContext`.

### UploadJob (aggregate root)

| Domain method | What it does |
|---|---|
| `UploadJob.Create(fileName, storedPath, size)` | Factory — the only way to create a new job |
| `MarkAsQueued()` | Status → Queued, Stage → JobQueued |
| `BeginReading()` | Status → Reading; sets `StartedAt` once |
| `SetTotalRows(count)` | Stores row count after the fast counting pass |
| `BeginProcessing()` | Status → Processing |
| `Complete(processed, skipped, failed, finalStatus)` | Sets counters + `CompletedAt` |
| `Fail(error)` | Status → Failed, stores error message |
| `BeginCancelling()` | Status → Cancelling, sets `IsCancellationRequested = true` |
| `MarkCancelled()` | Status → Cancelled, sets `CancelledAt` |
| `AdvanceStage(stage)` | Updates `CurrentStage` for fine-grained progress tracking |

### JobStageLog (immutable audit)

Created only via `JobStageLog.For(jobId, stage, message, chunkId?)`. No setters called after creation.

## Repository contracts

Repositories expose only query/add methods. **`SaveChangesAsync` is absent from all repository interfaces** — persistence is the caller's responsibility via `IUnitOfWork`.

```csharp
void Add(TEntity entity);   // registers with EF change tracker only
```

## Exception hierarchy

```
DomainException (400)
├── NotFoundException (404)   — ctor: (entityName, id)
└── ConflictException (409)   — ctor: (message)
```

Throw from Application-layer handlers; `ExceptionHandlingMiddleware` in the API project maps them to Problem Details responses automatically.
