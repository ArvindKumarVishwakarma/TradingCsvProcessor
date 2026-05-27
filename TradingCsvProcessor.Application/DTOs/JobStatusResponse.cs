namespace TradingCsvProcessor.Application.DTOs;

public record JobStatusResponse(
    Guid JobId,
    string FileName,
    string Status,
    string CurrentStage,
    int TotalRows,
    int ProcessedRows,
    int SkippedRows,
    int FailedRows,
    double ProgressPercent,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    string? ErrorMessage,
    IEnumerable<ChunkStatusDto> Chunks,
    IEnumerable<StageLogDto> StageLogs
);

public record ChunkStatusDto(
    Guid ChunkId,
    int ChunkNumber,
    int StartRow,
    int EndRow,
    int TotalRows,
    string Status,
    int ProcessedCount,
    int SkippedCount,
    int FailedCount,
    int RetryCount,
    DateTime? CompletedAt,
    string? ErrorMessage
);

public record StageLogDto(
    string Stage,
    string Message,
    DateTime Timestamp
);

public record CancelJobResponse(bool Success, string Message);
