using HideSolFound.MarketData.Core;
using Xunit;

namespace HideSolFound.MarketData.Tests;

public sealed class LockFreeRingBufferTests
{
    [Fact]
    public void Constructor_PowerOfTwo_Succeeds()
    {
        var buffer = new LockFreeRingBuffer<int>(1024);
        Assert.Equal(1024, buffer.Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(1000)]
    public void Constructor_NonPowerOfTwo_Throws(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockFreeRingBuffer<int>(capacity));
    }

    [Fact]
    public void TryWrite_TryRead_RoundTrip()
    {
        var buffer = new LockFreeRingBuffer<long>(4);
        Assert.True(buffer.TryWrite(42L));
        Assert.True(buffer.TryRead(out long value));
        Assert.Equal(42L, value);
    }

    [Fact]
    public void TryRead_EmptyBuffer_ReturnsFalse()
    {
        var buffer = new LockFreeRingBuffer<int>(4);
        Assert.False(buffer.TryRead(out _));
    }

    [Fact]
    public void TryWrite_FullBuffer_ReturnsFalse()
    {
        var buffer = new LockFreeRingBuffer<int>(4);
        Assert.True(buffer.TryWrite(1));
        Assert.True(buffer.TryWrite(2));
        Assert.True(buffer.TryWrite(3));
        Assert.True(buffer.TryWrite(4));
        Assert.False(buffer.TryWrite(5)); // Buffer full
    }

    [Fact]
    public void AvailableToRead_TracksItemCount()
    {
        var buffer = new LockFreeRingBuffer<int>(8);
        Assert.Equal(0, buffer.AvailableToRead);

        buffer.TryWrite(1);
        buffer.TryWrite(2);
        Assert.Equal(2, buffer.AvailableToRead);

        buffer.TryRead(out _);
        Assert.Equal(1, buffer.AvailableToRead);
    }

    [Fact]
    public void AvailableToWrite_TracksSlotCount()
    {
        var buffer = new LockFreeRingBuffer<int>(4);
        Assert.Equal(4, buffer.AvailableToWrite);

        buffer.TryWrite(1);
        Assert.Equal(3, buffer.AvailableToWrite);
    }

    [Fact]
    public void IsEmpty_IsFull_ReflectState()
    {
        var buffer = new LockFreeRingBuffer<int>(2);
        Assert.True(buffer.IsEmpty);
        Assert.False(buffer.IsFull);

        buffer.TryWrite(1);
        buffer.TryWrite(2);
        Assert.False(buffer.IsEmpty);
        Assert.True(buffer.IsFull);
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var buffer = new LockFreeRingBuffer<int>(4);
        buffer.TryWrite(1);
        buffer.TryWrite(2);
        buffer.Reset();

        Assert.True(buffer.IsEmpty);
        Assert.Equal(4, buffer.AvailableToWrite);
    }

    [Fact]
    public void FIFO_OrderPreserved()
    {
        var buffer = new LockFreeRingBuffer<int>(8);
        for (int i = 0; i < 5; i++) buffer.TryWrite(i);

        for (int i = 0; i < 5; i++)
        {
            Assert.True(buffer.TryRead(out int value));
            Assert.Equal(i, value);
        }
    }

    [Fact]
    public void WrapAround_WorksCorrectly()
    {
        var buffer = new LockFreeRingBuffer<int>(4);

        // Fill and drain multiple times to exercise wrap-around.
        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < 4; i++) Assert.True(buffer.TryWrite(round * 4 + i));
            for (int i = 0; i < 4; i++)
            {
                Assert.True(buffer.TryRead(out int value));
                Assert.Equal(round * 4 + i, value);
            }
        }
    }

    [Fact]
    public void MarketDataTick_RoundTrip()
    {
        var buffer = new LockFreeRingBuffer<MarketDataTick>(4);
        var tick = new MarketDataTick(42, 1, 2850, 1000, 999);

        Assert.True(buffer.TryWrite(in tick));
        Assert.True(buffer.TryRead(out MarketDataTick result));

        Assert.Equal(42, result.SequenceNumber);
        Assert.Equal(1, result.SymbolId);
        Assert.Equal(2850, result.Price);
        Assert.Equal(1000, result.Volume);
        Assert.Equal(999, result.TimestampNanos);
    }

    [Fact]
    public void ConcurrentProducerConsumer_NoDataLoss()
    {
        const int messageCount = 100_000;
        var buffer = new LockFreeRingBuffer<long>(4096);
        long producerSum = 0;
        long consumerSum = 0;

        var producer = new Thread(() =>
        {
            for (long i = 1; i <= messageCount; i++)
            {
                while (!buffer.TryWrite(i)) Thread.SpinWait(10);
                producerSum += i;
            }
        });

        var consumer = new Thread(() =>
        {
            int consumed = 0;
            while (consumed < messageCount)
            {
                if (buffer.TryRead(out long value))
                {
                    consumerSum += value;
                    consumed++;
                }
                else
                {
                    Thread.SpinWait(10);
                }
            }
        });

        producer.Start();
        consumer.Start();
        producer.Join(TimeSpan.FromSeconds(10));
        consumer.Join(TimeSpan.FromSeconds(10));

        Assert.Equal(producerSum, consumerSum);
    }
}
