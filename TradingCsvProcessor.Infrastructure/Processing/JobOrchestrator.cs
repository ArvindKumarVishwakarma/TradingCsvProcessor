using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingCsvProcessor.Application.Options;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Enums;
using TradingCsvProcessor.Domain.Interfaces;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Messaging;

namespace TradingCsvProcessor.Infrastructure.Processing;

internal sealed class JobOrchestrator(
    IServiceScopeFactory scopeFactory,
    ICsvStreamReader streamReader,
    IUploadJobRepository jobRepo,
    IJobStageLogRepository stageLogRepo,
    IUnitOfWork unitOfWork,
    JobCancellationRegistry cancellationRegistry,
    ProcessingChannel channel,
    IOptions<ProcessingOptions> options,
    ILogger<JobOrchestrator> logger) : IJobOrchestrator
{
    private readonly int _chunkSize = options.Value.ChunkSize;
    private readonly int _dop       = options.Value.DegreeOfParallelism;

    public async Task RequeueInterruptedJobsAsync(CancellationToken ct)
    {
        var ids = await jobRepo.GetInterruptedJobIdsAsync(ct);
        foreach (var id in ids)
        {
            await channel.EnqueueAsync(id, ct);
            logger.LogInformation("Re-queued interrupted job {JobId}.", id);
        }
    }

    public async Task OrchestrateAsync(Guid jobId, CancellationToken stoppingToken)
    {
        var job = await jobRepo.GetByIdWithChunksAsync(jobId, stoppingToken);
        if (job is null) { logger.LogWarning("Job {JobId} not found.", jobId); return; }

        if (job.IsCancellationRequested || job.Status == JobStatus.Cancelled)
        {
            job.MarkCancelled();
            await PersistStageAsync(job, ProcessingStage.JobCancelled,
                "Job cancelled before the worker picked it up.", stoppingToken);
            return;
        }

        var jobToken = cancellationRegistry.Register(jobId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobToken);
        var ct = linked.Token;

        try
        {
            job.BeginReading();
            await PersistStageAsync(job, ProcessingStage.FileReading,
                "Counting rows (O(1) memory pass).", stoppingToken);

            job.SetTotalRows(await streamReader.CountRowsAsync(job.StoredFilePath, ct));

            var completedChunks = job.Chunks
                .Where(c => c.Status == ChunkStatus.Completed)
                .Select(c => c.ChunkNumber)
                .ToHashSet();

            await PersistStageAsync(job, ProcessingStage.ChunksCreated,
                $"Rows: {job.TotalRows} | Skipping {completedChunks.Count} completed | DOP: {_dop}.",
                stoppingToken);

            job.BeginProcessing();

            int processed = 0, skipped = 0, failed = 0;

            await Parallel.ForEachAsync(
                streamReader.StreamChunksAsync(job.StoredFilePath, _chunkSize, ct),
                new ParallelOptions { MaxDegreeOfParallelism = _dop, CancellationToken = ct },
                async (payload, innerCt) =>
                {
                    if (completedChunks.Contains(payload.ChunkNumber)) return;

                    using var scope     = scopeFactory.CreateScope();
                    var processor       = scope.ServiceProvider.GetRequiredService<IChunkProcessor>();

                    var result = await processor.ProcessAsync(job.Id, payload, innerCt);
                    Interlocked.Add(ref processed, result.Processed);
                    Interlocked.Add(ref skipped,   result.Skipped);
                    Interlocked.Add(ref failed,     result.Failed);
                });

            var failedCount    = await jobRepo.CountChunksByStatusAsync(job.Id, ChunkStatus.Failed,    stoppingToken);
            var completedCount = await jobRepo.CountChunksByStatusAsync(job.Id, ChunkStatus.Completed, stoppingToken);

            var finalStatus = failedCount > 0 && completedCount > 0 ? JobStatus.PartiallyCompleted
                            : failedCount > 0                        ? JobStatus.Failed
                            :                                          JobStatus.Completed;

            job.Complete(processed, skipped, failed, finalStatus);
            var stage = finalStatus == JobStatus.Failed ? ProcessingStage.JobFailed : ProcessingStage.JobCompleted;
            await PersistStageAsync(job, stage,
                $"Done — inserted: {processed}, skipped (dup): {skipped}, failed: {failed}.", stoppingToken);
        }
        catch (OperationCanceledException) when (jobToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Job {JobId} cancelled by user request.", jobId);
            job.MarkCancelled();
            await PersistStageAsync(job, ProcessingStage.JobCancelled,
                "Job cancelled by user request.", stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // App shutdown — leave status as-is; will be re-queued on restart.
            logger.LogWarning("Job {JobId} interrupted by app shutdown — will resume on restart.", jobId);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fatal error on job {JobId}.", jobId);
            job.Fail(ex.Message);
            await PersistStageAsync(job, ProcessingStage.JobFailed, $"Fatal: {ex.Message}", stoppingToken);
        }
        finally
        {
            cancellationRegistry.Unregister(jobId);
        }
    }

    private async Task PersistStageAsync(UploadJob job, ProcessingStage stage, string message, CancellationToken ct)
    {
        job.AdvanceStage(stage);
        stageLogRepo.Add(JobStageLog.For(job.Id, stage, message));
        await unitOfWork.SaveChangesAsync(ct);
    }
}
