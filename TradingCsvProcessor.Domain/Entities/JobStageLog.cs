using TradingCsvProcessor.Domain.Enums;

namespace TradingCsvProcessor.Domain.Entities;

public class JobStageLog
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public Guid? ChunkId { get; set; }
    public ProcessingStage Stage { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public UploadJob Job { get; set; } = null!;
}
