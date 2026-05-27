using TradingCsvProcessor.Domain.Entities;

namespace TradingCsvProcessor.Domain.Repositories;

public interface IJobStageLogRepository
{
    void Add(JobStageLog log);
    Task SaveChangesAsync(CancellationToken ct = default);
}
