using HideSolFound.MarketData.Gateway.Models;

namespace HideSolFound.MarketData.Gateway.Services;

public interface IRedisBookPersistence
{
    Task SaveSnapshotAsync(string symbol, BookSnapshotDto snapshot);
    Task SaveIncrementalAsync(string symbol, BookIncrementalDto incremental);
    Task<BookSnapshotDto?> GetSnapshotAsync(string symbol);
    Task PublishUpdateAsync(string channel, string message);
}
