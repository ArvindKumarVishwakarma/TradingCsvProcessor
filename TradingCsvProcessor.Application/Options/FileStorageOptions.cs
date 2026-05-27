using System.ComponentModel.DataAnnotations;

namespace TradingCsvProcessor.Application.Options;

public sealed class FileStorageOptions
{
    public const string Section = "FileStorage";

    [Required]
    public string Path { get; init; } = "uploads";

    [Range(1L, long.MaxValue)]
    public long MaxFileSizeBytes { get; init; } = 104_857_600; // 100 MB
}
