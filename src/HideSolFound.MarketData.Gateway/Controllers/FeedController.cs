using System.Diagnostics;
using HideSolFound.MarketData.Gateway.Services;
using Microsoft.AspNetCore.Mvc;

namespace HideSolFound.MarketData.Gateway.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class FeedController : ControllerBase
{
    private readonly FeedControlService _feedService;

    public FeedController(FeedControlService feedService)
    {
        _feedService = feedService;
    }

    [HttpGet("status")]
    public IActionResult GetStatus() => Ok(new
    {
        state = _feedService.State.ToString(),
        totalMessages = _feedService.TotalMessagesSent,
        uptime = _feedService.StartedAt is not null
            ? (DateTime.UtcNow - _feedService.StartedAt.Value).ToString(@"hh\:mm\:ss")
            : "N/A",
        throughput = $"{_feedService.GetThroughput():N0} msg/s",
    });

    [HttpGet("metrics")]
    public IActionResult GetMetrics()
    {
        using var process = Process.GetCurrentProcess();
        return Ok(new
        {
            workingSetMb = process.WorkingSet64 / (1024.0 * 1024.0),
            gcGen0 = GC.CollectionCount(0),
            gcGen1 = GC.CollectionCount(1),
            gcGen2 = GC.CollectionCount(2),
            totalMessages = _feedService.TotalMessagesSent,
            throughput = $"{_feedService.GetThroughput():N0} msg/s",
        });
    }

    [HttpPost("start")]
    public IActionResult Start()
    {
        _feedService.StartFeed();
        return Ok(new { message = "Feed started" });
    }

    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _feedService.StopFeed();
        return Ok(new { message = "Feed stopped" });
    }

    [HttpPost("reset")]
    public async Task<IActionResult> Reset()
    {
        await _feedService.ResetAsync();
        return Ok(new { message = "Feed reset complete" });
    }
}
