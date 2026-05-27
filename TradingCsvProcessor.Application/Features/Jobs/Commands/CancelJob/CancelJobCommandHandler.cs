using Microsoft.Extensions.Logging;
using TradingCsvProcessor.Application.Abstractions;
using TradingCsvProcessor.Application.DTOs;
using TradingCsvProcessor.Application.Interfaces;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Enums;
using TradingCsvProcessor.Domain.Exceptions;
using TradingCsvProcessor.Domain.Interfaces;
using TradingCsvProcessor.Domain.Repositories;

namespace TradingCsvProcessor.Application.Features.Jobs.Commands.CancelJob;

public sealed class CancelJobCommandHandler(
    IUploadJobRepository jobRepo,
    IJobStageLogRepository stageLogRepo,
    IJobCancellationRegistry cancellationRegistry,
    IUnitOfWork unitOfWork,
    ILogger<CancelJobCommandHandler> logger)
    : ICommandHandler<CancelJobCommand, CancelJobResponse>
{
    private static readonly JobStatus[] TerminalStatuses =
    [
        JobStatus.Completed, JobStatus.PartiallyCompleted,
        JobStatus.Failed, JobStatus.Cancelled, JobStatus.Cancelling
    ];

    public async Task<CancelJobResponse> HandleAsync(CancelJobCommand command, CancellationToken ct = default)
    {
        var job = await jobRepo.GetByIdAsync(command.JobId, ct)
            ?? throw new NotFoundException(nameof(UploadJob), command.JobId);

        if (TerminalStatuses.Contains(job.Status))
            throw new ConflictException(
                $"Job cannot be cancelled: already in '{job.Status}' state.");

        job.BeginCancelling();
        stageLogRepo.Add(JobStageLog.For(job.Id, ProcessingStage.JobCancelling, "Cancellation requested by user."));
        await unitOfWork.SaveChangesAsync(ct);

        var signalled = cancellationRegistry.TryCancel(command.JobId);

        var detail = signalled
            ? "Worker signalled — job will stop after the current chunk completes."
            : "Cancellation recorded — job will be skipped when the worker picks it up.";

        logger.LogInformation("Cancel requested for job {JobId} (in-memory signal: {Signalled})",
            command.JobId, signalled);

        return new CancelJobResponse(true, detail);
    }
}
