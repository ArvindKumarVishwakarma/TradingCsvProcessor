using TradingCsvProcessor.Application.Abstractions;
using TradingCsvProcessor.Application.DTOs;
using TradingCsvProcessor.Application.Mappings;
using TradingCsvProcessor.Domain.Repositories;

namespace TradingCsvProcessor.Application.Features.Jobs.Queries.GetJobStatus;

public sealed class GetJobStatusQueryHandler(IUploadJobRepository jobRepo)
    : IQueryHandler<GetJobStatusQuery, JobStatusResponse?>
{
    public async Task<JobStatusResponse?> HandleAsync(GetJobStatusQuery query, CancellationToken ct = default)
    {
        var job = await jobRepo.GetByIdWithChunksAndLogsAsync(query.JobId, ct);
        return job?.ToStatusResponse(includeChunks: true, includeLogs: true);
    }
}
