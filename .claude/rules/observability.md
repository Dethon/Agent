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

## The dashboard's live connection

`MetricsLiveConnection` owns being live, and it is the only thing that does. Becoming live is one
ordered sequence inside it: bind the handlers to the hub connection, start it retrying until it
succeeds, publish the status, then catch up. Steps three and four also run when the transport
reconnects on its own. The layout calls connect and catches nothing, because the module does not
fail — it keeps trying.

- **The seam is `IMetricsHubConnection`**, one generic receive verb keyed by wire method name plus
  the three lifecycle events. A twelfth server push is a line in the binder, not a member on the
  interface, the implementation and the fake. Never hand-write a named registration method.
- **`MetricsRetryPolicy` is the one schedule**: zero, two, ten, thirty seconds, then thirty
  forever, and it never returns the value that means stop. It drives both automatic reconnection and
  the module's own first-start loop, which delays through the injected `TimeProvider`. Automatic
  reconnection has never covered the first attempt, so replacing only the policy would leave a
  dashboard opened during a deploy just as dead as before.
- **The started latch records a start that succeeded**, never one that was attempted.
- **`ConnectionStore` is the only source of connection status**: connecting, live or reconnecting,
  with no permanent disconnected state, because the module never gives up. The page-load path does
  not report a failed request as a lost connection. The indicator lives in the layout, so every page
  shows it; the overview reads the same store.
- **`MetricsCatchUp` walks the family table** for the range each family already holds, so a
  recovery does not move the user's group-by, metric or time choices. It is awaited as the last step
  of becoming live, and skipped when `ConnectionState.Epoch` is 1, where ordinary page load fetches
  the same data. A failure inside it is logged and leaves the connection live. It does not reload
  the overview's summary totals, which stay short until the next page load.
- `MetricsHubEffect` is the binder and nothing else: the mapping from a push to a store update and a
  family refresh, with `Bind` and `Unbind` driven by the module.

Health tiles come from `ServiceHealthRegistry`, a sorted-set roster (`metrics:health:seen`) scored by *last registration*, not last health — reachability is the separate TTL'd `metrics:health:<service>` key. Services publishing `HeartbeatEvent`s register themselves; third-party containers are registered by `HttpHealthProbeService`, which polls the URLs in `HttpProbes` (`Observability/appsettings.json`) and treats **any** HTTP response, even non-2xx, as up. A probe target re-registers every cycle whether or not it answers, so a down service stays visible as a red tile, while a retired one stops registering and ages off after `Retention` (7 days).

Key files: metric DTOs `Domain/DTOs/Metrics/*.cs` (dimension/metric enums in `Enums/`), publisher `Infrastructure/Metrics/*.cs`, `Observability/Services/*.cs` (incl. `MetricsQueryService.cs`), API endpoints `Observability/MetricsApiEndpoints.cs`, dashboard `Dashboard.Client/{Pages,Components,Services}/` with state in `Dashboard.Client/State/**/*.cs`.
