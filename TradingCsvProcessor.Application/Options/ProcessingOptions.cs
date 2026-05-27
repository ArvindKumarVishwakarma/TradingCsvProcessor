using System.ComponentModel.DataAnnotations;

namespace TradingCsvProcessor.Application.Options;

public sealed class ProcessingOptions
{
    public const string Section = "Processing";

    [Range(100, 100_000)]
    public int ChunkSize { get; init; } = 5000;

    [Range(1, 64)]
    public int DegreeOfParallelism { get; init; } = 4;

    [Range(1, 10_000)]
    public int ChannelCapacity { get; init; } = 100;
}
