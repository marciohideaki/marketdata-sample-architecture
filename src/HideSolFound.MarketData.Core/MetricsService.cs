using System.Collections.Concurrent;
using StackExchange.Redis;

namespace HideSolFound.MarketData.Core;

/// <summary>
/// Pipeline metrics collection service with two operating modes:
/// <list type="bullet">
///   <item><b>Hot-path:</b> Synchronous Redis writes per metric (high accuracy, ~98% throughput loss)</item>
///   <item><b>Outside hot-path:</b> Buffered async flush (low impact, ~10% overhead)</item>
/// </list>
/// This dual-mode design demonstrates the performance cost of I/O on the hot path
/// versus the minimal overhead of async buffered collection.
/// </summary>
public sealed class MetricsService : IDisposable
{
    private readonly MetricsConfig _config;
    private readonly ConnectionMultiplexer? _redis;
    private readonly IDatabase? _db;
    private readonly ConcurrentQueue<MetricEntry> _buffer = new();
    private readonly Timer? _flushTimer;
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    private long _totalCollected;
    private long _hotPathCount;
    private long _outsideHotPathCount;
    private volatile bool _disposing;

    public MetricsService(MetricsConfig config)
    {
        _config = config;

        if (!config.Enabled) return;

        try
        {
            _redis = ConnectionMultiplexer.Connect(config.RedisConnection);
            _db = _redis.GetDatabase();

            if (config.CollectOutsideHotPath)
            {
                _flushTimer = new Timer(
                    FlushBuffer, null,
                    TimeSpan.FromMilliseconds(config.FlushIntervalMs),
                    TimeSpan.FromMilliseconds(config.FlushIntervalMs));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[METRICS] Redis connection failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Records a metric synchronously on the hot path.
    /// Each call performs a blocking Redis write — use only for diagnostic scenarios.
    /// </summary>
    public void RecordInHotPath(string name, long value, long timestampNanos)
    {
        if (!_config.Enabled || !_config.CollectInHotPath || _db is null) return;

        try
        {
            _db.SortedSetAdd($"metrics:hotpath:{name}", value.ToString(), timestampNanos / 1_000_000.0);
            Interlocked.Increment(ref _hotPathCount);
            Interlocked.Increment(ref _totalCollected);
        }
        catch { /* Hot-path: swallow to avoid disrupting the pipeline */ }
    }

    /// <summary>
    /// Enqueues a metric for asynchronous flush. Zero blocking, zero I/O on the calling thread.
    /// </summary>
    public void RecordOutsideHotPath(string name, long value, long timestampNanos)
    {
        if (!_config.Enabled || !_config.CollectOutsideHotPath) return;

        _buffer.Enqueue(new MetricEntry { Name = name, Value = value, TimestampNanos = timestampNanos });
        Interlocked.Increment(ref _outsideHotPathCount);
        Interlocked.Increment(ref _totalCollected);

        if (_buffer.Count >= _config.BatchSize)
        {
            Task.Run(() => FlushBuffer(null));
        }
    }

    public MetricsStats GetStats() => new()
    {
        TotalCollected = _totalCollected,
        HotPathCount = _hotPathCount,
        OutsideHotPathCount = _outsideHotPathCount,
        BufferedCount = _buffer.Count,
    };

    private void FlushBuffer(object? state)
    {
        if (_disposing || _db is null || _buffer.IsEmpty) return;
        if (!_flushLock.Wait(0)) return;

        try
        {
            var tasks = new List<Task>();
            int flushed = 0;

            while (flushed < _config.BatchSize && _buffer.TryDequeue(out MetricEntry entry))
            {
                string key = $"metrics:outside:{entry.Name}";
                tasks.Add(_db.SortedSetAddAsync(key, entry.Value.ToString(), entry.TimestampNanos / 1_000_000.0));
                flushed++;

                if (tasks.Count >= 50)
                {
                    Task.WaitAll([.. tasks], TimeSpan.FromSeconds(30));
                    tasks.Clear();
                }
            }

            if (tasks.Count > 0)
            {
                Task.WaitAll([.. tasks], TimeSpan.FromSeconds(30));
            }
        }
        catch { /* Background flush: swallow and retry on next interval */ }
        finally
        {
            _flushLock.Release();
        }
    }

    public void Dispose()
    {
        _disposing = true;
        _flushTimer?.Dispose();

        // Final flush of remaining buffered metrics.
        _flushLock.Wait();
        try
        {
            if (_db is not null)
            {
                var tasks = new List<Task>();
                while (_buffer.TryDequeue(out MetricEntry entry))
                {
                    tasks.Add(_db.SortedSetAddAsync($"metrics:outside:{entry.Name}", entry.Value.ToString(), entry.TimestampNanos / 1_000_000.0));
                    if (tasks.Count >= 100)
                    {
                        Task.WaitAll([.. tasks], TimeSpan.FromSeconds(30));
                        tasks.Clear();
                    }
                }
                if (tasks.Count > 0) Task.WaitAll([.. tasks], TimeSpan.FromSeconds(30));
            }
        }
        finally { _flushLock.Release(); }

        _redis?.Dispose();
        _flushLock.Dispose();
    }

    private struct MetricEntry
    {
        public string Name;
        public long Value;
        public long TimestampNanos;
    }
}

public sealed class MetricsStats
{
    public long TotalCollected { get; init; }
    public long HotPathCount { get; init; }
    public long OutsideHotPathCount { get; init; }
    public long BufferedCount { get; init; }
}
