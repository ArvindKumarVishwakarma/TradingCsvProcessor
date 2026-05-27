namespace TradingCsvProcessor.Application.DTOs;

public record UploadResponse(
    Guid JobId,
    string FileName,
    long FileSizeBytes,
    string Status,
    string Message
);
