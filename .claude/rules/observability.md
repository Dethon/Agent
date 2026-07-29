---
paths:
  - "Observability/**"
  - "Dashboard.Client/**"
  - "Infrastructure/Metrics/**"
  - "Domain/DTOs/Metrics/**"
---

# Observability Architecture

Services publish `MetricEvent` DTOs via `IMetricsPublisher` → Redis Pub/Sub channel `metrics:events`. `MetricsCollectorService` subscribes, aggregates into Redis (sorted sets for time-series, hashes for totals, TTL keys for health), and forwards live events to the SignalR hub (`/hubs/metrics`); `MetricsQueryService` serves grouped aggregations by dimension/metric enum. The dashboard is hybrid: REST for history on page load, SignalR for live updates, `LocalStorageService` for UI state.

Health tiles come from `ServiceHealthRegistry`, a sorted-set roster (`metrics:health:seen`) scored by *last registration*, not last health — reachability is the separate TTL'd `metrics:health:<service>` key. Services publishing `HeartbeatEvent`s register themselves; third-party containers are registered by `HttpHealthProbeService`, which polls the URLs in `HttpProbes` (`Observability/appsettings.json`) and treats **any** HTTP response, even non-2xx, as up. A probe target re-registers every cycle whether or not it answers, so a down service stays visible as a red tile, while a retired one stops registering and ages off after `Retention` (7 days).

Key files: metric DTOs `Domain/DTOs/Metrics/*.cs` (dimension/metric enums in `Enums/`), publisher `Infrastructure/Metrics/*.cs`, `Observability/Services/*.cs` (incl. `MetricsQueryService.cs`), API endpoints `Observability/MetricsApiEndpoints.cs`, dashboard `Dashboard.Client/{Pages,Components,Services}/` with state in `Dashboard.Client/State/**/*.cs`.
