# Market Data Sample Architecture

High-performance market data processing pipeline built with .NET 9, demonstrating low-latency architecture patterns for real-time financial data distribution.

## Overview

This project showcases a production-grade architecture for processing real-time market data feeds with sub-microsecond latency targets. It demonstrates key patterns used in high-frequency trading (HFT) and market data distribution systems.

## Key Features

- **Lock-Free Ring Buffer** - Zero-allocation SPSC (Single Producer, Single Consumer) ring buffer with cache-line padding
- **FAST Protocol Decoder** - Zero-copy message decoding using `Span<byte>` for direct buffer access
- **Order Book Engine** - Pre-allocated order book with 256 price levels and O(1) best bid/ask access
- **Multi-Stage Pipeline** - UDP Receiver -> FAST Decoder -> Book Builder -> Cold Path (3 dedicated threads)
- **Hot/Cold Path Separation** - Ultra-low-latency hot path isolated from persistence and distribution
- **Real-Time Distribution** - SignalR hub for WebSocket-based market data broadcasting
- **Redis Persistence** - Snapshot storage with Streams for incremental updates and Pub/Sub for notifications

## Architecture

```
UDP Feed ──> [Ring Buffer] ──> FAST Decoder ──> [Ring Buffer] ──> Book Builder ──> [Ring Buffer] ──> Cold Path
                                                                                                        │
                                                                                        ┌───────────────┤
                                                                                        ▼               ▼
                                                                                   Redis Store    SignalR Hub
                                                                                                        │
                                                                                                        ▼
                                                                                                   Web Clients
```

## Projects

| Project | Description |
|---------|-------------|
| `HideSolFound.MarketData.Core` | Lock-free ring buffer, market data pipeline, order book engine |
| `HideSolFound.MarketData.Gateway` | ASP.NET Core gateway with SignalR, Redis, and REST API |
| `HideSolFound.MarketData.Producer` | Synthetic market data producer for testing |
| `HideSolFound.MarketData.Consumer` | Real-time market data consumer with latency tracking |
| `HideSolFound.MarketData.Benchmarks` | BenchmarkDotNet performance test suite |
| `HideSolFound.MarketData.Tests` | Unit tests for core components |

## Technology Stack

- .NET 9.0
- ASP.NET Core (SignalR, Web API)
- StackExchange.Redis
- BenchmarkDotNet
- xUnit

## License

[MIT](LICENSE)
