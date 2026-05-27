using Microsoft.EntityFrameworkCore;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Enums;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Persistence;

namespace TradingCsvProcessor.Infrastructure.Repositories;

public sealed class UploadJobRepository : IUploadJobRepository
{
    private readonly AppDbContext _db;

    public UploadJobRepository(AppDbContext db) => _db = db;

    public async Task<UploadJob?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => await _db.UploadJobs.FindAsync(new object[] { id }, ct);

    public Task<UploadJob?> GetByIdWithChunksAsync(Guid id, CancellationToken ct = default)
        => _db.UploadJobs.Include(j => j.Chunks).FirstOrDefaultAsync(j => j.Id == id, ct);

    public Task<UploadJob?> GetByIdWithChunksAndLogsAsync(Guid id, CancellationToken ct = default)
        => _db.UploadJobs
            .Include(j => j.Chunks)
            .Include(j => j.StageLogs)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

    public Task<List<UploadJob>> GetAllWithChunksAsync(CancellationToken ct = default)
        => _db.UploadJobs
            .Include(j => j.Chunks)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

    public Task<List<Guid>> GetInterruptedJobIdsAsync(CancellationToken ct = default)
        => _db.UploadJobs
            .Where(j => j.Status == JobStatus.Queued
                     || j.Status == JobStatus.Reading
                     || j.Status == JobStatus.Processing)
            .Select(j => j.Id)
            .ToListAsync(ct);

    public Task<int> CountChunksByStatusAsync(Guid jobId, ChunkStatus status, CancellationToken ct = default)
        => _db.UploadJobChunks.CountAsync(c => c.JobId == jobId && c.Status == status, ct);

    public void Add(UploadJob job) => _db.UploadJobs.Add(job);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
