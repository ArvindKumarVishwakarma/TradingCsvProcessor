using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TradingCsvProcessor.Application.Options;

namespace TradingCsvProcessor.API.HealthChecks;

public sealed class FileStorageHealthCheck(IOptions<FileStorageOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var path = options.Value.Path;

        if (!Directory.Exists(path))
        {
            try { Directory.CreateDirectory(path); }
            catch (Exception ex)
            {
                return Task.FromResult(HealthCheckResult.Unhealthy(
                    $"Cannot create upload directory '{path}': {ex.Message}"));
            }
        }

        try
        {
            var probe = Path.Combine(path, $".health_{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return Task.FromResult(HealthCheckResult.Healthy($"Storage path '{path}' is writable."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Storage path '{path}' is not writable: {ex.Message}"));
        }
    }
}
