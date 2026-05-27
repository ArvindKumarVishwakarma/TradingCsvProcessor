using Microsoft.AspNetCore.Http;

namespace TradingCsvProcessor.Application.Interfaces;

public interface IFileStorageService
{
    Task<string> StoreAsync(IFormFile file, CancellationToken ct = default);
    Task DeleteAsync(string filePath);
}
