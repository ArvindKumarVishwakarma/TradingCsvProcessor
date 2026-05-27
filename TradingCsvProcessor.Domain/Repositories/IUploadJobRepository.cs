using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Enums;

namespace TradingCsvProcessor.Domain.Repositories;

public interface IUploadJobRepository
{
    Task<UploadJob?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<UploadJob?> GetByIdWithChunksAsync(Guid id, CancellationToken ct = default);
    Task<UploadJob?> GetByIdWithChunksAndLogsAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<UploadJob>> GetAllWithChunksAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetInterruptedJobIdsAsync(CancellationToken ct = default);
    Task<int> CountChunksByStatusAsync(Guid jobId, ChunkStatus status, CancellationToken ct = default);
    void Add(UploadJob job);
}
