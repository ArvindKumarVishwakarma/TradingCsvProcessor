using Microsoft.EntityFrameworkCore;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Persistence;

namespace TradingCsvProcessor.Infrastructure.Repositories;

public sealed class UploadJobChunkRepository : IUploadJobChunkRepository
{
    private readonly AppDbContext _db;

    public UploadJobChunkRepository(AppDbContext db) => _db = db;

    public Task<UploadJobChunk?> GetByJobAndChunkNumberAsync(Guid jobId, int chunkNumber, CancellationToken ct = default)
        => _db.UploadJobChunks.FirstOrDefaultAsync(c => c.JobId == jobId && c.ChunkNumber == chunkNumber, ct);

    public void Add(UploadJobChunk chunk) => _db.UploadJobChunks.Add(chunk);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
