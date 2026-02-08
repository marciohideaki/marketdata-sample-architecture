using System.Runtime.InteropServices;

namespace HideSolFound.MarketData.Core;

/// <summary>
/// Compact market data tick stored as a value type for zero-allocation processing.
/// <para>
/// Uses <see cref="LayoutKind.Sequential"/> with Pack=1 for predictable memory layout
/// and efficient serialization across shared-memory boundaries.
/// </para>
/// </summary>
/// <remarks>
/// All monetary values use fixed-point representation (integer ticks) to avoid
/// floating-point precision issues inherent to financial calculations.
/// The price field stores values multiplied by 100 (e.g., 28.50 = 2850).
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct MarketDataTick
{
    /// <summary>Monotonically increasing sequence number assigned by the producer.</summary>
    public long SequenceNumber;

    /// <summary>Numeric symbol identifier for O(1) lookup (avoids string comparisons on the hot path).</summary>
    public int SymbolId;

    /// <summary>Price in fixed-point ticks (value * 100). Example: 28.50 is stored as 2850.</summary>
    public long Price;

    /// <summary>Trade volume for this tick.</summary>
    public long Volume;

    /// <summary>High-resolution timestamp in nanoseconds since epoch.</summary>
    public long TimestampNanos;

    public MarketDataTick(long sequenceNumber, int symbolId, long price, long volume, long timestampNanos)
    {
        SequenceNumber = sequenceNumber;
        SymbolId = symbolId;
        Price = price;
        Volume = volume;
        TimestampNanos = timestampNanos;
    }

    /// <summary>Gets the price as a decimal value (converts from fixed-point representation).</summary>
    public readonly decimal PriceAsDecimal => Price / 100m;

    public override readonly string ToString() =>
        $"Seq={SequenceNumber} SymbolId={SymbolId} Price={PriceAsDecimal:F2} Vol={Volume}";
}
