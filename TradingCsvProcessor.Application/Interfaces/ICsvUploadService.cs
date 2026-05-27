using Microsoft.AspNetCore.Http;
using TradingCsvProcessor.Application.DTOs;

namespace TradingCsvProcessor.Application.Interfaces;

public interface ICsvUploadService
{
    Task<UploadResponse> UploadAsync(IFormFile file, CancellationToken ct = default);
    Task<JobStatusResponse?> GetJobStatusAsync(Guid jobId, CancellationToken ct = default);
    Task<IEnumerable<JobStatusResponse>> GetAllJobsAsync(CancellationToken ct = default);
    Task<CancelJobResponse> CancelJobAsync(Guid jobId, CancellationToken ct = default);
}
