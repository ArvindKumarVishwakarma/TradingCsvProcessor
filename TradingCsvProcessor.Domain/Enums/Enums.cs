namespace TradingCsvProcessor.Domain.Enums;

public enum JobStatus
{
    Pending = 0,
    Queued = 1,
    Reading = 2,
    Processing = 3,
    Completed = 4,
    PartiallyCompleted = 5,
    Failed = 6,
    Cancelling = 7,
    Cancelled = 8
}

public enum ChunkStatus
{
    Pending = 0,
    Processing = 1,
    Completed = 2,
    Failed = 3
}

public enum ProcessingStage
{
    FileUploaded = 1,
    FileStored = 2,
    JobCreated = 3,
    JobQueued = 4,
    FileReading = 5,
    ChunksCreated = 6,
    ChunkProcessing = 7,
    ChunkBulkInserting = 8,
    ChunkCompleted = 9,
    ChunkFailed = 10,
    JobCompleted = 11,
    JobFailed = 12,
    JobCancelling = 13,
    JobCancelled = 14
}
