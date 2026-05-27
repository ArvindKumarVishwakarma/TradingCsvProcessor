using TradingCsvProcessor.Domain.Enums;

namespace TradingCsvProcessor.Domain.Entities;

public class UploadJobChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public int ChunkNumber { get; set; }
    public int StartRow { get; set; }
    public int EndRow { get; set; }
    public int TotalRows { get; set; }
    public ChunkStatus Status { get; set; } = ChunkStatus.Pending;
    public int ProcessedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public int RetryCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public UploadJob Job { get; set; } = null!;
}
