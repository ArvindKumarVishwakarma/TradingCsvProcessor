namespace TradingCsvProcessor.Infrastructure.Processing;

internal interface IJobOrchestrator
{
    Task RequeueInterruptedJobsAsync(CancellationToken ct);
    Task OrchestrateAsync(Guid jobId, CancellationToken stoppingToken);
}
