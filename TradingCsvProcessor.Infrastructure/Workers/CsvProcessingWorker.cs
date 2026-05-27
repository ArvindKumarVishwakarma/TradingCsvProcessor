using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using EFCore.BulkExtensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingCsvProcessor.Application.Options;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Enums;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Messaging;
using TradingCsvProcessor.Infrastructure.Models;

namespace TradingCsvProcessor.Infrastructure.Workers;

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
        IOptions<ProcessingOptions> options,
        ILogger<CsvProcessingWorker> logger)
    {
        _scopeFactory          = scopeFactory;
        _channel               = channel;
        _cancellationRegistry  = cancellationRegistry;
        _logger                = logger;
        _chunkSize             = options.Value.ChunkSize;
        _degreeOfParallelism   = options.Value.DegreeOfParallelism;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "CsvProcessingWorker started — ChunkSize: {ChunkSize}, DOP: {DOP}",
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
        using var scope    = _scopeFactory.CreateScope();
        var jobRepo        = scope.ServiceProvider.GetRequiredService<IUploadJobRepository>();
        var stageLogRepo   = scope.ServiceProvider.GetRequiredService<IJobStageLogRepository>();

        var job = await jobRepo.GetByIdWithChunksAsync(jobId, stoppingToken);
        if (job is null) { _logger.LogWarning("Job {JobId} not found.", jobId); return; }

        if (job.IsCancellationRequested || job.Status == JobStatus.Cancelled)
        {
            job.Status      = JobStatus.Cancelled;
            job.CancelledAt ??= DateTime.UtcNow;
            await PersistJobStageAsync(jobRepo, stageLogRepo, job, ProcessingStage.JobCancelled,
                "Job was cancelled before the worker picked it up.", stoppingToken);
            return;
        }

        var jobToken = _cancellationRegistry.Register(jobId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobToken);
        var ct = linked.Token;

        try
        {
            job.Status    = JobStatus.Reading;
            job.StartedAt ??= DateTime.UtcNow;
            await PersistJobStageAsync(jobRepo, stageLogRepo, job, ProcessingStage.FileReading,
                "Counting rows (O(1) memory pass).", stoppingToken);

            job.TotalRows = await CountRowsAsync(job.StoredFilePath, ct);

            var completedChunkNums = job.Chunks
                .Where(c => c.Status == ChunkStatus.Completed)
                .Select(c => c.ChunkNumber)
                .ToHashSet();

            await PersistJobStageAsync(jobRepo, stageLogRepo, job, ProcessingStage.ChunksCreated,
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

                    using var chunkScope  = _scopeFactory.CreateScope();
                    var chunkRepo         = chunkScope.ServiceProvider.GetRequiredService<IUploadJobChunkRepository>();
                    var tradeRepo         = chunkScope.ServiceProvider.GetRequiredService<ITradeRecordRepository>();
                    var chunkLogRepo      = chunkScope.ServiceProvider.GetRequiredService<IJobStageLogRepository>();

                    var chunk = await GetOrCreateChunkAsync(chunkRepo, job.Id, payload, innerCt);
                    var (processed, skipped, failed) =
                        await ProcessChunkCoreAsync(chunkRepo, tradeRepo, chunkLogRepo, job.Id, chunk, payload.Rows, innerCt);

                    Interlocked.Add(ref processedRows, processed);
                    Interlocked.Add(ref skippedRows,   skipped);
                    Interlocked.Add(ref failedRows,    failed);
                });

            job.ProcessedRows = processedRows;
            job.SkippedRows   = skippedRows;
            job.FailedRows    = failedRows;
            job.CompletedAt   = DateTime.UtcNow;

            var failedChunks    = await jobRepo.CountChunksByStatusAsync(job.Id, ChunkStatus.Failed,    stoppingToken);
            var completedChunks = await jobRepo.CountChunksByStatusAsync(job.Id, ChunkStatus.Completed, stoppingToken);

            job.Status = failedChunks > 0 && completedChunks > 0 ? JobStatus.PartiallyCompleted
                       : failedChunks > 0                         ? JobStatus.Failed
                       :                                            JobStatus.Completed;

            var finalStage = job.Status == JobStatus.Failed ? ProcessingStage.JobFailed : ProcessingStage.JobCompleted;
            await PersistJobStageAsync(jobRepo, stageLogRepo, job, finalStage,
                $"Done — inserted: {processedRows}, skipped (dup): {skippedRows}, failed: {failedRows}.",
                stoppingToken);
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Job {JobId} cancelled by user request.", jobId);
            job.Status      = JobStatus.Cancelled;
            job.CancelledAt = DateTime.UtcNow;
            await PersistJobStageAsync(jobRepo, stageLogRepo, job, ProcessingStage.JobCancelled,
                "Job cancelled by user request.", stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // App shutdown — do NOT mark Cancelled; it will be re-queued on next startup.
            _logger.LogWarning("Job {JobId} interrupted by app shutdown — will resume on restart.", jobId);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error on job {JobId}", jobId);
            job.Status       = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            await PersistJobStageAsync(jobRepo, stageLogRepo, job, ProcessingStage.JobFailed,
                $"Fatal: {ex.Message}", stoppingToken);
        }
        finally
        {
            _cancellationRegistry.Unregister(jobId);
        }
    }

    // ── Chunk processing ──────────────────────────────────────────────────────

    private static async Task<UploadJobChunk> GetOrCreateChunkAsync(
        IUploadJobChunkRepository chunkRepo, Guid jobId, ChunkPayload payload, CancellationToken ct)
    {
        var chunk = await chunkRepo.GetByJobAndChunkNumberAsync(jobId, payload.ChunkNumber, ct);
        if (chunk is not null) return chunk;

        chunk = new UploadJobChunk
        {
            JobId       = jobId,
            ChunkNumber = payload.ChunkNumber,
            StartRow    = payload.StartRow,
            EndRow      = payload.StartRow + payload.Rows.Count - 1,
            TotalRows   = payload.Rows.Count
        };
        chunkRepo.Add(chunk);
        await chunkRepo.SaveChangesAsync(ct);
        return chunk;
    }

    private async Task<(int processed, int skipped, int failed)> ProcessChunkCoreAsync(
        IUploadJobChunkRepository chunkRepo,
        ITradeRecordRepository tradeRepo,
        IJobStageLogRepository logRepo,
        Guid jobId, UploadJobChunk chunk,
        List<TradeCsvRow> rows, CancellationToken ct)
    {
        chunk.Status     = ChunkStatus.Processing;
        chunk.StartedAt ??= DateTime.UtcNow;
        chunk.RetryCount++;

        await PersistChunkLogAsync(chunkRepo, logRepo, jobId, chunk.Id, ProcessingStage.ChunkProcessing,
            $"Chunk {chunk.ChunkNumber}: rows {chunk.StartRow}–{chunk.EndRow}.", ct);

        try
        {
            var rowHashes      = rows.Select(r => (Row: r, Hash: ComputeRowHash(r))).ToList();
            var hashes         = rowHashes.Select(x => x.Hash).ToList();
            var existingHashes = await tradeRepo.GetExistingHashesAsync(hashes, ct);

            ct.ThrowIfCancellationRequested();

            var toInsert = new List<TradeRecord>(rows.Count);
            int skipped = 0, failed = 0;

            foreach (var (row, hash) in rowHashes)
            {
                if (existingHashes.Contains(hash)) { skipped++; continue; }
                try   { toInsert.Add(MapToRecord(row, hash, jobId)); }
                catch (Exception ex)
                {
                    failed++;
                    _logger.LogWarning("Row mapping failed TradeId={TradeId}: {Error}", row.TradeId, ex.Message);
                }
            }

            if (toInsert.Count > 0)
            {
                await PersistChunkLogAsync(chunkRepo, logRepo, jobId, chunk.Id, ProcessingStage.ChunkBulkInserting,
                    $"Chunk {chunk.ChunkNumber}: bulk inserting {toInsert.Count} records.", ct);

                try
                {
                    await tradeRepo.BulkInsertAsync(toInsert, ct);
                }
                catch (Exception ex) when (IsUniqueConstraintViolation(ex))
                {
                    // Parallel race: two chunks checked dedup before either inserted.
                    _logger.LogWarning("Unique constraint race on chunk {N} — re-filtering and retrying.", chunk.ChunkNumber);
                    var stillExisting = await tradeRepo.GetExistingHashesAsync(hashes, ct);
                    var retry         = toInsert.Where(r => !stillExisting.Contains(r.RecordHash)).ToList();
                    skipped += toInsert.Count - retry.Count;
                    if (retry.Count > 0)
                        await tradeRepo.BulkInsertAsync(retry, ct);
                    toInsert = retry;
                }
            }

            chunk.ProcessedCount = toInsert.Count;
            chunk.SkippedCount   = skipped;
            chunk.FailedCount    = failed;
            chunk.Status         = ChunkStatus.Completed;
            chunk.CompletedAt    = DateTime.UtcNow;

            await PersistChunkLogAsync(chunkRepo, logRepo, jobId, chunk.Id, ProcessingStage.ChunkCompleted,
                $"Chunk {chunk.ChunkNumber} done — inserted: {toInsert.Count}, skipped: {skipped}, failed: {failed}.", ct);

            return (toInsert.Count, skipped, failed);
        }
        catch (OperationCanceledException)
        {
            chunk.Status       = ChunkStatus.Failed;
            chunk.ErrorMessage = "Cancelled";
            chunk.FailedCount  = rows.Count - chunk.ProcessedCount - chunk.SkippedCount;

            await PersistChunkLogAsync(chunkRepo, logRepo, jobId, chunk.Id, ProcessingStage.ChunkFailed,
                $"Chunk {chunk.ChunkNumber} cancelled mid-processing.", CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Chunk {ChunkId} failed.", chunk.Id);
            chunk.Status       = ChunkStatus.Failed;
            chunk.ErrorMessage = ex.Message;
            chunk.FailedCount  = rows.Count - chunk.ProcessedCount - chunk.SkippedCount;

            await PersistChunkLogAsync(chunkRepo, logRepo, jobId, chunk.Id, ProcessingStage.ChunkFailed,
                $"Chunk {chunk.ChunkNumber} failed: {ex.Message}", ct);

            return (chunk.ProcessedCount, chunk.SkippedCount, chunk.FailedCount);
        }
    }

    // ── CSV Streaming ─────────────────────────────────────────────────────────

    private static async IAsyncEnumerable<ChunkPayload> StreamChunksAsync(
        string filePath, int chunkSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord  = true,
            MissingFieldFound = null,
            BadDataFound     = null,
            TrimOptions      = TrimOptions.Trim
        };

        using var reader = new StreamReader(filePath, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 65_536);
        using var csv = new CsvReader(reader, config);

        await csv.ReadAsync();
        csv.ReadHeader();

        var buffer       = new List<TradeCsvRow>(chunkSize);
        int absoluteRow  = 0;
        int chunkStartRow = 1;
        int chunkNumber  = 0;

        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            buffer.Add(csv.GetRecord<TradeCsvRow>()!);
            absoluteRow++;

            if (buffer.Count == chunkSize)
            {
                chunkNumber++;
                yield return new ChunkPayload(chunkNumber, buffer, chunkStartRow);
                buffer        = new List<TradeCsvRow>(chunkSize);
                chunkStartRow = absoluteRow + 1;
            }
        }

        if (buffer.Count > 0)
        {
            chunkNumber++;
            yield return new ChunkPayload(chunkNumber, buffer, chunkStartRow);
        }
    }

    private static async Task<int> CountRowsAsync(string filePath, CancellationToken ct)
    {
        using var reader = new StreamReader(filePath, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 65_536);
        await reader.ReadLineAsync(ct); // skip header
        int count = 0;
        while (await reader.ReadLineAsync(ct) is not null)
            count++;
        return count;
    }

    // ── Stage logging ─────────────────────────────────────────────────────────

    private static async Task PersistJobStageAsync(
        IUploadJobRepository jobRepo, IJobStageLogRepository logRepo,
        UploadJob job, ProcessingStage stage, string message, CancellationToken ct)
    {
        job.CurrentStage = stage;
        logRepo.Add(new JobStageLog { JobId = job.Id, Stage = stage, Message = message });
        await jobRepo.SaveChangesAsync(ct);
    }

    private static async Task PersistChunkLogAsync(
        IUploadJobChunkRepository chunkRepo, IJobStageLogRepository logRepo,
        Guid jobId, Guid chunkId, ProcessingStage stage, string message, CancellationToken ct)
    {
        logRepo.Add(new JobStageLog { JobId = jobId, ChunkId = chunkId, Stage = stage, Message = message });
        await chunkRepo.SaveChangesAsync(ct);
    }

    // ── Pure helpers ──────────────────────────────────────────────────────────

    private static TradeRecord MapToRecord(TradeCsvRow row, string hash, Guid jobId)
    {
        if (!DateTime.TryParse(row.TradeDate, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var tradeDate))
        {
            throw new FormatException(
                $"Cannot parse TradeDate '{row.TradeDate}' for TradeId '{row.TradeId}'.");
        }

        return new TradeRecord
        {
            JobId       = jobId,
            RecordHash  = hash,
            TradeId     = row.TradeId,
            Symbol      = row.Symbol,
            TradeDate   = tradeDate,
            Quantity    = row.Quantity,
            Price       = row.Price,
            Side        = row.Side,
            TotalValue  = row.Quantity * row.Price,
            Exchange    = row.Exchange,
            Currency    = row.Currency
        };
    }

    private static string ComputeRowHash(TradeCsvRow row)
    {
        var key = $"{row.TradeId}|{row.Symbol}|{row.TradeDate}|{row.Quantity}|{row.Price}|{row.Side}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }

    private static bool IsUniqueConstraintViolation(Exception ex)
    {
        var msg = ex.ToString();
        return msg.Contains("2601") || msg.Contains("2627") || msg.Contains("UNIQUE");
    }

    private async Task RequeueInterruptedJobsAsync(CancellationToken ct)
    {
        using var scope   = _scopeFactory.CreateScope();
        var jobRepo       = scope.ServiceProvider.GetRequiredService<IUploadJobRepository>();
        var interrupted   = await jobRepo.GetInterruptedJobIdsAsync(ct);

        foreach (var id in interrupted)
        {
            await _channel.EnqueueAsync(id, ct);
            _logger.LogInformation("Re-queued interrupted job {JobId}.", id);
        }
    }
}
