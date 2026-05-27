using System.Security.Cryptography;
using System.Text;
using TradingCsvProcessor.Infrastructure.Models;

namespace TradingCsvProcessor.Infrastructure.Processing;

internal static class RowHasher
{
    public static string Compute(TradeCsvRow row)
    {
        var key = $"{row.TradeId}|{row.Symbol}|{row.TradeDate}|{row.Quantity}|{row.Price}|{row.Side}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
    }
}
