namespace HideSolFound.MarketData.Core;

/// <summary>
/// Configuration for the pipeline metrics collection system.
/// Controls whether metrics are collected synchronously (hot-path, high impact)
/// or asynchronously (buffered, minimal impact).
/// </summary>
public sealed class MetricsConfig
{
    /// <summary>Master switch for all metrics collection.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Collect metrics synchronously on the hot path (blocks the ring buffer).
    /// WARNING: Causes ~98% throughput degradation. Use only for diagnostics.
    /// </summary>
    public bool CollectInHotPath { get; set; }

    /// <summary>
    /// Collect metrics asynchronously via a buffered flush timer (~10% overhead).
    /// Recommended for production monitoring.
    /// </summary>
    public bool CollectOutsideHotPath { get; set; } = true;

    /// <summary>Redis connection string for metrics storage.</summary>
    public string RedisConnection { get; set; } = "localhost:6379";

    /// <summary>Number of metric entries to buffer before flushing.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Interval between automatic flushes in milliseconds.</summary>
    public int FlushIntervalMs { get; set; } = 1000;
}
