using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace HideSolFound.MarketData.Core;

/// <summary>
/// Descriptor for a received UDP market data packet. Stored as a value type to avoid
/// heap allocation on the hot path. Contains metadata and a reference to the pooled
/// buffer that holds the actual payload bytes.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UdpMarketDataPacket
{
    /// <summary>High-resolution receive timestamp in nanoseconds.</summary>
    public long ReceiveTimestampNanos;

    /// <summary>Multicast sequence number for gap detection.</summary>
    public long SequenceNumber;

    /// <summary>Channel / multicast group identifier.</summary>
    public int ChannelId;

    /// <summary>Payload length within the pooled buffer.</summary>
    public int DataLength;

    /// <summary>Byte offset within the pooled buffer where payload starts.</summary>
    public int BufferOffset;

    /// <summary>Index into the buffer pool for zero-copy access.</summary>
    public int BufferId;

    /// <summary>Status flags for gap detection and recovery.</summary>
    public PacketFlags Flags;

    public UdpMarketDataPacket(long sequenceNumber, int channelId, int bufferOffset, int dataLength, int bufferId)
    {
        ReceiveTimestampNanos = HighResolutionTimestamp.Now;
        SequenceNumber = sequenceNumber;
        ChannelId = channelId;
        BufferOffset = bufferOffset;
        DataLength = dataLength;
        BufferId = bufferId;
        Flags = PacketFlags.None;
    }
}

/// <summary>
/// Packet status flags used for gap detection and feed recovery.
/// </summary>
[Flags]
public enum PacketFlags : byte
{
    None = 0,
    GapDetected = 1 << 0,
    Recovery = 1 << 1,
    Heartbeat = 1 << 2,
    Retransmission = 1 << 3,
}

/// <summary>
/// Provides nanosecond-precision timestamps using <see cref="Stopwatch"/>
/// for consistent high-resolution timing across the pipeline.
/// </summary>
public static class HighResolutionTimestamp
{
    private static readonly double NanosPerTick = 1_000_000_000.0 / Stopwatch.Frequency;

    /// <summary>Gets the current timestamp in nanoseconds.</summary>
    public static long Now
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (long)(Stopwatch.GetTimestamp() * NanosPerTick);
    }
}
