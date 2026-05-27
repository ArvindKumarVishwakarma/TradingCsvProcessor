# Trading CSV Processor

A .NET 8 Web API that accepts large CSV file uploads, processes them asynchronously via a background worker, and tracks every stage of processing with full duplicate-record prevention.

---

## Table of Contents

- [Architecture Overview](#architecture-overview)
- [Complete Flow](#complete-flow)
- [Stage Tracking](#stage-tracking)
- [Duplicate Detection](#duplicate-detection)
- [Project Structure](#project-structure)
- [Database Schema](#database-schema)
- [API Reference](#api-reference)
- [Configuration](#configuration)
- [Setup & Run](#setup--run)
- [Sample CSV Format](#sample-csv-format)
- [Sequence Diagram](#sequence-diagram)

---

## Architecture Overview

```
┌─────────────────────────────────────────────────────────────────────┐
│                         CLIENT (HTTP)                               │
│              POST /api/csv/upload  (multipart CSV)                  │
└─────────────────────────┬───────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   CsvUploadController                               │
│  • Validates file (CSV, ≤ 100 MB)                                   │
│  • Returns 202 Accepted + JobId immediately                         │
└─────────────────────────┬───────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   CsvUploadService                                  │
│                                                                     │
│  1. FileStorageService.StoreAsync()                                 │
│     └─ Saves file → uploads/YYYY-MM-DD/<uuid>_filename.csv          │
│                                                                     │
│  2. Creates UploadJob row (Status = Pending)                        │
│     └─ Writes stage logs: FileUploaded → FileStored → JobCreated    │
│                                                                     │
│  3. Enqueues JobId into ProcessingChannel                           │
│     └─ Updates Status = Queued, logs JobQueued                      │
└─────────────────────────┬───────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────────────┐
│                   ProcessingChannel                                 │
│   System.Threading.Channels.Channel<Guid>  (bounded, async)        │
│   Capacity: 1000 jobs  ·  Writer: HTTP thread  ·  Reader: Worker    │
└─────────────────────────┬───────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────────────┐
│              CsvProcessingWorker  (BackgroundService)               │
│                                                                     │
│  ┌──────────────────────────────────────────────────────────────┐   │
│  │  STAGE: FileReading                                          │   │
│  │  CsvHelper reads entire file → List<TradeCsvRow>             │   │
│  │  Sets TotalRows on job                                       │   │
│  └──────────────────────────────────┬───────────────────────────┘   │
│                                     │                               │
│  ┌──────────────────────────────────▼───────────────────────────┐   │
│  │  STAGE: ChunksCreated                                        │   │
│  │  Splits rows into N chunks of 500 rows each                  │   │
│  │  Creates UploadJobChunk rows in DB                           │   │
│  │  Already-Completed chunks are SKIPPED (crash-safe restart)   │   │
│  └──────────────────────────────────┬───────────────────────────┘   │
│                                     │                               │
│              ┌──────────────────────┘                               │
│              │  For each PENDING chunk:                             │
│  ┌───────────▼──────────────────────────────────────────────────┐   │
│  │  STAGE: ChunkProcessing                                      │   │
│  │  ┌────────────────────────────────────────────────────────┐  │   │
│  │  │  1. Compute SHA-256 hash for every row                 │  │   │
│  │  │     key = TradeId|Symbol|Date|Qty|Price|Side           │  │   │
│  │  │                                                        │  │   │
│  │  │  2. Batch query: SELECT RecordHash WHERE hash IN (..N) │  │   │
│  │  │     → existingHashes HashSet<string>                   │  │   │
│  │  │                                                        │  │   │
│  │  │  3. Filter:  hash ∈ existingHashes → SkippedCount++    │  │   │
│  │  │             hash ∉ existingHashes → toInsert list      │  │   │
│  │  │                                                        │  │   │
│  │  │  4. STAGE: ChunkBulkInserting                          │  │   │
│  │  │     BulkInsertAsync() → single SQL round-trip          │  │   │
│  │  │                                                        │  │   │
│  │  │  5. STAGE: ChunkCompleted                              │  │   │
│  │  │     Update counts: ProcessedCount / SkippedCount       │  │   │
│  │  └────────────────────────────────────────────────────────┘  │   │
│  └──────────────────────────────────────────────────────────────┘   │
│                                     │                               │
│  ┌──────────────────────────────────▼───────────────────────────┐   │
│  │  STAGE: JobCompleted / JobFailed / PartiallyCompleted        │   │
│  │  Aggregates all chunk counts into UploadJob                  │   │
│  └──────────────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────────────────┐
│                     SQL SERVER DATABASE                             │
│                                                                     │
│   UploadJobs ──┬── UploadJobChunks                                  │
│                └── JobStageLogs                                     │
│                                                                     │
│   TradeRecords  (UNIQUE INDEX on RecordHash)                        │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Complete Flow

### Step-by-step walkthrough

```
CLIENT                   CONTROLLER              SERVICE                  WORKER                   DATABASE
  │                          │                      │                        │                        │
  │── POST /api/csv/upload ──▶│                      │                        │                        │
  │                          │── UploadAsync() ─────▶│                        │                        │
  │                          │                      │── StoreFile ───────────────────────────────────▶│
  │                          │                      │── INSERT UploadJob ─────────────────────────────▶│
  │                          │                      │── INSERT StageLog×4 ────────────────────────────▶│
  │                          │                      │── Channel.WriteAsync(jobId)                      │
  │                          │                      │                        │                        │
  │◀─── 202 Accepted ─────────│◀─ UploadResponse ────│                        │                        │
  │     { jobId, status }     │                      │                        │                        │
  │                          │                      │                        │                        │
  │                          │                      │      Channel.ReadAsync()│                        │
  │                          │                      │                        │── SELECT UploadJob ────▶│
  │                          │                      │                        │── UPDATE Status=Reading │
  │                          │                      │                        │── INSERT StageLog ─────▶│
  │                          │                      │                        │                        │
  │                          │                      │                  ReadCsvAsync()                  │
  │                          │                      │                  (CsvHelper → List<Row>)         │
  │                          │                      │                        │                        │
  │                          │                      │                        │── UPDATE TotalRows ────▶│
  │                          │                      │                        │── INSERT Chunks ───────▶│
  │                          │                      │                        │── INSERT StageLog ─────▶│
  │                          │                      │                        │                        │
  │                          │          ┌───────────────────── For each Chunk ──────────────────────┐ │
  │                          │          │             │                        │                    │ │
  │                          │          │             │                  ComputeHash(rows)          │ │
  │                          │          │             │                        │── SELECT hashes ──▶│ │
  │                          │          │             │                  Filter duplicates          │ │
  │                          │          │             │                        │── BulkInsert ──────▶│ │
  │                          │          │             │                        │── UPDATE Chunk ────▶│ │
  │                          │          │             │                        │── INSERT StageLog ─▶│ │
  │                          │          └───────────────────────────────────────────────────────────┘ │
  │                          │                      │                        │                        │
  │                          │                      │                        │── UPDATE Job=Completed ▶│
  │                          │                      │                        │── INSERT StageLog ─────▶│
  │                          │                      │                        │                        │
  │── GET /api/csv/jobs/{id} ▶│                      │                        │                        │
  │                          │── GetJobStatusAsync()─▶│                        │                        │
  │                          │                      │── SELECT Job+Chunks+Logs ──────────────────────▶│
  │◀─── 200 OK ───────────────│◀─ JobStatusResponse ─│                        │                        │
  │     { status, progress,  │                      │                        │                        │
  │       chunks[], logs[] } │                      │                        │                        │
```

---

## Stage Tracking

Every transition through the pipeline writes a `JobStageLog` row. This gives you a full immutable audit trail per job.

| Stage | Who Sets It | Description |
|---|---|---|
| `FileUploaded` | `CsvUploadService` | HTTP layer received the file |
| `FileStored` | `CsvUploadService` | File written to disk |
| `JobCreated` | `CsvUploadService` | `UploadJob` row inserted |
| `JobQueued` | `CsvUploadService` | Job ID pushed to channel |
| `FileReading` | `CsvProcessingWorker` | Worker started reading CSV |
| `ChunksCreated` | `CsvProcessingWorker` | Chunk plan built and saved |
| `ChunkProcessing` | `CsvProcessingWorker` | Per-chunk: dedup + map phase |
| `ChunkBulkInserting` | `CsvProcessingWorker` | Per-chunk: bulk SQL insert |
| `ChunkCompleted` | `CsvProcessingWorker` | Per-chunk: counts updated |
| `ChunkFailed` | `CsvProcessingWorker` | Per-chunk: error captured |
| `JobCompleted` | `CsvProcessingWorker` | All chunks done successfully |
| `JobFailed` | `CsvProcessingWorker` | Fatal error or all chunks failed |

### Job Status lifecycle

```
Pending ──▶ Queued ──▶ Reading ──▶ Processing ──▶ Completed
                                        │
                                        ├──▶ PartiallyCompleted  (some chunks failed)
                                        └──▶ Failed              (all chunks failed)
```

### Chunk Status lifecycle

```
Pending ──▶ Processing ──▶ Completed
                  └──────▶ Failed
```

---

## Duplicate Detection

The system uses **content-addressable hashing** to prevent the same record from being inserted twice — even across different uploads.

### How it works

```
CSV Row fields used for hash key:
  TradeId | Symbol | TradeDate | Quantity | Price | Side
              │
              ▼
  SHA-256(UTF8(key))  →  64-char hex string (RecordHash)
              │
              ▼
  Batch SELECT WHERE RecordHash IN (chunk hashes)
              │
         ┌────┴────┐
    EXISTS?         NEW?
         │               │
    SkippedRows++    toInsert list
                         │
                    BulkInsertAsync
                         │
              DB UNIQUE INDEX on RecordHash
              (second line of defense)
```

### Dedup scope

| Scenario | Result |
|---|---|
| Same file uploaded twice | All rows skipped on second upload |
| Partial upload re-submitted | Only missing rows inserted |
| Different file, same trade data | Row skipped, counted as `SkippedRows` |
| Same file, new rows added | Only new rows inserted |

To change which fields constitute a "duplicate", edit `ComputeRowHash()` in `CsvProcessingWorker.cs`.

---

## Project Structure

```
TradingCsvProcessor/
│
├── Controllers/
│   └── CsvUploadController.cs     # 3 endpoints: upload, status, list
│
├── Data/
│   └── AppDbContext.cs            # EF Core DbContext + model configuration
│
├── Infrastructure/
│   └── ProcessingChannel.cs       # Singleton Channel<Guid> job queue
│
├── Models/
│   ├── Csv/
│   │   └── TradeCsvRow.cs         # CsvHelper-mapped CSV row model
│   ├── Domain/
│   │   ├── Enums.cs               # JobStatus, ChunkStatus, ProcessingStage
│   │   ├── UploadJob.cs           # Job aggregate root
│   │   ├── UploadJobChunk.cs      # Per-chunk tracking entity
│   │   ├── TradeRecord.cs         # Destination data entity
│   │   └── JobStageLog.cs         # Immutable audit log entry
│   └── DTOs/
│       ├── UploadResponse.cs      # POST /upload response
│       └── JobStatusResponse.cs   # GET /jobs/{id} response
│
├── Services/
│   ├── IFileStorageService.cs
│   ├── FileStorageService.cs      # Saves files to disk by date folder
│   ├── ICsvUploadService.cs
│   └── CsvUploadService.cs        # Orchestrates upload → DB → queue
│
├── Workers/
│   └── CsvProcessingWorker.cs     # BackgroundService: reads channel, processes jobs
│
├── Program.cs                     # DI wiring, EF auto-create, Swagger
├── appsettings.json
└── appsettings.Development.json
```

---

## Database Schema

### UploadJobs

| Column | Type | Notes |
|---|---|---|
| `Id` | `uniqueidentifier` | PK, job identity |
| `FileName` | `nvarchar(500)` | Original filename |
| `StoredFilePath` | `nvarchar(1000)` | Full path on disk |
| `FileSizeBytes` | `bigint` | |
| `TotalRows` | `int` | Set after file is read |
| `ProcessedRows` | `int` | Successfully inserted |
| `SkippedRows` | `int` | Duplicates skipped |
| `FailedRows` | `int` | Mapping/insert errors |
| `Status` | `nvarchar(30)` | JobStatus enum as string |
| `CurrentStage` | `nvarchar(50)` | Last ProcessingStage |
| `CreatedAt` | `datetime2` | |
| `StartedAt` | `datetime2` | Worker pick-up time |
| `CompletedAt` | `datetime2` | |
| `ErrorMessage` | `nvarchar(2000)` | Fatal error if any |

### UploadJobChunks

| Column | Type | Notes |
|---|---|---|
| `Id` | `uniqueidentifier` | PK |
| `JobId` | `uniqueidentifier` | FK → UploadJobs |
| `ChunkNumber` | `int` | 1-based sequence |
| `StartRow` | `int` | 1-based, inclusive |
| `EndRow` | `int` | 1-based, inclusive |
| `TotalRows` | `int` | EndRow - StartRow + 1 |
| `Status` | `nvarchar(30)` | ChunkStatus enum |
| `ProcessedCount` | `int` | |
| `SkippedCount` | `int` | |
| `FailedCount` | `int` | |
| `RetryCount` | `int` | Increments on each attempt |
| `CreatedAt` | `datetime2` | |
| `StartedAt` | `datetime2` | |
| `CompletedAt` | `datetime2` | |
| `ErrorMessage` | `nvarchar(2000)` | |

> Unique index on `(JobId, ChunkNumber)`

### TradeRecords

| Column | Type | Notes |
|---|---|---|
| `Id` | `bigint` | PK, identity |
| `JobId` | `uniqueidentifier` | Source job reference |
| `RecordHash` | `nvarchar(64)` | SHA-256 of key fields — **UNIQUE** |
| `TradeId` | `nvarchar(100)` | |
| `Symbol` | `nvarchar(50)` | |
| `TradeDate` | `datetime2` | |
| `Quantity` | `decimal(18,6)` | |
| `Price` | `decimal(18,6)` | |
| `Side` | `nvarchar(10)` | Buy / Sell |
| `TotalValue` | `decimal(18,6)` | Quantity × Price |
| `Exchange` | `nvarchar(50)` | Optional |
| `Currency` | `nvarchar(10)` | Optional |
| `CreatedAt` | `datetime2` | |

> Unique index on `RecordHash` — DB-level duplicate guard

### JobStageLogs

| Column | Type | Notes |
|---|---|---|
| `Id` | `bigint` | PK, identity |
| `JobId` | `uniqueidentifier` | FK → UploadJobs |
| `ChunkId` | `uniqueidentifier` | FK → UploadJobChunks (nullable) |
| `Stage` | `nvarchar(50)` | ProcessingStage enum |
| `Message` | `nvarchar(1000)` | Human-readable description |
| `CreatedAt` | `datetime2` | |

---

## API Reference

### POST `/api/csv/upload`

Upload a CSV file for async processing.

**Request** — `multipart/form-data`

| Field | Type | Required |
|---|---|---|
| `file` | `.csv` file | Yes |

**Response** — `202 Accepted`

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fileName": "trades_2026_05.csv",
  "fileSizeBytes": 2048000,
  "status": "Queued",
  "message": "Uploaded and queued for processing."
}
```

---

### GET `/api/csv/jobs/{jobId}`

Get full job status with per-chunk breakdown and complete stage audit log.

**Response** — `200 OK`

```json
{
  "jobId": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "fileName": "trades_2026_05.csv",
  "status": "Completed",
  "currentStage": "JobCompleted",
  "totalRows": 15000,
  "processedRows": 14750,
  "skippedRows": 230,
  "failedRows": 20,
  "progressPercent": 100.0,
  "createdAt": "2026-05-19T10:00:00Z",
  "startedAt": "2026-05-19T10:00:01Z",
  "completedAt": "2026-05-19T10:00:18Z",
  "errorMessage": null,
  "chunks": [
    {
      "chunkId": "...",
      "chunkNumber": 1,
      "startRow": 1,
      "endRow": 500,
      "totalRows": 500,
      "status": "Completed",
      "processedCount": 493,
      "skippedCount": 7,
      "failedCount": 0,
      "retryCount": 1,
      "completedAt": "2026-05-19T10:00:03Z",
      "errorMessage": null
    }
  ],
  "stageLogs": [
    { "stage": "FileUploaded",      "message": "Received trades_2026_05.csv (2048000 bytes)", "timestamp": "2026-05-19T10:00:00Z" },
    { "stage": "FileStored",        "message": "Stored to uploads/2026-05-19/abc123_trades.csv", "timestamp": "2026-05-19T10:00:00Z" },
    { "stage": "JobCreated",        "message": "Job 3fa8... created",                           "timestamp": "2026-05-19T10:00:00Z" },
    { "stage": "JobQueued",         "message": "Job enqueued for processing",                   "timestamp": "2026-05-19T10:00:00Z" },
    { "stage": "FileReading",       "message": "Reading CSV file",                              "timestamp": "2026-05-19T10:00:01Z" },
    { "stage": "ChunksCreated",     "message": "Plan: 30 chunks × 500 rows each for 15000 total rows", "timestamp": "2026-05-19T10:00:02Z" },
    { "stage": "ChunkProcessing",   "message": "Chunk 1: rows 1–500",                           "timestamp": "2026-05-19T10:00:02Z" },
    { "stage": "ChunkBulkInserting","message": "Chunk 1: bulk inserting 493 records",           "timestamp": "2026-05-19T10:00:02Z" },
    { "stage": "ChunkCompleted",    "message": "Chunk 1 done — inserted: 493, skipped: 7, failed: 0", "timestamp": "2026-05-19T10:00:03Z" },
    { "stage": "JobCompleted",      "message": "Done — inserted: 14750, skipped (dup): 230, failed: 20", "timestamp": "2026-05-19T10:00:18Z" }
  ]
}
```

---

### GET `/api/csv/jobs`

List all upload jobs (newest first), without stage logs.

**Response** — `200 OK` — array of `JobStatusResponse`

---

## Configuration

`appsettings.json`

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Server=(localdb)\\mssqllocaldb;Database=TradingCsvProcessor;Trusted_Connection=True"
  },
  "FileStorage": {
    "Path": "uploads"
  },
  "Processing": {
    "ChunkSize": 500,
    "ChannelCapacity": 1000
  }
}
```

| Key | Default | Description |
|---|---|---|
| `Processing:ChunkSize` | `500` | Rows per chunk for bulk insert |
| `Processing:ChannelCapacity` | `1000` | Max jobs queued in memory at once |
| `FileStorage:Path` | `uploads` | Root folder for stored CSV files |

---

## Setup & Run

### Prerequisites

- .NET 8 SDK
- SQL Server or SQL Server LocalDB

### 1. Configure connection string

Edit `appsettings.json` → `ConnectionStrings:DefaultConnection`.

### 2. Run the application

```bash
cd TradingCsvProcessor
dotnet run
```

The database schema is created automatically on first startup via `EnsureCreatedAsync()`.

### 3. Open Swagger UI

```
http://localhost:5000/swagger
```

### Switch to EF Core Migrations (recommended for production)

```bash
dotnet ef migrations add InitialCreate
dotnet ef database update
```

Then replace `EnsureCreatedAsync()` with `MigrateAsync()` in `Program.cs`.

---

## Sample CSV Format

```csv
TradeId,Symbol,TradeDate,Quantity,Price,Side,Exchange,Currency
TRD-001,AAPL,2026-05-19T09:30:00Z,100,182.50,Buy,NASDAQ,USD
TRD-002,GOOGL,2026-05-19T09:31:00Z,50,175.20,Sell,NASDAQ,USD
TRD-003,MSFT,2026-05-19T09:32:00Z,200,415.00,Buy,NASDAQ,USD
```

| Column | Required | Notes |
|---|---|---|
| `TradeId` | Yes | Used in duplicate hash |
| `Symbol` | Yes | Used in duplicate hash |
| `TradeDate` | Yes | ISO 8601 format |
| `Quantity` | Yes | Decimal |
| `Price` | Yes | Decimal |
| `Side` | Yes | `Buy` or `Sell` |
| `Exchange` | No | |
| `Currency` | No | |

---

## Sequence Diagram

```
┌──────┐     ┌──────────┐     ┌────────────┐     ┌──────────────┐     ┌────────┐
│Client│     │Controller│     │UploadService│     │  CsvWorker   │     │  DB    │
└──┬───┘     └────┬─────┘     └─────┬──────┘     └──────┬───────┘     └───┬────┘
   │              │                 │                    │                 │
   │─POST upload─▶│                 │                    │                 │
   │              │──UploadAsync()─▶│                    │                 │
   │              │                 │──StoreFile─────────────────────────▶│
   │              │                 │──INSERT Job────────────────────────▶│
   │              │                 │──INSERT StageLogs(×4)──────────────▶│
   │              │                 │──Channel.Write(jobId)               │
   │◀─202 Accepted│◀─UploadResponse─│                    │                 │
   │  { jobId }   │                 │                    │                 │
   │              │                 │         Channel.Read(jobId)          │
   │              │                 │                    │──SELECT Job────▶│
   │              │                 │                    │──ReadCsvAsync() │
   │              │                 │                    │──INSERT Chunks─▶│
   │              │                 │                    │                 │
   │              │        ╔════════════════╗            │                 │
   │              │        ║  CHUNK LOOP    ║            │                 │
   │              │        ╚════════════════╝            │                 │
   │              │                 │                    │──SHA256(rows)   │
   │              │                 │                    │──SELECT hashes─▶│
   │              │                 │                    │  (batch dedup)  │
   │              │                 │                    │──BulkInsert────▶│
   │              │                 │                    │──UPDATE Chunk──▶│
   │              │                 │                    │──INSERT Log────▶│
   │              │                 │        ╔═══════════╝                 │
   │              │                 │        ║  (repeat per chunk)         │
   │              │                 │        ╚════════════════════════════ │
   │              │                 │                    │──UPDATE Job────▶│
   │              │                 │                    │──INSERT Log────▶│
   │              │                 │                    │                 │
   │─GET /jobs/id▶│                 │                    │                 │
   │              │──GetStatus()───▶│                    │                 │
   │              │                 │──SELECT+Include────────────────────▶│
   │◀─200 { full  │◀─JobStatusResp──│                    │                 │
   │   status }   │                 │                    │                 │
```

---

## Crash Recovery

If the application restarts mid-processing, the worker automatically re-queues any jobs with status `Queued`, `Reading`, or `Processing` on startup. Chunks already marked `Completed` are skipped, so no duplicate inserts occur and processing resumes from the last incomplete chunk.

---

## Extending the System

| Need | How |
|---|---|
| Different CSV schema | Update `TradeCsvRow.cs` and `MapToRecord()` in the worker |
| Change dedup key fields | Update `ComputeRowHash()` in the worker |
| Larger files | Increase `Processing:ChunkSize` or switch to streaming insert |
| Multiple parallel workers | Add more `AddHostedService<CsvProcessingWorker>()` calls |
| Cross-process queue | Replace `ProcessingChannel` with RabbitMQ / Azure Service Bus |
| File storage in cloud | Implement `IFileStorageService` using Azure Blob / S3 |
