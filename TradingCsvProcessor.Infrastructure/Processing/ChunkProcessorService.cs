using System.Globalization;
using Microsoft.Extensions.Logging;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Enums;
using TradingCsvProcessor.Domain.Interfaces;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Models;

namespace TradingCsvProcessor.Infrastructure.Processing;

internal sealed class ChunkProcessorService(
    IUploadJobChunkRepository chunkRepo,
    ITradeRecordRepository tradeRepo,
    IJobStageLogRepository logRepo,
    IUnitOfWork unitOfWork,
    ILogger<ChunkProcessorService> logger) : IChunkProcessor
{
    public async Task<ChunkResult> ProcessAsync(Guid jobId, ChunkPayload payload, CancellationToken ct)
    {
        var chunk = await chunkRepo.GetByJobAndChunkNumberAsync(jobId, payload.ChunkNumber, ct);

        if (chunk is null)
        {
            chunk = UploadJobChunk.Create(
                jobId, payload.ChunkNumber,
                payload.StartRow, payload.StartRow + payload.Rows.Count - 1,
                payload.Rows.Count);
            chunkRepo.Add(chunk);
            await unitOfWork.SaveChangesAsync(ct);
        }

        chunk.BeginProcessing();
        Log(jobId, chunk.Id, ProcessingStage.ChunkProcessing,
            $"Chunk {chunk.ChunkNumber}: rows {chunk.StartRow}–{chunk.EndRow}.");
        await unitOfWork.SaveChangesAsync(ct);

        try
        {
            return await InsertChunkAsync(jobId, chunk, payload.Rows, ct);
        }
        catch (OperationCanceledException)
        {
            chunk.Fail("Cancelled");
            Log(jobId, chunk.Id, ProcessingStage.ChunkFailed,
                $"Chunk {chunk.ChunkNumber} cancelled mid-processing.", useNoneToken: true);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Chunk {ChunkId} failed.", chunk.Id);
            chunk.Fail(ex.Message);
            Log(jobId, chunk.Id, ProcessingStage.ChunkFailed,
                $"Chunk {chunk.ChunkNumber} failed: {ex.Message}");
            await unitOfWork.SaveChangesAsync(ct);
            return new ChunkResult(chunk.ProcessedCount, chunk.SkippedCount, chunk.FailedCount);
        }
    }

    private async Task<ChunkResult> InsertChunkAsync(
        Guid jobId, UploadJobChunk chunk, IReadOnlyList<TradeCsvRow> rows, CancellationToken ct)
    {
        var rowHashes      = rows.Select(r => (Row: r, Hash: RowHasher.Compute(r))).ToList();
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
                logger.LogWarning("Row mapping failed TradeId={TradeId}: {Error}", row.TradeId, ex.Message);
            }
        }

        if (toInsert.Count > 0)
        {
            Log(jobId, chunk.Id, ProcessingStage.ChunkBulkInserting,
                $"Chunk {chunk.ChunkNumber}: bulk inserting {toInsert.Count} records.");
            await unitOfWork.SaveChangesAsync(ct);

            try
            {
                await tradeRepo.BulkInsertAsync(toInsert, ct);
            }
            catch (Exception ex) when (IsUniqueViolation(ex))
            {
                // Parallel race: two chunks checked dedup before either inserted.
                logger.LogWarning("Unique constraint race on chunk {N} — re-filtering.", chunk.ChunkNumber);
                var still = await tradeRepo.GetExistingHashesAsync(hashes, ct);
                var retry = toInsert.Where(r => !still.Contains(r.RecordHash)).ToList();
                skipped  += toInsert.Count - retry.Count;
                if (retry.Count > 0)
                    await tradeRepo.BulkInsertAsync(retry, ct);
                toInsert = retry;
            }
        }

        chunk.Complete(toInsert.Count, skipped, failed);
        Log(jobId, chunk.Id, ProcessingStage.ChunkCompleted,
            $"Chunk {chunk.ChunkNumber} done — inserted: {toInsert.Count}, skipped: {skipped}, failed: {failed}.");
        await unitOfWork.SaveChangesAsync(ct);

        return new ChunkResult(toInsert.Count, skipped, failed);
    }

    private void Log(Guid jobId, Guid chunkId, ProcessingStage stage, string message,
        bool useNoneToken = false)
        => logRepo.Add(JobStageLog.For(jobId, stage, message, chunkId));

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
            JobId      = jobId,
            RecordHash = hash,
            TradeId    = row.TradeId,
            Symbol     = row.Symbol,
            TradeDate  = tradeDate,
            Quantity   = row.Quantity,
            Price      = row.Price,
            Side       = row.Side,
            TotalValue = row.Quantity * row.Price,
            Exchange   = row.Exchange,
            Currency   = row.Currency
        };
    }

    private static bool IsUniqueViolation(Exception ex)
    {
        var msg = ex.ToString();
        return msg.Contains("2601") || msg.Contains("2627") || msg.Contains("UNIQUE");
    }
}
