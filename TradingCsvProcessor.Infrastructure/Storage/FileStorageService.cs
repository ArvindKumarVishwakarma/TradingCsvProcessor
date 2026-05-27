using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradingCsvProcessor.Application.Interfaces;
using TradingCsvProcessor.Application.Options;

namespace TradingCsvProcessor.Infrastructure.Storage;

public sealed class FileStorageService : IFileStorageService
{
    private readonly string _basePath;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(IOptions<FileStorageOptions> options, ILogger<FileStorageService> logger)
    {
        _basePath = options.Value.Path;
        _logger = logger;
        Directory.CreateDirectory(_basePath);
    }

    public async Task<string> StoreAsync(IFormFile file, CancellationToken ct = default)
    {
        var dayFolder = Path.Combine(_basePath, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dayFolder);

        var uniqueName = $"{Guid.NewGuid():N}_{Path.GetFileName(file.FileName)}";
        var fullPath = Path.Combine(dayFolder, uniqueName);

        await using var stream = new FileStream(
            fullPath, FileMode.Create, FileAccess.Write,
            FileShare.None, bufferSize: 81_920, useAsync: true);
        await file.CopyToAsync(stream, ct);

        _logger.LogInformation("Stored {FileName} ({Bytes:N0} bytes) → {Path}",
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
