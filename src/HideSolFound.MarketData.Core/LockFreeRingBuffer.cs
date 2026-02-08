using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HideSolFound.MarketData.Core;

/// <summary>
/// Cache-line padded 64-bit integer to prevent false sharing between producer and consumer threads.
/// Modern x86/x64 CPUs use 64-byte cache lines; placing each counter on its own line eliminates
/// cross-core cache invalidation overhead that would otherwise destroy throughput.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = CacheLine.Size)]
internal struct PaddedLong
{
    [FieldOffset(0)]
    public long Value;
}

/// <summary>
/// Constants for CPU cache-line alignment.
/// </summary>
internal static class CacheLine
{
    /// <summary>
    /// Standard cache-line size for x86/x64 architectures (64 bytes).
    /// </summary>
    public const int Size = 64;
}

/// <summary>
/// Lock-free, zero-allocation ring buffer optimized for Single Producer, Single Consumer (SPSC) scenarios.
/// <para>
/// This implementation uses memory barriers (<see cref="Volatile"/>) instead of locks for thread safety,
/// achieving sub-20ns per operation latency. The design relies on three key performance techniques:
/// </para>
/// <list type="bullet">
///   <item><description>Cache-line padding on sequence counters to prevent false sharing</description></item>
///   <item><description>Cached position snapshots to minimize cross-thread volatile reads</description></item>
///   <item><description>Power-of-2 capacity with bitwise masking for branchless index calculation</description></item>
/// </list>
/// </summary>
/// <typeparam name="T">Value type (struct) to store. Using structs avoids heap allocations and GC pressure.</typeparam>
public sealed class LockFreeRingBuffer<T> where T : struct
{
    private readonly T[] _buffer;
    private readonly int _mask;
    private readonly int _capacity;

    // Each position counter sits on its own cache line to prevent false sharing.
    // Producer writes _writePosition; consumer writes _readPosition.
    private PaddedLong _writePosition;
    private PaddedLong _readPosition;

    // Cached snapshots reduce expensive volatile reads across thread boundaries.
    // The producer caches the consumer's read position; the consumer caches the producer's write position.
    // These are only refreshed when the stale value suggests the buffer is full/empty.
    private PaddedLong _cachedReadPosition;
    private PaddedLong _cachedWritePosition;

    /// <summary>
    /// Initializes a new ring buffer with the specified capacity.
    /// </summary>
    /// <param name="capacity">
    /// Buffer capacity. Must be a power of 2 (e.g., 1024, 4096, 65536) to enable
    /// branchless index calculation via bitwise AND instead of modulo.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when capacity is not a positive power of 2.</exception>
    public LockFreeRingBuffer(int capacity)
    {
        if (capacity <= 0 || (capacity & (capacity - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity),
                capacity,
                "Capacity must be a positive power of 2 (e.g., 1024, 4096, 65536).");
        }

        _capacity = capacity;
        _mask = capacity - 1;
        _buffer = new T[capacity];
    }

    /// <summary>Gets the total buffer capacity.</summary>
    public int Capacity => _capacity;

    /// <summary>Gets whether the buffer is currently empty (approximate, may be stale).</summary>
    public bool IsEmpty => AvailableToRead == 0;

    /// <summary>Gets whether the buffer is currently full (approximate, may be stale).</summary>
    public bool IsFull => AvailableToWrite == 0;

    /// <summary>
    /// Attempts to write a value into the buffer. Must be called from the producer thread only.
    /// </summary>
    /// <param name="value">The value to enqueue.</param>
    /// <returns><c>true</c> if the value was written; <c>false</c> if the buffer is full.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryWrite(in T value)
    {
        long currentWrite = _writePosition.Value;
        long nextWrite = currentWrite + 1;

        // Fast path: check against cached read position (avoids volatile read).
        if (nextWrite - _cachedReadPosition.Value > _capacity)
        {
            // Cached value is stale; refresh from the actual consumer position.
            _cachedReadPosition.Value = Volatile.Read(ref _readPosition.Value);

            if (nextWrite - _cachedReadPosition.Value > _capacity)
            {
                return false; // Buffer is genuinely full.
            }
        }

        // Write the data into the slot before publishing the new write position.
        _buffer[(int)(currentWrite & _mask)] = value;

        // Store barrier: ensures the data write completes before the position update is visible.
        Volatile.Write(ref _writePosition.Value, nextWrite);

        return true;
    }

    /// <summary>
    /// Attempts to read a value from the buffer. Must be called from the consumer thread only.
    /// </summary>
    /// <param name="value">The dequeued value if successful; <c>default</c> otherwise.</param>
    /// <returns><c>true</c> if a value was read; <c>false</c> if the buffer is empty.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryRead(out T value)
    {
        long currentRead = _readPosition.Value;

        // Fast path: check against cached write position (avoids volatile read).
        if (currentRead >= _cachedWritePosition.Value)
        {
            // Cached value is stale; refresh from the actual producer position.
            _cachedWritePosition.Value = Volatile.Read(ref _writePosition.Value);

            if (currentRead >= _cachedWritePosition.Value)
            {
                value = default;
                return false; // Buffer is genuinely empty.
            }
        }

        // Read the data from the slot before publishing the new read position.
        value = _buffer[(int)(currentRead & _mask)];

        // Store barrier: ensures the data read completes before the position update is visible.
        Volatile.Write(ref _readPosition.Value, currentRead + 1);

        return true;
    }

    /// <summary>
    /// Gets the approximate number of items available to read.
    /// This value may be stale by the time it's used; it is intended for monitoring, not synchronization.
    /// </summary>
    public long AvailableToRead
    {
        get
        {
            long write = Volatile.Read(ref _writePosition.Value);
            long read = Volatile.Read(ref _readPosition.Value);
            return write - read;
        }
    }

    /// <summary>
    /// Gets the approximate number of slots available for writing.
    /// This value may be stale by the time it's used; it is intended for monitoring, not synchronization.
    /// </summary>
    public long AvailableToWrite
    {
        get
        {
            long write = Volatile.Read(ref _writePosition.Value);
            long read = Volatile.Read(ref _readPosition.Value);
            return _capacity - (write - read);
        }
    }

    /// <summary>
    /// Resets the buffer to its initial empty state.
    /// <para>
    /// <b>Warning:</b> This method is NOT thread-safe. Only call when no producer or consumer
    /// threads are actively using the buffer.
    /// </para>
    /// </summary>
    public void Reset()
    {
        _writePosition.Value = 0;
        _readPosition.Value = 0;
        _cachedReadPosition.Value = 0;
        _cachedWritePosition.Value = 0;
    }
}
