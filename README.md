# Realtime Sales BI

A live sales dashboard: events stream into a rolling in-memory window, and
snapshots are pushed to every connected browser over **SignalR** — no polling,
no page refresh.

**ASP.NET Core 10 · SignalR · minimal APIs · xUnit.** No database, no external
services; `dotnet run` and it works.

---

## What it does

```
 producer ──▶ SlidingWindowAggregator ──▶ broadcast tick ──▶ SignalR ──▶ browsers
 (12/s)        (5-minute window,           (every 1s)
                30s buckets)
```

The two loops are **deliberately decoupled**. Broadcasting per event would
flood clients at high throughput; broadcasting on a fixed tick keeps the client
cost flat no matter how fast events arrive. At 12 events/sec that is 12 ingests
and 1 push per second.

Events reach the aggregator either from the built-in synthetic producer or by
`POST /api/events` from a real order system.

---

## Measured on a live run

After ~4 minutes at 12 events/sec:

| | |
|---|---|
| Revenue in window | £163,662.19 |
| Orders in window | 1,698 |
| Average order | £96.39 |
| Push interval | 998 ms (target 1000 ms) |
| Timeline | 10 buckets × 30 s |

Regional split came out North £47.6k / South £38.4k / East £32.3k / West £23.7k
/ Central £16.9k — tracking the generator's configured weights, which is the
cheap sanity check that aggregation is grouping correctly.

---

## Quick start

```bash
dotnet run --project src/RealtimeBi.Api
```

Then open <http://localhost:5240>. The dashboard connects on load and starts
updating within a second.

```bash
dotnet test        # 28 tests
```

---

## API

| Method | Endpoint | Purpose |
|---|---|---|
| `GET` | `/health` | Window size, events held, connected clients |
| `GET` | `/api/snapshot` | Current snapshot over plain HTTP |
| `POST` | `/api/events` | Push a real sales event into the window |
| — | `/hub/dashboard` | SignalR hub — `snapshot` push, `RequestSnapshot` pull |

```bash
curl -X POST http://localhost:5240/api/events \
  -H "Content-Type: application/json" \
  -d '{"orderId":"ORD-1","region":"West","channel":"Web",
       "amount":249.99,"occurredAt":"2026-08-03T10:15:00Z"}'
```

Configuration lives under `Feed` in `appsettings.json`: `EventsPerSecond`,
`BroadcastIntervalMs`, `WindowMinutes`, `BucketSeconds`,
`GenerateSyntheticEvents`. Invalid values fail at **startup**, not on first
request.

---

## Design decisions worth explaining

**Reader/writer lock, not a plain `lock`.** Ingest writes; snapshots read. Many
threads snapshot concurrently (the broadcast loop, HTTP requests, hub calls)
while only the producer writes, so `ReaderWriterLockSlim` lets readers run in
parallel and serialises only the writer.

**Snapshots are immutable records.** A snapshot pushed to a client can never be
mutated by the next event to arrive — a test asserts that a snapshot taken
before an ingest still shows the old total.

**Eviction happens on write, not on a timer.** No background sweeper to keep
alive, and the buffer cannot grow between sweeps. There is also a hard
`maxEvents` cap, because future-dated events would never age out by time alone.

**The timeline pre-seeds every bucket.** A quiet 30 seconds renders as a zero
bar, not as a gap the chart has to interpolate across.

**Connect pushes immediately.** A client that has just connected would
otherwise stare at an empty screen until the next tick. `OnConnectedAsync`
sends the current snapshot straight to the caller.

**Broadcast skips when nobody is watching.** If no clients are connected the
loop does not build a projection at all.

**Validation at the edge.** `SalesEvent.Validate()` runs before an event can
enter the window, so one malformed payload cannot corrupt a running aggregate.
`POST /api/events` returns the specific field errors.

---

## Tests — 28

| Area | Covers |
|---|---|
| Aggregation | totals, breakdowns, sorting, rounding to cents |
| Window | expiry, boundary, eviction, out-of-order arrivals |
| Timeline | bucket count, zero-fill, ordering, totals reconcile |
| Concurrency | 4,000 parallel ingests lose nothing; snapshots never tear |
| Validation | bad amounts, nulls, bucket larger than window |
| HTTP | health, snapshot, event push, field-level errors |
| SignalR | snapshot on connect, live broadcast, on-demand pull, connection counting |

Window behaviour is tested against an injected `TimeProvider`, so a 6-minute
expiry test runs instantly instead of sleeping.

The SignalR tests use a real `HubConnection` against `WebApplicationFactory` —
they exercise the actual transport, not a mock.

---

## Layout

```
realtime-bi/
├── src/RealtimeBi.Api/
│   ├── Domain/          # SalesEvent, DashboardSnapshot (immutable records)
│   ├── Services/        # SlidingWindowAggregator, generator, FeedWorker, options
│   ├── Hubs/            # DashboardHub
│   ├── wwwroot/         # dashboard (hand-rolled SVG chart, no chart library)
│   └── Program.cs
└── tests/RealtimeBi.Tests/
    ├── AggregatorTests.cs
    └── ApiTests.cs
```

## License

MIT
