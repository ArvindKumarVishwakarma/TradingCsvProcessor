namespace TradingCsvProcessor.Application.Interfaces;

public interface IJobCancellationRegistry
{
    bool TryCancel(Guid jobId);
}
