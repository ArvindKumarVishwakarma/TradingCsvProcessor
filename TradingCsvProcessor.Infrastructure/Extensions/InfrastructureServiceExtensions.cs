using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingCsvProcessor.Application.Interfaces;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Messaging;
using TradingCsvProcessor.Infrastructure.Persistence;
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
                    // Transient fault retry — handles brief SQL Azure / network blips
                    sql.EnableRetryOnFailure(
                        maxRetryCount:  5,
                        maxRetryDelay:  TimeSpan.FromSeconds(30),
                        errorNumbersToAdd: null);
                }));

        // ── Health check — EF Core DB context ping ────────────────────────────
        services.AddHealthChecks()
            .AddDbContextCheck<AppDbContext>(
                name: "database",
                tags: ["ready", "db"]);

        // ── In-memory job queue ───────────────────────────────────────────────
        services.AddSingleton<ProcessingChannel>();
        services.AddSingleton<JobCancellationRegistry>();

        // Expose concrete singletons through application-layer interfaces
        services.AddSingleton<IJobQueue>(sp => sp.GetRequiredService<ProcessingChannel>());
        services.AddSingleton<IJobCancellationRegistry>(sp => sp.GetRequiredService<JobCancellationRegistry>());

        // ── Repositories ──────────────────────────────────────────────────────
        services.AddScoped<IUploadJobRepository,      UploadJobRepository>();
        services.AddScoped<IUploadJobChunkRepository, UploadJobChunkRepository>();
        services.AddScoped<ITradeRecordRepository,    TradeRecordRepository>();
        services.AddScoped<IJobStageLogRepository,    JobStageLogRepository>();

        // ── Storage ───────────────────────────────────────────────────────────
        services.AddScoped<IFileStorageService, FileStorageService>();

        // ── Background worker ─────────────────────────────────────────────────
        services.AddHostedService<CsvProcessingWorker>();

        return services;
    }
}
