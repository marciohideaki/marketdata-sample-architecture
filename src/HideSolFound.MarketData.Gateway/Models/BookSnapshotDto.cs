namespace HideSolFound.MarketData.Gateway.Models;

public sealed class BookSnapshotDto
{
    public required string Symbol { get; init; }
    public long Timestamp { get; init; }
    public long UpdateCount { get; init; }
    public required PriceLevelDto Bid { get; init; }
    public required PriceLevelDto Ask { get; init; }
}

public sealed class PriceLevelDto
{
    public decimal Price { get; init; }
    public long Quantity { get; init; }
    public bool IsEmpty => Price == 0 && Quantity == 0;
}
