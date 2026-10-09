# Realtime Sales Dashboard

A live sales dashboard. Events land in a rolling in-memory window, and every connected browser receives a snapshot over SignalR. There is no polling.

## Overview

The API accepts sales events, keeps only the recent window, and pushes an immutable snapshot to the dashboard. A synthetic feed can run with no database and no external service.

## Features

- Sliding window with eviction on write, plus a hard `maxEvents` cap
- Timeline buckets pre-seeded with zeros, so a quiet interval is a zero bar
- Reader/writer lock so snapshots can run while one producer writes
- A new SignalR client receives the current snapshot immediately
- Broadcast is skipped when nobody is connected
- Invalid events are rejected before they enter the window

## Technology Stack

- ASP.NET Core 10
- SignalR
- xUnit
- A hand-built SVG chart in `wwwroot`, no chart library

## Architecture

Ingest writes. Snapshots read. `ReaderWriterLockSlim` lets readers run together and serializes the writer. A snapshot that was already pushed does not change when the next event arrives.

Window expiry is tested with an injected `TimeProvider`, so a six-minute expiry test does not sleep. SignalR tests use a real `HubConnection` on `WebApplicationFactory`.

## Installation

```bash
dotnet run --project src/RealtimeBi.Api
```

Open `http://localhost:5240`. The dashboard connects on load and starts updating within a second.

Feed settings live under `Feed` in `appsettings.json`: `EventsPerSecond`, `BroadcastIntervalMs`, `WindowMinutes`, `BucketSeconds`, `GenerateSyntheticEvents`. Invalid values fail at startup.

## Usage

| Method | Path | Purpose |
| --- | --- | --- |
| `GET` | `/health` | Window size, retained events, connected clients |
| `GET` | `/api/snapshot` | Current snapshot over HTTP |
| `POST` | `/api/events` | Inject one sales event |
| hub | `/hub/dashboard` | SignalR. Push is `snapshot`. Pull is `RequestSnapshot` |

```bash
curl -X POST http://localhost:5240/api/events \
  -H "Content-Type: application/json" \
  -d "{\"orderId\":\"ORD-1\",\"region\":\"West\",\"channel\":\"Web\",\"amount\":249.99,\"occurredAt\":\"2026-08-03T10:15:00Z\"}"
```

## Testing

```bash
dotnet test
```

28 tests cover aggregation, the window, the timeline, 4000 parallel ingests, validation, HTTP, and SignalR.

## Limitations

State is in memory. A restart clears the window. There is no database.

## License

MIT
