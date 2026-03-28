# Handoff: Memory Observability Integration

**Date:** 2026-03-28
**Status:** Not started
**Prerequisite:** Proactive memory system (`proactive-memory-impl` branch) must be merged first

---

## Context

The proactive memory system (Tasks 1-20) introduces three new metric event DTOs that are published by memory infrastructure but **not yet handled by the Observability service**:

| Event | Published by | What it captures |
|-------|-------------|-----------------|
| `MemoryRecallEvent` | `MemoryRecallHook` | Duration, memory count, userId per recall |
| `MemoryExtractionEvent` | `MemoryExtractionWorker` | Duration, candidate count, stored count, userId per extraction |
| `MemoryDreamingEvent` | `MemoryDreamingService` | Merged count, decayed count, profile regenerated, userId per dreaming cycle |

These events are correctly:
- Defined in `Domain/DTOs/Metrics/` with `[JsonDerivedType]` attributes on `MetricEvent`
- Published via `IMetricsPublisher` in the infrastructure layer
- Serialized through Redis Pub/Sub on the `metrics:events` channel

But they are **silently dropped** in `Observability/Services/MetricsCollectorService.cs` at the `switch (evt)` statement (line 92) — no matching `case` exists for any of them.

## Gap Analysis

### MetricsCollectorService (`Observability/Services/MetricsCollectorService.cs`)

Needs new `case` branches in the `switch (evt)` block for:
- `case MemoryRecallEvent recall:` → store recall duration, memory count time-series
- `case MemoryExtractionEvent extraction:` → store extraction duration, candidate/stored counts
- `case MemoryDreamingEvent dreaming:` → store merge/decay counts, profile regeneration flag

Follow the existing patterns for `ProcessTokenUsageAsync`, `ProcessToolCallAsync`, etc. Each handler stores data in Redis sorted sets (time-series) and hashes (totals).

### MetricsQueryService (`Observability/Services/MetricsQueryService.cs`)

Needs query support for memory metrics — grouped aggregation by userId, time range, etc. Follow existing dimension/metric enum patterns.

### MetricsApiEndpoints (`Observability/MetricsApiEndpoints.cs`)

Needs REST API endpoints for the Dashboard to query memory metrics (e.g., `/api/metrics/memory/recall`, `/api/metrics/memory/extraction`, `/api/metrics/memory/dreaming`).

### Metric Dimension/Metric Enums (`Domain/DTOs/Metrics/Enums/`)

May need new enum values for memory-related dimensions and metrics so the existing query infrastructure can handle them.

### Dashboard (Optional, separate task)

Dashboard pages/components to visualize memory metrics (recall latency, extraction throughput, dreaming cycle stats). This is a separate scope and can be done later.

## Files to Modify

| File | Change |
|------|--------|
| `Observability/Services/MetricsCollectorService.cs` | Add case handlers for 3 new events |
| `Observability/Services/MetricsQueryService.cs` | Add memory metric query support |
| `Observability/MetricsApiEndpoints.cs` | Add memory metric API endpoints |
| `Domain/DTOs/Metrics/Enums/*.cs` | Add memory dimension/metric enum values |
| `Dashboard.Client/` | (Optional) Memory metrics visualization |

## How to Verify the Gap

```bash
# Shows the switch statement that drops unknown events:
grep -n "case.*Event" Observability/Services/MetricsCollectorService.cs

# Shows no memory references in Observability:
grep -rn "Memory.*Event\|Recall\|Extraction\|Dreaming" Observability/
```
