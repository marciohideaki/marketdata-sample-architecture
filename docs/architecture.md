# Architecture Documentation

## System Overview

This document describes the architecture of the Market Data Sample, a high-performance pipeline for processing real-time financial market data feeds. The system prioritizes latency minimization through lock-free data structures, hot/cold path separation, and zero-allocation processing on the critical path.

## Design Principles

1. **Zero-allocation hot path** — No heap allocation during message decoding or book building
2. **Lock-free synchronization** — Memory barriers (`Volatile.Read`/`Volatile.Write`) instead of locks
3. **Cache-line isolation** — Prevent false sharing via 64-byte padded sequence counters
4. **Hot/cold path separation** — I/O-bound work (Redis, SignalR) runs on separate threads at lower priority
5. **Pre-allocation** — Order books, buffers, and arrays initialized at startup

## Pipeline Architecture

### Data Flow

```
  UDP Multicast Feed
        │
        ▼
  ┌─────────────────┐
  │ PublishUdpPacket │  Hot path entry point
  │ (inline, ~20ns) │  Copies packet to pre-allocated buffer pool
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │   Ring Buffer    │  LockFreeRingBuffer<UdpMarketDataPacket>
  │   (65536 slots)  │  Capacity: 65536 (default)
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │  FAST Decoder    │  Thread: Pipeline-FastDecoder (Highest priority)
  │  (zero-copy)     │  Decodes FAST protocol via Span<byte>
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │   Ring Buffer    │  LockFreeRingBuffer<FixMessage>
  │   (65536 slots)  │  Capacity: 65536 (default)
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │  Book Builder    │  Thread: Pipeline-BookBuilder (Highest priority)
  │  (pre-allocated) │  1000 order books, 256 levels each
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │   Ring Buffer    │  LockFreeRingBuffer<BookSnapshot>
  │   (32768 slots)  │  Capacity: 32768 (default)
  └────────┬────────┘
           │
           ▼
  ┌─────────────────┐
  │   Cold Path      │  Thread: Pipeline-ColdPath (Normal priority)
  │   (I/O allowed)  │  Redis persistence, SignalR broadcast, metrics
  └─────────────────┘
```

### Thread Model

| Thread | Priority | CPU Affinity | Loop Type | Purpose |
|--------|----------|--------------|-----------|---------|
| Pipeline-FastDecoder | Highest | N/A (OS scheduled) | Busy-spin with SpinWait(100) | Decode FAST packets |
| Pipeline-BookBuilder | Highest | N/A (OS scheduled) | Busy-spin with SpinWait(100) | Apply messages to order books |
| Pipeline-ColdPath | Normal | N/A | Sleep(1) on empty | Forward snapshots to I/O layer |

All threads use `IsBackground = false` (except cold path) to ensure graceful shutdown with drain semantics.

## Core Components

### LockFreeRingBuffer\<T\>

**Purpose:** Zero-allocation inter-thread communication for SPSC (Single Producer, Single Consumer) scenarios.

**Performance characteristics:**
- Write latency: ~10-20ns (single write, no contention)
- Read latency: ~10-20ns (single read, no contention)
- Memory: Pre-allocated array of `T[]`, no GC pressure during operation

**Key implementation details:**

```
Memory Layout (each PaddedLong occupies 64 bytes — one cache line):

Cache Line 0: [_writePosition (8B) | padding (56B)]
Cache Line 1: [_readPosition  (8B) | padding (56B)]
Cache Line 2: [_cachedReadPosition  (8B) | padding (56B)]
Cache Line 3: [_cachedWritePosition (8B) | padding (56B)]
```

The cached position pattern reduces volatile reads:

1. Producer checks `_cachedReadPosition` (local read, ~1ns) before each write
2. Only reads `_readPosition` via `Volatile.Read` when cache suggests buffer is full
3. Same pattern applies to consumer with `_cachedWritePosition`

This reduces cross-core cache invalidation from every operation to only when the buffer approaches full/empty state.

### FastDecoder

**Purpose:** Decode FAST (FIX Adapted for Streaming) protocol messages with zero allocation.

**Encoding format:** Stop-bit encoding — each byte contributes 7 data bits; the MSB (bit 7) is the stop flag:
- `0xxxxxxx` — continuation byte (more bytes follow)
- `1xxxxxxx` — stop byte (final byte of the integer)

**Decimal handling:** FAST transmits decimals as (exponent, mantissa) pairs. The decoder uses a pre-computed `ReadOnlySpan<long>` of powers of 10 for O(1) fixed-point conversion, avoiding `Math.Pow` on the hot path.

**Presence map:** A single byte at the packet start indicates which optional fields are present, enabling conditional field decoding without branching overhead.

### OrderBook

**Purpose:** Maintain a full Level-3 order book with O(1) best bid/ask access.

**Pre-allocation strategy:**
- 256 price levels per side (bid/ask)
- 32 orders per price level
- All arrays allocated at construction time

**Sorted maintenance:**
- Bids: descending order (highest price at index 0)
- Asks: ascending order (lowest price at index 0)
- `FindOrCreateLevel` performs linear scan + array shift for insertion
- `RefreshTopOfBook` reads index 0 — always O(1)

**Message types supported:**
| MsgType | Action |
|---------|--------|
| NewOrder | Insert order at correct price level |
| OrderCancel | Remove order by ID, collapse empty levels |
| Execution | Reduce order quantity, remove if zero |
| IncrementalRefresh | Set level aggregate quantity directly |

### MetricsService

**Purpose:** Demonstrate the performance impact of I/O on the hot path.

**Two operating modes:**

| Mode | Mechanism | Impact |
|------|-----------|--------|
| Hot-path | Synchronous `SortedSetAdd` per metric | ~98% throughput loss |
| Buffered | `ConcurrentQueue` + timer-based async flush | ~10% overhead |

The hot-path mode exists intentionally as a diagnostic tool — it quantifies the cost of synchronous I/O in latency-critical code paths.

## Gateway Architecture

### SignalR Distribution

The Gateway uses ASP.NET Core SignalR for WebSocket-based real-time market data distribution:

- `BookHub` — Central hub for market data subscriptions
- `BookHubNotifier` — Background service that reads cold-path snapshots and broadcasts to connected clients
- Group-based subscription model (clients subscribe to specific symbols)

### Redis Integration

Three Redis patterns for different use cases:

| Pattern | Redis Type | Purpose |
|---------|-----------|---------|
| Snapshot Storage | Strings (JSON) | Latest full book snapshot per symbol |
| Incremental Updates | Streams | Append-only log of book changes |
| Notifications | Pub/Sub | Real-time change notification to Gateway instances |

### REST API

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/api/feed/status` | GET | Pipeline statistics and buffer depths |
| `/api/feed/start` | POST | Start the processing pipeline |
| `/api/feed/stop` | POST | Stop the processing pipeline |
| `/api/feed/book/{symbol}` | GET | Current book snapshot for a symbol |

## IPC via Shared Memory

The Producer and Consumer applications communicate through `SharedRingBuffer`, which wraps `MemoryMappedFile` for inter-process shared memory:

```
Process A (Producer)                    Process B (Consumer)
┌──────────────┐                       ┌──────────────┐
│ TryWrite()   │ ──► Shared Memory ──► │ TryRead()    │
│              │     (MMF + View)      │              │
└──────────────┘                       └──────────────┘
```

The shared memory region contains the ring buffer's backing array and position counters, enabling zero-copy cross-process data exchange without serialization overhead.

> **Platform note:** `SharedRingBuffer` uses Windows-specific `MemoryMappedFile.CreateOrOpen` and `OpenExisting` APIs, annotated with `[SupportedOSPlatform("windows")]`.

## Build Configuration

### Directory.Build.props

Centralized build settings applied to all projects:

- `TreatWarningsAsErrors=true` — Zero-warning policy
- `EnforceCodeStyleInBuild=true` — Code style enforcement at build time
- `Nullable=enable` — Nullable reference type analysis
- `LangVersion=latest` — Latest C# features
- `TargetFramework=net9.0` — .NET 9.0 across all projects

## Performance Considerations

### What this architecture optimizes for

- **Latency** — Sub-microsecond message processing on the hot path
- **Determinism** — No GC pauses during processing (struct-based, pre-allocated)
- **Throughput** — Lock-free buffers sustain millions of operations per second

### What a production system would add

- Thread affinity / CPU pinning for deterministic scheduling
- Kernel bypass (e.g., DPDK, Solarflare OpenOnload) for UDP reception
- NUMA-aware memory allocation
- Full FAST template specification (nullable fields, copy/increment operators, dictionary compression)
- Failover, sequence gap detection, and market data recovery
- Hardware timestamping for nanosecond-precision latency measurement
