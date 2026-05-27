namespace TradingCsvProcessor.Application.Interfaces;

public interface IJobQueue
{
    ValueTask EnqueueAsync(Guid jobId, CancellationToken ct = default);
}
