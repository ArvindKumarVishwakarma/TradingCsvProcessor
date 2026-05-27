using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Persistence;

namespace TradingCsvProcessor.Infrastructure.Repositories;

public sealed class JobStageLogRepository : IJobStageLogRepository
{
    private readonly AppDbContext _db;

    public JobStageLogRepository(AppDbContext db) => _db = db;

    public void Add(JobStageLog log) => _db.JobStageLogs.Add(log);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
