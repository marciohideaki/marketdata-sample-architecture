using System.Diagnostics;
using HideSolFound.MarketData.Core;

Console.Title = "CONSUMER - Ring Buffer Reader";
Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
Console.WriteLine("║        HideSolFound Market Data Consumer                 ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
Console.ResetColor();

Console.WriteLine("\n[INFO] Waiting for shared ring buffer...");

SharedRingBuffer? ringBuffer = null;
for (int retry = 1; retry <= 30 && ringBuffer is null; retry++)
{
    try
    {
        ringBuffer = new SharedRingBuffer(isProducer: false, create: false);
        Console.WriteLine("[INFO] Connected to ring buffer!");
    }
    catch
    {
        Console.WriteLine($"[INFO] Waiting for producer... (attempt {retry}/30)");
        Thread.Sleep(1000);
    }
}

if (ringBuffer is null)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("\n[ERROR] Could not connect. Is the producer running?");
    Console.ResetColor();
    return;
}

using (ringBuffer)
{
    Console.WriteLine($"[INFO] Buffer capacity: {ringBuffer.Capacity:N0}");
    Console.WriteLine("[INFO] Starting consumer... Press Ctrl+C to stop.\n");

    long messagesReceived = 0;
    long totalLatencyNanos = 0;
    long minLatencyNanos = long.MaxValue;
    long maxLatencyNanos = long.MinValue;
    var stopwatch = Stopwatch.StartNew();

    using var statsTimer = new Timer(_ =>
    {
        double rate = stopwatch.Elapsed.TotalSeconds > 0 ? messagesReceived / stopwatch.Elapsed.TotalSeconds : 0;
        long avgLatency = messagesReceived > 0 ? totalLatencyNanos / messagesReceived : 0;

        Console.SetCursorPosition(0, Console.WindowHeight - 4);
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"┌──────────────────────────────────────────────────────────────────┐");
        Console.WriteLine($"│ Msgs: {messagesReceived,-10:N0} │ Rate: {rate,-10:N0} msg/s │ Avg: {avgLatency / 1000.0,-8:F2} us │");
        Console.WriteLine($"│ Min: {minLatencyNanos / 1000.0,-8:F2} us │ Max: {maxLatencyNanos / 1000.0,-8:F2} us │ Buf: {ringBuffer.AvailableToRead,-8:N0}     │");
        Console.WriteLine($"└──────────────────────────────────────────────────────────────────┘");
        Console.ResetColor();
    }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

    try
    {
        while (true)
        {
            if (ringBuffer.TryRead(out MarketDataTick tick))
            {
                long receiveTime = Stopwatch.GetTimestamp();
                long latencyNanos = (receiveTime - tick.TimestampNanos) * 1_000_000_000 / Stopwatch.Frequency;

                messagesReceived++;
                totalLatencyNanos += latencyNanos;
                if (latencyNanos < minLatencyNanos) minLatencyNanos = latencyNanos;
                if (latencyNanos > maxLatencyNanos) maxLatencyNanos = latencyNanos;

                Console.ForegroundColor = GetSymbolColor(tick.SymbolId);
                Console.Write($"[READ  {messagesReceived,10:N0}] ");
                Console.ResetColor();
                Console.Write(tick);

                Console.ForegroundColor = latencyNanos switch
                {
                    < 1_000 => ConsoleColor.Green,
                    < 10_000 => ConsoleColor.Yellow,
                    < 100_000 => ConsoleColor.Magenta,
                    _ => ConsoleColor.Red,
                };
                Console.WriteLine($" | Latency: {latencyNanos / 1000.0,8:F2} us");
                Console.ResetColor();

                Thread.Sleep(50);
                if (Console.CursorTop > Console.WindowHeight - 7) Console.SetCursorPosition(0, 4);
            }
            else
            {
                Thread.SpinWait(100);
            }
        }
    }
    finally
    {
        long avgLatency = messagesReceived > 0 ? totalLatencyNanos / messagesReceived : 0;
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\nConsumer stopped. Received: {messagesReceived:N0}");
        Console.WriteLine($"Avg latency: {avgLatency / 1000.0:F2} us | Min: {minLatencyNanos / 1000.0:F2} us | Max: {maxLatencyNanos / 1000.0:F2} us");
        Console.ResetColor();
    }
}

static ConsoleColor GetSymbolColor(int symbol) => symbol switch
{
    1 => ConsoleColor.Green,
    2 => ConsoleColor.Yellow,
    3 => ConsoleColor.Blue,
    4 => ConsoleColor.Magenta,
    5 => ConsoleColor.Cyan,
    _ => ConsoleColor.White,
};
