# Latency Instrumentation & Dashboard — Design Spec

**Date:** 2026-05-16
**Status:** Approved (design); pending implementation plan
**Origin:** Throughput review suggestion #6 — "instrument the hot path so bottlenecks are measured, not guessed," with the timers visible on the observability dashboard in a way that makes sense.

## Goal

Measure per-stage latency of the agent turn and surface it on the Observability dashboard so the bottleneck stage is obvious at a glance and regressions over time are visible.

## Decisions (from brainstorming)

1. **Per-stage latency events** — each hot-path stage independently emits a `LatencyEvent`. No per-turn correlation id (YAGNI; revisit only if per-turn drilldown is needed later).
2. **One unified stream covering all stages** — including `MemoryRecall` and `ToolExec`, which already emit their own duration on `MemoryRecallEvent`/`ToolCallEvent`. Those existing events are unchanged (they serve the Memory/Tools pages and track non-timing data); the `LatencyEvent` stream is the single, consistent source for the Latency page. The small duplicate emission of recall/tool duration is accepted in exchange for a trivial single-source query and decoupled pages.
3. **Combined dashboard page (layout C)** — KPI chips + per-stage bars + percentile trend line + recent-slow-turns table.

## Non-Goals / Scope Guardrails (YAGNI)

- No per-turn/turn-correlation id threaded through the pipeline.
- No new Redis infrastructure (reuse Pub/Sub channel `metrics:events` and sorted-set/hash storage pattern).
- No changes to existing Tokens/Tools/Memory/Errors/Schedules pages or their events.
- Percentiles computed in-query over the stored events (no histograms/HDR/streaming quantiles). Acceptable at current event volume; revisit only if query latency becomes a problem.
- Latency emission is best-effort and MUST NOT fail or slow a turn.

## 1. Data Model & Emission

New DTO `Domain/DTOs/Metrics/LatencyEvent.cs`:

```csharp
public sealed record LatencyEvent : MetricEvent
{
    public required LatencyStage Stage { get; init; }
    public required long DurationMs { get; init; }
    public string? Model { get; init; }   // set for LlmFirstToken / LlmTotal
    // inherited: Timestamp, AgentId, ConversationId
}
```

Register on the base type: `[JsonDerivedType(typeof(LatencyEvent), "latency")]` in `Domain/DTOs/Metrics/MetricEvent.cs`.

New enums in `Domain/DTOs/Metrics/Enums/`:

- `LatencyStage { SessionWarmup, MemoryRecall, LlmFirstToken, LlmTotal, ToolExec, HistoryStore }`
- `LatencyDimension { Stage, Agent, Model }`
- `LatencyMetric { Avg, P50, P95, P99, Count, Max }`

Emission uses the existing `IMetricsPublisher.PublishAsync` (Redis Pub/Sub channel `metrics:events`, camelCase JSON, `type` discriminator). No publisher/infra change.

`MemoryRecall` and `ToolExec` emit a `LatencyEvent` **in addition to** their current `MemoryRecallEvent`/`ToolCallEvent` (the latter are left exactly as-is).

## 2. Collector / Query / API

**Collector** (`Observability/Services/MetricsCollectorService.cs`):
- Add a `LatencyEvent` case to `ProcessEventAsync` and a `ProcessLatencyAsync(LatencyEvent, IDatabase)` handler mirroring `ProcessMemoryRecallAsync`:
  - Sorted set `metrics:latency:{date}` — full event JSON, score = `Timestamp.ToUnixTimeMilliseconds()`, 30-day TTL (`_dailyKeyTtl`).
  - Hash increments on `metrics:totals:{date}` for cheap summary (e.g. `latency:{stage}:count`, `latency:{stage}:totalMs`).
  - Broadcast `hubContext.Clients.All.SendAsync("OnLatency", evt)`.

**Query** (`Observability/Services/MetricsQueryService.cs`):
- `GetLatencyGroupedAsync(LatencyDimension dimension, LatencyMetric metric, DateOnly from, DateOnly to) -> Dictionary<string, decimal>` — fetch `LatencyEvent`s via the existing generic `GetEventsAsync<T>("metrics:latency:", ...)`, group by dimension, aggregate by metric.
- `static decimal ComputePercentile(IEnumerable<decimal> values, decimal q)` — sort + nearest-rank index selection. Used for P50/P95/P99.
- `GetLatencyTrendAsync(LatencyMetric metric, DateOnly from, DateOnly to) -> Dictionary<LatencyStage, IReadOnlyList<(DateTimeOffset Bucket, decimal Value)>>` — events bucketed by time for the trend line. Bucket granularity: hourly for ranges ≤ 2 days, daily otherwise (chosen to keep the line readable; adjustable).

**API** (`Observability/MetricsApiEndpoints.cs`):
- `GET /api/metrics/latency?from&to` → `List<LatencyEvent>`
- `GET /api/metrics/latency/by/{dimension}?metric&from&to` → `Dictionary<string,decimal>`
- `GET /api/metrics/latency/trend?metric&from&to` → trend series DTO

## 3. Dashboard — Combined Latency Page (layout C)

Follows the `Tokens.razor` pattern (REST on load + SignalR live updates + Redux-like store + `LocalStorageService` persistence).

New/changed client files:
- `Dashboard.Client/Pages/Latency.razor` — page.
- `Dashboard.Client/State/.../LatencyStore.cs` + `LatencyState` — events, breakdown, trend, group/metric, from/to.
- `DataLoadEffect` — parallel `GetLatencyAsync`, `GetLatencyGroupedAsync`, `GetLatencyTrendAsync`.
- `MetricsHubEffect` — `hub.OnLatency(...)` → append event + refresh breakdown/trend.
- `MetricsApiService` — the three new client methods.
- `Dashboard.Client/Components/LatencyTrendChart.razor` — **the one genuinely new UI component**: a multi-series line chart (per stage, p-metric over time buckets) built on the **existing Blazor-ApexCharts dependency** (the same `ApexChart`/`ApexPointSeries` primitives `DynamicChart` uses), `SeriesType.Line`. Existing `DynamicChart` (donut/bar) is reused for the per-stage bars.
- Nav entry for the page.

Page composition:
- **KPI chips:** p50, p95, p99 across all stages combined for the selected range, plus slowest stage (the stage with the highest p95).
- **Per-stage bars:** `DynamicChart` HorizontalBar over `/latency/by/{dimension}`; `PillSelector` for metric (Avg/P50/P95/P99) and group (Stage/Agent/Model).
- **Trend:** `LatencyTrendChart` over `/latency/trend` (per-stage line for the selected metric).
- **Recent slow turns table:** top 50 `LatencyEvent`s by `DurationMs` in range (stage, duration, agent, conversation, timestamp), matching the 50-row convention of existing events tables.

## 4. Instrumentation Sites

`Stopwatch`-wrapped; `conversationId`/`agentId` taken from the context already available at each site. Emission wrapped so a publish failure/cancellation never affects the turn.

| Stage | Site |
|---|---|
| `SessionWarmup` | `Infrastructure/Agents/McpAgent.cs` — `WarmupSessionAsync` |
| `MemoryRecall` | `Infrastructure/Memory/MemoryRecallHook.cs` — `EnrichAsync` (already stopwatches; add `LatencyEvent` emit alongside `MemoryRecallEvent`) |
| `LlmFirstToken` / `LlmTotal` | `Infrastructure/Agents/McpAgent.cs` — `RunStreamingCoreAsync` (time to first yielded update; time to stream end). `Model` populated. |
| `ToolExec` | `Infrastructure/Agents/ChatClients/ToolApprovalChatClient.cs` — `InvokeWithMetricsAsync` (already stopwatches; add emit alongside `ToolCallEvent`) |
| `HistoryStore` | `Infrastructure/Agents/ChatClients/RedisChatMessageStore.cs` — `StoreChatHistoryAsync` |

## Testing (TDD)

Red → Green for every unit:
- `LatencyEvent` polymorphic (de)serialization round-trip (`type":"latency"`).
- `ComputePercentile` — known inputs (incl. tiny/edge sets).
- `GetLatencyGroupedAsync` + `GetLatencyTrendAsync` — `RedisFixture` integration, each `LatencyDimension`/`LatencyMetric`.
- Collector `ProcessLatencyAsync` — stores sorted set + totals + broadcasts (`RedisFixture`).
- API endpoints — route + DTO shape.
- Each instrumentation site — asserts a `LatencyEvent` with the correct `Stage` and `conversationId`/`agentId`/`Model` is published (mock `IMetricsPublisher`); assert a publisher exception does not propagate / fail the turn.
- Dashboard store/effects — reducer + hub-append + breakdown refresh, mirroring existing dashboard tests.

## Risks / Notes

- **Duplicate duration for recall/tool:** intentional; tiny extra event volume, keeps the Latency query single-source and pages decoupled.
- **In-query percentiles:** O(events in range); fine at current volume, flagged for revisit.
- **New trend chart:** the only net-new UI component; built on the dashboard's existing Blazor-ApexCharts dependency (`ApexChart` + `ApexPointSeries`, `SeriesType.Line`, dark theme), mirroring `DynamicChart.razor`'s house style rather than introducing a hand-rolled SVG primitive. (Earlier spec text assumed no charting dependency existed; that was incorrect — ApexCharts is already used dashboard-wide.)
- **Best-effort emission:** instrumentation must be wrapped so publish errors/cancellation are swallowed and never alter turn behavior or latency.
