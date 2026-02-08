using System.Collections.Concurrent;
using HideSolFound.MarketData.Gateway.Models;
using HideSolFound.MarketData.Core;

namespace HideSolFound.MarketData.Gateway.Services;

/// <summary>
/// Manages order book snapshot generation and incremental update distribution.
/// Bridges the hot-path pipeline output with cold-path consumers (Redis, SignalR).
/// </summary>
public sealed class BookSnapshotManager : IDisposable
{
    private readonly IRedisBookPersistence _persistence;
    private readonly ILogger<BookSnapshotManager> _logger;
    private readonly ConcurrentDictionary<string, SymbolState> _symbolStates = new();
    private readonly Timer _processingTimer;
    private const int ProcessingIntervalMs = 50;

    public event Action<string, BookSnapshotDto>? SnapshotGenerated;
    public event Action<string, BookIncrementalDto>? IncrementalGenerated;

    public BookSnapshotManager(IRedisBookPersistence persistence, ILogger<BookSnapshotManager> logger)
    {
        _persistence = persistence;
        _logger = logger;
        _processingTimer = new Timer(ProcessPendingUpdates, null, ProcessingIntervalMs, ProcessingIntervalMs);
    }

    public void OnBookUpdate(string symbol, BookSnapshot snapshot)
    {
        SymbolState state = _symbolStates.GetOrAdd(symbol, _ => new SymbolState());

        lock (state)
        {
            state.LatestSnapshot = snapshot;
            state.HasPendingUpdate = true;
        }
    }

    private void ProcessPendingUpdates(object? state)
    {
        foreach ((string symbol, SymbolState symbolState) in _symbolStates)
        {
            BookSnapshot snapshot;
            lock (symbolState)
            {
                if (!symbolState.HasPendingUpdate) continue;
                snapshot = symbolState.LatestSnapshot;
                symbolState.HasPendingUpdate = false;
            }

            BookSnapshotDto dto = ToSnapshotDto(symbol, snapshot);
            BookIncrementalDto incremental = ToIncrementalDto(symbol, snapshot);

            _ = _persistence.SaveSnapshotAsync(symbol, dto);
            _ = _persistence.SaveIncrementalAsync(symbol, incremental);

            SnapshotGenerated?.Invoke(symbol, dto);
            IncrementalGenerated?.Invoke(symbol, incremental);
        }
    }

    public BookSnapshotDto? GetLatestSnapshot(string symbol)
    {
        if (!_symbolStates.TryGetValue(symbol, out SymbolState? state)) return null;
        lock (state)
        {
            return ToSnapshotDto(symbol, state.LatestSnapshot);
        }
    }

    private static BookSnapshotDto ToSnapshotDto(string symbol, BookSnapshot snap) => new()
    {
        Symbol = symbol,
        Timestamp = snap.Timestamp,
        UpdateCount = snap.UpdateCount,
        Bid = new PriceLevelDto { Price = snap.BestBidPrice / 100_000_000m, Quantity = snap.BestBidQuantity },
        Ask = new PriceLevelDto { Price = snap.BestAskPrice / 100_000_000m, Quantity = snap.BestAskQuantity },
    };

    private static BookIncrementalDto ToIncrementalDto(string symbol, BookSnapshot snap) => new()
    {
        Symbol = symbol,
        Timestamp = snap.Timestamp,
        UpdateCount = snap.UpdateCount,
        Changes =
        [
            new PriceChangeDto { Side = "bid", Price = snap.BestBidPrice / 100_000_000m, Quantity = snap.BestBidQuantity },
            new PriceChangeDto { Side = "ask", Price = snap.BestAskPrice / 100_000_000m, Quantity = snap.BestAskQuantity },
        ],
    };

    public void Dispose() => _processingTimer.Dispose();

    private sealed class SymbolState
    {
        public BookSnapshot LatestSnapshot;
        public bool HasPendingUpdate;
    }
}
