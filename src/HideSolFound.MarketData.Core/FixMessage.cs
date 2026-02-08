using System.Runtime.InteropServices;

namespace HideSolFound.MarketData.Core;

/// <summary>
/// Decoded FIX protocol message optimized for zero-allocation hot-path processing.
/// <para>
/// Stores only the fields critical to market data distribution. All monetary values
/// use fixed-point representation with 8 decimal places to avoid floating-point
/// precision loss (e.g., price 28.50 is stored as 2_850_000_000).
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct FixMessage
{
    // ── Identification ──
    public long MsgSeqNum;
    public FixMsgType MsgType;
    public long SendingTime;

    // ── Instrument ──
    public long SecurityId;
    public int SymbolIndex;

    // ── Order / Quote ──
    public long OrderId;
    public long Price;
    public long Quantity;
    public FixSide Side;

    // ── Execution ──
    public long TradeId;
    public long TradePrice;
    public long TradeQuantity;

    // ── Processing metadata ──
    public long ReceiveTimestampNanos;
    public long DecodeTimestampNanos;
    public int ChannelId;

    /// <summary>
    /// Converts the fixed-point price (8 decimals) to a floating-point value.
    /// Intended for display only; all internal processing uses the integer representation.
    /// </summary>
    public readonly double PriceAsDouble => Price / 100_000_000.0;

    /// <summary>Converts a floating-point price to the fixed-point representation.</summary>
    public static long ToFixedPointPrice(double price) => (long)(price * 100_000_000);
}

/// <summary>
/// FIX message types relevant to market data feeds.
/// Values correspond to the ASCII byte of the FIX MsgType tag (35).
/// </summary>
public enum FixMsgType : byte
{
    Unknown = 0,
    NewOrder = (byte)'D',
    OrderCancel = (byte)'F',
    Execution = (byte)'8',
    Quote = (byte)'S',
    MarketDataSnapshot = (byte)'W',
    IncrementalRefresh = (byte)'X',
}

/// <summary>
/// Order side (Buy / Sell) matching FIX tag 54 ASCII values.
/// </summary>
public enum FixSide : byte
{
    Unknown = 0,
    Buy = (byte)'1',
    Sell = (byte)'2',
}
