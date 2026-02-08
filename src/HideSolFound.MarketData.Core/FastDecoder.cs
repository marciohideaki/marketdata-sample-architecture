using System.Runtime.CompilerServices;

namespace HideSolFound.MarketData.Core;

/// <summary>
/// Simplified FAST (FIX Adapted for Streaming) protocol decoder optimized for
/// zero-allocation hot-path processing.
/// <para>
/// Operates directly on <see cref="ReadOnlySpan{T}"/> to avoid buffer copies.
/// Uses stop-bit encoding for variable-length integers and a pre-computed
/// power-of-10 lookup table for efficient fixed-point decimal scaling.
/// </para>
/// </summary>
/// <remarks>
/// This is a simplified implementation focused on demonstrating the decoding
/// architecture. A production decoder would handle the full FAST template
/// specification including nullable fields, copy/increment operators, and
/// dictionary-based compression.
/// </remarks>
public static class FastDecoder
{
    private const int MinimumPacketSize = 16;
    private const int TargetDecimalScale = 8;

    /// <summary>
    /// Pre-computed powers of 10 for O(1) decimal scaling (avoids Math.Pow on hot path).
    /// </summary>
    private static ReadOnlySpan<long> PowersOf10 =>
    [
        1L,
        10L,
        100L,
        1_000L,
        10_000L,
        100_000L,
        1_000_000L,
        10_000_000L,
        100_000_000L,
        1_000_000_000L,
        10_000_000_000L,
    ];

    /// <summary>
    /// Decodes a FAST-encoded packet into a <see cref="FixMessage"/>.
    /// </summary>
    /// <param name="data">Raw packet bytes.</param>
    /// <param name="receiveTimestamp">Nanosecond timestamp when the packet was received.</param>
    /// <param name="channelId">Source channel / multicast group identifier.</param>
    /// <param name="message">The decoded message if successful.</param>
    /// <returns><c>true</c> if decoding succeeded; <c>false</c> on malformed input.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryDecode(
        ReadOnlySpan<byte> data,
        long receiveTimestamp,
        int channelId,
        out FixMessage message)
    {
        message = default;

        if (data.Length < MinimumPacketSize)
        {
            return false;
        }

        int offset = 0;

        try
        {
            // ── FAST Header ──
            byte presenceMap = data[offset++];
            _ = DecodeStopBitInt32(data, ref offset); // Template ID (consumed but unused in this simplified version)

            // ── Processing metadata ──
            message.ReceiveTimestampNanos = receiveTimestamp;
            message.DecodeTimestampNanos = HighResolutionTimestamp.Now;
            message.ChannelId = channelId;

            // ── Required fields ──
            message.MsgSeqNum = DecodeStopBitInt64(data, ref offset);
            message.MsgType = (FixMsgType)data[offset++];
            message.SendingTime = DecodeStopBitInt64(data, ref offset);

            // ── Optional fields (presence-map driven) ──
            if ((presenceMap & 0x01) != 0)
            {
                message.SecurityId = DecodeStopBitInt64(data, ref offset);
                message.SymbolIndex = (int)(message.SecurityId % 1000);
            }

            if ((presenceMap & 0x02) != 0)
            {
                int exponent = DecodeStopBitInt32(data, ref offset);
                long mantissa = DecodeStopBitInt64(data, ref offset);
                message.Price = ScaleToFixedPoint(mantissa, exponent);
            }

            if ((presenceMap & 0x04) != 0)
            {
                message.Quantity = DecodeStopBitInt64(data, ref offset);
            }

            if ((presenceMap & 0x08) != 0)
            {
                message.Side = (FixSide)data[offset++];
            }

            if ((presenceMap & 0x10) != 0)
            {
                message.OrderId = DecodeStopBitInt64(data, ref offset);
            }

            // ── Trade fields (execution reports only) ──
            if (message.MsgType == FixMsgType.Execution && (presenceMap & 0x20) != 0)
            {
                message.TradeId = DecodeStopBitInt64(data, ref offset);
                int tradeExponent = DecodeStopBitInt32(data, ref offset);
                long tradeMantissa = DecodeStopBitInt64(data, ref offset);
                message.TradePrice = ScaleToFixedPoint(tradeMantissa, tradeExponent);
                message.TradeQuantity = DecodeStopBitInt64(data, ref offset);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes a stop-bit encoded 32-bit integer.
    /// Each byte contributes 7 data bits; the MSB (stop bit) signals the final byte.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeStopBitInt32(ReadOnlySpan<byte> data, ref int offset)
    {
        int value = 0;
        byte b;
        do
        {
            b = data[offset++];
            value = (value << 7) | (b & 0x7F);
        }
        while ((b & 0x80) == 0);

        return value;
    }

    /// <summary>
    /// Decodes a stop-bit encoded 64-bit integer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long DecodeStopBitInt64(ReadOnlySpan<byte> data, ref int offset)
    {
        long value = 0;
        byte b;
        do
        {
            b = data[offset++];
            value = (value << 7) | (long)(b & 0x7F);
        }
        while ((b & 0x80) == 0);

        return value;
    }

    /// <summary>
    /// Converts a FAST decimal (mantissa + exponent) to 8-decimal-place fixed-point.
    /// Example: mantissa=12345, exponent=-2 → 123.45 → 12_345_000_000 (×10^8).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long ScaleToFixedPoint(long mantissa, int exponent)
    {
        int adjustment = TargetDecimalScale + exponent;

        if (adjustment > 0 && adjustment < PowersOf10.Length)
        {
            return mantissa * PowersOf10[adjustment];
        }

        if (adjustment < 0 && -adjustment < PowersOf10.Length)
        {
            return mantissa / PowersOf10[-adjustment];
        }

        return mantissa;
    }
}
