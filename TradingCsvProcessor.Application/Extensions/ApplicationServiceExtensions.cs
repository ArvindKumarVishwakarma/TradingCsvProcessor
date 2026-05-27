using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TradingCsvProcessor.Application.Interfaces;
using TradingCsvProcessor.Application.Options;
using TradingCsvProcessor.Application.Services;

namespace TradingCsvProcessor.Application.Extensions;

public static class ApplicationServiceExtensions
{
    public static IServiceCollection AddApplication(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ProcessingOptions>()
            .Bind(configuration.GetSection(ProcessingOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<FileStorageOptions>()
            .Bind(configuration.GetSection(FileStorageOptions.Section))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<ICsvUploadService, CsvUploadService>();
        return services;
    }
}
