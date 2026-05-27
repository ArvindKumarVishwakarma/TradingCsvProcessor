using CsvHelper.Configuration.Attributes;

namespace TradingCsvProcessor.Models.Csv;

public class TradeCsvRow
{
    [Name("TradeId")]
    public string TradeId { get; set; } = string.Empty;

    [Name("Symbol")]
    public string Symbol { get; set; } = string.Empty;

    [Name("TradeDate")]
    public string TradeDate { get; set; } = string.Empty;

    [Name("Quantity")]
    public decimal Quantity { get; set; }

    [Name("Price")]
    public decimal Price { get; set; }

    [Name("Side")]
    public string Side { get; set; } = string.Empty;

    [Name("Exchange")]
    [Optional]
    public string? Exchange { get; set; }

    [Name("Currency")]
    [Optional]
    public string? Currency { get; set; }
}
