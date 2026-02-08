using HideSolFound.MarketData.Core;
using Xunit;

namespace HideSolFound.MarketData.Tests;

public sealed class OrderBookTests
{
    private static FixMessage CreateOrder(FixSide side, long price, long qty, long orderId) => new()
    {
        MsgType = FixMsgType.NewOrder,
        Side = side,
        Price = price,
        Quantity = qty,
        OrderId = orderId,
        DecodeTimestampNanos = 1,
    };

    [Fact]
    public void NewBidOrder_UpdatesBestBid()
    {
        var book = new OrderBook(0);
        var msg = CreateOrder(FixSide.Buy, 100, 50, 1);

        book.ApplyMessage(in msg);

        Assert.Equal(100, book.BestBidPrice);
        Assert.Equal(50, book.BestBidQuantity);
    }

    [Fact]
    public void NewAskOrder_UpdatesBestAsk()
    {
        var book = new OrderBook(0);
        var msg = CreateOrder(FixSide.Sell, 200, 30, 1);

        book.ApplyMessage(in msg);

        Assert.Equal(200, book.BestAskPrice);
        Assert.Equal(30, book.BestAskQuantity);
    }

    [Fact]
    public void MultipleBids_BestBidIsHighest()
    {
        var book = new OrderBook(0);
        book.ApplyMessage(CreateOrder(FixSide.Buy, 100, 10, 1));
        book.ApplyMessage(CreateOrder(FixSide.Buy, 150, 20, 2));
        book.ApplyMessage(CreateOrder(FixSide.Buy, 120, 30, 3));

        Assert.Equal(150, book.BestBidPrice);
        Assert.Equal(20, book.BestBidQuantity);
    }

    [Fact]
    public void MultipleAsks_BestAskIsLowest()
    {
        var book = new OrderBook(0);
        book.ApplyMessage(CreateOrder(FixSide.Sell, 200, 10, 1));
        book.ApplyMessage(CreateOrder(FixSide.Sell, 150, 20, 2));
        book.ApplyMessage(CreateOrder(FixSide.Sell, 180, 30, 3));

        Assert.Equal(150, book.BestAskPrice);
        Assert.Equal(20, book.BestAskQuantity);
    }

    [Fact]
    public void CancelOrder_RemovesFromBook()
    {
        var book = new OrderBook(0);
        book.ApplyMessage(CreateOrder(FixSide.Buy, 100, 50, 1));

        var cancel = new FixMessage
        {
            MsgType = FixMsgType.OrderCancel,
            Side = FixSide.Buy,
            Price = 100,
            OrderId = 1,
        };
        book.ApplyMessage(in cancel);

        Assert.Equal(0, book.BestBidPrice);
        Assert.Equal(0, book.BestBidQuantity);
    }

    [Fact]
    public void Execution_ReducesQuantity()
    {
        var book = new OrderBook(0);
        book.ApplyMessage(CreateOrder(FixSide.Buy, 100, 50, 1));

        var exec = new FixMessage
        {
            MsgType = FixMsgType.Execution,
            Side = FixSide.Buy,
            Price = 100,
            OrderId = 1,
            TradeQuantity = 30,
        };
        book.ApplyMessage(in exec);

        Assert.Equal(100, book.BestBidPrice);
        Assert.Equal(20, book.BestBidQuantity);
    }

    [Fact]
    public void GetSnapshot_ReturnsCurrentState()
    {
        var book = new OrderBook(5);
        book.ApplyMessage(CreateOrder(FixSide.Buy, 100, 50, 1));
        book.ApplyMessage(CreateOrder(FixSide.Sell, 200, 30, 2));

        BookSnapshot snapshot = book.GetSnapshot();

        Assert.Equal(5, snapshot.SymbolIndex);
        Assert.Equal(100, snapshot.BestBidPrice);
        Assert.Equal(50, snapshot.BestBidQuantity);
        Assert.Equal(200, snapshot.BestAskPrice);
        Assert.Equal(30, snapshot.BestAskQuantity);
        Assert.Equal(2, snapshot.UpdateCount);
    }

    [Fact]
    public void IncrementalRefresh_UpdatesLevel()
    {
        var book = new OrderBook(0);
        var refresh = new FixMessage
        {
            MsgType = FixMsgType.IncrementalRefresh,
            Side = FixSide.Buy,
            Price = 100,
            Quantity = 500,
        };

        book.ApplyMessage(in refresh);

        Assert.Equal(100, book.BestBidPrice);
        Assert.Equal(500, book.BestBidQuantity);
    }
}
