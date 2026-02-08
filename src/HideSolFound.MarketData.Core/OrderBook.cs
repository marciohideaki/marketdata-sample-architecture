using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HideSolFound.MarketData.Core;

/// <summary>
/// High-performance order book with pre-allocated arrays for zero-allocation updates.
/// <para>
/// Maintains sorted bid (descending) and ask (ascending) price levels, each containing
/// individual orders for full Level-3 market data. The top-of-book (BBO) is tracked
/// incrementally so <see cref="GetSnapshot"/> is O(1).
/// </para>
/// </summary>
public sealed class OrderBook
{
    private const int MaxPriceLevels = 256;
    internal const int MaxOrdersPerLevel = 32;

    private readonly int _symbolIndex;
    private readonly PriceLevel[] _bidLevels;
    private readonly PriceLevel[] _askLevels;
    private int _bidCount;
    private int _askCount;

    // ── Top-of-book state (updated incrementally) ──
    public long TotalUpdates;
    public long LastUpdateTimestamp;
    public long BestBidPrice;
    public long BestAskPrice;
    public long BestBidQuantity;
    public long BestAskQuantity;

    public OrderBook(int symbolIndex)
    {
        _symbolIndex = symbolIndex;
        _bidLevels = new PriceLevel[MaxPriceLevels];
        _askLevels = new PriceLevel[MaxPriceLevels];

        for (int i = 0; i < MaxPriceLevels; i++)
        {
            _bidLevels[i] = new PriceLevel();
            _askLevels[i] = new PriceLevel();
        }
    }

    /// <summary>
    /// Applies a decoded FIX message to this book.
    /// </summary>
    /// <returns><c>true</c> if the best bid or ask changed (top-of-book update).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool ApplyMessage(in FixMessage msg)
    {
        LastUpdateTimestamp = msg.DecodeTimestampNanos;
        Interlocked.Increment(ref TotalUpdates);

        return msg.MsgType switch
        {
            FixMsgType.NewOrder => AddOrder(msg.Side, msg.Price, msg.Quantity, msg.OrderId),
            FixMsgType.OrderCancel => RemoveOrder(msg.Side, msg.Price, msg.OrderId),
            FixMsgType.Execution => ExecuteOrder(msg.Side, msg.Price, msg.TradeQuantity, msg.OrderId),
            FixMsgType.IncrementalRefresh => UpdateLevel(msg.Side, msg.Price, msg.Quantity),
            _ => false,
        };
    }

    /// <summary>
    /// Creates an immutable snapshot of the current top-of-book state for cold-path consumption.
    /// </summary>
    public BookSnapshot GetSnapshot() => new()
    {
        SymbolIndex = _symbolIndex,
        BestBidPrice = BestBidPrice,
        BestBidQuantity = BestBidQuantity,
        BestAskPrice = BestAskPrice,
        BestAskQuantity = BestAskQuantity,
        Timestamp = LastUpdateTimestamp,
        UpdateCount = TotalUpdates,
    };

    // ── Private mutators ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool AddOrder(FixSide side, long price, long quantity, long orderId)
    {
        if (price == 0 || quantity == 0) return false;

        PriceLevel[] levels = side == FixSide.Buy ? _bidLevels : _askLevels;
        int count = side == FixSide.Buy ? _bidCount : _askCount;

        int idx = FindOrCreateLevel(levels, ref count, price, isBid: side == FixSide.Buy);
        if (idx < 0 || idx >= MaxPriceLevels) return false;

        levels[idx].AddOrder(orderId, quantity);
        SetCount(side, count);

        return RefreshTopOfBook(side);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RemoveOrder(FixSide side, long price, long orderId)
    {
        PriceLevel[] levels = side == FixSide.Buy ? _bidLevels : _askLevels;
        int count = side == FixSide.Buy ? _bidCount : _askCount;

        int idx = FindLevel(levels, count, price);
        if (idx < 0) return false;

        levels[idx].RemoveOrder(orderId);

        if (levels[idx].TotalQuantity == 0)
        {
            ShiftLevelsDown(levels, ref count, idx);
            SetCount(side, count);
        }

        return RefreshTopOfBook(side);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ExecuteOrder(FixSide side, long price, long executedQty, long orderId)
    {
        PriceLevel[] levels = side == FixSide.Buy ? _bidLevels : _askLevels;
        int count = side == FixSide.Buy ? _bidCount : _askCount;

        int idx = FindLevel(levels, count, price);
        if (idx < 0) return false;

        levels[idx].ReduceQuantity(orderId, executedQty);
        return RefreshTopOfBook(side);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool UpdateLevel(FixSide side, long price, long newQuantity)
    {
        if (newQuantity == 0)
        {
            return RemoveLevelByPrice(side, price);
        }

        PriceLevel[] levels = side == FixSide.Buy ? _bidLevels : _askLevels;
        int count = side == FixSide.Buy ? _bidCount : _askCount;

        int idx = FindOrCreateLevel(levels, ref count, price, isBid: side == FixSide.Buy);
        if (idx < 0) return false;

        levels[idx].SetTotalQuantity(newQuantity);
        SetCount(side, count);

        return RefreshTopOfBook(side);
    }

    private bool RemoveLevelByPrice(FixSide side, long price)
    {
        PriceLevel[] levels = side == FixSide.Buy ? _bidLevels : _askLevels;
        int count = side == FixSide.Buy ? _bidCount : _askCount;

        int idx = FindLevel(levels, count, price);
        if (idx < 0) return false;

        ShiftLevelsDown(levels, ref count, idx);
        SetCount(side, count);

        return RefreshTopOfBook(side);
    }

    // ── Level array operations ──

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindLevel(PriceLevel[] levels, int count, long price)
    {
        for (int i = 0; i < count; i++)
        {
            if (levels[i].Price == price) return i;
        }
        return -1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int FindOrCreateLevel(PriceLevel[] levels, ref int count, long price, bool isBid)
    {
        int insertPos = 0;
        for (; insertPos < count; insertPos++)
        {
            long current = levels[insertPos].Price;
            if (current == price) return insertPos;
            if (isBid ? price > current : price < current) break;
        }

        if (count >= MaxPriceLevels) return -1;

        // Shift existing levels to make room at insertPos.
        for (int i = count; i > insertPos; i--)
        {
            levels[i].CopyFrom(levels[i - 1]);
        }

        levels[insertPos].Reset(price);
        count++;
        return insertPos;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ShiftLevelsDown(PriceLevel[] levels, ref int count, int removeIndex)
    {
        for (int i = removeIndex; i < count - 1; i++)
        {
            levels[i].CopyFrom(levels[i + 1]);
        }
        count--;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SetCount(FixSide side, int count)
    {
        if (side == FixSide.Buy) _bidCount = count;
        else _askCount = count;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool RefreshTopOfBook(FixSide side)
    {
        if (side == FixSide.Buy)
        {
            long prevPrice = BestBidPrice;
            long prevQty = BestBidQuantity;

            (BestBidPrice, BestBidQuantity) = _bidCount > 0
                ? (_bidLevels[0].Price, _bidLevels[0].TotalQuantity)
                : (0L, 0L);

            return prevPrice != BestBidPrice || prevQty != BestBidQuantity;
        }
        else
        {
            long prevPrice = BestAskPrice;
            long prevQty = BestAskQuantity;

            (BestAskPrice, BestAskQuantity) = _askCount > 0
                ? (_askLevels[0].Price, _askLevels[0].TotalQuantity)
                : (0L, 0L);

            return prevPrice != BestAskPrice || prevQty != BestAskQuantity;
        }
    }
}

/// <summary>
/// A single price level in the order book containing individual orders.
/// </summary>
public sealed class PriceLevel
{
    public long Price;
    public long TotalQuantity;
    private readonly Order[] _orders;
    private int _orderCount;

    public PriceLevel()
    {
        _orders = new Order[OrderBook.MaxOrdersPerLevel];
    }

    public void Reset(long price)
    {
        Price = price;
        TotalQuantity = 0;
        _orderCount = 0;
    }

    public void AddOrder(long orderId, long quantity)
    {
        if (_orderCount >= _orders.Length) return;
        _orders[_orderCount++] = new Order { OrderId = orderId, Quantity = quantity };
        TotalQuantity += quantity;
    }

    public void RemoveOrder(long orderId)
    {
        for (int i = 0; i < _orderCount; i++)
        {
            if (_orders[i].OrderId != orderId) continue;

            TotalQuantity -= _orders[i].Quantity;
            for (int j = i; j < _orderCount - 1; j++)
            {
                _orders[j] = _orders[j + 1];
            }
            _orderCount--;
            return;
        }
    }

    public void ReduceQuantity(long orderId, long reduceBy)
    {
        for (int i = 0; i < _orderCount; i++)
        {
            if (_orders[i].OrderId != orderId) continue;

            long newQty = Math.Max(0, _orders[i].Quantity - reduceBy);
            TotalQuantity -= (_orders[i].Quantity - newQty);
            _orders[i].Quantity = newQty;

            if (newQty == 0) RemoveOrder(orderId);
            return;
        }
    }

    public void SetTotalQuantity(long quantity)
    {
        TotalQuantity = quantity;
        _orderCount = 0;
    }

    public void CopyFrom(PriceLevel other)
    {
        Price = other.Price;
        TotalQuantity = other.TotalQuantity;
        _orderCount = other._orderCount;
        Array.Copy(other._orders, _orders, _orderCount);
    }

    private struct Order
    {
        public long OrderId;
        public long Quantity;
    }
}

/// <summary>
/// Immutable top-of-book snapshot for cold-path consumption. Value type for zero-allocation.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct BookSnapshot
{
    public int SymbolIndex;
    public long BestBidPrice;
    public long BestBidQuantity;
    public long BestAskPrice;
    public long BestAskQuantity;
    public long Timestamp;
    public long UpdateCount;
}
