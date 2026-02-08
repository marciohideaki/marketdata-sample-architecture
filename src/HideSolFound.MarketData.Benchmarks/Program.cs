using BenchmarkDotNet.Running;
using HideSolFound.MarketData.Benchmarks;

if (args.Length > 0)
{
    BenchmarkSwitcher.FromAssembly(typeof(RingBufferBenchmarks).Assembly).Run(args);
    return;
}

Console.WriteLine("╔═══════════════════════════════════════════════════════════╗");
Console.WriteLine("║     HideSolFound Market Data - Performance Benchmarks    ║");
Console.WriteLine("╚═══════════════════════════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine("  1. Ring Buffer Benchmarks");
Console.WriteLine("  2. Comparative Benchmarks (Lock-Free vs Lock-Based)");
Console.WriteLine("  3. All Benchmarks");
Console.WriteLine();
Console.Write("Select: ");

string? option = Console.ReadLine();
switch (option)
{
    case "1":
        BenchmarkRunner.Run<RingBufferBenchmarks>();
        break;
    case "2":
        BenchmarkRunner.Run<ComparativeBenchmarks>();
        break;
    case "3":
        BenchmarkRunner.Run<RingBufferBenchmarks>();
        BenchmarkRunner.Run<ComparativeBenchmarks>();
        break;
    default:
        BenchmarkRunner.Run<RingBufferBenchmarks>();
        break;
}

Console.WriteLine("\nBenchmarks complete. See BenchmarkDotNet.Artifacts/ for detailed results.");
