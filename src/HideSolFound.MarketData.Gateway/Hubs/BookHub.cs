using HideSolFound.MarketData.Gateway.Models;
using HideSolFound.MarketData.Gateway.Services;
using Microsoft.AspNetCore.SignalR;

namespace HideSolFound.MarketData.Gateway.Hubs;

/// <summary>
/// SignalR hub for real-time order book distribution to connected clients.
/// Clients subscribe to symbol-specific groups and receive snapshot + incremental updates.
/// </summary>
public sealed class BookHub : Hub
{
    private readonly BookSnapshotManager _snapshotManager;
    private readonly ILogger<BookHub> _logger;

    public BookHub(BookSnapshotManager snapshotManager, ILogger<BookHub> logger)
    {
        _snapshotManager = snapshotManager;
        _logger = logger;
    }

    public async Task SubscribeToSymbol(string symbol)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, symbol);

        BookSnapshotDto? snapshot = _snapshotManager.GetLatestSnapshot(symbol);
        if (snapshot is not null)
        {
            await Clients.Caller.SendAsync("ReceiveSnapshot", snapshot);
        }

        _logger.LogDebug("Client {ConnectionId} subscribed to {Symbol}", Context.ConnectionId, symbol);
    }

    public async Task UnsubscribeFromSymbol(string symbol)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, symbol);
        _logger.LogDebug("Client {ConnectionId} unsubscribed from {Symbol}", Context.ConnectionId, symbol);
    }

    public async Task RequestSnapshot(string symbol)
    {
        BookSnapshotDto? snapshot = _snapshotManager.GetLatestSnapshot(symbol);
        if (snapshot is not null)
        {
            await Clients.Caller.SendAsync("ReceiveSnapshot", snapshot);
        }
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogDebug("Client connected: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogDebug("Client disconnected: {ConnectionId}", Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }
}

/// <summary>
/// Bridges <see cref="BookSnapshotManager"/> events to SignalR group broadcasts.
/// Registered as a hosted service to wire up event handlers at startup.
/// </summary>
public sealed class BookHubNotifier : IHostedService
{
    private readonly BookSnapshotManager _snapshotManager;
    private readonly IHubContext<BookHub> _hubContext;
    private readonly ILogger<BookHubNotifier> _logger;

    public BookHubNotifier(
        BookSnapshotManager snapshotManager,
        IHubContext<BookHub> hubContext,
        ILogger<BookHubNotifier> logger)
    {
        _snapshotManager = snapshotManager;
        _hubContext = hubContext;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _snapshotManager.SnapshotGenerated += OnSnapshot;
        _snapshotManager.IncrementalGenerated += OnIncremental;
        _logger.LogInformation("BookHub notifier started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _snapshotManager.SnapshotGenerated -= OnSnapshot;
        _snapshotManager.IncrementalGenerated -= OnIncremental;
        return Task.CompletedTask;
    }

    private void OnSnapshot(string symbol, BookSnapshotDto snapshot)
    {
        _ = _hubContext.Clients.Group(symbol).SendAsync("ReceiveSnapshot", snapshot);
    }

    private void OnIncremental(string symbol, BookIncrementalDto incremental)
    {
        _ = _hubContext.Clients.Group(symbol).SendAsync("ReceiveIncremental", incremental);
    }
}
