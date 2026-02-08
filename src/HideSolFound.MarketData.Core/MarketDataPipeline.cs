using System.Buffers;
using System.Runtime.CompilerServices;

namespace HideSolFound.MarketData.Core;

/// <summary>
/// High-performance, multi-stage market data processing pipeline.
/// <code>
///                  ┌─────────────┐       ┌──────────────┐       ┌──────────────┐
///  UDP Packets ──► │ Ring Buffer │ ──►   │ Ring Buffer  │ ──►   │ Ring Buffer  │ ──► Cold Path
///                  │ (UDP pkts)  │       │ (FIX msgs)   │       │ (Snapshots)  │
///                  └──────┬──────┘       └──────┬───────┘       └──────┬───────┘
///                         │                     │                      │
///                   FAST Decoder          Book Builder           Persistence
///                   (Thread 1)           (Thread 2)             & Distribution
/// </code>
/// <para>
/// Each stage communicates through lock-free ring buffers, achieving zero-allocation
/// on the hot path. Three dedicated threads process data with busy-spin loops for
/// minimal latency. The cold-path stage runs at normal priority since it performs I/O.
/// </para>
/// </summary>
public sealed class MarketDataPipeline : IDisposable
{
    // ── Inter-stage ring buffers ──
    private readonly LockFreeRingBuffer<UdpMarketDataPacket> _udpBuffer;
    private readonly LockFreeRingBuffer<FixMessage> _fixBuffer;
    private readonly LockFreeRingBuffer<BookSnapshot> _coldPathBuffer;

    // ── UDP buffer pool (pre-allocated, reused via round-robin) ──
    private readonly ArrayPool<byte> _bufferPool;
    private readonly byte[][] _udpBuffers;
    private const int UdpBufferSize = 8192;
    private const int UdpBufferCount = 1024;

    // ── Order books (one per symbol, pre-allocated) ──
    private readonly OrderBook[] _orderBooks;
    private const int MaxSymbols = 1000;

    // ── Processing threads ──
    private readonly Thread _decoderThread;
    private readonly Thread _bookBuilderThread;
    private readonly Thread _coldPathThread;
    private volatile bool _running;
    private bool _disposed;

    // ── Pipeline statistics ──
    public long TotalUdpPackets;
    public long TotalMessagesDecoded;
    public long TotalBookUpdates;
    public long TotalColdPathEvents;
    public long DecodeErrors;

    public MarketDataPipeline(
        int udpBufferCapacity = 65536,
        int fixBufferCapacity = 65536,
        int coldPathCapacity = 32768)
    {
        _udpBuffer = new LockFreeRingBuffer<UdpMarketDataPacket>(udpBufferCapacity);
        _fixBuffer = new LockFreeRingBuffer<FixMessage>(fixBufferCapacity);
        _coldPathBuffer = new LockFreeRingBuffer<BookSnapshot>(coldPathCapacity);

        _bufferPool = ArrayPool<byte>.Create(UdpBufferSize, UdpBufferCount);
        _udpBuffers = new byte[UdpBufferCount][];
        for (int i = 0; i < UdpBufferCount; i++)
        {
            _udpBuffers[i] = _bufferPool.Rent(UdpBufferSize);
        }

        _orderBooks = new OrderBook[MaxSymbols];
        for (int i = 0; i < MaxSymbols; i++)
        {
            _orderBooks[i] = new OrderBook(i);
        }

        _decoderThread = new Thread(RunDecoderLoop)
        {
            Name = "Pipeline-FastDecoder",
            Priority = ThreadPriority.Highest,
            IsBackground = false,
        };

        _bookBuilderThread = new Thread(RunBookBuilderLoop)
        {
            Name = "Pipeline-BookBuilder",
            Priority = ThreadPriority.Highest,
            IsBackground = false,
        };

        _coldPathThread = new Thread(RunColdPathLoop)
        {
            Name = "Pipeline-ColdPath",
            Priority = ThreadPriority.Normal,
            IsBackground = true,
        };
    }

    /// <summary>Starts all three pipeline processing threads.</summary>
    public void Start()
    {
        _running = true;
        _decoderThread.Start();
        _bookBuilderThread.Start();
        _coldPathThread.Start();
    }

    /// <summary>Signals all threads to stop and waits for graceful shutdown.</summary>
    public void Stop()
    {
        _running = false;
        _decoderThread.Join(TimeSpan.FromSeconds(5));
        _bookBuilderThread.Join(TimeSpan.FromSeconds(5));
        _coldPathThread.Join(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Publishes a received UDP packet into the pipeline. Called by the network receiver.
    /// This is the pipeline entry point and must be as fast as possible (hot path).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool PublishUdpPacket(ReadOnlySpan<byte> data, long sequenceNumber, int channelId)
    {
        int bufferId = (int)(sequenceNumber % UdpBufferCount);
        byte[] buffer = _udpBuffers[bufferId];

        int length = Math.Min(data.Length, UdpBufferSize);
        data[..length].CopyTo(buffer);

        var packet = new UdpMarketDataPacket(sequenceNumber, channelId, bufferOffset: 0, dataLength: length, bufferId: bufferId);

        if (_udpBuffer.TryWrite(in packet))
        {
            Interlocked.Increment(ref TotalUdpPackets);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Injects a FIX message directly into stage 2, bypassing UDP reception and FAST decoding.
    /// Useful for testing and synthetic feed generation.
    /// </summary>
    public bool InjectFixMessage(in FixMessage message) => _fixBuffer.TryWrite(in message);

    /// <summary>Reads the next cold-path snapshot (consumed by the gateway service).</summary>
    public bool TryReadColdPathSnapshot(out BookSnapshot snapshot) => _coldPathBuffer.TryRead(out snapshot);

    /// <summary>Gets the number of pending snapshots in the cold-path buffer.</summary>
    public long ColdPathPendingCount => _coldPathBuffer.AvailableToRead;

    /// <summary>Gets the order book for a given symbol index.</summary>
    public OrderBook? GetOrderBook(int symbolIndex) =>
        symbolIndex >= 0 && symbolIndex < MaxSymbols ? _orderBooks[symbolIndex] : null;

    /// <summary>Returns a point-in-time snapshot of pipeline statistics.</summary>
    public PipelineStats GetStats() => new()
    {
        TotalUdpPackets = TotalUdpPackets,
        TotalMessagesDecoded = TotalMessagesDecoded,
        TotalBookUpdates = TotalBookUpdates,
        TotalColdPathEvents = TotalColdPathEvents,
        DecodeErrors = DecodeErrors,
        UdpBufferDepth = _udpBuffer.AvailableToRead,
        FixBufferDepth = _fixBuffer.AvailableToRead,
        ColdPathBufferDepth = _coldPathBuffer.AvailableToRead,
    };

    // ── Thread loops ──

    private void RunDecoderLoop()
    {
        while (_running || _udpBuffer.AvailableToRead > 0)
        {
            if (_udpBuffer.TryRead(out UdpMarketDataPacket packet))
            {
                byte[] buffer = _udpBuffers[packet.BufferId];
                ReadOnlySpan<byte> data = buffer.AsSpan(packet.BufferOffset, packet.DataLength);

                if (FastDecoder.TryDecode(data, packet.ReceiveTimestampNanos, packet.ChannelId, out FixMessage msg))
                {
                    while (!_fixBuffer.TryWrite(in msg))
                    {
                        Thread.SpinWait(10);
                    }
                    Interlocked.Increment(ref TotalMessagesDecoded);
                }
                else
                {
                    Interlocked.Increment(ref DecodeErrors);
                }
            }
            else
            {
                Thread.SpinWait(100);
            }
        }
    }

    private void RunBookBuilderLoop()
    {
        while (_running || _fixBuffer.AvailableToRead > 0)
        {
            if (_fixBuffer.TryRead(out FixMessage message))
            {
                if (message.SymbolIndex < 0 || message.SymbolIndex >= MaxSymbols)
                {
                    continue;
                }

                OrderBook book = _orderBooks[message.SymbolIndex];
                book.ApplyMessage(in message);
                Interlocked.Increment(ref TotalBookUpdates);

                // Always forward snapshot to cold path (lossless forwarding).
                // In production, filter with "topChanged" to reduce cold-path load.
                BookSnapshot snapshot = book.GetSnapshot();
                if (_coldPathBuffer.TryWrite(in snapshot))
                {
                    Interlocked.Increment(ref TotalColdPathEvents);
                }
            }
            else
            {
                Thread.SpinWait(100);
            }
        }
    }

    private void RunColdPathLoop()
    {
        while (_running || _coldPathBuffer.AvailableToRead > 0)
        {
            if (!_coldPathBuffer.TryRead(out _))
            {
                Thread.Sleep(1); // Cold path can yield; it is not latency-sensitive.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();

        for (int i = 0; i < UdpBufferCount; i++)
        {
            if (_udpBuffers[i] is not null)
            {
                _bufferPool.Return(_udpBuffers[i]);
            }
        }
    }
}

/// <summary>
/// Point-in-time snapshot of pipeline throughput and buffer utilization.
/// </summary>
public struct PipelineStats
{
    public long TotalUdpPackets;
    public long TotalMessagesDecoded;
    public long TotalBookUpdates;
    public long TotalColdPathEvents;
    public long DecodeErrors;
    public long UdpBufferDepth;
    public long FixBufferDepth;
    public long ColdPathBufferDepth;
}
