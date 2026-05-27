using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using TradingCsvProcessor.Application.DTOs;
using TradingCsvProcessor.Application.Interfaces;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Enums;
using TradingCsvProcessor.Domain.Repositories;

namespace TradingCsvProcessor.Application.Services;

public sealed class CsvUploadService : ICsvUploadService
{
    private readonly IUploadJobRepository _jobRepo;
    private readonly IJobStageLogRepository _stageLogRepo;
    private readonly IFileStorageService _fileStorage;
    private readonly IJobQueue _jobQueue;
    private readonly IJobCancellationRegistry _cancellationRegistry;
    private readonly ILogger<CsvUploadService> _logger;

    public CsvUploadService(
        IUploadJobRepository jobRepo,
        IJobStageLogRepository stageLogRepo,
        IFileStorageService fileStorage,
        IJobQueue jobQueue,
        IJobCancellationRegistry cancellationRegistry,
        ILogger<CsvUploadService> logger)
    {
        _jobRepo = jobRepo;
        _stageLogRepo = stageLogRepo;
        _fileStorage = fileStorage;
        _jobQueue = jobQueue;
        _cancellationRegistry = cancellationRegistry;
        _logger = logger;
    }

    public async Task<UploadResponse> UploadAsync(IFormFile file, CancellationToken ct = default)
    {
        if (!file.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only CSV files are accepted.");

        var storedPath = await _fileStorage.StoreAsync(file, ct);

        var job = new UploadJob
        {
            FileName = file.FileName,
            StoredFilePath = storedPath,
            FileSizeBytes = file.Length,
            Status = JobStatus.Pending,
            CurrentStage = ProcessingStage.FileStored
        };
        _jobRepo.Add(job);

        AddStageLog(job, ProcessingStage.FileUploaded, $"Received {file.FileName} ({file.Length:N0} bytes)");
        AddStageLog(job, ProcessingStage.FileStored, $"Stored to {storedPath}");
        AddStageLog(job, ProcessingStage.JobCreated, $"Job {job.Id} created");
        await _jobRepo.SaveChangesAsync(ct);

        await _jobQueue.EnqueueAsync(job.Id, ct);
        job.Status = JobStatus.Queued;
        AddStageLog(job, ProcessingStage.JobQueued, "Job enqueued for processing");
        await _jobRepo.SaveChangesAsync(ct);

        _logger.LogInformation("Job {JobId} queued for {FileName}", job.Id, file.FileName);
        return new UploadResponse(job.Id, file.FileName, file.Length, job.Status.ToString(), "Uploaded and queued for processing.");
    }

    public async Task<CancelJobResponse> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _jobRepo.GetByIdAsync(jobId, ct);

        if (job is null)
            return new CancelJobResponse(false, "Job not found.");

        if (job.Status is JobStatus.Completed or JobStatus.PartiallyCompleted
                       or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Cancelling)
        {
            return new CancelJobResponse(false, $"Job cannot be cancelled: already in '{job.Status}' state.");
        }

        job.IsCancellationRequested = true;
        job.Status = JobStatus.Cancelling;
        _stageLogRepo.Add(new JobStageLog
        {
            JobId = job.Id,
            Stage = ProcessingStage.JobCancelling,
            Message = "Cancellation requested by user"
        });
        await _jobRepo.SaveChangesAsync(ct);

        var signalledInMemory = _cancellationRegistry.TryCancel(jobId);

        var detail = signalledInMemory
            ? "Worker signalled — job will stop after the current chunk completes."
            : "Cancellation recorded — job will be skipped when the worker picks it up.";

        _logger.LogInformation("Cancel requested for job {JobId} (in-memory signal: {Signalled})", jobId, signalledInMemory);
        return new CancelJobResponse(true, detail);
    }

    public async Task<JobStatusResponse?> GetJobStatusAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _jobRepo.GetByIdWithChunksAndLogsAsync(jobId, ct);
        return job is null ? null : ToResponse(job, includeChunks: true, includeLogs: true);
    }

    public async Task<IEnumerable<JobStatusResponse>> GetAllJobsAsync(CancellationToken ct = default)
    {
        var jobs = await _jobRepo.GetAllWithChunksAsync(ct);
        return jobs.Select(j => ToResponse(j, includeChunks: true, includeLogs: false));
    }

    private void AddStageLog(UploadJob job, ProcessingStage stage, string message, Guid? chunkId = null)
    {
        job.CurrentStage = stage;
        _stageLogRepo.Add(new JobStageLog { JobId = job.Id, ChunkId = chunkId, Stage = stage, Message = message });
    }

    private static JobStatusResponse ToResponse(UploadJob job, bool includeChunks, bool includeLogs)
    {
        var progress = job.TotalRows > 0
            ? Math.Round((double)(job.ProcessedRows + job.SkippedRows + job.FailedRows) / job.TotalRows * 100, 2)
            : 0;

        var chunks = includeChunks
            ? job.Chunks.OrderBy(c => c.ChunkNumber).Select(c => new ChunkStatusDto(
                c.Id, c.ChunkNumber, c.StartRow, c.EndRow, c.TotalRows,
                c.Status.ToString(), c.ProcessedCount, c.SkippedCount, c.FailedCount,
                c.RetryCount, c.CompletedAt, c.ErrorMessage))
            : Enumerable.Empty<ChunkStatusDto>();

        var logs = includeLogs
            ? job.StageLogs.OrderBy(s => s.CreatedAt).Select(s => new StageLogDto(
                s.Stage.ToString(), s.Message, s.CreatedAt))
            : Enumerable.Empty<StageLogDto>();

        return new JobStatusResponse(
            job.Id, job.FileName, job.Status.ToString(), job.CurrentStage.ToString(),
            job.TotalRows, job.ProcessedRows, job.SkippedRows, job.FailedRows, progress,
            job.CreatedAt, job.StartedAt, job.CompletedAt, job.ErrorMessage, chunks, logs);
    }
}
