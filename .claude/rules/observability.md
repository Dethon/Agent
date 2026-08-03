---
paths:
  - "Observability/**"
  - "Dashboard.Client/**"
  - "Infrastructure/Metrics/**"
  - "Domain/DTOs/Metrics/**"
  - "Domain/Metrics/**"
---

# Observability Architecture

## Publishing

Two roles. A **metrics publisher** (`IMetricsPublisher`, one `void Publish(MetricEvent)`) is what a caller holds: it cannot fail, cannot block and cannot be observed, so no call site has to decide what a failed publish means. A **metric sink** (`IMetricSink`, `Task SendAsync`, may throw) is the transport behind it; `RedisMetricSink` is its one adapter and lives in `Infrastructure/Metrics/` because Domain never consumes a sink. `BufferedMetricsPublisher` is the only publisher a host registers: publishing writes to a bounded channel (drop-on-full, logged), and a background reader drains into the sink, logging whatever it refuses. `docs/adr/0002-metrics-publishing-is-fire-and-forget.md` records why the interface is not awaitable — do not "fix" it back into a `Task`.

Becoming a metrics-publishing host is **one call**: `services.AddMetricsPublishing(serviceName)` registers the sink, the buffered publisher and the `HeartbeatService` together, so a host cannot resolve a bare sink as its caller-facing publisher and cannot publish without appearing on the health roster. `Tests/Integration/Metrics/MetricsRegistrationContractTests.cs` boots each host's real registration module and asserts both.

Measuring a span is a **scope**, not a stopwatch triple: `publisher.MeasureLatency(stage, conversationId, agentId, model)` (`Domain/Metrics/LatencyScope.cs`) publishes its `LatencyEvent` on disposal, covering the return path and the throw path from one statement, and exposes `ElapsedMilliseconds` for a site that also emits a domain-specific event carrying the same duration. It publishes on an early return too, so open it *after* any guard that can return before the measured work begins.

Optional publisher parameters coalesce once to `NoOpMetricsPublisher.Instance` where the publisher is stored, so no type null-checks before publishing.

## Collection

Published events reach the Redis Pub/Sub channel `metrics:events`. `MetricsCollectorService` subscribes, aggregates into Redis (sorted sets for time-series, hashes for totals, TTL keys for health), and forwards live events to the SignalR hub (`/hubs/metrics`); `MetricsQueryService` serves grouped aggregations by dimension/metric enum. The dashboard is hybrid: REST for history on page load, SignalR for live updates, `LocalStorageService` for UI state.

Health tiles come from `ServiceHealthRegistry`, a sorted-set roster (`metrics:health:seen`) scored by *last registration*, not last health — reachability is the separate TTL'd `metrics:health:<service>` key. Services publishing `HeartbeatEvent`s register themselves; third-party containers are registered by `HttpHealthProbeService`, which polls the URLs in `HttpProbes` (`Observability/appsettings.json`) and treats **any** HTTP response, even non-2xx, as up. A probe target re-registers every cycle whether or not it answers, so a down service stays visible as a red tile, while a retired one stops registering and ages off after `Retention` (7 days).

Key files: metric DTOs `Domain/DTOs/Metrics/*.cs` (dimension/metric enums in `Enums/`), publisher `Infrastructure/Metrics/*.cs`, `Observability/Services/*.cs` (incl. `MetricsQueryService.cs`), API endpoints `Observability/MetricsApiEndpoints.cs`, dashboard `Dashboard.Client/{Pages,Components,Services}/` with state in `Dashboard.Client/State/**/*.cs`.
