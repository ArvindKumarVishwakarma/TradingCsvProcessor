using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingCsvProcessor.Application.Abstractions;
using TradingCsvProcessor.Application.DTOs;
using TradingCsvProcessor.Application.Features.Jobs.Commands.CancelJob;
using TradingCsvProcessor.Application.Features.Jobs.Commands.UploadCsv;
using TradingCsvProcessor.Application.Features.Jobs.Queries.GetAllJobs;
using TradingCsvProcessor.Application.Features.Jobs.Queries.GetJobStatus;
using TradingCsvProcessor.Application.Options;

namespace TradingCsvProcessor.Application.Extensions;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services, IConfiguration configuration)
    {
        // ── Configuration options ─────────────────────────────────────────────
        services.AddOptions<ProcessingOptions>()
            .Bind(configuration.GetSection(ProcessingOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // ── CQRS handlers ─────────────────────────────────────────────────────
        services.AddScoped<
            ICommandHandler<UploadCsvCommand, UploadResponse>,
            UploadCsvCommandHandler>();

        services.AddScoped<
            ICommandHandler<CancelJobCommand, CancelJobResponse>,
            CancelJobCommandHandler>();

        services.AddScoped<
            IQueryHandler<GetJobStatusQuery, JobStatusResponse?>,
            GetJobStatusQueryHandler>();

        services.AddScoped<
            IQueryHandler<GetAllJobsQuery, IReadOnlyList<JobStatusResponse>>,
            GetAllJobsQueryHandler>();

        return services;
    }
}
