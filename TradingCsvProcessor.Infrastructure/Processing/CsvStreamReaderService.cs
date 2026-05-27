using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using TradingCsvProcessor.Infrastructure.Models;

namespace TradingCsvProcessor.Infrastructure.Processing;

internal sealed class CsvStreamReaderService : ICsvStreamReader
{
    private static CsvConfiguration BuildConfig() => new(CultureInfo.InvariantCulture)
    {
        HasHeaderRecord   = true,
        MissingFieldFound = null,
        BadDataFound      = null,
        TrimOptions       = TrimOptions.Trim
    };

    public async IAsyncEnumerable<ChunkPayload> StreamChunksAsync(
        string filePath, int chunkSize,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = OpenReader(filePath);
        using var csv    = new CsvReader(reader, BuildConfig());

        await csv.ReadAsync();
        csv.ReadHeader();

        var buffer        = new List<TradeCsvRow>(chunkSize);
        int absoluteRow   = 0;
        int chunkStartRow = 1;
        int chunkNumber   = 0;

        while (await csv.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            buffer.Add(csv.GetRecord<TradeCsvRow>()!);
            absoluteRow++;

            if (buffer.Count == chunkSize)
            {
                chunkNumber++;
                yield return new ChunkPayload(chunkNumber, buffer, chunkStartRow);
                buffer        = new List<TradeCsvRow>(chunkSize);
                chunkStartRow = absoluteRow + 1;
            }
        }

        if (buffer.Count > 0)
        {
            chunkNumber++;
            yield return new ChunkPayload(chunkNumber, buffer, chunkStartRow);
        }
    }

    public async Task<int> CountRowsAsync(string filePath, CancellationToken ct)
    {
        using var reader = OpenReader(filePath);
        await reader.ReadLineAsync(ct); // skip header
        int count = 0;
        while (await reader.ReadLineAsync(ct) is not null)
            count++;
        return count;
    }

    private static StreamReader OpenReader(string filePath) =>
        new(filePath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65_536);
}
