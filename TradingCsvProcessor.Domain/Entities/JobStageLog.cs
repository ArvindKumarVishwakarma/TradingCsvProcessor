using TradingCsvProcessor.Domain.Enums;

namespace TradingCsvProcessor.Domain.Entities;

public class JobStageLog
{
    // EF Core materialisation
    private JobStageLog() { }

    public long            Id        { get; private set; }
    public Guid            JobId     { get; private set; }
    public Guid?           ChunkId   { get; private set; }
    public ProcessingStage Stage     { get; private set; }
    public string          Message   { get; private set; } = string.Empty;
    public DateTime        CreatedAt { get; private set; } = DateTime.UtcNow;

    public UploadJob Job { get; private set; } = null!;

    public static JobStageLog For(Guid jobId, ProcessingStage stage, string message, Guid? chunkId = null) => new()
    {
        JobId   = jobId,
        ChunkId = chunkId,
        Stage   = stage,
        Message = message
    };
}
