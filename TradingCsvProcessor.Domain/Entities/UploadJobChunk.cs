using TradingCsvProcessor.Domain.Enums;

namespace TradingCsvProcessor.Domain.Entities;

public class UploadJobChunk
{
    // EF Core materialisation
    private UploadJobChunk() { }

    public Guid         Id             { get; private set; } = Guid.NewGuid();
    public Guid         JobId          { get; private set; }
    public int          ChunkNumber    { get; private set; }
    public int          StartRow       { get; private set; }
    public int          EndRow         { get; private set; }
    public int          TotalRows      { get; private set; }
    public ChunkStatus  Status         { get; private set; } = ChunkStatus.Pending;
    public int          ProcessedCount { get; private set; }
    public int          SkippedCount   { get; private set; }
    public int          FailedCount    { get; private set; }
    public int          RetryCount     { get; private set; }
    public DateTime     CreatedAt      { get; private set; } = DateTime.UtcNow;
    public DateTime?    StartedAt      { get; private set; }
    public DateTime?    CompletedAt    { get; private set; }
    public string?      ErrorMessage   { get; private set; }

    public UploadJob    Job { get; private set; } = null!;

    // ── Factory ───────────────────────────────────────────────────────────────

    public static UploadJobChunk Create(Guid jobId, int chunkNumber, int startRow, int endRow, int totalRows) => new()
    {
        JobId       = jobId,
        ChunkNumber = chunkNumber,
        StartRow    = startRow,
        EndRow      = endRow,
        TotalRows   = totalRows
    };

    // ── Domain state transitions ──────────────────────────────────────────────

    public void BeginProcessing()
    {
        Status    = ChunkStatus.Processing;
        StartedAt ??= DateTime.UtcNow;
        RetryCount++;
    }

    public void Complete(int processed, int skipped, int failed)
    {
        ProcessedCount = processed;
        SkippedCount   = skipped;
        FailedCount    = failed;
        Status         = ChunkStatus.Completed;
        CompletedAt    = DateTime.UtcNow;
    }

    public void Fail(string? errorMessage)
    {
        Status       = ChunkStatus.Failed;
        ErrorMessage = errorMessage;
        FailedCount  = TotalRows - ProcessedCount - SkippedCount;
    }
}
