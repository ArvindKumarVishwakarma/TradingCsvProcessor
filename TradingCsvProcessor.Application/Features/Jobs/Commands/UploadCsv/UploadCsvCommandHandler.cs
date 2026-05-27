using Microsoft.Extensions.Logging;
using TradingCsvProcessor.Application.Abstractions;
using TradingCsvProcessor.Application.DTOs;
using TradingCsvProcessor.Application.Interfaces;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Enums;
using TradingCsvProcessor.Domain.Interfaces;
using TradingCsvProcessor.Domain.Repositories;

namespace TradingCsvProcessor.Application.Features.Jobs.Commands.UploadCsv;

public sealed class UploadCsvCommandHandler(
    IUploadJobRepository jobRepo,
    IJobStageLogRepository stageLogRepo,
    IFileStorageService fileStorage,
    IJobQueue jobQueue,
    IUnitOfWork unitOfWork,
    ILogger<UploadCsvCommandHandler> logger)
    : ICommandHandler<UploadCsvCommand, UploadResponse>
{
    public async Task<UploadResponse> HandleAsync(UploadCsvCommand command, CancellationToken ct = default)
    {
        var storedPath = await fileStorage.StoreAsync(command.File, ct);

        var job = UploadJob.Create(command.File.FileName, storedPath, command.File.Length);
        jobRepo.Add(job);

        stageLogRepo.Add(JobStageLog.For(job.Id, ProcessingStage.FileUploaded,
            $"Received {command.File.FileName} ({command.File.Length:N0} bytes)"));
        stageLogRepo.Add(JobStageLog.For(job.Id, ProcessingStage.FileStored,
            $"Stored to {storedPath}"));
        stageLogRepo.Add(JobStageLog.For(job.Id, ProcessingStage.JobCreated,
            $"Job {job.Id} created."));

        // Save first — if enqueue fails, job stays in Queued state and resumes on restart.
        job.MarkAsQueued();
        await unitOfWork.SaveChangesAsync(ct);

        await jobQueue.EnqueueAsync(job.Id, ct);

        stageLogRepo.Add(JobStageLog.For(job.Id, ProcessingStage.JobQueued, "Job enqueued for processing."));
        await unitOfWork.SaveChangesAsync(ct);

        logger.LogInformation("Job {JobId} queued for {FileName}", job.Id, command.File.FileName);

        return new UploadResponse(job.Id, command.File.FileName, command.File.Length,
            job.Status.ToString(), "Uploaded and queued for processing.");
    }
}
