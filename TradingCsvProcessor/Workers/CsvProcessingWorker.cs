using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using TradingCsvProcessor.Data;
using TradingCsvProcessor.Infrastructure;
using TradingCsvProcessor.Models.Csv;
using TradingCsvProcessor.Models.Domain;

namespace TradingCsvProcessor.Workers;

// Passed from the sequential CSV reader to each parallel chunk worker
internal readonly record struct ChunkPayload(int ChunkNumber, List<TradeCsvRow> Rows, int StartRow);

public sealed class CsvProcessingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessingChannel _channel;
    private readonly JobCancellationRegistry _cancellationRegistry;
    private readonly ILogger<CsvProcessingWorker> _logger;
    private readonly int _chunkSize;
    private readonly int _degreeOfParallelism;

    public CsvProcessingWorker(
        IServiceScopeFactory scopeFactory,
        ProcessingChannel channel,
        JobCancellationRegistry cancellationRegistry,
        IConfiguration config,
        ILogger<CsvProcessingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _channel = channel;
        _cancellationRegistry = cancellationRegistry;
        _logger = logger;
        _chunkSize = config.GetValue("Processing:ChunkSize", 5000);
        _degreeOfParallelism = config.GetValue("Processing:DegreeOfParallelism", 4);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "CsvProcessingWorker started — chunkSize: {ChunkSize}, DOP: {DOP}",
            _chunkSize, _degreeOfParallelism);

        await RequeueInterruptedJobsAsync(stoppingToken);

        await foreach (var jobId in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            try { await ProcessJobAsync(jobId, stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { _logger.LogError(ex, "Unhandled error on job {JobId}", jobId); }
        }
    }

    // ── Job orchestration ─────────────────────────────────────────────────────

    private async Task ProcessJobAsync(Guid jobId, CancellationToken stoppingToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var job = await db.UploadJobs
            .Include(j => j.Chunks)
            .FirstOrDefaultAsync(j => j.Id == jobId, stoppingToken);

        if (job is null) { _logger.LogWarning("Job {JobId} not found.", jobId); return; }

        // Guard: cancellation may have been requested while the job was still queued
        if (job.IsCancellationRequested || job.Status == JobStatus.Cancelled)
        {
            job.Status = JobStatus.Cancelled;
            job.CancelledAt ??= DateTime.UtcNow;
            await PersistJobStageAsync(db, job, ProcessingStage.JobCancelled,
                "Job was cancelled before the worker picked it up", stoppingToken);
            return;
        }

        // Register a per-job CTS so the cancel API can signal this specific job.
        // Link it with stoppingToken so app shutdown also propagates.
        var jobToken = _cancellationRegistry.Register(jobId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobToken);
        var ct = linked.Token;

        try
        {
            job.Status = JobStatus.Reading;
            job.StartedAt ??= DateTime.UtcNow;
            await PersistJobStageAsync(db, job, ProcessingStage.FileReading, "Counting rows (O(1) memory pass)", stoppingToken);

            job.TotalRows = await CountRowsAsync(job.StoredFilePath, ct);

            var completedChunkNums = job.Chunks
                .Where(c => c.Status == ChunkStatus.Completed)
                .Select(c => c.ChunkNumber)
                .ToHashSet();

            await PersistJobStageAsync(db, job, ProcessingStage.ChunksCreated,
                $"Rows: {job.TotalRows} | Skipping {completedChunkNums.Count} completed chunks | DOP: {_degreeOfParallelism}",
                stoppingToken);

            job.Status = JobStatus.Processing;

            int processedRows = 0, skippedRows = 0, failedRows = 0;

            await Parallel.ForEachAsync(
                StreamChunksAsync(job.StoredFilePath, _chunkSize, ct),
                new ParallelOptions { MaxDegreeOfParallelism = _degreeOfParallelism, CancellationToken = ct },
                async (payload, innerCt) =>
                {
                    if (completedChunkNums.Contains(payload.ChunkNumber))
                    {
                        _logger.LogDebug("Skipping completed chunk {N}", payload.ChunkNumber);
                        return;
                    }

                    using var chunkScope = _scopeFactory.CreateScope();
                    var chunkDb = chunkScope.ServiceProvider.GetRequiredService<AppDbContext>();

                    var chunk = await GetOrCreateChunkAsync(chunkDb, job.Id, payload, innerCt);

                    var (processed, skipped, failed) =
                        await ProcessChunkCoreAsync(chunkDb, job.Id, chunk, payload.Rows, innerCt);

                    Interlocked.Add(ref processedRows, processed);
                    Interlocked.Add(ref skippedRows, skipped);
                    Interlocked.Add(ref failedRows, failed);
                });

            job.ProcessedRows = processedRows;
            job.SkippedRows = skippedRows;
            job.FailedRows = failedRows;
            job.CompletedAt = DateTime.UtcNow;

            var failedChunks = await db.UploadJobChunks
                .CountAsync(c => c.JobId == job.Id && c.Status == ChunkStatus.Failed, stoppingToken);
            var completedChunks = await db.UploadJobChunks
                .CountAsync(c => c.JobId == job.Id && c.Status == ChunkStatus.Completed, stoppingToken);

            job.Status = failedChunks > 0 && completedChunks > 0 ? JobStatus.PartiallyCompleted
                       : failedChunks > 0 ? JobStatus.Failed
                       : JobStatus.Completed;

            var finalStage = job.Status == JobStatus.Failed ? ProcessingStage.JobFailed : ProcessingStage.JobCompleted;
            await PersistJobStageAsync(db, job, finalStage,
                $"Done — inserted: {processedRows}, skipped (dup): {skippedRows}, failed: {failedRows}", stoppingToken);
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            // ── USER-REQUESTED CANCELLATION ──────────────────────────────────
            // jobToken fired (cancel API called) but app is not shutting down.
            // Mark Cancelled and write the audit log using stoppingToken (ct is already cancelled).
            _logger.LogInformation("Job {JobId} was cancelled by user request", jobId);
            job.Status = JobStatus.Cancelled;
            job.CancelledAt = DateTime.UtcNow;
            job.ProcessedRows += 0;  // keep whatever was accumulated via Interlocked before cancellation
            await PersistJobStageAsync(db, job, ProcessingStage.JobCancelled,
                "Job cancelled by user request", stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // ── APP SHUTDOWN ─────────────────────────────────────────────────
            // stoppingToken fired. Do NOT mark the job as Cancelled — it will be
            // re-queued on the next startup via RequeueInterruptedJobsAsync.
            _logger.LogWarning("Job {JobId} interrupted by app shutdown — will resume on restart", jobId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error on job {JobId}", jobId);
            job.Status = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            await PersistJobStageAsync(db, job, ProcessingStage.JobFailed, $"Fatal: {ex.Message}", stoppingToken);
        }
        finally
        {
            // Always release the per-job CTS — regardless of how processing ended
            _cancellationRegistry.Unregister(jobId);
        }
    }

    // ── Chunk processing ──────────────────────────────────────────────────────

    private static async Task<UploadJobChunk> GetOrCreateChunkAsync(
        AppDbContext chunkDb, Guid jobId, ChunkPayload payload, CancellationToken ct)
    {
        var chunk = await chunkDb.UploadJobChunks
            .FirstOrDefaultAsync(c => c.JobId == jobId && c.ChunkNumber == payload.ChunkNumber, ct);

        if (chunk is not null) return chunk;

        chunk = new UploadJobChunk
        {
            JobId = jobId,
            ChunkNumber = payload.ChunkNumber,
            StartRow = payload.StartRow,
            EndRow = payload.StartRow + payload.Rows.Count - 1,
            TotalRows = payload.Rows.Count
        };
        chunkDb.UploadJobChunks.Add(chunk);
        await chunkDb.SaveChangesAsync(ct);
        return chunk;
    }

    // Returns (processed, skipped, failed) — no shared mutable state.
    // Uses its own AppDbContext; never reads or writes the UploadJob entity.
    private async Task<(int processed, int skipped, int failed)> ProcessChunkCoreAsync(
        AppDbContext chunkDb, Guid jobId, UploadJobChunk chunk,
        List<TradeCsvRow> rows, CancellationToken ct)
    {
        chunk.Status = ChunkStatus.Processing;
        chunk.StartedAt ??= DateTime.UtcNow;
        chunk.RetryCount++;

        await PersistChunkLogAsync(chunkDb, jobId, chunk.Id, ProcessingStage.ChunkProcessing,
            $"Chunk {chunk.ChunkNumber}: rows {chunk.StartRow}–{chunk.EndRow}", ct);

        try
        {
            var rowHashes = rows.Select(r => (Row: r, Hash: ComputeRowHash(r))).ToList();

            // Single batch query per chunk for dedup
            var hashes = rowHashes.Select(x => x.Hash).ToList();
            var existingHashes = (await chunkDb.TradeRecords
                .Where(t => hashes.Contains(t.RecordHash))
                .Select(t => t.RecordHash)
                .ToListAsync(ct))
                .ToHashSet(StringComparer.Ordinal);

            // Check cancellation flag AFTER the dedup query — gives a natural checkpoint
            // between chunks without needing to poll on every row.
            ct.ThrowIfCancellationRequested();

            var toInsert = new List<TradeRecord>(rows.Count);
            int skipped = 0, failed = 0;

            foreach (var (row, hash) in rowHashes)
            {
                if (existingHashes.Contains(hash)) { skipped++; continue; }

                try { toInsert.Add(MapToRecord(row, hash, jobId)); }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning("Row mapping failed TradeId={TradeId}: {Error}", row.TradeId, ex.Message);
                }
            }

            if (toInsert.Count > 0)
            {
                await PersistChunkLogAsync(chunkDb, jobId, chunk.Id, ProcessingStage.ChunkBulkInserting,
                    $"Chunk {chunk.ChunkNumber}: bulk inserting {toInsert.Count} records", ct);

                try
                {
                    await chunkDb.BulkInsertAsync(toInsert,
                        new BulkConfig { PreserveInsertOrder = false },
                        cancellationToken: ct);
                }
                catch (Exception ex) when (IsUniqueConstraintViolation(ex))
                {
                    // Rare parallel race: two chunks checked dedup before either inserted.
                    // Re-query and retry with only the truly new records.
                    _logger.LogWarning("Unique constraint race on chunk {N} — re-filtering and retrying", chunk.ChunkNumber);

                    var stillExisting = (await chunkDb.TradeRecords
                        .Where(t => hashes.Contains(t.RecordHash))
                        .Select(t => t.RecordHash)
                        .ToListAsync(ct))
                        .ToHashSet(StringComparer.Ordinal);

                    var retry = toInsert.Where(r => !stillExisting.Contains(r.RecordHash)).ToList();
                    skipped += toInsert.Count - retry.Count;

                    if (retry.Count > 0)
                        await chunkDb.BulkInsertAsync(retry,
                            new BulkConfig { PreserveInsertOrder = false },
                            cancellationToken: ct);

                    toInsert = retry;
                }
            }

            chunk.ProcessedCount = toInsert.Count;
            chunk.SkippedCount = skipped;
            chunk.FailedCount = failed;
            chunk.Status = ChunkStatus.Completed;
            chunk.CompletedAt = DateTime.UtcNow;

            await PersistChunkLogAsync(chunkDb, jobId, chunk.Id, ProcessingStage.ChunkCompleted,
                $"Chunk {chunk.ChunkNumber} done — inserted: {toInsert.Count}, skipped: {skipped}, failed: {failed}", ct);

            return (toInsert.Count, skipped, failed);
        }
        catch (OperationCanceledException)
        {
            // ct was cancelled (user cancel or app shutdown).
            // Save chunk state with CancellationToken.None — ct is already cancelled so
            // SaveChangesAsync(ct) would throw, leaving the chunk stuck in 'Processing' forever.
            chunk.Status = ChunkStatus.Failed;
            chunk.ErrorMessage = "Cancelled";
            chunk.FailedCount = rows.Count - chunk.ProcessedCount - chunk.SkippedCount;

            await PersistChunkLogAsync(chunkDb, jobId, chunk.Id, ProcessingStage.ChunkFailed,
                $"Chunk {chunk.ChunkNumber} cancelled mid-processing", CancellationToken.None);

            throw;  // propagate to Parallel.ForEachAsync → propagates to ProcessJobAsync catch
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chunk {ChunkId} failed", chunk.Id);
            chunk.Status = ChunkStatus.Failed;
            chunk.ErrorMessage = ex.Message;
            chunk.FailedCount = rows.Count - chunk.ProcessedCount - chunk.SkippedCount;

            await PersistChunkLogAsync(chunkDb, jobId, chunk.Id, ProcessingStage.ChunkFailed,
                $"Chunk {chunk.ChunkNumber} failed: {ex.Message}", ct);

            return (chunk.ProcessedCount, chunk.SkippedCount, chunk.FailedCount);
        }
    }

    // ── Streaming ─────────────────────────────────────────────────────────────

    // Reads the CSV file exactly once, sequentially.
    // Yields one fixed-size ChunkPayload at a time — Parallel.ForEachAsync dispatches each
    // payload to a worker as soon as a slot is free, keeping at most DOP chunks in memory.
    private static async IAsyncEnumerable<ChunkPayload> StreamChunksAsync(
        string filePath, int chunkSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null,
            TrimOptions = TrimOptions.Trim
        };

        using var reader = new StreamReader(
            filePath, System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 65_536);
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync();
        csv.ReadHeader();

        var buffer = new List<TradeCsvRow>(chunkSize);
        int absoluteRow = 0;
        int chunkStartRow = 1;
        int chunkNumber = 0;

        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            buffer.Add(csv.GetRecord<TradeCsvRow>()!);
            absoluteRow++;

            if (buffer.Count == chunkSize)
            {
                chunkNumber++;
                yield return new ChunkPayload(chunkNumber, buffer, chunkStartRow);
                // New list — lets the yielded buffer become GC-eligible once the consumer is done
                buffer = new List<TradeCsvRow>(chunkSize);
                chunkStartRow = absoluteRow + 1;
            }
        }

        if (buffer.Count > 0)
        {
            chunkNumber++;
            yield return new ChunkPayload(chunkNumber, buffer, chunkStartRow);
        }
    }

    // O(1) memory row counter — reads raw lines, no CSV parsing
    private static async Task<int> CountRowsAsync(string filePath, CancellationToken ct)
    {
        using var reader = new StreamReader(
            filePath, System.Text.Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 65_536);
        await reader.ReadLineAsync(ct);  // skip header
        int count = 0;
        while (await reader.ReadLineAsync(ct) is not null)
            count++;
        return count;
    }

    // ── Stage logging ─────────────────────────────────────────────────────────

    // MAIN THREAD ONLY — mutates job.CurrentStage on the tracked UploadJob entity.
    // Calling this from a parallel task would cause a data race on the job object.
    private static async Task PersistJobStageAsync(
        AppDbContext db, UploadJob job, ProcessingStage stage, string message, CancellationToken ct)
    {
        job.CurrentStage = stage;
        db.JobStageLogs.Add(new JobStageLog { JobId = job.Id, Stage = stage, Message = message });
        await db.SaveChangesAsync(ct);
    }

    // PARALLEL CHUNK TASKS — uses the task's own isolated AppDbContext.
    // Does NOT touch the UploadJob entity — only writes a log row by job ID (FK only).
    private static async Task PersistChunkLogAsync(
        AppDbContext chunkDb, Guid jobId, Guid chunkId,
        ProcessingStage stage, string message, CancellationToken ct)
    {
        chunkDb.JobStageLogs.Add(new JobStageLog { JobId = jobId, ChunkId = chunkId, Stage = stage, Message = message });

        // Flush chunk entity changes (status, counts) along with the log entry
        await chunkDb.SaveChangesAsync(ct);
    }

    // ── Pure helpers ──────────────────────────────────────────────────────────

    private static TradeRecord MapToRecord(TradeCsvRow row, string hash, Guid jobId)
    {
        if (!DateTime.TryParse(row.TradeDate, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tradeDate))
        {
            throw new FormatException($"Cannot parse TradeDate '{row.TradeDate}' for TradeId '{row.TradeId}'");
        }

        return new TradeRecord
        {
            JobId = jobId,
            RecordHash = hash,
            TradeId = row.TradeId,
            Symbol = row.Symbol,
            TradeDate = tradeDate,
            Quantity = row.Quantity,
            Price = row.Price,
            Side = row.Side,
            TotalValue = row.Quantity * row.Price,
            Exchange = row.Exchange,
            Currency = row.Currency
        };
    }

    private static string ComputeRowHash(TradeCsvRow row)
    {
        var key = $"{row.TradeId}|{row.Symbol}|{row.TradeDate}|{row.Quantity}|{row.Price}|{row.Side}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        // SQL Server error 2601 = Cannot insert duplicate key row
        // SQL Server error 2627 = Violation of UNIQUE KEY constraint
        var msg = ex.ToString();
        return msg.Contains("2601") || msg.Contains("2627") || msg.Contains("UNIQUE");
    }

    private async Task RequeueInterruptedJobsAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var interrupted = await db.UploadJobs
            .Where(j => j.Status == JobStatus.Queued
                     || j.Status == JobStatus.Reading
                     || j.Status == JobStatus.Processing)
            .Select(j => j.Id)
            .ToListAsync(ct);

        foreach (var id in interrupted)
        {
            await _channel.EnqueueAsync(id, ct);
            _logger.LogInformation("Re-queued interrupted job {JobId}", id);
        }
    }
}
