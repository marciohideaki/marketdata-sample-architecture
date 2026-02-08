using HideSolFound.MarketData.Core;

namespace HideSolFound.MarketData.Gateway.Services;

/// <summary>
/// Background service that drains the pipeline's cold-path ring buffer and forwards
/// snapshots to the <see cref="BookSnapshotManager"/> for persistence and distribution.
/// </summary>
public sealed class PipelineColdPathService : BackgroundService
{
    private readonly MarketDataPipeline _pipeline;
    private readonly BookSnapshotManager _snapshotManager;
    private readonly SymbolMapper _symbolMapper;
    private readonly ILogger<PipelineColdPathService> _logger;

    public PipelineColdPathService(
        MarketDataPipeline pipeline,
        BookSnapshotManager snapshotManager,
        SymbolMapper symbolMapper,
        ILogger<PipelineColdPathService> logger)
    {
        _pipeline = pipeline;
        _snapshotManager = snapshotManager;
        _symbolMapper = symbolMapper;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Cold-path consumer started");

        while (!stoppingToken.IsCancellationRequested)
        {
            int processed = 0;

            while (_pipeline.TryReadColdPathSnapshot(out BookSnapshot snapshot))
            {
                string? symbol = _symbolMapper.GetSymbolName(snapshot.SymbolIndex);
                if (symbol is not null)
                {
                    _snapshotManager.OnBookUpdate(symbol, snapshot);
                }

                if (++processed >= 100)
                {
                    processed = 0;
                    await Task.Yield();
                }
            }

            await Task.Delay(1, stoppingToken);
        }
    }
}
