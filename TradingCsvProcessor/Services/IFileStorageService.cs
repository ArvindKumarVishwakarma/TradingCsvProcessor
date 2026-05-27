namespace TradingCsvProcessor.Services;

public interface IFileStorageService
{
    Task<string> StoreAsync(IFormFile file, CancellationToken ct = default);
    Task DeleteAsync(string filePath);
}
