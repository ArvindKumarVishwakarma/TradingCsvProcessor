using TradingCsvProcessor.Application.Abstractions;
using TradingCsvProcessor.Application.DTOs;
using TradingCsvProcessor.Application.Mappings;
using TradingCsvProcessor.Domain.Repositories;

namespace TradingCsvProcessor.Application.Features.Jobs.Queries.GetAllJobs;

public sealed class GetAllJobsQueryHandler(IUploadJobRepository jobRepo)
    : IQueryHandler<GetAllJobsQuery, IReadOnlyList<JobStatusResponse>>
{
    public async Task<IReadOnlyList<JobStatusResponse>> HandleAsync(GetAllJobsQuery query, CancellationToken ct = default)
    {
        var jobs = await jobRepo.GetAllWithChunksAsync(ct);
        return jobs.Select(j => j.ToStatusResponse(includeChunks: true, includeLogs: false)).ToList();
    }
}
