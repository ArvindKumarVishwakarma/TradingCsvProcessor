using TradingCsvProcessor.Domain.Enums;

namespace TradingCsvProcessor.Domain.Entities;

public class UploadJob
{
    // EF Core materialisation
    private UploadJob() { }

    private readonly List<UploadJobChunk> _chunks = [];
    private readonly List<JobStageLog>    _stageLogs = [];

    public Guid             Id              { get; private set; } = Guid.NewGuid();
    public string           FileName        { get; private set; } = string.Empty;
    public string           StoredFilePath  { get; private set; } = string.Empty;
    public long             FileSizeBytes   { get; private set; }
    public int              TotalRows       { get; private set; }
    public int              ProcessedRows   { get; private set; }
    public int              SkippedRows     { get; private set; }
    public int              FailedRows      { get; private set; }
    public JobStatus        Status          { get; private set; } = JobStatus.Pending;
    public ProcessingStage  CurrentStage    { get; private set; } = ProcessingStage.FileUploaded;
    public DateTime         CreatedAt       { get; private set; } = DateTime.UtcNow;
    public DateTime?        StartedAt       { get; private set; }
    public DateTime?        CompletedAt     { get; private set; }
    public string?          ErrorMessage    { get; private set; }
    public bool             IsCancellationRequested { get; private set; }
    public DateTime?        CancelledAt     { get; private set; }

    public IReadOnlyCollection<UploadJobChunk> Chunks     => _chunks.AsReadOnly();
    public IReadOnlyCollection<JobStageLog>    StageLogs  => _stageLogs.AsReadOnly();

    // ── Factory ───────────────────────────────────────────────────────────────

    public static UploadJob Create(string fileName, string storedFilePath, long fileSizeBytes) => new()
    {
        FileName       = fileName,
        StoredFilePath = storedFilePath,
        FileSizeBytes  = fileSizeBytes,
        Status         = JobStatus.Pending,
        CurrentStage   = ProcessingStage.FileStored
    };

    // ── Domain state transitions ──────────────────────────────────────────────

    public void AdvanceStage(ProcessingStage stage) => CurrentStage = stage;

    public void MarkAsQueued()
    {
        Status       = JobStatus.Queued;
        CurrentStage = ProcessingStage.JobQueued;
    }

    public void BeginReading()
    {
        Status    = JobStatus.Reading;
        StartedAt ??= DateTime.UtcNow;
    }

    public void SetTotalRows(int count) => TotalRows = count;

    public void BeginProcessing() => Status = JobStatus.Processing;

    public void Complete(int processed, int skipped, int failed, JobStatus finalStatus)
    {
        ProcessedRows = processed;
        SkippedRows   = skipped;
        FailedRows    = failed;
        CompletedAt   = DateTime.UtcNow;
        Status        = finalStatus;
    }

    public void Fail(string error)
    {
        Status       = JobStatus.Failed;
        ErrorMessage = error;
    }

    public void BeginCancelling()
    {
        Status                  = JobStatus.Cancelling;
        IsCancellationRequested = true;
    }

    public void MarkCancelled()
    {
        Status      = JobStatus.Cancelled;
        CancelledAt = DateTime.UtcNow;
    }
}
