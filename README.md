# Market Data Sample Architecture

High-performance market data processing pipeline built with .NET 9, demonstrating low-latency architecture patterns for real-time financial data distribution.

## Overview

This project showcases a production-grade architecture for processing real-time market data feeds with sub-microsecond latency targets. It demonstrates key patterns used in high-frequency trading (HFT) and market data distribution systems, including lock-free data structures, hot/cold path separation, zero-allocation processing, and multi-stage pipeline design.

> **Note:** This is a reference architecture sample — not a production system. It prioritizes clarity and demonstrability of low-latency patterns over completeness.

## Architecture

```
                      HOT PATH (dedicated threads, zero-allocation)
  ┌─────────────────────────────────────────────────────────────────────────┐
  │                                                                         │
  │  UDP Feed ──► [Ring Buffer] ──► FAST Decoder ──► [Ring Buffer] ──►     │
  │                                  (Thread 1)                            │
  │                                                                         │
  │               Book Builder ──► [Ring Buffer] ──► Cold Path Forwarder   │
  │                (Thread 2)                         (Thread 3)            │
  │                                                                         │
  └──────────────────────────────────────────────────┬──────────────────────┘
                                                     │
                      COLD PATH (async, I/O tolerant)│
                                                     │
                                     ┌───────────────┼───────────────┐
                                     ▼               ▼               ▼
                                Redis Store    SignalR Hub     Metrics
                              (Snapshots +    (WebSocket      (Buffered
                               Streams +      broadcast)      async flush)
                               Pub/Sub)            │
                                                   ▼
                                              Web Clients
```

### Pipeline Stages

| Stage | Thread | Priority | Description |
|-------|--------|----------|-------------|
| **FAST Decoder** | Pipeline-FastDecoder | Highest | Decodes FAST protocol packets using `Span<byte>` for zero-copy processing |
| **Book Builder** | Pipeline-BookBuilder | Highest | Applies decoded messages to pre-allocated order books (256 levels, 32 orders/level) |
| **Cold Path** | Pipeline-ColdPath | Normal | Forwards snapshots to Redis persistence and SignalR distribution |

### Key Design Decisions

- **Lock-Free Ring Buffers** — SPSC (Single Producer, Single Consumer) ring buffers with cache-line padding (64 bytes) eliminate false sharing and achieve sub-20ns per operation
- **Cached Position Snapshots** — Producer and consumer cache each other's positions to minimize expensive cross-thread volatile reads
- **Power-of-2 Capacity** — Enables branchless index calculation via bitwise AND instead of modulo
- **Pre-allocated Order Books** — 1000 order books initialized at startup, zero heap allocation during processing
- **Stop-bit Integer Encoding** — Variable-length encoding with pre-computed power-of-10 lookup for O(1) fixed-point decimal scaling
- **Hot/Cold Path Separation** — Ultra-low-latency hot path runs on highest-priority threads; cold path (I/O, persistence, distribution) runs at normal priority

## Projects

| Project | Type | Description |
|---------|------|-------------|
| `HideSolFound.MarketData.Core` | Class Library | Lock-free ring buffer, FAST decoder, order book engine, market data pipeline |
| `HideSolFound.MarketData.Gateway` | ASP.NET Core | REST API, SignalR hub for WebSocket broadcasting, Redis persistence |
| `HideSolFound.MarketData.Producer` | Console App | Synthetic market data producer via shared memory ring buffer |
| `HideSolFound.MarketData.Consumer` | Console App | Real-time market data consumer with latency tracking |
| `HideSolFound.MarketData.Benchmarks` | Console App | BenchmarkDotNet micro-benchmarks for ring buffer and pipeline components |
| `HideSolFound.MarketData.Tests` | xUnit Tests | 27 unit tests covering ring buffer, order book, and FAST decoder |

## Technology Stack

| Category | Technology |
|----------|------------|
| Runtime | .NET 9.0 |
| Web Framework | ASP.NET Core (SignalR, Web API) |
| Cache / Persistence | StackExchange.Redis |
| IPC | MemoryMappedFile (Windows shared memory) |
| Benchmarking | BenchmarkDotNet |
| Testing | xUnit 2.9.3 |
| Serialization | System.Text.Json |

## Getting Started

### Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [Redis](https://redis.io/) (for Gateway project — optional for core/tests)
- Windows (for SharedRingBuffer IPC via MemoryMappedFile)

### Build

```bash
dotnet build --configuration Release
```

### Run Tests

```bash
dotnet test --configuration Release
```

### Run Benchmarks

```bash
dotnet run --project src/HideSolFound.MarketData.Benchmarks --configuration Release
```

### Run Gateway

```bash
# Requires Redis running on localhost:6379
dotnet run --project src/HideSolFound.MarketData.Gateway --configuration Release
```

### Producer / Consumer (Shared Memory IPC)

```bash
# Terminal 1: Start producer (writes to shared ring buffer)
dotnet run --project src/HideSolFound.MarketData.Producer --configuration Release

# Terminal 2: Start consumer (reads from shared ring buffer)
dotnet run --project src/HideSolFound.MarketData.Consumer --configuration Release
```

## Core Components

### LockFreeRingBuffer\<T\>

Zero-allocation SPSC ring buffer with three key performance techniques:

1. **Cache-line padding** — `PaddedLong` struct uses `StructLayout(Explicit, Size=64)` to prevent false sharing between producer/consumer sequence counters
2. **Cached position snapshots** — Reduces cross-thread volatile reads; only refreshes when stale value suggests full/empty state
3. **Bitwise masking** — Power-of-2 capacity enables `index & mask` instead of `index % capacity`

```csharp
var buffer = new LockFreeRingBuffer<MarketDataTick>(65536);
buffer.TryWrite(in tick);  // Producer thread
buffer.TryRead(out tick);  // Consumer thread
```

### FastDecoder

Simplified FAST (FIX Adapted for Streaming) protocol decoder operating directly on `ReadOnlySpan<byte>`:

- Stop-bit encoding for variable-length integers (7 data bits per byte, MSB = stop flag)
- Pre-computed `ReadOnlySpan<long> PowersOf10` for O(1) decimal scaling
- Presence-map driven optional field decoding
- Zero heap allocation on the hot path

### OrderBook

Pre-allocated order book engine with O(1) best bid/ask tracking:

- 256 price levels per side (bid/ask), 32 orders per level
- Sorted insertion with array shift (bids descending, asks ascending)
- Supports NewOrder, Cancel, Execution, and IncrementalRefresh message types
- Incremental top-of-book tracking — `GetSnapshot()` is O(1)

### MarketDataPipeline

Three-stage pipeline connecting ring buffers with dedicated processing threads:

- **Stage 1** (Decoder): UDP packets → FAST decode → FIX messages
- **Stage 2** (Book Builder): FIX messages → order book updates → snapshots
- **Stage 3** (Cold Path): snapshots → persistence and distribution

### MetricsService

Dual-mode metrics collection demonstrating hot-path I/O cost:

- **Hot-path mode**: Synchronous Redis writes per metric (diagnostic — ~98% throughput loss)
- **Buffered mode**: Async flush with configurable batch size and interval (~10% overhead)

## Testing

The test suite covers core components with 27 test cases:

| Component | Tests | Coverage |
|-----------|-------|----------|
| LockFreeRingBuffer | 13 | Constructor validation, SPSC read/write, FIFO ordering, wrap-around, concurrent producer-consumer (100K messages), MarketDataTick round-trip |
| OrderBook | 8 | Bid/ask management, cancel, execution, snapshot generation, incremental refresh |
| FastDecoder | 3 | Minimum packet size, valid decode with metadata, corrupt data handling |

```bash
dotnet test --configuration Release --verbosity normal
```

## Project Structure

```
marketdata-sample-architecture/
├── Directory.Build.props              # Shared build configuration
├── HideSolFound.MarketData.sln
├── src/
│   ├── HideSolFound.MarketData.Core/
│   │   ├── LockFreeRingBuffer.cs      # SPSC lock-free ring buffer
│   │   ├── MarketDataTick.cs          # Value type for market data ticks
│   │   ├── SharedRingBuffer.cs        # IPC via MemoryMappedFile (Windows)
│   │   ├── FixMessage.cs              # FIX protocol message struct
│   │   ├── UdpMarketDataPacket.cs     # UDP packet descriptor
│   │   ├── FastDecoder.cs             # FAST protocol decoder
│   │   ├── OrderBook.cs              # Pre-allocated order book engine
│   │   ├── MarketDataPipeline.cs      # Multi-stage processing pipeline
│   │   ├── MetricsConfig.cs           # Metrics configuration
│   │   └── MetricsService.cs          # Dual-mode metrics collection
│   ├── HideSolFound.MarketData.Gateway/
│   │   ├── Controllers/               # REST API (FeedController)
│   │   ├── Hubs/                      # SignalR hub (BookHub)
│   │   ├── Models/                    # DTOs (BookSnapshotDto, BookIncrementalDto)
│   │   └── Services/                  # SymbolMapper, Redis persistence, Feed control
│   ├── HideSolFound.MarketData.Producer/
│   ├── HideSolFound.MarketData.Consumer/
│   └── HideSolFound.MarketData.Benchmarks/
│       ├── RingBufferBenchmarks.cs    # Per-operation latency benchmarks
│       └── ComparativeBenchmarks.cs   # Lock-free vs concurrent collection comparison
└── tests/
    └── HideSolFound.MarketData.Tests/
        ├── LockFreeRingBufferTests.cs
        ├── OrderBookTests.cs
        └── FastDecoderTests.cs
```

## License

[MIT](LICENSE)
