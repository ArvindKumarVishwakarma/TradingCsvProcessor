namespace TradingCsvProcessor.Services;

public sealed class FileStorageService : IFileStorageService
{
    private readonly string _basePath;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(IConfiguration config, ILogger<FileStorageService> logger)
    {
        _basePath = config["FileStorage:Path"] ?? Path.Combine(Directory.GetCurrentDirectory(), "uploads");
        _logger = logger;
        Directory.CreateDirectory(_basePath);
    }

    public async Task<string> StoreAsync(IFormFile file, CancellationToken ct = default)
    {
        var dayFolder = Path.Combine(_basePath, DateTime.UtcNow.ToString("yyyy-MM-dd"));
        Directory.CreateDirectory(dayFolder);

        var uniqueName = $"{Guid.NewGuid():N}_{Path.GetFileName(file.FileName)}";
        var fullPath = Path.Combine(dayFolder, uniqueName);

        await using var stream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await file.CopyToAsync(stream, ct);

        _logger.LogInformation("Stored {FileName} ({Bytes} bytes) → {Path}", file.FileName, file.Length, fullPath);
        return fullPath;
    }

    public Task DeleteAsync(string filePath)
    {
        if (File.Exists(filePath))
            File.Delete(filePath);
        return Task.CompletedTask;
    }
}
