using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TradingCsvProcessor.Infrastructure.Messaging;
using TradingCsvProcessor.Infrastructure.Processing;

namespace TradingCsvProcessor.Infrastructure.Workers;

public sealed class CsvProcessingWorker(
    ProcessingChannel channel,
    IServiceScopeFactory scopeFactory,
    ILogger<CsvProcessingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("CsvProcessingWorker started.");

        // Re-queue any jobs that were in-flight when the previous instance shut down.
        using (var scope = scopeFactory.CreateScope())
        {
            var orchestrator = scope.ServiceProvider.GetRequiredService<IJobOrchestrator>();
            await orchestrator.RequeueInterruptedJobsAsync(stoppingToken);
        }

        await foreach (var jobId in channel.Reader.ReadAllAsync(stoppingToken))
        {
            using var scope      = scopeFactory.CreateScope();
            var orchestrator     = scope.ServiceProvider.GetRequiredService<IJobOrchestrator>();

            try   { await orchestrator.OrchestrateAsync(jobId, stoppingToken); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            { logger.LogError(ex, "Unhandled error on job {JobId}.", jobId); }
        }
    }
}
