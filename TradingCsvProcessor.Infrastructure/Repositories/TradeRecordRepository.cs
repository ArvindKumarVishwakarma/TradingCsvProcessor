using EFCore.BulkExtensions;
using Microsoft.EntityFrameworkCore;
using TradingCsvProcessor.Domain.Entities;
using TradingCsvProcessor.Domain.Repositories;
using TradingCsvProcessor.Infrastructure.Persistence;

namespace TradingCsvProcessor.Infrastructure.Repositories;

public sealed class TradeRecordRepository : ITradeRecordRepository
{
    private readonly AppDbContext _db;

    public TradeRecordRepository(AppDbContext db) => _db = db;

    public async Task<HashSet<string>> GetExistingHashesAsync(IEnumerable<string> hashes, CancellationToken ct = default)
    {
        var hashList = hashes.ToList();
        return (await _db.TradeRecords
            .Where(t => hashList.Contains(t.RecordHash))
            .Select(t => t.RecordHash)
            .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
    }

    public Task BulkInsertAsync(List<TradeRecord> records, CancellationToken ct = default)
        => _db.BulkInsertAsync(records, new BulkConfig { PreserveInsertOrder = false }, cancellationToken: ct);
}
