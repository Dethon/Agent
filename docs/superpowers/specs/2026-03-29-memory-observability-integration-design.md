# Memory Observability Integration — Design Spec

**Date:** 2026-03-29
**Status:** Approved
**Prerequisite:** Proactive memory system (`proactive-memory-impl` branch) must be merged first

---

## Overview

Integrate the three memory metric events (MemoryRecallEvent, MemoryExtractionEvent, MemoryDreamingEvent) into the full observability stack: Redis storage, query service, REST API, SignalR live updates, and a new Dashboard Memory page. Follows the Unified Metrics Pattern — the exact same conventions used by Token/Tool/Error/Schedule events.

## Scope

- MetricsCollectorService: 3 new case handlers
- MetricsQueryService: event retrieval + grouped aggregation
- MetricsApiEndpoints: 4 new REST endpoints
- New enums: MemoryDimension, MemoryMetric
- MetricsSummary: 6 new fields
- SignalR: 3 new hub broadcast methods + client handlers
- Dashboard: Memory page with KPIs, chart, tabbed event tables
- Dashboard state: MemoryStore, MemoryState, MemoryEffect

---

## 1. Redis Storage & Collector

### MetricsCollectorService — New Case Handlers

Three new cases in the `switch (evt)` block, each following the existing `ProcessTokenUsageAsync`/`ProcessToolCallAsync` patterns.

### Redis Keys

| Event | Sorted Set (time-series) | TTL |
|-------|-------------------------|-----|
| MemoryRecallEvent | `metrics:memory-recall:{yyyy-MM-dd}` | 30 days |
| MemoryExtractionEvent | `metrics:memory-extraction:{yyyy-MM-dd}` | 30 days |
| MemoryDreamingEvent | `metrics:memory-dreaming:{yyyy-MM-dd}` | 30 days |

Each event is JSON-serialized and stored with a Unix timestamp (ms) score, identical to how TokenUsageEvent and ToolCallEvent are stored.

### Hash Totals (`metrics:totals:{yyyy-MM-dd}`)

**Recall fields:**
- `memory:recalls` — increment by 1
- `memory:recallDuration` — increment by DurationMs (cumulative)
- `memory:recallMemories` — increment by MemoryCount (cumulative)
- `memory:byUser:{userId}` — increment by 1

**Extraction fields:**
- `memory:extractions` — increment by 1
- `memory:extractionDuration` — increment by DurationMs (cumulative)
- `memory:candidates` — increment by CandidateCount
- `memory:stored` — increment by StoredCount
- `memory:byUser:{userId}` — increment by 1

**Dreaming fields:**
- `memory:dreamings` — increment by 1
- `memory:merged` — increment by MergedCount
- `memory:decayed` — increment by DecayedCount
- `memory:profileRegens` — increment by 1 if ProfileRegenerated is true

### SignalR Broadcast

Each handler broadcasts to all connected clients:
- `hubContext.Clients.All.SendAsync("OnMemoryRecall", recall)`
- `hubContext.Clients.All.SendAsync("OnMemoryExtraction", extraction)`
- `hubContext.Clients.All.SendAsync("OnMemoryDreaming", dreaming)`

---

## 2. Enums

### `Domain/DTOs/Metrics/Enums/MemoryDimension.cs`

```csharp
public enum MemoryDimension { User, EventType, Agent }
```

- **User** — groups by UserId
- **EventType** — groups by "Recall" / "Extraction" / "Dreaming"
- **Agent** — groups by AgentId (inherited from MetricEvent base)

### `Domain/DTOs/Metrics/Enums/MemoryMetric.cs`

```csharp
public enum MemoryMetric { Count, AvgDuration, StoredCount, MergedCount, DecayedCount }
```

- **Count** — total operations
- **AvgDuration** — average DurationMs (only meaningful for Recall + Extraction; Dreaming has no duration)
- **StoredCount** — total memories stored (from extraction events)
- **MergedCount** — total memories merged (from dreaming events)
- **DecayedCount** — total memories decayed (from dreaming events)

---

## 3. Query Service

### MetricsQueryService — New Methods

| Method | Returns | Description |
|--------|---------|-------------|
| `GetMemoryRecallEventsAsync(from, to)` | `IReadOnlyList<MemoryRecallEvent>` | Deserialize from `metrics:memory-recall:{date}` sorted sets |
| `GetMemoryExtractionEventsAsync(from, to)` | `IReadOnlyList<MemoryExtractionEvent>` | Deserialize from `metrics:memory-extraction:{date}` sorted sets |
| `GetMemoryDreamingEventsAsync(from, to)` | `IReadOnlyList<MemoryDreamingEvent>` | Deserialize from `metrics:memory-dreaming:{date}` sorted sets |
| `GetMemoryGroupedAsync(dimension, metric, from, to)` | `Dictionary<string, decimal>` | Grouped aggregation across all 3 event types |

### GetMemoryGroupedAsync Logic

Reads events from all 3 sorted sets for the date range, then:

**Grouping by dimension:**
- `User` → key is `UserId`
- `EventType` → key is `"Recall"`, `"Extraction"`, or `"Dreaming"`
- `Agent` → key is `AgentId`

**Aggregating by metric:**
- `Count` → count of events per group
- `AvgDuration` → average DurationMs (Recall + Extraction only; Dreaming events contribute 0)
- `StoredCount` → sum of StoredCount from Extraction events
- `MergedCount` → sum of MergedCount from Dreaming events
- `DecayedCount` → sum of DecayedCount from Dreaming events

### MetricsSummary — New Fields

Add to `MetricsSummary` record:
- `long TotalRecalls` — from `memory:recalls` hash field
- `long TotalExtractions` — from `memory:extractions` hash field
- `long TotalDreamings` — from `memory:dreamings` hash field
- `long MemoriesStored` — from `memory:stored` hash field
- `long MemoriesMerged` — from `memory:merged` hash field
- `long MemoriesDecayed` — from `memory:decayed` hash field

`GetSummaryAsync` reads these from `metrics:totals:{date}` hashes, same as existing token/tool fields.

---

## 4. REST API Endpoints

New endpoints under `/api/metrics/memory`:

| Method | Endpoint | Query Params | Returns |
|--------|----------|-------------|---------|
| GET | `/memory/recall` | `from?`, `to?` (DateOnly) | `List<MemoryRecallEvent>` |
| GET | `/memory/extraction` | `from?`, `to?` | `List<MemoryExtractionEvent>` |
| GET | `/memory/dreaming` | `from?`, `to?` | `List<MemoryDreamingEvent>` |
| GET | `/memory/by/{dimension}` | `metric`, `from?`, `to?` | `Dictionary<string, decimal>` |

Default behavior: `from`/`to` default to today's DateOnly if omitted. The grouped endpoint parses `MemoryDimension` and `MemoryMetric` enums from route/query params.

The existing `/api/metrics/summary` endpoint automatically picks up the new MetricsSummary fields.

---

## 5. SignalR & Dashboard State

### MetricsHubService — New Client Handlers

Register 3 new handlers matching the existing pattern:
- `OnMemoryRecall(Action<MemoryRecallEvent> handler)`
- `OnMemoryExtraction(Action<MemoryExtractionEvent> handler)`
- `OnMemoryDreaming(Action<MemoryDreamingEvent> handler)`

### Dashboard State — New Files

**`MemoryState.cs`:**
- `IReadOnlyList<MemoryRecallEvent> RecallEvents`
- `IReadOnlyList<MemoryExtractionEvent> ExtractionEvents`
- `IReadOnlyList<MemoryDreamingEvent> DreamingEvents`
- `MemoryDimension GroupBy`
- `MemoryMetric Metric`
- `Dictionary<string, decimal> Breakdown`
- `int Days` (date range: 1, 7, 30)

**`MemoryStore.cs`:**
Reducer actions: SetRecallEvents, SetExtractionEvents, SetDreamingEvents, AppendRecall, AppendExtraction, AppendDreaming, SetBreakdown, SetGroupBy, SetMetric, SetDays. Uses `record with { }` immutable updates.

**`MemoryEffect.cs`:**
- On page load: fetches all 3 event lists + grouped breakdown via REST API
- On GroupBy/Metric/Days change: re-fetches breakdown
- Subscribes to SignalR events: appends to relevant list, refreshes breakdown (with cancellation token for concurrent requests)

### MetricsStore/MetricsState — New Fields

Add 6 fields: `TotalRecalls`, `TotalExtractions`, `TotalDreamings`, `MemoriesStored`, `MemoriesMerged`, `MemoriesDecayed`. Populated from `/summary` on load, incremented by SignalR events.

### MetricsHubEffect — New Subscriptions

3 new SignalR subscriptions that dispatch to MemoryStore (append events) and increment MetricsStore counters.

---

## 6. Dashboard Memory Page

### Route: `/memory`

### Layout (top to bottom)

1. **Top bar** — PillSelector row at page level:
   - Left: Group By pills (User / EventType / Agent) + Metric pills (Count / AvgDuration / Stored / Merged / Decayed)
   - Right: Days pills (Today / 7d / 30d)
   - All selections persisted via LocalStorage keys: `memory.groupBy`, `memory.metric`, `memory.days`

2. **KPI cards** — 6 cards in a grid row:
   - Total Recalls (blue)
   - Total Extractions (green)
   - Avg Latency (amber) — computed as `(recallDuration + extractionDuration) / (recalls + extractions)`
   - Memories Stored (purple)
   - Merged (pink)
   - Decayed (red)

3. **Breakdown chart** — DynamicChart (HorizontalBar mode), data from `GetMemoryGroupedAsync`

4. **Tabbed event tables** — single card with tab bar:
   - 3 tabs: Recall, Extraction, Dreaming
   - Active tab has blue underline + blue text; inactive tabs muted
   - Active tab persisted via LocalStorage key `memory.activeTab`
   - Each tab renders its own table (last 50 events, sorted by time desc):

   **Recall table columns:** Time, User, Duration (ms), Memories Found
   **Extraction table columns:** Time, User, Duration (ms), Candidates, Stored
   **Dreaming table columns:** Time, User, Merged, Decayed, Profile Regen (checkmark/dash)

### Navigation

Add "Memory" entry to the Dashboard sidebar/nav, positioned after "Schedules".

---

## Files to Create

| File | Purpose |
|------|---------|
| `Domain/DTOs/Metrics/Enums/MemoryDimension.cs` | Dimension enum |
| `Domain/DTOs/Metrics/Enums/MemoryMetric.cs` | Metric enum |
| `Dashboard.Client/Pages/Memory.razor` | Memory page |
| `Dashboard.Client/State/Memory/MemoryState.cs` | State record |
| `Dashboard.Client/State/Memory/MemoryStore.cs` | Store with reducers |
| `Dashboard.Client/State/Memory/MemoryEffect.cs` | Side effects (API + SignalR) |

## Files to Modify

| File | Change |
|------|--------|
| `Observability/Services/MetricsCollectorService.cs` | Add 3 case handlers + process methods |
| `Observability/Services/MetricsQueryService.cs` | Add 4 query methods + summary fields |
| `Observability/MetricsApiEndpoints.cs` | Add 4 REST endpoints |
| `Domain/DTOs/Metrics/MetricsSummary.cs` | Add 6 memory fields |
| `Dashboard.Client/Services/MetricsHubService.cs` | Add 3 handler registrations |
| `Dashboard.Client/State/Metrics/MetricsState.cs` | Add 6 memory counter fields |
| `Dashboard.Client/State/Metrics/MetricsStore.cs` | Add memory counter reducers |
| `Dashboard.Client/State/Metrics/MetricsHubEffect.cs` | Add 3 SignalR subscriptions |
| `Dashboard.Client/Shared/NavMenu.razor` (or equivalent) | Add Memory nav entry |
