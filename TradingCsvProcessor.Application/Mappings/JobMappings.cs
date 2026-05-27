using TradingCsvProcessor.Application.DTOs;
using TradingCsvProcessor.Domain.Entities;

namespace TradingCsvProcessor.Application.Mappings;

public static class JobMappings
{
    public static JobStatusResponse ToStatusResponse(
        this UploadJob job, bool includeChunks, bool includeLogs)
    {
        var progress = job.TotalRows > 0
            ? Math.Round((double)(job.ProcessedRows + job.SkippedRows + job.FailedRows) / job.TotalRows * 100, 2)
            : 0d;

        var chunks = includeChunks
            ? job.Chunks
                .OrderBy(c => c.ChunkNumber)
                .Select(c => new ChunkStatusDto(
                    c.Id, c.ChunkNumber, c.StartRow, c.EndRow, c.TotalRows,
                    c.Status.ToString(), c.ProcessedCount, c.SkippedCount, c.FailedCount,
                    c.RetryCount, c.CompletedAt, c.ErrorMessage))
                .ToList()
            : [];

        var logs = includeLogs
            ? job.StageLogs
                .OrderBy(s => s.CreatedAt)
                .Select(s => new StageLogDto(s.Stage.ToString(), s.Message, s.CreatedAt))
                .ToList()
            : [];

        return new JobStatusResponse(
            job.Id, job.FileName, job.Status.ToString(), job.CurrentStage.ToString(),
            job.TotalRows, job.ProcessedRows, job.SkippedRows, job.FailedRows, progress,
            job.CreatedAt, job.StartedAt, job.CompletedAt, job.ErrorMessage, chunks, logs);
    }
}
