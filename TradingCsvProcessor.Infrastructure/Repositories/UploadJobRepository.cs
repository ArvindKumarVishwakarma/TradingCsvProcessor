using Microsoft.EntityFrameworkCore;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Enums;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Persistence;

namespace TradingCsvProcessor.Infrastructure.Repositories;

public sealed class UploadJobRepository(AppDbContext db) : IUploadJobRepository
{
    public async Task<UploadJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await db.UploadJobs.FindAsync([id], ct);

    public Task<UploadJob?> GetByIdWithChunksAsync(Guid id, CancellationToken ct = default)
        => db.UploadJobs.Include(j => j.Chunks).FirstOrDefaultAsync(j => j.Id == id, ct);

    public Task<UploadJob?> GetByIdWithChunksAndLogsAsync(Guid id, CancellationToken ct = default)
        => db.UploadJobs
            .Include(j => j.Chunks)
            .Include(j => j.StageLogs)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

    public async Task<IReadOnlyList<UploadJob>> GetAllWithChunksAsync(CancellationToken ct = default)
        => await db.UploadJobs
            .Include(j => j.Chunks)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<Guid>> GetInterruptedJobIdsAsync(CancellationToken ct = default)
        => await db.UploadJobs
            .Where(j => j.Status == JobStatus.Queued
                     || j.Status == JobStatus.Reading
                     || j.Status == JobStatus.Processing)
            .Select(j => j.Id)
            .ToListAsync(ct);

    public Task<int> CountChunksByStatusAsync(Guid jobId, ChunkStatus status, CancellationToken ct = default)
        => db.UploadJobChunks.CountAsync(c => c.JobId == jobId && c.Status == status, ct);

    public void Add(UploadJob job) => db.UploadJobs.Add(job);
}
