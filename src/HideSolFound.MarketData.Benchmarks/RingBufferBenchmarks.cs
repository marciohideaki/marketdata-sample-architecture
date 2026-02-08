using BenchmarkDotNet.Attributes;
using HideSolFound.MarketData.Core;

namespace HideSolFound.MarketData.Benchmarks;

/// <summary>
/// Micro-benchmarks for LockFreeRingBuffer measuring per-operation latency
/// and throughput across different buffer sizes.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class RingBufferBenchmarks
{
    private LockFreeRingBuffer<MarketDataTick> _buffer = null!;
    private MarketDataTick _tick;

    [Params(1024, 4096, 16384, 65536)]
    public int BufferSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new LockFreeRingBuffer<MarketDataTick>(BufferSize);
        _tick = new MarketDataTick(1, 1, 2850, 1000, 123456789);
    }

    [Benchmark(Description = "Single Write")]
    public bool SingleWrite() => _buffer.TryWrite(in _tick);

    [Benchmark(Description = "Single Read")]
    public bool SingleRead()
    {
        _buffer.TryWrite(in _tick);
        return _buffer.TryRead(out _);
    }

    [Benchmark(Description = "Write-Read Pair")]
    public bool WriteReadPair()
    {
        _buffer.TryWrite(in _tick);
        return _buffer.TryRead(out _);
    }

    [Benchmark(Description = "Burst Write (Fill Buffer)")]
    public int BurstWrite()
    {
        _buffer.Reset();
        int written = 0;
        for (int i = 0; i < BufferSize; i++)
        {
            if (_buffer.TryWrite(in _tick)) written++;
            else break;
        }
        return written;
    }

    [Benchmark(Description = "Burst Read (Drain Buffer)")]
    public int BurstRead()
    {
        _buffer.Reset();
        for (int i = 0; i < BufferSize; i++) _buffer.TryWrite(in _tick);

        int read = 0;
        while (_buffer.TryRead(out _)) read++;
        return read;
    }

    [Benchmark(Description = "Alternating Write/Read (10k ops)")]
    public int AlternatingWriteRead()
    {
        _buffer.Reset();
        int ops = 0;
        for (int i = 0; i < 10_000; i++)
        {
            if (_buffer.TryWrite(in _tick)) ops++;
            if (_buffer.TryRead(out _)) ops++;
        }
        return ops;
    }
}
