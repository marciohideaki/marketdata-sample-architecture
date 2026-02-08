using HideSolFound.MarketData.Core;
using Xunit;

namespace HideSolFound.MarketData.Tests;

public sealed class FastDecoderTests
{
    [Fact]
    public void TryDecode_PacketTooSmall_ReturnsFalse()
    {
        byte[] data = new byte[8]; // Less than minimum 16 bytes
        Assert.False(FastDecoder.TryDecode(data, 0, 0, out _));
    }

    [Fact]
    public void TryDecode_ValidPacket_PopulatesMetadata()
    {
        byte[] packet = BuildMinimalPacket();

        bool result = FastDecoder.TryDecode(packet, receiveTimestamp: 12345, channelId: 7, out FixMessage msg);

        Assert.True(result);
        Assert.Equal(12345, msg.ReceiveTimestampNanos);
        Assert.Equal(7, msg.ChannelId);
        Assert.True(msg.DecodeTimestampNanos > 0);
    }

    [Fact]
    public void TryDecode_CorruptData_ReturnsFalse()
    {
        // Craft a packet that will cause an index-out-of-range during decoding.
        byte[] data = new byte[16];
        data[0] = 0xFF; // All presence bits set — decoder will try to read more fields than available.
        data[1] = 0x80; // Template ID (stop-bit set, value=0)

        // The remaining bytes are zeroes, which encode as stop-bit integers of value 0.
        // The decoder will eventually run out of bytes and throw.
        Assert.False(FastDecoder.TryDecode(data, 0, 0, out _));
    }

    /// <summary>
    /// Builds a minimal valid FAST-encoded packet for testing.
    /// Presence map = 0x00 (no optional fields), template = 0, minimal required fields.
    /// </summary>
    private static byte[] BuildMinimalPacket()
    {
        var packet = new List<byte>
        {
            0x00,       // Presence map: no optional fields
            0x80,       // Template ID: 0 (stop-bit encoded)
        };

        // MsgSeqNum = 1 (stop-bit encoded)
        packet.Add(0x81); // value=1, stop bit set

        // MsgType = 'D' (NewOrder)
        packet.Add((byte)'D');

        // SendingTime = 100 (stop-bit encoded)
        packet.Add(0x80 | 100); // value=100, stop bit set

        // Pad to minimum 16 bytes
        while (packet.Count < 16) packet.Add(0x00);

        return [.. packet];
    }
}
