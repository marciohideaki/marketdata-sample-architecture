using System.Diagnostics;
using HideSolFound.MarketData.Core;

namespace HideSolFound.MarketData.Gateway.Services;

/// <summary>
/// Controls a synthetic market data feed for demonstration and testing.
/// Generates realistic FIX messages at configurable throughput rates.
/// </summary>
public sealed class FeedControlService
{
    private readonly MarketDataPipeline _pipeline;
    private readonly SymbolMapper _symbolMapper;
    private readonly BookSnapshotManager _snapshotManager;
    private readonly ILogger<FeedControlService> _logger;
    private readonly long _targetMessagesPerSecond;
    private readonly int _throttleBatchSize;

    private CancellationTokenSource? _feedCts;
    private Task? _feedTask;
    private readonly Random _random = new();

    public FeedState State { get; private set; } = FeedState.Stopped;
    public long TotalMessagesSent { get; private set; }
    public DateTime? StartedAt { get; private set; }

    public FeedControlService(
        MarketDataPipeline pipeline,
        SymbolMapper symbolMapper,
        BookSnapshotManager snapshotManager,
        IConfiguration configuration,
        ILogger<FeedControlService> logger)
    {
        _pipeline = pipeline;
        _symbolMapper = symbolMapper;
        _snapshotManager = snapshotManager;
        _logger = logger;

        _targetMessagesPerSecond = configuration.GetValue("FeedSettings:TargetMessagesPerSecond", 10_000_000L);
        _throttleBatchSize = configuration.GetValue("FeedSettings:ThrottleBatchSize", 10);
    }

    public void StartFeed()
    {
        if (State == FeedState.Running) return;

        _feedCts = new CancellationTokenSource();
        _pipeline.Start();

        State = FeedState.Running;
        StartedAt = DateTime.UtcNow;
        TotalMessagesSent = 0;

        _feedTask = Task.Run(() => RunFeedAsync(_feedCts.Token));
        _logger.LogInformation("Feed started at {TargetRate:N0} msg/s target", _targetMessagesPerSecond);
    }

    public void StopFeed()
    {
        if (State != FeedState.Running) return;

        _feedCts?.Cancel();
        _feedTask?.Wait(TimeSpan.FromSeconds(5));
        _pipeline.Stop();

        State = FeedState.Stopped;
        _logger.LogInformation("Feed stopped after {Total:N0} messages", TotalMessagesSent);
    }

    public async Task ResetAsync()
    {
        State = FeedState.Resetting;
        StopFeed();

        foreach (string symbol in _symbolMapper.AllSymbols)
        {
            int? idx = _symbolMapper.GetSymbolIndex(symbol);
            if (idx is null) continue;

            OrderBook? book = _pipeline.GetOrderBook(idx.Value);
            if (book is null) continue;

            BookSnapshot snapshot = book.GetSnapshot();
            _snapshotManager.OnBookUpdate(symbol, snapshot);
        }

        await Task.Delay(100);
        StartFeed();
    }

    public double GetThroughput()
    {
        if (StartedAt is null || State != FeedState.Running) return 0;
        double elapsed = (DateTime.UtcNow - StartedAt.Value).TotalSeconds;
        return elapsed > 0 ? TotalMessagesSent / elapsed : 0;
    }

    private async Task RunFeedAsync(CancellationToken ct)
    {
        int batchCount = 0;
        long seqNum = 1;
        int[] symbolIndices = [0, 1]; // PETR4, VALE3

        while (!ct.IsCancellationRequested)
        {
            int symbolIdx = symbolIndices[_random.Next(symbolIndices.Length)];
            int roll = _random.Next(100);

            FixMsgType msgType = roll switch
            {
                < 70 => FixMsgType.NewOrder,
                < 90 => FixMsgType.Execution,
                _ => FixMsgType.IncrementalRefresh,
            };

            var msg = new FixMessage
            {
                MsgSeqNum = seqNum++,
                MsgType = msgType,
                SecurityId = symbolIdx,
                SymbolIndex = symbolIdx,
                Side = _random.Next(2) == 0 ? FixSide.Buy : FixSide.Sell,
                Price = FixMessage.ToFixedPointPrice(20.0 + _random.NextDouble() * 10.0),
                Quantity = (_random.Next(1, 100)) * 100,
                OrderId = seqNum,
                SendingTime = HighResolutionTimestamp.Now,
            };

            if (msgType == FixMsgType.Execution)
            {
                msg.TradeId = seqNum;
                msg.TradePrice = msg.Price;
                msg.TradeQuantity = msg.Quantity / 2;
            }

            _pipeline.InjectFixMessage(in msg);
            TotalMessagesSent++;
            batchCount++;

            if (batchCount >= _throttleBatchSize)
            {
                batchCount = 0;
                await Task.Yield();
            }
        }
    }
}

public enum FeedState
{
    Stopped,
    Running,
    Resetting,
}
