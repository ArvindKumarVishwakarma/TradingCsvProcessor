namespace TradingCsvProcessor.Models.Domain;

public class TradeRecord
{
    public long Id { get; set; }
    public Guid JobId { get; set; }
    public string RecordHash { get; set; } = string.Empty;  // SHA-256 of key fields — prevents duplicate inserts
    public string TradeId { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public DateTime TradeDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public string Side { get; set; } = string.Empty;
    public decimal TotalValue { get; set; }
    public string? Exchange { get; set; }
    public string? Currency { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
