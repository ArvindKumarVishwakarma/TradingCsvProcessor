using TradingCsvProcessor.Domain.Entities;

namespace TradingCsvProcessor.Domain.Repositories;

public interface ITradeRecordRepository
{
    Task<HashSet<string>> GetExistingHashesAsync(IEnumerable<string> hashes, CancellationToken ct = default);
    Task BulkInsertAsync(List<TradeRecord> records, CancellationToken ct = default);
}
