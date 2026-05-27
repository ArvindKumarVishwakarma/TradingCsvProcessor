using TradingCsvProcessor.Domain.Entities;

namespace TradingCsvProcessor.Domain.Repositories;

public interface IUploadJobChunkRepository
{
    Task<UploadJobChunk?> GetByJobAndChunkNumberAsync(Guid jobId, int chunkNumber, CancellationToken ct = default);
    void Add(UploadJobChunk chunk);
}
