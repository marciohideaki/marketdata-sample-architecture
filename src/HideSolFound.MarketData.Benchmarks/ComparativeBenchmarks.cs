using BenchmarkDotNet.Attributes;
using HideSolFound.MarketData.Core;

namespace HideSolFound.MarketData.Benchmarks;

/// <summary>
/// Head-to-head comparison: lock-free ring buffer vs. lock-based Queue
/// demonstrating the throughput advantage of lock-free data structures.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class ComparativeBenchmarks
{
    private LockFreeRingBuffer<MarketDataTick> _lockFreeBuffer = null!;
    private Queue<MarketDataTick> _lockBasedQueue = null!;
    private readonly object _queueLock = new();
    private MarketDataTick _tick;

    [Params(10_000)]
    public int OperationCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _lockFreeBuffer = new LockFreeRingBuffer<MarketDataTick>(65536);
        _lockBasedQueue = new Queue<MarketDataTick>(65536);
        _tick = new MarketDataTick(1, 1, 2850, 1000, 123456789);
    }

    [Benchmark(Baseline = true, Description = "Lock-Free RingBuffer")]
    public int LockFreeRingBuffer_WriteRead()
    {
        _lockFreeBuffer.Reset();
        int ops = 0;

        for (int i = 0; i < OperationCount; i++)
        {
            _lockFreeBuffer.TryWrite(in _tick);
            ops++;
        }
        for (int i = 0; i < OperationCount; i++)
        {
            _lockFreeBuffer.TryRead(out _);
            ops++;
        }

        return ops;
    }

    [Benchmark(Description = "Lock-Based Queue")]
    public int LockBasedQueue_WriteRead()
    {
        lock (_queueLock) { _lockBasedQueue.Clear(); }

        int ops = 0;

        for (int i = 0; i < OperationCount; i++)
        {
            lock (_queueLock) { _lockBasedQueue.Enqueue(_tick); ops++; }
        }
        for (int i = 0; i < OperationCount; i++)
        {
            lock (_queueLock) { if (_lockBasedQueue.Count > 0) { _lockBasedQueue.Dequeue(); ops++; } }
        }

        return ops;
    }
}
