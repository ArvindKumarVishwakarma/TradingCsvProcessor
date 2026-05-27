using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingCsvProcessor.Application.Interfaces;
using TradingCsvProcessor.Domain.Interfaces;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Messaging;
using TradingCsvProcessor.Infrastructure.Persistence;
using TradingCsvProcessor.Infrastructure.Processing;
using TradingCsvProcessor.Infrastructure.Repositories;
using TradingCsvProcessor.Infrastructure.Storage;
using TradingCsvProcessor.Infrastructure.Workers;

namespace TradingCsvProcessor.Infrastructure.Extensions;

public static class InfrastructureServiceExtensions
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, IConfiguration configuration)
    {
        // ── Database ──────────────────────────────────────────────────────────
        services.AddDbContext<AppDbContext>(opts =>
            opts.UseSqlServer(
                configuration.GetConnectionString("DefaultConnection"),
                sql =>
                {
                    sql.CommandTimeout(120);
                    sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(30), null);
                }));

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        // ── Health check ──────────────────────────────────────────────────────
        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>(name: "database", tags: ["ready", "db"]);

        // ── In-process job queue ──────────────────────────────────────────────
        services.AddSingleton<ProcessingChannel>();
        services.AddSingleton<JobCancellationRegistry>();
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<ProcessingChannel>());
        services.AddSingleton<IJobCancellationRegistry>(sp => sp.GetRequiredService<JobCancellationRegistry>());

        // ── Repositories ──────────────────────────────────────────────────────
        services.AddScoped<IUploadJobRepository,      UploadJobRepository>();
        services.AddScoped<IUploadJobChunkRepository, UploadJobChunkRepository>();
        services.AddScoped<ITradeRecordRepository,    TradeRecordRepository>();
        services.AddScoped<IJobStageLogRepository,    JobStageLogRepository>();

        // ── Storage ───────────────────────────────────────────────────────────
        services.AddScoped<IFileStorageService, FileStorageService>();

        // ── Processing pipeline ───────────────────────────────────────────────
        services.AddScoped<ICsvStreamReader,  CsvStreamReaderService>();
        services.AddScoped<IChunkProcessor,   ChunkProcessorService>();
        services.AddScoped<IJobOrchestrator,  JobOrchestrator>();

        // ── Background worker ─────────────────────────────────────────────────
        services.AddHostedService<CsvProcessingWorker>();

        return services;
    }
}
