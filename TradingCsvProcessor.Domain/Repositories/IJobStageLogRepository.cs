using TradingCsvProcessor.Domain.Entities;

namespace TradingCsvProcessor.Domain.Repositories;

public interface IJobStageLogRepository
{
    void Add(JobStageLog log);
}
