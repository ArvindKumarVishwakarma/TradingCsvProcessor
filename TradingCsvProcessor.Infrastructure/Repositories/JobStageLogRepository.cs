using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Persistence;

namespace TradingCsvProcessor.Infrastructure.Repositories;

public sealed class JobStageLogRepository(AppDbContext db) : IJobStageLogRepository
{
    public void Add(JobStageLog log) => db.JobStageLogs.Add(log);
}
