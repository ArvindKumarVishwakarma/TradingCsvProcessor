using Microsoft.EntityFrameworkCore;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Persistence;

namespace TradingCsvProcessor.Infrastructure.Repositories;

public sealed class UploadJobChunkRepository(AppDbContext db) : IUploadJobChunkRepository
{
    public Task<UploadJobChunk?> GetByJobAndChunkNumberAsync(Guid jobId, int chunkNumber, CancellationToken ct = default)
        => db.UploadJobChunks.FirstOrDefaultAsync(c => c.JobId == jobId && c.ChunkNumber == chunkNumber, ct);

    public void Add(UploadJobChunk chunk) => db.UploadJobChunks.Add(chunk);
}
