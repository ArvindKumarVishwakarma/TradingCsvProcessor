using Microsoft.EntityFrameworkCore;
using TradingCsvProcessor.Data;
using TradingCsvProcessor.Infrastructure;
using TradingCsvProcessor.Models.Domain;
using TradingCsvProcessor.Models.DTOs;

namespace TradingCsvProcessor.Services;

public sealed class CsvUploadService : ICsvUploadService
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _fileStorage;
    private readonly ProcessingChannel _channel;
    private readonly JobCancellationRegistry _cancellationRegistry;
    private readonly ILogger<CsvUploadService> _logger;

    public CsvUploadService(
        AppDbContext db,
        IFileStorageService fileStorage,
        ProcessingChannel channel,
        JobCancellationRegistry cancellationRegistry,
        ILogger<CsvUploadService> logger)
    {
        _db = db;
        _fileStorage = fileStorage;
        _channel = channel;
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
        _db.UploadJobs.Add(job);

        AddStageLog(job, ProcessingStage.FileUploaded, $"Received {file.FileName} ({file.Length:N0} bytes)");
        AddStageLog(job, ProcessingStage.FileStored, $"Stored to {storedPath}");
        AddStageLog(job, ProcessingStage.JobCreated, $"Job {job.Id} created");
        await _db.SaveChangesAsync(ct);

        await _channel.EnqueueAsync(job.Id, ct);
        job.Status = JobStatus.Queued;
        AddStageLog(job, ProcessingStage.JobQueued, "Job enqueued for processing");
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("Job {JobId} queued for {FileName}", job.Id, file.FileName);
        return new UploadResponse(job.Id, file.FileName, file.Length, job.Status.ToString(), "Uploaded and queued for processing.");
    }

    public async Task<CancelJobResponse> CancelJobAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.UploadJobs.FindAsync(new object[] { jobId }, ct);

        if (job is null)
            return new CancelJobResponse(false, "Job not found.");

        // Terminal states — nothing to cancel
        if (job.Status is JobStatus.Completed or JobStatus.PartiallyCompleted
                       or JobStatus.Failed or JobStatus.Cancelled or JobStatus.Cancelling)
        {
            return new CancelJobResponse(false, $"Job cannot be cancelled: already in '{job.Status}' state.");
        }

        // Persist the intent — survives app restarts.
        // The worker checks this flag when it picks up a job AND on every chunk boundary.
        job.IsCancellationRequested = true;
        job.Status = JobStatus.Cancelling;
        _db.JobStageLogs.Add(new JobStageLog
        {
            JobId = job.Id,
            Stage = ProcessingStage.JobCancelling,
            Message = "Cancellation requested by user"
        });
        await _db.SaveChangesAsync(ct);

        // Signal the in-memory CTS if the worker is currently processing this job.
        // If the job is still queued (not yet picked up), the DB flag above is sufficient.
        var signalledInMemory = _cancellationRegistry.TryCancel(jobId);

        var detail = signalledInMemory
            ? "Worker signalled — job will stop after the current chunk completes."
            : "Cancellation recorded — job will be skipped when the worker picks it up.";

        _logger.LogInformation("Cancel requested for job {JobId} (in-memory signal: {Signalled})", jobId, signalledInMemory);
        return new CancelJobResponse(true, detail);
    }

    public async Task<JobStatusResponse?> GetJobStatusAsync(Guid jobId, CancellationToken ct = default)
    {
        var job = await _db.UploadJobs
            .Include(j => j.Chunks)
            .Include(j => j.StageLogs)
            .FirstOrDefaultAsync(j => j.Id == jobId, ct);

        return job is null ? null : ToResponse(job, includeChunks: true, includeLogs: true);
    }

    public async Task<IEnumerable<JobStatusResponse>> GetAllJobsAsync(CancellationToken ct = default)
    {
        var jobs = await _db.UploadJobs
            .Include(j => j.Chunks)
            .OrderByDescending(j => j.CreatedAt)
            .ToListAsync(ct);

        return jobs.Select(j => ToResponse(j, includeChunks: true, includeLogs: false));
    }

    private static void AddStageLog(UploadJob job, ProcessingStage stage, string message, Guid? chunkId = null)
    {
        job.CurrentStage = stage;
        job.StageLogs.Add(new JobStageLog { JobId = job.Id, ChunkId = chunkId, Stage = stage, Message = message });
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
