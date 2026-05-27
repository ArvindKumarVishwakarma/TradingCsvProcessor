using TradingCsvProcessor.Infrastructure.Models;

namespace TradingCsvProcessor.Infrastructure.Processing;

internal readonly record struct ChunkPayload(int ChunkNumber, IReadOnlyList<TradeCsvRow> Rows, int StartRow);

internal interface ICsvStreamReader
{
    IAsyncEnumerable<ChunkPayload> StreamChunksAsync(string filePath, int chunkSize, CancellationToken ct);
    Task<int> CountRowsAsync(string filePath, CancellationToken ct);
}
