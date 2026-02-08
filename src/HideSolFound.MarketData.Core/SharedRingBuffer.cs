using System.IO.MemoryMappedFiles;
using System.Runtime.Versioning;

namespace HideSolFound.MarketData.Core;

/// <summary>
/// Ring buffer backed by OS shared memory for inter-process communication (IPC).
/// <para>
/// Uses <see cref="MemoryMappedFile"/> to allow a producer process and a consumer process
/// to exchange <see cref="MarketDataTick"/> messages through a memory-mapped region visible
/// to both processes without kernel-mode transitions for each message.
/// </para>
/// </summary>
/// <remarks>
/// Memory layout:
/// <code>
/// [0..7]   WritePosition (int64)
/// [8..15]  ReadPosition  (int64)
/// [16..]   Tick data slots (each TickSize bytes)
/// </code>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SharedRingBuffer : IDisposable
{
    private const string DefaultSharedMemoryName = "HideSolFound_MarketData_SharedBuffer";
    private const int DefaultCapacity = 8192; // Must be power of 2
    private const int TickSize = 40; // sizeof(MarketDataTick): 8+4+8+8+8 = 36, rounded to 40 for alignment

    private const long WritePositionOffset = 0;
    private const long ReadPositionOffset = 8;
    private const long DataOffset = 16;

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly bool _isProducer;
    private readonly int _capacity;
    private readonly int _mask;
    private bool _disposed;

    /// <summary>
    /// Creates or opens a shared-memory ring buffer.
    /// </summary>
    /// <param name="isProducer">
    /// <c>true</c> if this instance is the producer (writer); <c>false</c> for consumer (reader).
    /// </param>
    /// <param name="create">
    /// <c>true</c> to create the shared memory region (producer should set this);
    /// <c>false</c> to open an existing one (consumer).
    /// </param>
    /// <param name="name">Optional custom name for the shared memory region.</param>
    /// <param name="capacity">Buffer capacity (must be power of 2). Defaults to 8192.</param>
    public SharedRingBuffer(bool isProducer, bool create = false, string? name = null, int capacity = DefaultCapacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, "Capacity must be a positive power of 2.");
        }

        _isProducer = isProducer;
        _capacity = capacity;
        _mask = capacity - 1;

        string memoryName = name ?? DefaultSharedMemoryName;
        long bufferSize = DataOffset + ((long)capacity * TickSize);

        if (create)
        {
            _mmf = MemoryMappedFile.CreateOrOpen(memoryName, bufferSize, MemoryMappedFileAccess.ReadWrite);
            _accessor = _mmf.CreateViewAccessor();

            // Initialize sequence counters to zero.
            _accessor.Write(WritePositionOffset, 0L);
            _accessor.Write(ReadPositionOffset, 0L);
        }
        else
        {
            _mmf = MemoryMappedFile.OpenExisting(memoryName, MemoryMappedFileRights.ReadWrite);
            _accessor = _mmf.CreateViewAccessor();
        }
    }

    /// <summary>Gets the buffer capacity in number of ticks.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Writes a tick into the shared buffer. Must be called from the producer process only.
    /// </summary>
    /// <returns><c>true</c> if written; <c>false</c> if the buffer is full.</returns>
    /// <exception cref="InvalidOperationException">Thrown when called from the consumer side.</exception>
    public bool TryWrite(in MarketDataTick tick)
    {
        if (!_isProducer)
        {
            throw new InvalidOperationException("Only the producer process can write to the shared buffer.");
        }

        long writePos = _accessor.ReadInt64(WritePositionOffset);
        long readPos = _accessor.ReadInt64(ReadPositionOffset);

        if (writePos - readPos >= _capacity)
        {
            return false; // Buffer full.
        }

        int index = (int)(writePos & _mask);
        long offset = DataOffset + ((long)index * TickSize);

        _accessor.Write(offset, tick.SequenceNumber);
        _accessor.Write(offset + 8, tick.SymbolId);
        _accessor.Write(offset + 12, tick.Price);
        _accessor.Write(offset + 20, tick.Volume);
        _accessor.Write(offset + 28, tick.TimestampNanos);

        // Publish new write position after data is written.
        _accessor.Write(WritePositionOffset, writePos + 1);

        return true;
    }

    /// <summary>
    /// Reads a tick from the shared buffer. Must be called from the consumer process only.
    /// </summary>
    /// <returns><c>true</c> if a tick was read; <c>false</c> if the buffer is empty.</returns>
    /// <exception cref="InvalidOperationException">Thrown when called from the producer side.</exception>
    public bool TryRead(out MarketDataTick tick)
    {
        if (_isProducer)
        {
            throw new InvalidOperationException("Only the consumer process can read from the shared buffer.");
        }

        long writePos = _accessor.ReadInt64(WritePositionOffset);
        long readPos = _accessor.ReadInt64(ReadPositionOffset);

        if (readPos >= writePos)
        {
            tick = default;
            return false; // Buffer empty.
        }

        int index = (int)(readPos & _mask);
        long offset = DataOffset + ((long)index * TickSize);

        tick = new MarketDataTick(
            sequenceNumber: _accessor.ReadInt64(offset),
            symbolId: _accessor.ReadInt32(offset + 8),
            price: _accessor.ReadInt64(offset + 12),
            volume: _accessor.ReadInt64(offset + 20),
            timestampNanos: _accessor.ReadInt64(offset + 28));

        // Publish new read position after data is consumed.
        _accessor.Write(ReadPositionOffset, readPos + 1);

        return true;
    }

    /// <summary>Gets the approximate number of items available to read.</summary>
    public long AvailableToRead
    {
        get
        {
            long writePos = _accessor.ReadInt64(WritePositionOffset);
            long readPos = _accessor.ReadInt64(ReadPositionOffset);
            return writePos - readPos;
        }
    }

    /// <summary>Gets the approximate number of slots available for writing.</summary>
    public long AvailableToWrite
    {
        get
        {
            long writePos = _accessor.ReadInt64(WritePositionOffset);
            long readPos = _accessor.ReadInt64(ReadPositionOffset);
            return _capacity - (writePos - readPos);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _accessor.Dispose();
        _mmf.Dispose();
    }
}
