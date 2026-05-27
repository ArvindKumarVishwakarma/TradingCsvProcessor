using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingCsvProcessor.Application.Interfaces;
using TradingCsvProcessor.Application.Options;

namespace TradingCsvProcessor.Infrastructure.Storage;

public sealed class FileStorageService(IOptions<FileStorageOptions> options, ILogger<FileStorageService> logger)
    : IFileStorageService
{
    private readonly string _basePath = options.Value.Path;

    public async Task<string> StoreAsync(IFormFile file, CancellationToken ct = default)
    {
        var dayFolder = Path.Combine(_basePath, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dayFolder);

        // Sanitise: strip directory components, keep only the filename.
        var safeName  = Path.GetFileName(file.FileName);
        var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
        var fullPath   = Path.GetFullPath(Path.Combine(dayFolder, uniqueName));

        // Guard against path traversal — resolved path must stay inside basePath.
        var resolvedBase = Path.GetFullPath(_basePath);
        if (!fullPath.StartsWith(resolvedBase, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Resolved file path escapes the storage root.");

        await using var stream = new FileStream(
            fullPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 81_920, useAsync: true);
        await file.CopyToAsync(stream, ct);

        logger.LogInformation("Stored {FileName} ({Bytes:N0} bytes) → {Path}",
            file.FileName, file.Length, fullPath);
        return fullPath;
    }

    public Task DeleteAsync(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }
}
