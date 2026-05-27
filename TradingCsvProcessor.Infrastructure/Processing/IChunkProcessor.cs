namespace TradingCsvProcessor.Infrastructure.Processing;

internal readonly record struct ChunkResult(int Processed, int Skipped, int Failed);

internal interface IChunkProcessor
{
    Task<ChunkResult> ProcessAsync(Guid jobId, ChunkPayload payload, CancellationToken ct);
}
