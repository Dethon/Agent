# Agent Observability Dashboard — Design Spec

Admin dashboard providing operational visibility into agent behavior: token usage & costs, tool call analytics, error rates, schedule execution history, and live service health.

## Audience & Access

- Single admin user (the operator)
- Per-user usage breakdowns available within the admin view
- Network-level access only (no app-level auth) — dashboard is only reachable on the local network/VPN
- Separate Blazor WASM application, not embedded in WebChat

## Architecture Overview

Three layers:

1. **Instrumentation** — services publish metric events to Redis Pub/Sub
2. **Collection & Storage** — `Observability` backend subscribes, aggregates, stores in Redis, and exposes APIs
3. **Dashboard UI** — `Dashboard.Client` Blazor WASM app consumes APIs

```
┌─────────────┐   ┌──────────────┐   ┌───────────────┐
│   Agent      │   │ McpChannel*  │   │ScheduleExec.  │
│ (tokens,     │   │ (future)     │   │ (schedule      │
│  tools,      │   │              │   │  events)       │
│  errors)     │   │              │   │                │
└──────┬───────┘   └──────┬───────┘   └───────┬────────┘
       │                  │                   │
       └──────────┬───────┴───────────────────┘
                  │  Redis Pub/Sub
                  │  channel: metrics:events
                  ▼
       ┌──────────────────────┐
       │   Observability      │
       │  ┌────────────────┐  │
       │  │ MetricsCollector│  │  ← BackgroundService subscribes to Pub/Sub
       │  │ (aggregates →   │  │
       │  │  Redis storage) │  │
       │  └────────────────┘  │
       │  ┌────────────────┐  │
       │  │ REST API        │  │  ← Historical queries
       │  └────────────────┘  │
       │  ┌────────────────┐  │
       │  │ SignalR Hub     │  │  ← Live streaming to dashboard
       │  │ /hubs/metrics   │  │
       │  └────────────────┘  │
       │  ┌────────────────┐  │
       │  │ Serves          │  │  ← Blazor WASM host
       │  │ Dashboard.Client│  │
       │  └────────────────┘  │
       └──────────────────────┘
```

## 1. Metric Emission (Instrumentation Layer)

### Contract

`Domain/Contracts/IMetricsPublisher.cs`:

```csharp
public interface IMetricsPublisher
{
    Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default);
}
```

### Metric Event DTOs

All in `Domain/DTOs/Metrics/`:

**Base record:**

```csharp
public abstract record MetricEvent
{
    public required string Type { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? AgentId { get; init; }
    public string? ConversationId { get; init; }
}
```

**Token usage** — emitted directly from `OpenRouterChatClient`. The client already reads `GetSenderId()` from user messages (to build the text prefix). Before streaming, it captures the sender from the last user message. After streaming completes, it publishes the event with sender, model, tokens, and cost:

```csharp
public record TokenUsageEvent : MetricEvent
{
    public required string Sender { get; init; }
    public required string Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal Cost { get; init; }  // USD from OpenRouter usage.cost
}
```

**Tool call** — emitted from `ToolApprovalChatClient.InvokeFunctionAsync()`:

```csharp
public record ToolCallEvent : MetricEvent
{
    public required string ToolName { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
```

**Error** — emitted from catch blocks in key services:

```csharp
public record ErrorEvent : MetricEvent
{
    public required string Service { get; init; }
    public required string ErrorType { get; init; }
    public required string Message { get; init; }
}
```

**Schedule execution** — emitted from `ScheduleExecutor.ProcessScheduleAsync()`:

```csharp
public record ScheduleExecutionEvent : MetricEvent
{
    public required string ScheduleId { get; init; }
    public required string Prompt { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
```

**Heartbeat** — emitted by each service periodically:

```csharp
public record HeartbeatEvent : MetricEvent
{
    public required string Service { get; init; }
}
```

### Implementation

`Infrastructure/Metrics/RedisMetricsPublisher.cs`:

- Implements `IMetricsPublisher`
- Uses `ISubscriber.PublishAsync("metrics:events", json)`
- Serializes with `System.Text.Json` using a type discriminator on the `Type` field

### Instrumentation Points

| Event | Where | What to capture |
|-------|-------|----------------|
| Token usage | `OpenRouterChatClient.GetStreamingResponseAsync()` | Before streaming, capture sender from the last user message via `GetSenderId()` (already called for prefix building). Extract `usage.cost` from the raw OpenRouter JSON in the final SSE chunk via a separate extraction path (e.g., a parallel `ConcurrentQueue<decimal?>` or callback alongside the existing reasoning queue in `ReasoningTeeStream`). The Microsoft.Extensions.AI `UsageContent` provides `InputTokenCount`/`OutputTokenCount`; cost comes from the raw response. Model is known at construction time. All data needed for `TokenUsageEvent` is available directly in the client. |
| Tool calls | `ToolApprovalChatClient.InvokeFunctionAsync()` | Wrap `base.InvokeFunctionAsync()` with a `Stopwatch`. Capture `context.Function.Name`, duration, success/failure. |
| Errors | `ChatMonitor.ProcessChatThread()`, `ScheduleExecutor.ProcessScheduleAsync()` | Existing catch blocks — add `IMetricsPublisher.PublishAsync(new ErrorEvent(...))`. |
| Schedule execution | `ScheduleExecutor.ProcessScheduleAsync()` | Wrap execution with `Stopwatch`. Emit on completion with duration and success/failure. |
| Heartbeat | Each service (Agent, Observability, MCP servers) | `HeartbeatService : BackgroundService` — publishes every 30s. |

### Heartbeat Service

`Infrastructure/Metrics/HeartbeatService.cs` — a generic `BackgroundService` parameterized with service name. Publishes a `HeartbeatEvent` every 30 seconds via `IMetricsPublisher`. Registered in each container's DI.

## 2. Collection & Storage (Observability Backend)

### Project: `Observability`

ASP.NET Core host with three responsibilities: metric collection, REST API, and SignalR hub. Also serves the `Dashboard.Client` Blazor WASM app.

### Metric Collector (`MetricsCollectorService : BackgroundService`)

Subscribes to Redis Pub/Sub channel `metrics:events`. Deserializes each event by `Type` discriminator and writes to Redis:

| Event type | Redis storage | Key pattern |
|------------|--------------|-------------|
| Token usage | Sorted set (score=timestamp, member=JSON) | `metrics:tokens:{yyyy-MM-dd}` |
| Token usage | Hash increments | `metrics:totals:{yyyy-MM-dd}` — fields: `tokens:input`, `tokens:output`, `tokens:cost`, `tokens:byUser:{sender}`, `tokens:byModel:{model}` |
| Tool call | Sorted set | `metrics:tools:{yyyy-MM-dd}` |
| Tool call | Hash increments | `metrics:totals:{yyyy-MM-dd}` — fields: `tools:count`, `tools:errors`, `tools:byName:{toolName}` |
| Error | Sorted set | `metrics:errors:{yyyy-MM-dd}` |
| Error | Capped list (last 100) | `metrics:errors:recent` |
| Schedule execution | Sorted set | `metrics:schedules:{yyyy-MM-dd}` |
| Heartbeat | String with TTL | `metrics:health:{service}` — 60s TTL. Absence = unhealthy. |

All daily keys get a **30-day TTL** set on first write.

The collector also **forwards live events to the SignalR hub** for real-time dashboard updates.

### REST API

Endpoints served by the Observability host:

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/api/metrics/summary?from=&to=` | Aggregated totals (tokens, costs, tool calls, errors) for a date range |
| `GET` | `/api/metrics/tokens?from=&to=` | Time-series token usage and costs |
| `GET` | `/api/metrics/tools?from=&to=` | Tool call breakdown by name, with counts and error rates |
| `GET` | `/api/metrics/errors?from=&to=&limit=` | Error list, filterable by date range |
| `GET` | `/api/metrics/schedules?from=&to=` | Schedule execution history |
| `GET` | `/api/metrics/health` | Current health status of all services (checks `metrics:health:*` key existence) |

Query parameters `from` and `to` accept ISO 8601 date strings. Default to today if omitted.

### SignalR Hub (`/hubs/metrics`)

Streams live events to connected dashboard clients:

| Method | Payload | Trigger |
|--------|---------|---------|
| `OnTokenUsage` | `TokenUsageEvent` | Each chat completion |
| `OnToolCall` | `ToolCallEvent` | Each tool invocation |
| `OnError` | `ErrorEvent` | Each error |
| `OnScheduleExecution` | `ScheduleExecutionEvent` | Each schedule run |
| `OnHealthUpdate` | `{ service, isHealthy, timestamp }` | Heartbeat received or key expired |

## 3. Dashboard UI (`Dashboard.Client`)

### Project Structure

Blazor WASM app following the same patterns as `WebChat.Client`.

### Layout

Icon sidebar navigation with 5 pages:

| Icon | Page | Content |
|------|------|---------|
| Overview (home) | `/` | KPI cards, mini token chart, health grid, recent activity feed |
| Tokens | `/tokens` | Time-series chart (input vs output), cost breakdown, per-user table, per-model table, time-range selector |
| Tools | `/tools` | Tool call frequency, success/failure rates, average duration, top tools ranking |
| Errors | `/errors` | Error timeline, error list with type/service/message, service filter |
| Schedules | `/schedules` | Schedule list with last run status, execution history, next scheduled runs |

### State Management (Redux-like)

Same pattern as WebChat.Client — Stores with `BehaviorSubject<T>`, actions, reducers, selectors:

| Store | State | Purpose |
|-------|-------|---------|
| `MetricsStore` | `MetricsState` | Summary KPIs, current totals |
| `HealthStore` | `HealthState` | Per-service health status |
| `TokensStore` | `TokensState` | Time-series token/cost data |
| `ToolsStore` | `ToolsState` | Tool call breakdowns |
| `ErrorsStore` | `ErrorsState` | Error list and aggregates |
| `SchedulesStore` | `SchedulesState` | Schedule execution history |
| `ConnectionStore` | `ConnectionState` | SignalR connection status |

### Effects

| Effect | Responsibility |
|--------|---------------|
| `MetricsHubEffect` | Connects to `/hubs/metrics`, dispatches live events to stores |
| `DataLoadEffect` | Fetches historical data from REST API on page load and time-range changes |

### Data Delivery (Hybrid)

- **Page load / time-range change**: REST API fetch for historical data
- **While viewing**: SignalR pushes live events that update KPIs and prepend to lists in real-time
- **Health status**: SignalR-driven, updates immediately on heartbeat changes

### Charting

CSS-only bar charts using styled `<div>` elements (no JS charting library). Sufficient for the bar/sparkline charts needed. If richer visualization is needed later, a lightweight library can be added.

## 4. Infrastructure & Deployment

### New Projects

| Project | Type | Purpose |
|---------|------|---------|
| `Observability/` | ASP.NET Core (net10.0) | Collector + REST API + SignalR hub + Blazor host |
| `Dashboard.Client/` | Blazor WASM (net10.0) | Dashboard UI |

### Docker Compose

New service in `DockerCompose/docker-compose.yml`:

```yaml
observability:
  build:
    context: ..
    dockerfile: Observability/Dockerfile
  ports:
    - "5002:8080"
  depends_on:
    - redis
```

### Caddy Routing

New route in `DockerCompose/caddy/Caddyfile`:

```
handle_path /dashboard/* {
    reverse_proxy observability:8080
}
```

Uses `handle_path` (not `handle`) to strip the `/dashboard` prefix before proxying. The Blazor WASM app must be configured with `<base href="/dashboard/">` so client-side routing and static asset paths work correctly.

Dashboard accessible at `https://assistants.herfluffness.com/dashboard/`.

### Shared Code

| What | Where |
|------|-------|
| `IMetricsPublisher` | `Domain/Contracts/` |
| Metric event DTOs | `Domain/DTOs/Metrics/` |
| `RedisMetricsPublisher` | `Infrastructure/Metrics/` |
| `HeartbeatService` | `Infrastructure/Metrics/` |

### DI Registration

Each service that emits metrics adds to its DI:

```csharp
services.AddSingleton<IMetricsPublisher, RedisMetricsPublisher>();
services.AddHostedService(sp => new HeartbeatService(sp.GetRequiredService<IMetricsPublisher>(), "service-name"));
```

### Cost Extraction from OpenRouter

OpenRouter includes `usage.cost` (USD) in every chat completion response, including the final SSE chunk during streaming. The `OpenRouterChatClient` must be modified to extract this value from the raw JSON response — the Microsoft.Extensions.AI `UsageContent` abstraction does not expose it. The existing `TeeHttpContent`/`ReasoningHandler` pipeline that intercepts SSE chunks for reasoning content extraction is the natural place to also capture the `cost` field via a parallel extraction path (e.g., a `ConcurrentQueue<decimal?>` alongside the existing reasoning queue). The client already has all context needed to emit `TokenUsageEvent` directly: sender (from `GetSenderId()` on user messages), model (from construction), tokens (from `UsageContent`), and cost (from raw response).

### Data Retention

All daily metric keys in Redis receive a 30-day TTL on creation, consistent with the existing chat thread TTL. Heartbeat health keys use a 60-second TTL — absence indicates the service is unhealthy.

## 5. Testing Strategy

- **Unit tests** for `RedisMetricsPublisher`, `MetricsCollectorService` aggregation logic, REST API endpoints
- **Unit tests** for Dashboard.Client stores, reducers, and selectors
- **Integration tests** for the full Pub/Sub → collector → Redis storage → API query pipeline
- Follow existing TDD patterns in the codebase
