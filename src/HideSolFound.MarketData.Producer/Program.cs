using System.Diagnostics;
using HideSolFound.MarketData.Core;

Console.Title = "PRODUCER - Ring Buffer Writer";
Console.ForegroundColor = ConsoleColor.Green;
Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
Console.WriteLine("║        HideSolFound Market Data Producer                 ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
Console.ResetColor();

Console.WriteLine("\n[INFO] Creating shared ring buffer...");
using var ringBuffer = new SharedRingBuffer(isProducer: true, create: true);
Console.WriteLine($"[INFO] Buffer capacity: {ringBuffer.Capacity:N0}");
Console.WriteLine("[INFO] Starting producer... Press Ctrl+C to stop.\n");

var random = new Random(42);
long sequenceNumber = 0;
long messagesSent = 0;
var stopwatch = Stopwatch.StartNew();

using var statsTimer = new Timer(_ =>
{
    double rate = stopwatch.Elapsed.TotalSeconds > 0 ? messagesSent / stopwatch.Elapsed.TotalSeconds : 0;
    Console.SetCursorPosition(0, Console.WindowHeight - 3);
    Console.ForegroundColor = ConsoleColor.Cyan;
    Console.WriteLine($"┌────────────────────────────────────────────────────────┐");
    Console.WriteLine($"│ Sent: {messagesSent,-10:N0} │ Rate: {rate,-15:N0} msg/s │");
    Console.WriteLine($"└────────────────────────────────────────────────────────┘");
    Console.ResetColor();
}, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

try
{
    while (true)
    {
        int symbolId = random.Next(1, 6);
        var tick = new MarketDataTick(
            sequenceNumber: sequenceNumber++,
            symbolId: symbolId,
            price: 2800 + random.Next(-50, 50),
            volume: random.Next(100, 10000),
            timestampNanos: Stopwatch.GetTimestamp());

        int attempts = 0;
        while (!ringBuffer.TryWrite(in tick))
        {
            if (++attempts > 100)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[WARN] Buffer full - waiting... (available: {ringBuffer.AvailableToWrite})");
                Console.ResetColor();
                Thread.Sleep(10);
                attempts = 0;
            }
            else
            {
                Thread.SpinWait(100);
            }
        }

        messagesSent++;
        Console.ForegroundColor = GetSymbolColor(symbolId);
        Console.Write($"[WRITE {messagesSent,10:N0}] ");
        Console.ResetColor();
        Console.WriteLine(tick);

        Thread.Sleep(50);
        if (Console.CursorTop > Console.WindowHeight - 6) Console.SetCursorPosition(0, 4);
    }
}
finally
{
    Console.ForegroundColor = ConsoleColor.Yellow;
    Console.WriteLine($"\nProducer stopped. Total sent: {messagesSent:N0} in {stopwatch.Elapsed.TotalSeconds:F2}s");
    Console.ResetColor();
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
