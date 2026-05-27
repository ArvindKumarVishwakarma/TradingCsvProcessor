using TradingCsvProcessor.Domain.Enums;

namespace TradingCsvProcessor.Domain.Entities;

public class UploadJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string StoredFilePath { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public int TotalRows { get; set; }
    public int ProcessedRows { get; set; }
    public int SkippedRows { get; set; }
    public int FailedRows { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public ProcessingStage CurrentStage { get; set; } = ProcessingStage.FileUploaded;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsCancellationRequested { get; set; }
    public DateTime? CancelledAt { get; set; }

    public ICollection<UploadJobChunk> Chunks { get; set; } = new List<UploadJobChunk>();
    public ICollection<JobStageLog> StageLogs { get; set; } = new List<JobStageLog>();
}
