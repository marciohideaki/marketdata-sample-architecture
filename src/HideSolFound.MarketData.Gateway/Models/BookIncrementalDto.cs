namespace HideSolFound.MarketData.Gateway.Models;

public sealed class BookIncrementalDto
{
    public required string Symbol { get; init; }
    public long Timestamp { get; init; }
    public long UpdateCount { get; init; }
    public required List<PriceChangeDto> Changes { get; init; }
}

public sealed class PriceChangeDto
{
    public required string Side { get; init; }
    public decimal Price { get; init; }
    public long Quantity { get; init; }
}
