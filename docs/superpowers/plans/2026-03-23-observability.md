# Observability Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Admin dashboard providing operational visibility into agent behavior — token costs, tool analytics, error rates, schedule history, and live service health.

**Architecture:** Three layers — instrumentation (Redis Pub/Sub emission), collection (Observability ASP.NET backend with REST API + SignalR), and UI (Dashboard.Client Blazor WASM). Services publish `MetricEvent` DTOs via `IMetricsPublisher` → Redis Pub/Sub. A `MetricsCollectorService` subscribes, aggregates into Redis sorted sets/hashes, and forwards to a SignalR hub for live updates. The dashboard uses the same Redux-like state pattern as WebChat.Client.

**Tech Stack:** .NET 10, Blazor WASM, SignalR, StackExchange.Redis, System.Reactive, xUnit/Moq/Shouldly

**Spec:** `docs/superpowers/specs/2026-03-23-observability-design.md`

---

## File Map

### Domain Layer (new files)

| File | Responsibility |
|------|---------------|
| `Domain/Contracts/IMetricsPublisher.cs` | Contract for publishing metric events |
| `Domain/DTOs/Metrics/MetricEvent.cs` | Base abstract record for all metric events |
| `Domain/DTOs/Metrics/TokenUsageEvent.cs` | Token count + cost per completion |
| `Domain/DTOs/Metrics/ToolCallEvent.cs` | Tool invocation timing + success/failure |
| `Domain/DTOs/Metrics/ErrorEvent.cs` | Error details per service |
| `Domain/DTOs/Metrics/ScheduleExecutionEvent.cs` | Schedule run result |
| `Domain/DTOs/Metrics/HeartbeatEvent.cs` | Service liveness signal |

### Infrastructure Layer (new files)

| File | Responsibility |
|------|---------------|
| `Infrastructure/Metrics/RedisMetricsPublisher.cs` | Publishes events to Redis Pub/Sub `metrics:events` channel |
| `Infrastructure/Metrics/HeartbeatService.cs` | BackgroundService that emits heartbeats every 30s |

### Infrastructure Layer (modified files)

| File | Change |
|------|--------|
| `Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs` | Add cost extraction from SSE final chunk alongside reasoning extraction |
| `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` | Capture sender + cost, publish `TokenUsageEvent` after streaming |
| `Infrastructure/Agents/ChatClients/ToolApprovalChatClient.cs` | Wrap `InvokeFunctionAsync` with timing, publish `ToolCallEvent` |

### Domain Layer (modified files)

| File | Change |
|------|--------|
| `Domain/Monitor/ChatMonitor.cs` | Publish `ErrorEvent` in catch blocks |
| `Domain/Monitor/ScheduleExecutor.cs` | Publish `ScheduleExecutionEvent` with timing |

### Observability Backend (new project)

| File | Responsibility |
|------|---------------|
| `Observability/Observability.csproj` | ASP.NET Core web project, references Domain + Dashboard.Client |
| `Observability/Program.cs` | Host setup: Redis, SignalR hub, REST API, serves Blazor WASM |
| `Observability/Services/MetricsCollectorService.cs` | BackgroundService: subscribes Pub/Sub, aggregates to Redis, forwards to hub |
| `Observability/Services/MetricsQueryService.cs` | Reads aggregated metrics from Redis for API endpoints |
| `Observability/Hubs/MetricsHub.cs` | SignalR hub for live metric streaming |
| `Observability/Dockerfile` | Multi-stage build for the observability container |

### Dashboard.Client (new project)

| File | Responsibility |
|------|---------------|
| `Dashboard.Client/Dashboard.Client.csproj` | Blazor WASM project, references Domain |
| `Dashboard.Client/Program.cs` | WASM host, DI registration for stores/effects/services |
| `Dashboard.Client/wwwroot/index.html` | HTML shell with `<base href="/dashboard/">` |
| `Dashboard.Client/Layout/MainLayout.razor` | Icon sidebar + main content area |
| `Dashboard.Client/Layout/MainLayout.razor.css` | Sidebar and layout styles |
| `Dashboard.Client/State/Store.cs` | Generic store (copy pattern from WebChat.Client) |
| `Dashboard.Client/State/Metrics/MetricsStore.cs` | KPI summary state |
| `Dashboard.Client/State/Health/HealthStore.cs` | Service health state |
| `Dashboard.Client/State/Tokens/TokensStore.cs` | Token time-series state |
| `Dashboard.Client/State/Tools/ToolsStore.cs` | Tool call breakdown state |
| `Dashboard.Client/State/Errors/ErrorsStore.cs` | Error list state |
| `Dashboard.Client/State/Schedules/SchedulesStore.cs` | Schedule history state |
| `Dashboard.Client/State/Connection/ConnectionStore.cs` | SignalR connection state |
| `Dashboard.Client/Services/MetricsApiService.cs` | REST API client for historical data |
| `Dashboard.Client/Services/MetricsHubService.cs` | SignalR client for live updates |
| `Dashboard.Client/Effects/DataLoadEffect.cs` | Fetches historical data on page load / time-range change |
| `Dashboard.Client/Effects/MetricsHubEffect.cs` | Dispatches live SignalR events to stores |
| `Dashboard.Client/Pages/Overview.razor` | KPI cards, mini chart, health grid, activity feed |
| `Dashboard.Client/Pages/Tokens.razor` | Token/cost charts, per-user/model tables |
| `Dashboard.Client/Pages/Tools.razor` | Tool frequency, success rates, duration |
| `Dashboard.Client/Pages/Errors.razor` | Error timeline and list |
| `Dashboard.Client/Pages/Schedules.razor` | Schedule execution history |
| `Dashboard.Client/Components/KpiCard.razor` | Reusable KPI display card |
| `Dashboard.Client/Components/BarChart.razor` | CSS-only bar chart component |
| `Dashboard.Client/Components/HealthGrid.razor` | Service health status grid |
| `Dashboard.Client/Components/TimeRangeSelector.razor` | Date range picker |

### Infrastructure / Deployment (modified files)

| File | Change |
|------|--------|
| `Agent/Modules/InjectorModule.cs` | Register `IMetricsPublisher` + `HeartbeatService` |
| `agent.sln` | Add Observability + Dashboard.Client projects |
| `DockerCompose/docker-compose.yml` | Add `observability` service |
| `DockerCompose/caddy/Caddyfile` | Add `/dashboard/*` route |

### Tests (new files)

| File | What it tests |
|------|--------------|
| `Tests/Unit/Infrastructure/Metrics/RedisMetricsPublisherTests.cs` | Serialization + Pub/Sub publish |
| `Tests/Unit/Infrastructure/Metrics/HeartbeatServiceTests.cs` | Periodic heartbeat emission |
| `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientMetricsTests.cs` | Token usage event emission |
| `Tests/Unit/Infrastructure/Agents/ChatClients/ToolApprovalChatClientMetricsTests.cs` | Tool call event emission |
| `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterHttpHelpersTests.cs` | Cost extraction from SSE |
| `Tests/Unit/Observability/Services/MetricsCollectorServiceTests.cs` | Event aggregation logic |
| `Tests/Unit/Observability/Services/MetricsQueryServiceTests.cs` | Query logic |
| `Tests/Unit/Dashboard/State/*StoreTests.cs` | Store reducers and selectors |

---

## Task 1: Metric Event DTOs

**Files:**
- Create: `Domain/DTOs/Metrics/MetricEvent.cs`
- Create: `Domain/DTOs/Metrics/TokenUsageEvent.cs`
- Create: `Domain/DTOs/Metrics/ToolCallEvent.cs`
- Create: `Domain/DTOs/Metrics/ErrorEvent.cs`
- Create: `Domain/DTOs/Metrics/ScheduleExecutionEvent.cs`
- Create: `Domain/DTOs/Metrics/HeartbeatEvent.cs`
- Create: `Domain/Contracts/IMetricsPublisher.cs`

- [ ] **Step 1: Create MetricEvent base record**

```csharp
// Domain/DTOs/Metrics/MetricEvent.cs
using System.Text.Json.Serialization;

namespace Domain.DTOs.Metrics;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TokenUsageEvent), "token_usage")]
[JsonDerivedType(typeof(ToolCallEvent), "tool_call")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
[JsonDerivedType(typeof(ScheduleExecutionEvent), "schedule_execution")]
[JsonDerivedType(typeof(HeartbeatEvent), "heartbeat")]
public abstract record MetricEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? AgentId { get; init; }
    public string? ConversationId { get; init; }
}
```

Note: The `Type` field from the spec is handled by the `[JsonPolymorphic]` discriminator — no need for a separate string property.

- [ ] **Step 2: Create all derived event records**

```csharp
// Domain/DTOs/Metrics/TokenUsageEvent.cs
namespace Domain.DTOs.Metrics;

public record TokenUsageEvent : MetricEvent
{
    public required string Sender { get; init; }
    public required string Model { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal Cost { get; init; }
}
```

```csharp
// Domain/DTOs/Metrics/ToolCallEvent.cs
namespace Domain.DTOs.Metrics;

public record ToolCallEvent : MetricEvent
{
    public required string ToolName { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
```

```csharp
// Domain/DTOs/Metrics/ErrorEvent.cs
namespace Domain.DTOs.Metrics;

public record ErrorEvent : MetricEvent
{
    public required string Service { get; init; }
    public required string ErrorType { get; init; }
    public required string Message { get; init; }
}
```

```csharp
// Domain/DTOs/Metrics/ScheduleExecutionEvent.cs
namespace Domain.DTOs.Metrics;

public record ScheduleExecutionEvent : MetricEvent
{
    public required string ScheduleId { get; init; }
    public required string Prompt { get; init; }
    public long DurationMs { get; init; }
    public bool Success { get; init; }
    public string? Error { get; init; }
}
```

```csharp
// Domain/DTOs/Metrics/HeartbeatEvent.cs
namespace Domain.DTOs.Metrics;

public record HeartbeatEvent : MetricEvent
{
    public required string Service { get; init; }
}
```

- [ ] **Step 3: Create IMetricsPublisher contract**

```csharp
// Domain/Contracts/IMetricsPublisher.cs
using Domain.DTOs.Metrics;

namespace Domain.Contracts;

public interface IMetricsPublisher
{
    Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default);
}
```

- [ ] **Step 4: Verify build**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Metrics/ Domain/Contracts/IMetricsPublisher.cs
git commit -m "feat(observability): add metric event DTOs and IMetricsPublisher contract"
```

---

## Task 2: RedisMetricsPublisher

**Files:**
- Create: `Infrastructure/Metrics/RedisMetricsPublisher.cs`
- Create: `Tests/Unit/Infrastructure/Metrics/RedisMetricsPublisherTests.cs`

- [ ] **Step 1: Write failing test for RedisMetricsPublisher**

```csharp
// Tests/Unit/Infrastructure/Metrics/RedisMetricsPublisherTests.cs
using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Infrastructure.Metrics;

public class RedisMetricsPublisherTests
{
    private readonly Mock<ISubscriber> _subscriber = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly RedisMetricsPublisher _sut;

    public RedisMetricsPublisherTests()
    {
        _redis.Setup(r => r.GetSubscriber(It.IsAny<object>())).Returns(_subscriber.Object);
        _sut = new RedisMetricsPublisher(_redis.Object);
    }

    [Fact]
    public async Task PublishAsync_publishes_serialized_event_to_metrics_channel()
    {
        var evt = new HeartbeatEvent { Service = "agent" };

        await _sut.PublishAsync(evt);

        _subscriber.Verify(s => s.PublishAsync(
            RedisChannel.Literal("metrics:events"),
            It.Is<RedisValue>(v => v.ToString().Contains("\"service\":\"agent\"")),
            It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_includes_type_discriminator()
    {
        var evt = new TokenUsageEvent
        {
            Sender = "user1",
            Model = "gpt-4",
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.01m
        };

        await _sut.PublishAsync(evt);

        _subscriber.Verify(s => s.PublishAsync(
            RedisChannel.Literal("metrics:events"),
            It.Is<RedisValue>(v => v.ToString().Contains("\"type\":\"token_usage\"")),
            It.IsAny<CommandFlags>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RedisMetricsPublisherTests" -v minimal`
Expected: FAIL — `RedisMetricsPublisher` does not exist

- [ ] **Step 3: Implement RedisMetricsPublisher**

```csharp
// Infrastructure/Metrics/RedisMetricsPublisher.cs
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using StackExchange.Redis;

namespace Infrastructure.Metrics;

public sealed class RedisMetricsPublisher(IConnectionMultiplexer redis) : IMetricsPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly RedisChannel Channel = RedisChannel.Literal("metrics:events");
    private readonly ISubscriber _subscriber = redis.GetSubscriber();

    public async Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(metricEvent, JsonOptions);
        await _subscriber.PublishAsync(Channel, json);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RedisMetricsPublisherTests" -v minimal`
Expected: 2 passed

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Metrics/RedisMetricsPublisher.cs Tests/Unit/Infrastructure/Metrics/RedisMetricsPublisherTests.cs
git commit -m "feat(observability): add RedisMetricsPublisher with Pub/Sub emission"
```

---

## Task 3: HeartbeatService

**Files:**
- Create: `Infrastructure/Metrics/HeartbeatService.cs`
- Create: `Tests/Unit/Infrastructure/Metrics/HeartbeatServiceTests.cs`

- [ ] **Step 1: Write failing test for HeartbeatService**

```csharp
// Tests/Unit/Infrastructure/Metrics/HeartbeatServiceTests.cs
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

public class HeartbeatServiceTests
{
    [Fact]
    public async Task ExecuteAsync_publishes_heartbeat_event_with_service_name()
    {
        var publisher = new Mock<IMetricsPublisher>();
        var sut = new HeartbeatService(publisher.Object, "test-service");

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // StartAsync triggers the background loop; it will publish at least once before cancellation
        await sut.StartAsync(cts.Token);
        await Task.Delay(50); // give it time to publish
        await sut.StopAsync(CancellationToken.None);

        publisher.Verify(p => p.PublishAsync(
            It.Is<HeartbeatEvent>(e => e.Service == "test-service"),
            It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HeartbeatServiceTests" -v minimal`
Expected: FAIL — `HeartbeatService` does not exist

- [ ] **Step 3: Implement HeartbeatService**

```csharp
// Infrastructure/Metrics/HeartbeatService.cs
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Hosting;

namespace Infrastructure.Metrics;

public sealed class HeartbeatService(IMetricsPublisher publisher, string serviceName) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await publisher.PublishAsync(
                new HeartbeatEvent { Service = serviceName },
                stoppingToken);

            await Task.Delay(Interval, stoppingToken);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HeartbeatServiceTests" -v minimal`
Expected: 1 passed

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Metrics/HeartbeatService.cs Tests/Unit/Infrastructure/Metrics/HeartbeatServiceTests.cs
git commit -m "feat(observability): add HeartbeatService for periodic liveness signals"
```

---

## Task 4: Cost Extraction from OpenRouter SSE

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs`
- Create: `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterHttpHelpersCostTests.cs`

Reference: The existing `ReasoningTeeStream` in `OpenRouterHttpHelpers.cs` (lines 125-229) parses SSE `data:` lines and extracts reasoning content into a `ConcurrentQueue<string>`. We add a parallel queue for cost extraction from the final SSE chunk's `usage.cost` field.

- [ ] **Step 1: Write failing test for cost extraction from SSE data**

```csharp
// Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterHttpHelpersCostTests.cs
using System.Collections.Concurrent;
using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class OpenRouterHttpHelpersCostTests
{
    [Fact]
    public void ExtractCostFromSseData_extracts_cost_from_usage_object()
    {
        var sseData = """
            {"id":"gen-123","choices":[{"delta":{"content":""}}],"usage":{"prompt_tokens":100,"completion_tokens":50,"total_tokens":150,"cost":0.0042}}
            """;

        var result = OpenRouterHttpHelpers.ExtractCostFromSseData(sseData);

        result.ShouldBe(0.0042m);
    }

    [Fact]
    public void ExtractCostFromSseData_returns_null_when_no_usage()
    {
        var sseData = """
            {"id":"gen-123","choices":[{"delta":{"content":"hello"}}]}
            """;

        var result = OpenRouterHttpHelpers.ExtractCostFromSseData(sseData);

        result.ShouldBeNull();
    }

    [Fact]
    public void ExtractCostFromSseData_returns_null_when_usage_has_no_cost()
    {
        var sseData = """
            {"id":"gen-123","choices":[],"usage":{"prompt_tokens":100,"completion_tokens":50}}
            """;

        var result = OpenRouterHttpHelpers.ExtractCostFromSseData(sseData);

        result.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterHttpHelpersCostTests" -v minimal`
Expected: FAIL — `ExtractCostFromSseData` does not exist

- [ ] **Step 3: Implement cost extraction**

Add to `Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs`, near the existing `ExtractReasoningFromSseData` method:

```csharp
internal static decimal? ExtractCostFromSseData(string data)
{
    try
    {
        using var doc = JsonDocument.Parse(data);
        if (doc.RootElement.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("cost", out var cost))
        {
            return cost.GetDecimal();
        }
    }
    catch (JsonException)
    {
        // Malformed JSON — skip
    }

    return null;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterHttpHelpersCostTests" -v minimal`
Expected: 3 passed

- [ ] **Step 5: Add cost queue to ReasoningTeeStream**

Modify the `ReasoningTeeStream` class and `WrapWithReasoningTee` / `TeeHttpContent` to accept and populate a `ConcurrentQueue<decimal?>` for cost values. In the `ProcessLine` method (or equivalent line-processing logic), after checking for reasoning, also call `ExtractCostFromSseData` and enqueue any non-null result.

Update `WrapWithReasoningTee` signature:

```csharp
internal static HttpContent WrapWithReasoningTee(
    HttpContent inner,
    ConcurrentQueue<string> reasoningQueue,
    ConcurrentQueue<decimal> costQueue)
```

In the SSE line processing loop, after the existing reasoning extraction:

```csharp
var cost = ExtractCostFromSseData(sseData);
if (cost.HasValue)
{
    costQueue.Enqueue(cost.Value);
}
```

- [ ] **Step 6: Update OpenRouterChatClient to pass cost queue**

In `OpenRouterChatClient.cs`, add a `ConcurrentQueue<decimal> _costQueue` field alongside the existing `_reasoningQueue`. Pass it to `WrapWithReasoningTee` in the `ReasoningHandler`. Add a `DrainCostQueue()` method that returns the last enqueued cost (or null).

```csharp
private readonly ConcurrentQueue<decimal> _costQueue = new();
```

Update `CreateHttpClient` to accept and pass the cost queue.

Update `ReasoningHandler` constructor and `SendAsync` to pass cost queue to `WrapWithReasoningTee`.

Add helper:

```csharp
private decimal? DrainCostQueue()
{
    decimal? last = null;
    while (_costQueue.TryDequeue(out var cost))
    {
        last = cost;
    }
    return last;
}
```

- [ ] **Step 7: Verify build**

Run: `dotnet build Infrastructure/Infrastructure.csproj`
Expected: Build succeeded

- [ ] **Step 8: Commit**

```bash
git add Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterHttpHelpersCostTests.cs
git commit -m "feat(observability): extract cost from OpenRouter SSE stream"
```

---

## Task 5: Token Usage Emission from OpenRouterChatClient

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`
- Create: `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientMetricsTests.cs`

Reference: `OpenRouterChatClient.GetStreamingResponseAsync()` (lines 39-72) iterates user messages calling `GetSenderId()`. After streaming all updates, we extract `UsageContent` for token counts, `DrainCostQueue()` for cost, and publish a `TokenUsageEvent`.

- [ ] **Step 1: Write failing test for token usage emission**

The test needs a WireMock server to simulate OpenRouter streaming responses with `usage.cost` in the final SSE chunk. Study existing test patterns in `Tests/` — search for WireMock usage or tests of HTTP-dependent classes to follow the same approach.

Test outline:
1. **Arrange**: Create a WireMock stub that returns an SSE stream with a final chunk containing `"usage":{"prompt_tokens":100,"completion_tokens":50,"cost":0.0042}`. Create a `Mock<IMetricsPublisher>`. Construct `OpenRouterChatClient` pointing at the WireMock URL with the mock publisher.
2. **Act**: Build a `ChatMessage` list with a user message that has `SetSenderId("user1")`. Call `GetResponseAsync` (which internally calls `GetStreamingResponseAsync`).
3. **Assert**: Verify `publisher.PublishAsync` was called with a `TokenUsageEvent` where `Sender == "user1"`, `InputTokens == 100`, `OutputTokens == 50`, `Cost == 0.0042m`.

The exact WireMock SSE response format should match OpenRouter's streaming format (SSE `data:` lines with JSON, ending with `data: [DONE]`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterChatClientMetricsTests" -v minimal`
Expected: FAIL

- [ ] **Step 3: Modify OpenRouterChatClient to accept IMetricsPublisher and emit TokenUsageEvent**

Add `IMetricsPublisher?` as an optional constructor parameter (nullable to avoid breaking existing construction without metrics):

```csharp
public sealed class OpenRouterChatClient : IChatClient
{
    private readonly IChatClient _client;
    private readonly HttpClient _httpClient;
    private readonly HttpClientPipelineTransport _transport;
    private readonly ConcurrentQueue<string> _reasoningQueue = new();
    private readonly ConcurrentQueue<decimal> _costQueue = new();
    private readonly IMetricsPublisher? _metricsPublisher;
    private readonly string _model;

    public OpenRouterChatClient(string endpoint, string apiKey, string model, IMetricsPublisher? metricsPublisher = null)
    {
        _model = model;
        _metricsPublisher = metricsPublisher;
        _httpClient = CreateHttpClient(_reasoningQueue, _costQueue);
        _transport = new HttpClientPipelineTransport(_httpClient);
        _client = CreateClient(endpoint, apiKey, model, _transport);
    }
```

In `GetStreamingResponseAsync`, capture sender before the streaming loop, then after the loop, publish the event:

```csharp
public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
    IEnumerable<ChatMessage> messages,
    ChatOptions? options = null,
    [EnumeratorCancellation] CancellationToken ct = default)
{
    var messageList = messages.ToList();

    // Capture sender from last user message
    var sender = messageList
        .LastOrDefault(m => m.Role == ChatRole.User)
        ?.GetSenderId();

    // Existing message transformation...
    var transformedMessages = messageList.Select(x => { /* existing prefix logic */ });

    int inputTokens = 0, outputTokens = 0;

    await foreach (var update in _client.GetStreamingResponseAsync(transformedMessages, options, ct))
    {
        AppendReasoningContent(update);
        update.SetTimestamp(DateTimeOffset.UtcNow);

        // Capture usage from final chunk
        var usage = update.Contents?.OfType<UsageContent>().FirstOrDefault();
        if (usage is not null)
        {
            inputTokens = usage.Details.InputTokenCount ?? 0;
            outputTokens = usage.Details.OutputTokenCount ?? 0;
        }

        yield return update;
    }

    // Publish token usage event
    if (_metricsPublisher is not null && (inputTokens > 0 || outputTokens > 0))
    {
        var cost = DrainCostQueue();
        await _metricsPublisher.PublishAsync(new TokenUsageEvent
        {
            Sender = sender ?? "unknown",
            Model = _model,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Cost = cost ?? 0m
        }, ct);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterChatClientMetricsTests" -v minimal`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientMetricsTests.cs
git commit -m "feat(observability): emit TokenUsageEvent from OpenRouterChatClient"
```

---

## Task 6: Tool Call Metrics from ToolApprovalChatClient

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/ToolApprovalChatClient.cs`
- Create: `Tests/Unit/Infrastructure/Agents/ChatClients/ToolApprovalChatClientMetricsTests.cs`

Reference: `ToolApprovalChatClient.InvokeFunctionAsync()` (lines 27-65) handles tool approval and invocation. Wrap the `base.InvokeFunctionAsync()` call with a `Stopwatch` and publish a `ToolCallEvent`.

- [ ] **Step 1: Write failing tests for tool call metrics**

Check existing tests at `Tests/Unit/Infrastructure/Agents/ChatClients/` for how `ToolApprovalChatClient` is currently tested — use the same patterns for constructing mock `IChatClient`, `IToolApprovalHandler`, and triggering `InvokeFunctionAsync`.

Test outlines:

**Test 1: `InvokeFunctionAsync_publishes_tool_call_event_on_success`**
1. **Arrange**: Create `Mock<IMetricsPublisher>`, `Mock<IToolApprovalHandler>` (auto-approves). Create `ToolApprovalChatClient` with a mock inner `IChatClient` that has a registered function. Pass the mock publisher.
2. **Act**: Trigger a chat completion that causes the tool to be invoked (the inner client returns a `FunctionCallContent`).
3. **Assert**: Verify `publisher.PublishAsync` was called with `ToolCallEvent` where `ToolName == "test_tool"`, `Success == true`, `DurationMs >= 0`.

**Test 2: `InvokeFunctionAsync_publishes_tool_call_event_on_failure`**
1. **Arrange**: Same as above, but the registered function throws an exception.
2. **Act**: Trigger tool invocation.
3. **Assert**: Verify `publisher.PublishAsync` was called with `ToolCallEvent` where `Success == false`, `Error` is not null.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ToolApprovalChatClientMetricsTests" -v minimal`
Expected: FAIL

- [ ] **Step 3: Implement tool call metrics**

Add `IMetricsPublisher?` to `ToolApprovalChatClient` constructor. In `InvokeFunctionAsync`, wrap the base invocation:

```csharp
protected override async ValueTask<object?> InvokeFunctionAsync(
    FunctionInvocationContext context,
    CancellationToken cancellationToken)
{
    var toolName = context.Function.Name;
    // ... existing approval logic ...

    var sw = Stopwatch.StartNew();
    try
    {
        var result = await base.InvokeFunctionAsync(context, cancellationToken);
        sw.Stop();

        if (_metricsPublisher is not null)
        {
            await _metricsPublisher.PublishAsync(new ToolCallEvent
            {
                ToolName = toolName,
                DurationMs = sw.ElapsedMilliseconds,
                Success = true
            }, cancellationToken);
        }

        return result;
    }
    catch (Exception ex)
    {
        sw.Stop();

        if (_metricsPublisher is not null)
        {
            await _metricsPublisher.PublishAsync(new ToolCallEvent
            {
                ToolName = toolName,
                DurationMs = sw.ElapsedMilliseconds,
                Success = false,
                Error = ex.Message
            }, cancellationToken);
        }

        throw;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ToolApprovalChatClientMetricsTests" -v minimal`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/ToolApprovalChatClient.cs Tests/Unit/Infrastructure/Agents/ChatClients/ToolApprovalChatClientMetricsTests.cs
git commit -m "feat(observability): emit ToolCallEvent from ToolApprovalChatClient"
```

---

## Task 7: Error and Schedule Metrics from Domain Monitor

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs`
- Modify: `Domain/Monitor/ScheduleExecutor.cs`

Reference: `ChatMonitor.ProcessChatThread()` has a catch block (around line 90-100). `ScheduleExecutor.ProcessScheduleAsync()` has catch blocks in `ExecuteWithNotifications` and `ExecuteSilently`. Both need `IMetricsPublisher` injected via primary constructor.

Note: Domain classes should depend on `IMetricsPublisher` (the Domain contract), not the Infrastructure implementation. This is consistent with the existing pattern where Domain depends on interfaces.

- [ ] **Step 1: Add IMetricsPublisher to ChatMonitor constructor and emit ErrorEvent**

Add `IMetricsPublisher metricsPublisher` to the primary constructor parameters. In the catch block of `ProcessChatThread`, publish:

```csharp
catch (Exception ex) when (ex is not OperationCanceledException)
{
    logger.LogError(ex, "...");
    await metricsPublisher.PublishAsync(new ErrorEvent
    {
        Service = "agent",
        AgentId = agentKey.AgentId,
        ConversationId = agentKey.ConversationId,
        ErrorType = ex.GetType().Name,
        Message = ex.Message
    });
}
```

- [ ] **Step 2: Add IMetricsPublisher to ScheduleExecutor and emit ScheduleExecutionEvent + ErrorEvent**

Add `IMetricsPublisher metricsPublisher` to the primary constructor. Wrap `ExecuteScheduleCore` calls with a `Stopwatch`. After success:

```csharp
await metricsPublisher.PublishAsync(new ScheduleExecutionEvent
{
    ScheduleId = schedule.Id,
    AgentId = schedule.Agent.Name,
    Prompt = schedule.Prompt,
    DurationMs = sw.ElapsedMilliseconds,
    Success = true
});
```

In catch blocks:

```csharp
await metricsPublisher.PublishAsync(new ScheduleExecutionEvent
{
    ScheduleId = schedule.Id,
    AgentId = schedule.Agent.Name,
    Prompt = schedule.Prompt,
    DurationMs = sw.ElapsedMilliseconds,
    Success = false,
    Error = ex.Message
});

await metricsPublisher.PublishAsync(new ErrorEvent
{
    Service = "scheduler",
    AgentId = schedule.Agent.Name,
    ErrorType = ex.GetType().Name,
    Message = ex.Message
});
```

- [ ] **Step 3: Verify build**

Run: `dotnet build Agent/Agent.csproj`
Expected: Build may fail due to missing DI registration — that's expected. Fix in Task 8.

- [ ] **Step 4: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs Domain/Monitor/ScheduleExecutor.cs
git commit -m "feat(observability): emit error and schedule execution metrics from monitors"
```

---

## Task 8: DI Registration and Wiring

**Files:**
- Modify: `Agent/Modules/InjectorModule.cs`
- Modify: `Agent/Modules/SchedulingModule.cs`

Reference: `InjectorModule.AddAgent()` (line 16) registers Redis. `InjectorModule.AddChatMonitoring()` (line 44) creates `ChatMonitor`. `SchedulingModule.AddScheduling()` (line 15) creates `ScheduleExecutor`.

- [ ] **Step 1: Register IMetricsPublisher and HeartbeatService in InjectorModule**

In `AddAgent` or a new `AddMetrics` extension method:

```csharp
services.AddSingleton<IMetricsPublisher, RedisMetricsPublisher>();
services.AddHostedService(sp =>
    new HeartbeatService(sp.GetRequiredService<IMetricsPublisher>(), "agent"));
```

- [ ] **Step 2: Pass IMetricsPublisher to OpenRouterChatClient construction**

Find where `OpenRouterChatClient` is constructed (likely in `MultiAgentFactory` or `AgentRegistryOptions` setup). Pass the `IMetricsPublisher` instance. The implementer should search for `new OpenRouterChatClient(` to find the exact construction site.

- [ ] **Step 3: Pass IMetricsPublisher to ToolApprovalChatClient construction**

Find where `ToolApprovalChatClient` is constructed. Pass the `IMetricsPublisher` instance. Search for `new ToolApprovalChatClient(`.

- [ ] **Step 4: Pass IMetricsPublisher to ChatMonitor via DI**

In `AddChatMonitoring`, ensure `ChatMonitor` can resolve `IMetricsPublisher` from DI (it should auto-resolve since it's a primary constructor parameter and `IMetricsPublisher` is registered as singleton).

- [ ] **Step 5: Pass IMetricsPublisher to ScheduleExecutor via DI**

In `AddScheduling`, ensure `ScheduleExecutor` can resolve `IMetricsPublisher`. Same approach — it should auto-resolve if registered.

- [ ] **Step 6: Verify full build**

Run: `dotnet build Agent/Agent.csproj`
Expected: Build succeeded

- [ ] **Step 7: Run all existing tests to check for regressions**

Run: `dotnet test Tests/Tests.csproj -v minimal`
Expected: All tests pass (existing tests that construct `ChatMonitor`, `ScheduleExecutor`, `OpenRouterChatClient`, or `ToolApprovalChatClient` directly may need updated constructor calls with `null` for the new `IMetricsPublisher` parameter)

- [ ] **Step 8: Commit**

```bash
git add Agent/Modules/InjectorModule.cs Agent/Modules/SchedulingModule.cs
git commit -m "feat(observability): wire IMetricsPublisher and HeartbeatService into Agent DI"
```

---

## Task 9: Observability Project Scaffolding

**Files:**
- Create: `Observability/Observability.csproj`
- Create: `Observability/Program.cs`
- Create: `Observability/Hubs/MetricsHub.cs`
- Create: `Observability/Dockerfile`
- Modify: `agent.sln`

- [ ] **Step 1: Create Observability.csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <UserSecretsId>bae64127-c00e-4499-8325-0fb6b452133c</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="StackExchange.Redis" Version="2.12.4" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Domain\Domain.csproj" />
    <ProjectReference Include="..\Dashboard.Client\Dashboard.Client.csproj" />
  </ItemGroup>

</Project>
```

Note: `Dashboard.Client` project reference will fail until Task 13 creates it. Add it later or create a minimal stub first. The implementer should create both projects in sequence.

- [ ] **Step 2: Create MetricsHub**

```csharp
// Observability/Hubs/MetricsHub.cs
using Microsoft.AspNetCore.SignalR;

namespace Observability.Hubs;

public sealed class MetricsHub : Hub;
```

- [ ] **Step 3: Create Program.cs**

```csharp
// Observability/Program.cs
using Observability.Hubs;
using Observability.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddUserSecrets<Program>();

var redisConnection = builder.Configuration["Redis:ConnectionString"]
    ?? throw new InvalidOperationException("Redis:ConnectionString is required");

builder.Services.AddSingleton<IConnectionMultiplexer>(
    ConnectionMultiplexer.Connect(redisConnection));

builder.Services.AddSignalR();
builder.Services.AddSingleton<MetricsQueryService>();
builder.Services.AddHostedService<MetricsCollectorService>();

var app = builder.Build();

app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

app.MapHub<MetricsHub>("/hubs/metrics");

// REST API endpoints (mapped in Task 11)
app.MapMetricsApi();

app.MapFallbackToFile("index.html");

app.Run();
```

- [ ] **Step 4: Create Dockerfile**

```dockerfile
# Observability/Dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Domain/Domain.csproj Domain/
COPY Dashboard.Client/Dashboard.Client.csproj Dashboard.Client/
COPY Observability/Observability.csproj Observability/
RUN dotnet restore Observability/Observability.csproj
COPY Domain/ Domain/
COPY Dashboard.Client/ Dashboard.Client/
COPY Observability/ Observability/
RUN dotnet publish Observability/Observability.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Observability.dll"]
```

- [ ] **Step 5: Add to solution**

Run:
```bash
dotnet sln agent.sln add Observability/Observability.csproj
```

- [ ] **Step 6: Verify build**

Run: `dotnet build Observability/Observability.csproj`
Expected: May fail due to missing `MetricsCollectorService`, `MetricsQueryService`, `MapMetricsApi` — these are created in the next tasks. Create stubs if needed, or build after those tasks.

- [ ] **Step 7: Commit**

```bash
git add Observability/ agent.sln
git commit -m "feat(observability): scaffold Observability project with SignalR hub"
```

---

## Task 10: MetricsCollectorService

**Files:**
- Create: `Observability/Services/MetricsCollectorService.cs`
- Create: `Tests/Unit/Observability/Services/MetricsCollectorServiceTests.cs`

Reference spec: Section 2 — the collector subscribes to `metrics:events` Pub/Sub, deserializes by type discriminator, writes to Redis sorted sets/hashes, and forwards to the SignalR hub.

- [ ] **Step 1: Write failing test for collector aggregation**

```csharp
// Tests/Unit/Observability/Services/MetricsCollectorServiceTests.cs
using Domain.DTOs.Metrics;
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsCollectorServiceTests
{
    [Fact]
    public async Task ProcessEvent_token_usage_increments_daily_totals()
    {
        var db = new Mock<IDatabase>();
        var evt = new TokenUsageEvent
        {
            Sender = "user1",
            Model = "gpt-4",
            InputTokens = 100,
            OutputTokens = 50,
            Cost = 0.01m,
            Timestamp = new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero)
        };

        // Call the processing method (extracted for testability)
        // Assert: HashIncrementAsync called for tokens:input, tokens:output
        // Assert: SortedSetAddAsync called for metrics:tokens:2026-03-23
        db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-23", "tokens:input", 100, It.IsAny<CommandFlags>()),
            Times.Once);
        db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-23", "tokens:output", 50, It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task ProcessEvent_heartbeat_sets_health_key_with_ttl()
    {
        var db = new Mock<IDatabase>();
        var evt = new HeartbeatEvent { Service = "agent" };

        // Assert: StringSetAsync called for metrics:health:agent with 60s TTL
        db.Verify(d => d.StringSetAsync(
            "metrics:health:agent",
            It.IsAny<RedisValue>(),
            TimeSpan.FromSeconds(60),
            It.IsAny<bool>(),
            It.IsAny<When>(),
            It.IsAny<CommandFlags>()),
            Times.Once);
    }
}
```

Note: Extract the event processing logic into a testable method (e.g., `ProcessEventAsync(MetricEvent, IDatabase)`) separate from the Pub/Sub subscription loop.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsCollectorServiceTests" -v minimal`
Expected: FAIL

- [ ] **Step 3: Implement MetricsCollectorService**

```csharp
// Observability/Services/MetricsCollectorService.cs
using System.Text.Json;
using Domain.DTOs.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Observability.Hubs;
using StackExchange.Redis;

namespace Observability.Services;

public sealed class MetricsCollectorService(
    IConnectionMultiplexer redis,
    IHubContext<MetricsHub> hubContext) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly TimeSpan DailyKeyTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan HealthKeyTtl = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();
        var db = redis.GetDatabase();

        await subscriber.SubscribeAsync(RedisChannel.Literal("metrics:events"), async (_, message) =>
        {
            if (message.IsNullOrEmpty) return;

            try
            {
                var evt = JsonSerializer.Deserialize<MetricEvent>(message!, JsonOptions);
                if (evt is not null)
                {
                    await ProcessEventAsync(evt, db);
                }
            }
            catch (JsonException)
            {
                // Skip malformed events
            }
        });

        // Keep alive until cancellation
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    internal async Task ProcessEventAsync(MetricEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.ToString("yyyy-MM-dd");
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize(evt, JsonOptions);

        switch (evt)
        {
            case TokenUsageEvent t:
                await db.SortedSetAddAsync($"metrics:tokens:{dateKey}", json, score);
                await db.HashIncrementAsync($"metrics:totals:{dateKey}", "tokens:input", t.InputTokens);
                await db.HashIncrementAsync($"metrics:totals:{dateKey}", "tokens:output", t.OutputTokens);
                await db.HashIncrementAsync($"metrics:totals:{dateKey}", "tokens:cost", (long)(t.Cost * 10000)); // store as fixed-point
                await db.HashIncrementAsync($"metrics:totals:{dateKey}", $"tokens:byUser:{t.Sender}", t.InputTokens + t.OutputTokens);
                await db.HashIncrementAsync($"metrics:totals:{dateKey}", $"tokens:byModel:{t.Model}", t.InputTokens + t.OutputTokens);
                await db.KeyExpireAsync($"metrics:tokens:{dateKey}", DailyKeyTtl, CommandFlags.FireAndForget);
                await db.KeyExpireAsync($"metrics:totals:{dateKey}", DailyKeyTtl, CommandFlags.FireAndForget);
                await hubContext.Clients.All.SendAsync("OnTokenUsage", t);
                break;

            case ToolCallEvent tc:
                await db.SortedSetAddAsync($"metrics:tools:{dateKey}", json, score);
                await db.HashIncrementAsync($"metrics:totals:{dateKey}", "tools:count", 1);
                if (!tc.Success)
                    await db.HashIncrementAsync($"metrics:totals:{dateKey}", "tools:errors", 1);
                await db.HashIncrementAsync($"metrics:totals:{dateKey}", $"tools:byName:{tc.ToolName}", 1);
                await db.KeyExpireAsync($"metrics:tools:{dateKey}", DailyKeyTtl, CommandFlags.FireAndForget);
                await hubContext.Clients.All.SendAsync("OnToolCall", tc);
                break;

            case ErrorEvent e:
                await db.SortedSetAddAsync($"metrics:errors:{dateKey}", json, score);
                await db.ListLeftPushAsync("metrics:errors:recent", json);
                await db.ListTrimAsync("metrics:errors:recent", 0, 99); // cap at 100
                await db.KeyExpireAsync($"metrics:errors:{dateKey}", DailyKeyTtl, CommandFlags.FireAndForget);
                await hubContext.Clients.All.SendAsync("OnError", e);
                break;

            case ScheduleExecutionEvent s:
                await db.SortedSetAddAsync($"metrics:schedules:{dateKey}", json, score);
                await db.KeyExpireAsync($"metrics:schedules:{dateKey}", DailyKeyTtl, CommandFlags.FireAndForget);
                await hubContext.Clients.All.SendAsync("OnScheduleExecution", s);
                break;

            case HeartbeatEvent h:
                await db.StringSetAsync($"metrics:health:{h.Service}", evt.Timestamp.ToString("o"), HealthKeyTtl);
                await hubContext.Clients.All.SendAsync("OnHealthUpdate", new { h.Service, IsHealthy = true, evt.Timestamp });
                break;
        }
    }
}
```

- [ ] **Step 4: Update tests to match actual implementation and run**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsCollectorServiceTests" -v minimal`
Expected: PASS

- [ ] **Step 5: Add Tests project reference to Observability**

The test project needs a reference to the Observability project:

```bash
dotnet add Tests/Tests.csproj reference Observability/Observability.csproj
```

- [ ] **Step 6: Commit**

```bash
git add Observability/Services/MetricsCollectorService.cs Tests/Unit/Observability/Services/MetricsCollectorServiceTests.cs Tests/Tests.csproj
git commit -m "feat(observability): add MetricsCollectorService with Pub/Sub aggregation"
```

---

## Task 11: MetricsQueryService and REST API

**Files:**
- Create: `Observability/Services/MetricsQueryService.cs`
- Create: `Observability/MetricsApiEndpoints.cs`
- Create: `Tests/Unit/Observability/Services/MetricsQueryServiceTests.cs`

- [ ] **Step 1: Write failing tests for MetricsQueryService**

```csharp
// Tests/Unit/Observability/Services/MetricsQueryServiceTests.cs
using Moq;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsQueryServiceTests
{
    [Fact]
    public async Task GetSummaryAsync_aggregates_totals_across_date_range()
    {
        // Mock IDatabase to return hash entries for metrics:totals:{date}
        // Assert aggregated totals are summed correctly across multiple days
    }

    [Fact]
    public async Task GetHealthAsync_returns_healthy_for_existing_keys()
    {
        // Mock IServer.KeysAsync for metrics:health:* pattern
        // Assert services with keys are healthy, absent ones are not listed
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceTests" -v minimal`
Expected: FAIL

- [ ] **Step 3: Implement MetricsQueryService**

```csharp
// Observability/Services/MetricsQueryService.cs
using System.Text.Json;
using Domain.DTOs.Metrics;
using StackExchange.Redis;

namespace Observability.Services;

public record MetricsSummary(long InputTokens, long OutputTokens, long TotalTokens, decimal Cost, long ToolCalls, long ToolErrors);

public sealed class MetricsQueryService(IConnectionMultiplexer redis)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly IDatabase _db = redis.GetDatabase();
    private readonly IServer _server = redis.GetServer(redis.GetEndPoints()[0]);

    public async Task<MetricsSummary> GetSummaryAsync(DateOnly from, DateOnly to)
    {
        long totalInput = 0, totalOutput = 0, totalCostFixed = 0;
        long toolCount = 0, toolErrors = 0;

        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var key = $"metrics:totals:{date:yyyy-MM-dd}";
            var entries = await _db.HashGetAllAsync(key);
            var dict = entries.ToDictionary(e => e.Name.ToString(), e => (long)e.Value);

            totalInput += dict.GetValueOrDefault("tokens:input");
            totalOutput += dict.GetValueOrDefault("tokens:output");
            totalCostFixed += dict.GetValueOrDefault("tokens:cost");
            toolCount += dict.GetValueOrDefault("tools:count");
            toolErrors += dict.GetValueOrDefault("tools:errors");
        }

        return new MetricsSummary(totalInput, totalOutput, totalInput + totalOutput,
            totalCostFixed / 10000m, toolCount, toolErrors);
    }

    public async Task<IReadOnlyList<T>> GetEventsAsync<T>(string keyPrefix, DateOnly from, DateOnly to) where T : MetricEvent
    {
        var events = new List<T>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var key = $"{keyPrefix}:{date:yyyy-MM-dd}";
            var entries = await _db.SortedSetRangeByScoreAsync(key);
            events.AddRange(entries
                .Where(e => !e.IsNullOrEmpty)
                .Select(e => JsonSerializer.Deserialize<T>(e!, JsonOptions))
                .Where(e => e is not null)!);
        }
        return events;
    }

    public async Task<IReadOnlyList<ErrorEvent>> GetRecentErrorsAsync(int limit = 100)
    {
        var entries = await _db.ListRangeAsync("metrics:errors:recent", 0, limit - 1);
        return entries
            .Where(e => !e.IsNullOrEmpty)
            .Select(e => JsonSerializer.Deserialize<ErrorEvent>(e!, JsonOptions))
            .Where(e => e is not null)
            .ToList()!;
    }

    public async Task<IReadOnlyList<object>> GetHealthAsync()
    {
        var keys = _server.Keys(pattern: "metrics:health:*").ToList();
        var results = new List<object>();

        foreach (var key in keys)
        {
            var service = key.ToString().Replace("metrics:health:", "");
            var timestamp = await _db.StringGetAsync(key);
            results.Add(new { Service = service, IsHealthy = true, LastSeen = timestamp.ToString() });
        }

        return results;
    }

    public async Task<Dictionary<string, long>> GetTokenBreakdownAsync(string prefix, DateOnly from, DateOnly to)
    {
        var result = new Dictionary<string, long>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var key = $"metrics:totals:{date:yyyy-MM-dd}";
            var entries = await _db.HashGetAllAsync(key);
            foreach (var entry in entries.Where(e => e.Name.ToString().StartsWith(prefix)))
            {
                var name = entry.Name.ToString().Replace(prefix, "");
                result[name] = result.GetValueOrDefault(name) + (long)entry.Value;
            }
        }
        return result;
    }
}
```

- [ ] **Step 4: Create REST API endpoint mappings**

```csharp
// Observability/MetricsApiEndpoints.cs
using Domain.DTOs.Metrics;
using Observability.Services;

namespace Observability;

public static class MetricsApiEndpoints
{
    public static void MapMetricsApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/metrics");

        api.MapGet("/summary", async (MetricsQueryService svc, DateOnly? from, DateOnly? to) =>
        {
            var f = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var t = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await svc.GetSummaryAsync(f, t);
        });

        api.MapGet("/tokens", async (MetricsQueryService svc, DateOnly? from, DateOnly? to) =>
        {
            var f = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var t = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await svc.GetEventsAsync<TokenUsageEvent>("metrics:tokens", f, t);
        });

        api.MapGet("/tools", async (MetricsQueryService svc, DateOnly? from, DateOnly? to) =>
        {
            var f = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var t = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await svc.GetEventsAsync<ToolCallEvent>("metrics:tools", f, t);
        });

        api.MapGet("/errors", async (MetricsQueryService svc, DateOnly? from, DateOnly? to, int? limit) =>
        {
            if (from is null && to is null)
                return Results.Ok(await svc.GetRecentErrorsAsync(limit ?? 100));

            var f = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var t = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return Results.Ok(await svc.GetEventsAsync<ErrorEvent>("metrics:errors", f, t));
        });

        api.MapGet("/schedules", async (MetricsQueryService svc, DateOnly? from, DateOnly? to) =>
        {
            var f = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var t = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await svc.GetEventsAsync<ScheduleExecutionEvent>("metrics:schedules", f, t);
        });

        api.MapGet("/health", async (MetricsQueryService svc) =>
            await svc.GetHealthAsync());

        api.MapGet("/tokens/by-user", async (MetricsQueryService svc, DateOnly? from, DateOnly? to) =>
        {
            var f = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var t = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await svc.GetTokenBreakdownAsync("tokens:byUser:", f, t);
        });

        api.MapGet("/tokens/by-model", async (MetricsQueryService svc, DateOnly? from, DateOnly? to) =>
        {
            var f = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var t = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await svc.GetTokenBreakdownAsync("tokens:byModel:", f, t);
        });
    }
}
```

- [ ] **Step 5: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceTests" -v minimal`
Expected: PASS

- [ ] **Step 6: Verify build**

Run: `dotnet build Observability/Observability.csproj`
Expected: Build succeeded (or fails on missing Dashboard.Client reference — add `<ProjectReference>` later)

- [ ] **Step 7: Commit**

```bash
git add Observability/Services/MetricsQueryService.cs Observability/MetricsApiEndpoints.cs Tests/Unit/Observability/Services/MetricsQueryServiceTests.cs
git commit -m "feat(observability): add MetricsQueryService and REST API endpoints"
```

---

## Task 12: Dashboard.Client Project Scaffolding

**Files:**
- Create: `Dashboard.Client/Dashboard.Client.csproj`
- Create: `Dashboard.Client/Program.cs`
- Create: `Dashboard.Client/wwwroot/index.html`
- Create: `Dashboard.Client/wwwroot/css/app.css`
- Create: `Dashboard.Client/_Imports.razor`
- Create: `Dashboard.Client/App.razor`
- Create: `Dashboard.Client/Routes.razor`
- Modify: `agent.sln`

- [ ] **Step 1: Create Dashboard.Client.csproj**

Follow `WebChat.Client/WebChat.Client.csproj` pattern:

```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <LangVersion>14</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.0.5" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="10.0.5" />
    <PackageReference Include="System.Reactive" Version="6.1.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Domain\Domain.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create wwwroot/index.html**

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Agent Dashboard</title>
    <base href="/dashboard/" />
    <link href="css/app.css" rel="stylesheet" />
    <link href="Dashboard.Client.styles.css" rel="stylesheet" />
</head>
<body>
    <div id="app">Loading...</div>
    <script src="_framework/blazor.webassembly.js"></script>
</body>
</html>
```

- [ ] **Step 3: Create App.razor, Routes.razor, _Imports.razor**

```razor
@* Dashboard.Client/App.razor *@
<Routes />
```

```razor
@* Dashboard.Client/Routes.razor *@
<Router AppAssembly="typeof(App).Assembly">
    <Found Context="routeData">
        <RouteView RouteData="routeData" DefaultLayout="typeof(Layout.MainLayout)" />
    </Found>
    <NotFound>
        <LayoutView Layout="typeof(Layout.MainLayout)">
            <p>Page not found</p>
        </LayoutView>
    </NotFound>
</Router>
```

```razor
@* Dashboard.Client/_Imports.razor *@
@using System.Net.Http
@using System.Net.Http.Json
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Routing
@using Microsoft.AspNetCore.Components.Web
@using Microsoft.AspNetCore.Components.WebAssembly.Http
@using Dashboard.Client
@using Dashboard.Client.Layout
@using Dashboard.Client.Components
@using Dashboard.Client.State
```

- [ ] **Step 4: Create minimal Program.cs**

```csharp
// Dashboard.Client/Program.cs
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using Dashboard.Client;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(_ =>
    new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

// State stores and effects will be registered in later tasks

await builder.Build().RunAsync();
```

- [ ] **Step 5: Create minimal app.css**

```css
/* Dashboard.Client/wwwroot/css/app.css */
:root {
    --bg-primary: #1a1a2e;
    --bg-secondary: #16213e;
    --bg-card: #16213e;
    --text-primary: #e0e0e0;
    --text-secondary: #888;
    --accent-blue: #00d2ff;
    --accent-green: #4ade80;
    --accent-purple: #a78bfa;
    --accent-red: #f87171;
    --accent-yellow: #fbbf24;
    --sidebar-width: 56px;
}

* { margin: 0; padding: 0; box-sizing: border-box; }

body {
    font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, monospace;
    background: var(--bg-primary);
    color: var(--text-primary);
    min-height: 100vh;
}
```

- [ ] **Step 6: Add to solution**

```bash
dotnet sln agent.sln add Dashboard.Client/Dashboard.Client.csproj
```

- [ ] **Step 7: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: Build succeeded

- [ ] **Step 8: Commit**

```bash
git add Dashboard.Client/ agent.sln
git commit -m "feat(observability): scaffold Dashboard.Client Blazor WASM project"
```

---

## Task 13: Dashboard State Management

**Files:**
- Create: `Dashboard.Client/State/Store.cs`
- Create: `Dashboard.Client/State/Metrics/MetricsStore.cs`
- Create: `Dashboard.Client/State/Health/HealthStore.cs`
- Create: `Dashboard.Client/State/Tokens/TokensStore.cs`
- Create: `Dashboard.Client/State/Tools/ToolsStore.cs`
- Create: `Dashboard.Client/State/Errors/ErrorsStore.cs`
- Create: `Dashboard.Client/State/Schedules/SchedulesStore.cs`
- Create: `Dashboard.Client/State/Connection/ConnectionStore.cs`
- Create: `Tests/Unit/Dashboard/State/MetricsStoreTests.cs`

Follow `WebChat.Client/State/Store.cs` pattern (31 lines — generic store with `BehaviorSubject<T>`).

- [ ] **Step 1: Write failing tests for store reducers**

```csharp
// Tests/Unit/Dashboard/State/MetricsStoreTests.cs
using Shouldly;

namespace Tests.Unit.Dashboard.State;

public class MetricsStoreTests
{
    [Fact]
    public void UpdateSummary_replaces_summary_state()
    {
        // Create MetricsStore with default state
        // Dispatch UpdateSummary action
        // Assert state contains new summary values
    }

    [Fact]
    public void IncrementTokens_adds_to_running_totals()
    {
        // Dispatch IncrementTokens with a TokenUsageEvent
        // Assert totals are incremented
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsStoreTests" -v minimal`
Expected: FAIL

- [ ] **Step 3: Copy Store.cs from WebChat.Client and create all state stores**

Copy the `Store<TState>` class from `WebChat.Client/State/Store.cs` to `Dashboard.Client/State/Store.cs`.

Then create each store with its state record and reducers. Example for `MetricsStore`:

```csharp
// Dashboard.Client/State/Metrics/MetricsStore.cs
using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Metrics;

public record MetricsState
{
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public decimal Cost { get; init; }
    public long ToolCalls { get; init; }
    public long ToolErrors { get; init; }
}

public sealed class MetricsStore() : Store<MetricsState>(new MetricsState())
{
    public void UpdateSummary(MetricsState summary) =>
        Dispatch(summary, (_, action) => action);

    public void IncrementFromTokenUsage(TokenUsageEvent evt) =>
        Dispatch(evt, (state, action) => state with
        {
            InputTokens = state.InputTokens + action.InputTokens,
            OutputTokens = state.OutputTokens + action.OutputTokens,
            Cost = state.Cost + action.Cost
        });

    public void IncrementFromToolCall(ToolCallEvent evt) =>
        Dispatch(evt, (state, action) => state with
        {
            ToolCalls = state.ToolCalls + 1,
            ToolErrors = state.ToolErrors + (action.Success ? 0 : 1)
        });
}
```

Create similar stores for Health, Tokens, Tools, Errors, Schedules, Connection — each with appropriate state records, reducers, and selectors. Follow the same patterns established in `WebChat.Client/State/`.

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsStoreTests" -v minimal`
Expected: PASS

- [ ] **Step 5: Register stores in Program.cs**

Update `Dashboard.Client/Program.cs` to register all stores as scoped services.

- [ ] **Step 6: Commit**

```bash
git add Dashboard.Client/State/ Tests/Unit/Dashboard/
git commit -m "feat(observability): add Dashboard.Client state management stores"
```

---

## Task 14: Dashboard API and Hub Services

**Files:**
- Create: `Dashboard.Client/Services/MetricsApiService.cs`
- Create: `Dashboard.Client/Services/MetricsHubService.cs`
- Create: `Dashboard.Client/Effects/DataLoadEffect.cs`
- Create: `Dashboard.Client/Effects/MetricsHubEffect.cs`

- [ ] **Step 1: Create MetricsApiService**

REST client for fetching historical data:

```csharp
// Dashboard.Client/Services/MetricsApiService.cs
using System.Net.Http.Json;
using Domain.DTOs.Metrics;

namespace Dashboard.Client.Services;

public sealed class MetricsApiService(HttpClient http)
{
    public Task<MetricsSummaryResponse?> GetSummaryAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<MetricsSummaryResponse>($"api/metrics/summary?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<TokenUsageEvent>?> GetTokensAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<TokenUsageEvent>>($"api/metrics/tokens?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ToolCallEvent>?> GetToolsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ToolCallEvent>>($"api/metrics/tools?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ErrorEvent>?> GetErrorsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ErrorEvent>>($"api/metrics/errors?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ScheduleExecutionEvent>?> GetSchedulesAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ScheduleExecutionEvent>>($"api/metrics/schedules?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ServiceHealthResponse>?> GetHealthAsync() =>
        http.GetFromJsonAsync<List<ServiceHealthResponse>>("api/metrics/health");
}

public record MetricsSummaryResponse(long InputTokens, long OutputTokens, long TotalTokens, decimal Cost, long ToolCalls, long ToolErrors);
public record ServiceHealthResponse(string Service, bool IsHealthy, string LastSeen);
```

- [ ] **Step 2: Create MetricsHubService**

SignalR client for live updates:

```csharp
// Dashboard.Client/Services/MetricsHubService.cs
using Domain.DTOs.Metrics;
using Microsoft.AspNetCore.SignalR.Client;

namespace Dashboard.Client.Services;

public sealed class MetricsHubService : IAsyncDisposable
{
    private readonly HubConnection _connection;

    public MetricsHubService(string hubUrl)
    {
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();
    }

    public HubConnection Connection => _connection;

    public void OnTokenUsage(Action<TokenUsageEvent> handler) =>
        _connection.On("OnTokenUsage", handler);

    public void OnToolCall(Action<ToolCallEvent> handler) =>
        _connection.On("OnToolCall", handler);

    public void OnError(Action<ErrorEvent> handler) =>
        _connection.On("OnError", handler);

    public void OnScheduleExecution(Action<ScheduleExecutionEvent> handler) =>
        _connection.On("OnScheduleExecution", handler);

    public void OnHealthUpdate(Action<ServiceHealthUpdate> handler) =>
        _connection.On("OnHealthUpdate", handler);

    public Task StartAsync(CancellationToken ct = default) =>
        _connection.StartAsync(ct);

    public async ValueTask DisposeAsync() =>
        await _connection.DisposeAsync();
}

public record ServiceHealthUpdate(string Service, bool IsHealthy, DateTimeOffset Timestamp);
```

- [ ] **Step 3: Create DataLoadEffect**

Effect that fetches data on page load and time-range changes — dispatches to stores.

- [ ] **Step 4: Create MetricsHubEffect**

Effect that connects to SignalR and dispatches live events to stores.

- [ ] **Step 5: Register services and effects in Program.cs**

- [ ] **Step 6: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: Build succeeded

- [ ] **Step 7: Commit**

```bash
git add Dashboard.Client/Services/ Dashboard.Client/Effects/ Dashboard.Client/Program.cs
git commit -m "feat(observability): add Dashboard API service, SignalR hub service, and effects"
```

---

## Task 15: Dashboard Layout and Shared Components

**Files:**
- Create: `Dashboard.Client/Layout/MainLayout.razor`
- Create: `Dashboard.Client/Layout/MainLayout.razor.css`
- Create: `Dashboard.Client/Components/KpiCard.razor`
- Create: `Dashboard.Client/Components/BarChart.razor`
- Create: `Dashboard.Client/Components/HealthGrid.razor`
- Create: `Dashboard.Client/Components/TimeRangeSelector.razor`

- [ ] **Step 1: Create MainLayout with icon sidebar**

```razor
@* Dashboard.Client/Layout/MainLayout.razor *@
@inherits LayoutComponentBase

<div class="layout">
    <nav class="sidebar">
        <NavLink href="/dashboard/" Match="NavLinkMatch.All" class="sidebar-icon" title="Overview">
            <span>◉</span>
        </NavLink>
        <NavLink href="/dashboard/tokens" class="sidebar-icon" title="Tokens">
            <span>$</span>
        </NavLink>
        <NavLink href="/dashboard/tools" class="sidebar-icon" title="Tools">
            <span>⚡</span>
        </NavLink>
        <NavLink href="/dashboard/errors" class="sidebar-icon" title="Errors">
            <span>⚠</span>
        </NavLink>
        <NavLink href="/dashboard/schedules" class="sidebar-icon" title="Schedules">
            <span>⏰</span>
        </NavLink>
    </nav>
    <main class="content">
        @Body
    </main>
</div>
```

- [ ] **Step 2: Create MainLayout.razor.css**

Scoped CSS for the sidebar layout (dark theme, icon sidebar on left, content area fills remaining space).

- [ ] **Step 3: Create shared components**

`KpiCard.razor` — displays a label, value, and optional accent color.
`BarChart.razor` — CSS-only bar chart: takes `IReadOnlyList<(string Label, double Value)>` and renders styled divs.
`HealthGrid.razor` — grid of service names with colored status dots.
`TimeRangeSelector.razor` — dropdown or buttons for time range (Today, 7d, 30d, custom).

- [ ] **Step 4: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add Dashboard.Client/Layout/ Dashboard.Client/Components/
git commit -m "feat(observability): add Dashboard layout with icon sidebar and shared components"
```

---

## Task 16: Dashboard Pages — Overview

**Files:**
- Create: `Dashboard.Client/Pages/Overview.razor`

- [ ] **Step 1: Create Overview page**

```razor
@page "/"
@* KPI cards row: tokens today, cost today, tool calls, errors *@
@* Mini token usage bar chart (last 24h) *@
@* Health grid showing all services *@
@* Recent activity feed (last N events) *@
```

Wire to `MetricsStore`, `HealthStore` via `@inject`. Subscribe to `StateObservable` for reactivity. Use `KpiCard`, `BarChart`, `HealthGrid` components.

- [ ] **Step 2: Verify build and visual inspection**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Pages/Overview.razor
git commit -m "feat(observability): add Overview dashboard page"
```

---

## Task 17: Dashboard Pages — Tokens, Tools, Errors, Schedules

**Files:**
- Create: `Dashboard.Client/Pages/Tokens.razor`
- Create: `Dashboard.Client/Pages/Tools.razor`
- Create: `Dashboard.Client/Pages/Errors.razor`
- Create: `Dashboard.Client/Pages/Schedules.razor`

- [ ] **Step 1: Create Tokens page**

Time-series bar chart (input vs output), cost breakdown, per-user table, per-model table. Uses `TimeRangeSelector` and `BarChart`. Injects `TokensStore`.

- [ ] **Step 2: Create Tools page**

Tool call frequency bar chart, success/failure rates per tool, average duration table. Injects `ToolsStore`.

- [ ] **Step 3: Create Errors page**

Error list with type/service/message columns, filterable by service. Injects `ErrorsStore`.

- [ ] **Step 4: Create Schedules page**

Schedule execution history list with status, duration, next run. Injects `SchedulesStore`.

- [ ] **Step 5: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add Dashboard.Client/Pages/
git commit -m "feat(observability): add Tokens, Tools, Errors, and Schedules dashboard pages"
```

---

## Task 18: Infrastructure — Docker Compose and Caddy

**Files:**
- Modify: `DockerCompose/docker-compose.yml`
- Modify: `DockerCompose/caddy/Caddyfile`

- [ ] **Step 1: Add observability service to docker-compose.yml**

Add after the `webui` service definition:

```yaml
  observability:
    build:
      context: ..
      dockerfile: Observability/Dockerfile
    ports:
      - "5002:8080"
    depends_on:
      - redis
    networks:
      - jackbot
```

- [ ] **Step 2: Add Caddy route for dashboard**

In `DockerCompose/caddy/Caddyfile`, add before the catch-all `handle` block in the `assistants.herfluffness.com` section:

```
    handle_path /dashboard/* {
        reverse_proxy observability:8080
    }
```

- [ ] **Step 3: Add user secrets mount to Docker Compose override files**

The observability service uses `AddUserSecrets<Program>()` and reads `Redis:ConnectionString`. It needs the user secrets volume mounted, same as other services.

In `DockerCompose/docker-compose.override.linux.yml`, add to the observability service:

```yaml
  observability:
    volumes:
      - ${HOME}/.microsoft/usersecrets:/home/app/.microsoft/usersecrets:ro
```

In `DockerCompose/docker-compose.override.windows.yml`, add:

```yaml
  observability:
    volumes:
      - ${APPDATA}/Microsoft/UserSecrets:/home/app/.microsoft/usersecrets:ro
```

- [ ] **Step 4: Create Observability/appsettings.json**

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning"
    }
  },
  "Redis": {
    "ConnectionString": "changeme"
  }
}
```

- [ ] **Step 5: Update CLAUDE.md launch commands**

Add `observability` to the service list in both the Linux and Windows launch command examples in `CLAUDE.md`.

- [ ] **Step 6: Commit**

```bash
git add DockerCompose/docker-compose.yml DockerCompose/docker-compose.override.linux.yml DockerCompose/docker-compose.override.windows.yml DockerCompose/caddy/Caddyfile Observability/appsettings.json CLAUDE.md
git commit -m "feat(observability): add observability service to Docker Compose and Caddy routing"
```

---

## Task 19: Final Integration and Verification

- [ ] **Step 1: Verify full solution builds**

Run: `dotnet build agent.sln`
Expected: Build succeeded (0 errors)

- [ ] **Step 2: Run full test suite**

Run: `dotnet test Tests/Tests.csproj -v minimal`
Expected: All tests pass (no regressions from instrumentation changes)

- [ ] **Step 3: Fix any build errors or test failures**

Address any issues found in steps 1-2.

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "feat(observability): final integration fixes"
```

(Only if there are changes from step 3.)
