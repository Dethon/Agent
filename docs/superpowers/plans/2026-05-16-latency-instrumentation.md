# Latency Instrumentation & Dashboard Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Measure per-stage latency of the agent turn (session warmup, memory recall, LLM time-to-first-token, LLM total, tool exec, history store) and surface it on the Observability dashboard as a combined page (KPI chips + per-stage bars + percentile trend + slow-turns table).

**Architecture:** Each hot-path stage emits a best-effort `LatencyEvent : MetricEvent` over the existing `IMetricsPublisher` → Redis Pub/Sub (`metrics:events`) pipeline. The collector stores it in a daily sorted set + totals hash and broadcasts `OnLatency`. The query service aggregates in-query (percentiles by nearest-rank). The dashboard adds a `Latency` page following the `Tokens.razor` Redux-like pattern, reusing `DynamicChart` for bars and adding one new ApexCharts line component for the trend.

**Tech Stack:** .NET 10, C#, System.Text.Json polymorphic DTOs, StackExchange.Redis, ASP.NET Core minimal APIs, SignalR, Blazor WebAssembly, Blazor-ApexCharts, xUnit + Shouldly + Moq.

**Spec:** `docs/superpowers/specs/2026-05-16-latency-instrumentation-design.md`

**Conventions discovered (follow these, they override the spec where the spec guessed):**
- Collector/query unit tests use `Mock<IDatabase>` + `Mock<IConnectionMultiplexer>` (see `Tests/Unit/Observability/Services/MetricsCollectorServiceTests.cs`), **not** `RedisFixture`. Mirror the existing pattern.
- `AgentMetricsPublisher` rewrites `AgentId` via `metricEvent with { AgentId = agentId }`. Sites that receive the per-agent publisher get `AgentId` for free; they only need to set `ConversationId`/`Model`.
- The trend service method returns a serialization-safe DTO list (`List<LatencyTrendSeries>`), not an enum-keyed dictionary.
- JSON everywhere uses `JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }`.

**Test command:** `dotnet test --filter "<expr>"` from repo root. Full build check: `dotnet build`.

---

## File Structure

**Domain (data model):**
- Create `Domain/DTOs/Metrics/LatencyEvent.cs` — the event record.
- Create `Domain/DTOs/Metrics/Enums/LatencyStage.cs`, `LatencyDimension.cs`, `LatencyMetric.cs`.
- Modify `Domain/DTOs/Metrics/MetricEvent.cs` — add `[JsonDerivedType]`.

**Observability (backend):**
- Modify `Observability/Services/MetricsCollectorService.cs` — `ProcessLatencyAsync` + dispatch case.
- Modify `Observability/Services/MetricsQueryService.cs` — `ComputePercentile`, `GetLatencyGroupedAsync`, `GetLatencyTrendAsync`, internal `AggregateLatency`.
- Create `Domain/DTOs/Metrics/LatencyTrend.cs` — `LatencyTrendPoint`, `LatencyTrendSeries`.
- Modify `Observability/MetricsApiEndpoints.cs` — 3 endpoints.

**Instrumentation sites:**
- Modify `Infrastructure/Memory/MemoryRecallHook.cs` — emit `LatencyEvent` alongside `MemoryRecallEvent`.
- Modify `Infrastructure/Agents/ChatClients/ToolApprovalChatClient.cs` — emit alongside `ToolCallEvent`.
- Modify `Infrastructure/Agents/ChatClients/RedisChatMessageStore.cs` — inject publisher, time `StoreChatHistoryAsync`.
- Modify `Infrastructure/Agents/McpAgent.cs` — inject publisher/model/conversationId, time warmup + LLM first/total.
- Modify `Infrastructure/Agents/MultiAgentFactory.cs` — pass publisher/model/conversationId into `McpAgent`.

**Dashboard client:**
- Modify `Dashboard.Client/Services/MetricsHubService.cs` — `OnLatency`.
- Modify `Dashboard.Client/Services/MetricsApiService.cs` — 3 client methods.
- Create `Dashboard.Client/State/Latency/LatencyState.cs`, `LatencyStore.cs`.
- Modify `Dashboard.Client/Effects/DataLoadEffect.cs`, `Dashboard.Client/Effects/MetricsHubEffect.cs`.
- Create `Dashboard.Client/Components/LatencyTrendChart.razor`.
- Create `Dashboard.Client/Pages/Latency.razor`.
- Modify `Dashboard.Client/Layout/MainLayout.razor` — nav entry.
- Modify `Dashboard.Client/Program.cs` — register `LatencyStore`.

---

## Task 1: LatencyEvent DTO + enums + polymorphic registration

**Files:**
- Create: `Domain/DTOs/Metrics/LatencyEvent.cs`
- Create: `Domain/DTOs/Metrics/Enums/LatencyStage.cs`
- Create: `Domain/DTOs/Metrics/Enums/LatencyDimension.cs`
- Create: `Domain/DTOs/Metrics/Enums/LatencyMetric.cs`
- Modify: `Domain/DTOs/Metrics/MetricEvent.cs`
- Test: `Tests/Unit/Infrastructure/Metrics/RedisMetricsPublisherTests.cs` (add a theory case)

- [ ] **Step 1: Write the failing test**

In `Tests/Unit/Infrastructure/Metrics/RedisMetricsPublisherTests.cs`, add a case to the existing `EventCases` `TheoryData` (it already round-trips events through `RedisMetricsPublisher` and asserts the serialized fragment). Add after the `TokenUsageEvent` entry:

```csharp
        {
            new LatencyEvent
            {
                Stage = LatencyStage.LlmTotal,
                DurationMs = 1234,
                Model = "anthropic/claude",
                ConversationId = "conv1"
            },
            "\"type\":\"latency\""
        }
```

Add `using Domain.DTOs.Metrics.Enums;` to the test file's usings if not present.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~RedisMetricsPublisherTests"`
Expected: FAIL — `LatencyEvent` / `LatencyStage` do not compile (type not found).

- [ ] **Step 3: Create the enums**

`Domain/DTOs/Metrics/Enums/LatencyStage.cs`:
```csharp
namespace Domain.DTOs.Metrics.Enums;

public enum LatencyStage
{
    SessionWarmup,
    MemoryRecall,
    LlmFirstToken,
    LlmTotal,
    ToolExec,
    HistoryStore
}
```

`Domain/DTOs/Metrics/Enums/LatencyDimension.cs`:
```csharp
namespace Domain.DTOs.Metrics.Enums;

public enum LatencyDimension { Stage, Agent, Model }
```

`Domain/DTOs/Metrics/Enums/LatencyMetric.cs`:
```csharp
namespace Domain.DTOs.Metrics.Enums;

public enum LatencyMetric { Avg, P50, P95, P99, Count, Max }
```

- [ ] **Step 4: Create the event record**

`Domain/DTOs/Metrics/LatencyEvent.cs`:
```csharp
using Domain.DTOs.Metrics.Enums;

namespace Domain.DTOs.Metrics;

public sealed record LatencyEvent : MetricEvent
{
    public required LatencyStage Stage { get; init; }
    public required long DurationMs { get; init; }
    public string? Model { get; init; }
}
```

- [ ] **Step 5: Register the derived type**

In `Domain/DTOs/Metrics/MetricEvent.cs`, add this line immediately after the `[JsonDerivedType(typeof(ContextTruncationEvent), "context_truncation")]` line:
```csharp
[JsonDerivedType(typeof(LatencyEvent), "latency")]
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RedisMetricsPublisherTests"`
Expected: PASS (all theory cases including the new `latency` one).

- [ ] **Step 7: Commit**

```bash
git add Domain/DTOs/Metrics/LatencyEvent.cs Domain/DTOs/Metrics/Enums/LatencyStage.cs Domain/DTOs/Metrics/Enums/LatencyDimension.cs Domain/DTOs/Metrics/Enums/LatencyMetric.cs Domain/DTOs/Metrics/MetricEvent.cs Tests/Unit/Infrastructure/Metrics/RedisMetricsPublisherTests.cs
git commit -m "feat(metrics): add LatencyEvent DTO + enums + polymorphic registration"
```

---

## Task 2: Collector — ProcessLatencyAsync

**Files:**
- Modify: `Observability/Services/MetricsCollectorService.cs`
- Test: `Tests/Unit/Observability/Services/MetricsCollectorServiceTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `MetricsCollectorServiceTests.cs` (mirrors the existing `ProcessEventAsync_MemoryRecall_*` tests; the class already wires `_db`, `_hubContext`, `_hubClients`, `_clientProxy`, `_sut`):

```csharp
    [Fact]
    public async Task ProcessEventAsync_Latency_StoresSortedSetAndTotals()
    {
        var evt = new LatencyEvent
        {
            Stage = LatencyStage.LlmTotal,
            DurationMs = 1500,
            Model = "anthropic/claude",
            Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:latency:2026-03-15",
            It.Is<RedisValue>(v => v.ToString().Contains("\"type\":\"latency\"")),
            evt.Timestamp.ToUnixTimeMilliseconds(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "latency:LlmTotal:count", 1, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-03-15", "latency:LlmTotal:totalMs", 1500, It.IsAny<CommandFlags>()), Times.Once);
    }

    [Fact]
    public async Task ProcessEventAsync_Latency_ForwardsToSignalR()
    {
        var evt = new LatencyEvent { Stage = LatencyStage.MemoryRecall, DurationMs = 42 };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _clientProxy.Verify(c => c.SendCoreAsync(
            "OnLatency",
            It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], evt)),
            It.IsAny<CancellationToken>()), Times.Once);
    }
```

Add `using Domain.DTOs.Metrics.Enums;` to the test usings if absent.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MetricsCollectorServiceTests"`
Expected: FAIL — the new `LatencyEvent` is not handled (no sorted-set/hash/broadcast calls; verifies fail).

- [ ] **Step 3: Add the dispatch case and handler**

In `MetricsCollectorService.cs`, inside the `switch (evt)` in `ProcessEventAsync`, add immediately after the `case ContextTruncationEvent truncation:` block:
```csharp
            case LatencyEvent latency:
                await ProcessLatencyAsync(latency, db);
                break;
```

Then add this private method next to `ProcessMemoryRecallAsync`:
```csharp
    private async Task ProcessLatencyAsync(LatencyEvent evt, IDatabase db)
    {
        var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
        var sortedSetKey = $"metrics:latency:{dateKey}";
        var totalsKey = $"metrics:totals:{dateKey}";
        var score = evt.Timestamp.ToUnixTimeMilliseconds();
        var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

        await Task.WhenAll(
            db.SortedSetAddAsync(sortedSetKey, json, score),
            db.HashIncrementAsync(totalsKey, $"latency:{evt.Stage}:count"),
            db.HashIncrementAsync(totalsKey, $"latency:{evt.Stage}:totalMs", evt.DurationMs),
            db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
            db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

        await hubContext.Clients.All.SendAsync("OnLatency", evt);
    }
```

Add `using Domain.DTOs.Metrics.Enums;` to the file usings if absent (the `LatencyEvent` type lives in `Domain.DTOs.Metrics`, already imported; the enum interpolation `evt.Stage` only needs `ToString()` so no extra using is strictly required — include it only if the file references the enum type directly).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MetricsCollectorServiceTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Observability/Services/MetricsCollectorService.cs Tests/Unit/Observability/Services/MetricsCollectorServiceTests.cs
git commit -m "feat(metrics): collector stores + broadcasts LatencyEvent"
```

---

## Task 3: Query — ComputePercentile + GetLatencyGroupedAsync

**Files:**
- Modify: `Observability/Services/MetricsQueryService.cs`
- Test: `Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `MetricsQueryServiceGroupingTests.cs` (the class has `_db`, `_redis`, `_sut`, `_jsonOptions`, and a `SetupSortedSet(key, events)` helper):

```csharp
    [Theory]
    [InlineData(50, 30)]
    [InlineData(95, 100)]
    [InlineData(99, 100)]
    public void ComputePercentile_NearestRank_ReturnsExpected(int q, int expected)
    {
        decimal[] values = [10, 20, 30, 40, 100];
        MetricsQueryService.ComputePercentile(values, q).ShouldBe(expected);
    }

    [Fact]
    public void ComputePercentile_EmptyAndSingle_AreSafe()
    {
        MetricsQueryService.ComputePercentile([], 95).ShouldBe(0m);
        MetricsQueryService.ComputePercentile([7m], 95).ShouldBe(7m);
    }

    [Fact]
    public async Task GetLatencyGroupedAsync_ByStage_P95_PercentilePerStage()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:latency:2026-03-15",
        [
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 100 },
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 200 },
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 5000 },
            new LatencyEvent { Stage = LatencyStage.MemoryRecall, DurationMs = 40 },
        ]);

        var result = await _sut.GetLatencyGroupedAsync(
            LatencyDimension.Stage, LatencyMetric.P95, date, date);

        result["LlmTotal"].ShouldBe(5000m);
        result["MemoryRecall"].ShouldBe(40m);
    }

    [Fact]
    public async Task GetLatencyGroupedAsync_ByModel_Avg_GroupsByModel()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:latency:2026-03-15",
        [
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 100, Model = "m1" },
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 300, Model = "m1" },
            new LatencyEvent { Stage = LatencyStage.SessionWarmup, DurationMs = 50 },
        ]);

        var result = await _sut.GetLatencyGroupedAsync(
            LatencyDimension.Model, LatencyMetric.Avg, date, date);

        result["m1"].ShouldBe(200m);
        result["unknown"].ShouldBe(50m);
    }
```

Add `using Domain.DTOs.Metrics.Enums;` if absent.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests"`
Expected: FAIL — `ComputePercentile` / `GetLatencyGroupedAsync` not defined.

- [ ] **Step 3: Implement percentile + grouped query + shared aggregator**

In `MetricsQueryService.cs`, add (place near `GetMemoryGroupedAsync`):

```csharp
    public static decimal ComputePercentile(IEnumerable<decimal> values, decimal q)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0m;
        if (sorted.Length == 1) return sorted[0];
        var rank = (int)Math.Ceiling((double)q / 100m * sorted.Length);
        var index = Math.Clamp(rank - 1, 0, sorted.Length - 1);
        return sorted[index];
    }

    internal static decimal AggregateLatency(IEnumerable<decimal> values, LatencyMetric metric)
    {
        var list = values.ToArray();
        if (list.Length == 0) return 0m;
        return metric switch
        {
            LatencyMetric.Avg => Math.Round(list.Average(), 2),
            LatencyMetric.P50 => ComputePercentile(list, 50),
            LatencyMetric.P95 => ComputePercentile(list, 95),
            LatencyMetric.P99 => ComputePercentile(list, 99),
            LatencyMetric.Count => list.Length,
            LatencyMetric.Max => list.Max(),
            _ => throw new ArgumentOutOfRangeException(nameof(metric))
        };
    }

    public async Task<Dictionary<string, decimal>> GetLatencyGroupedAsync(
        LatencyDimension dimension, LatencyMetric metric, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<LatencyEvent>("metrics:latency:", from, to);
        return events
            .GroupBy(e => dimension switch
            {
                LatencyDimension.Stage => e.Stage.ToString(),
                LatencyDimension.Agent => e.AgentId ?? "unknown",
                LatencyDimension.Model => e.Model ?? "unknown",
                _ => throw new ArgumentOutOfRangeException(nameof(dimension))
            })
            .ToDictionary(
                g => g.Key,
                g => AggregateLatency(g.Select(e => (decimal)e.DurationMs), metric));
    }
```

Add `using Domain.DTOs.Metrics.Enums;` to the file usings.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Observability/Services/MetricsQueryService.cs Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs
git commit -m "feat(metrics): latency grouped query + nearest-rank percentile"
```

---

## Task 4: Query — GetLatencyTrendAsync + trend DTOs

**Files:**
- Create: `Domain/DTOs/Metrics/LatencyTrend.cs`
- Modify: `Observability/Services/MetricsQueryService.cs`
- Test: `Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `MetricsQueryServiceGroupingTests.cs`:

```csharp
    [Fact]
    public async Task GetLatencyTrendAsync_ShortRange_BucketsHourlyPerStage()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:latency:2026-03-15",
        [
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 100,
                Timestamp = new DateTimeOffset(2026, 3, 15, 10, 5, 0, TimeSpan.Zero) },
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 300,
                Timestamp = new DateTimeOffset(2026, 3, 15, 10, 50, 0, TimeSpan.Zero) },
            new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 999,
                Timestamp = new DateTimeOffset(2026, 3, 15, 11, 1, 0, TimeSpan.Zero) },
        ]);

        var result = await _sut.GetLatencyTrendAsync(LatencyMetric.Avg, date, date);

        var series = result.Single(s => s.Stage == "LlmTotal");
        series.Points.Count.ShouldBe(2);
        series.Points[0].Bucket.ShouldBe(new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));
        series.Points[0].Value.ShouldBe(200m);   // avg(100,300)
        series.Points[1].Bucket.ShouldBe(new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero));
        series.Points[1].Value.ShouldBe(999m);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~GetLatencyTrendAsync_ShortRange"`
Expected: FAIL — `GetLatencyTrendAsync` / `LatencyTrendSeries` not defined.

- [ ] **Step 3: Create the DTOs**

`Domain/DTOs/Metrics/LatencyTrend.cs`:
```csharp
namespace Domain.DTOs.Metrics;

public sealed record LatencyTrendPoint(DateTimeOffset Bucket, decimal Value);

public sealed record LatencyTrendSeries(string Stage, IReadOnlyList<LatencyTrendPoint> Points);
```

- [ ] **Step 4: Implement GetLatencyTrendAsync**

In `MetricsQueryService.cs` add (uses `AggregateLatency` from Task 3):

```csharp
    public async Task<List<LatencyTrendSeries>> GetLatencyTrendAsync(
        LatencyMetric metric, DateOnly from, DateOnly to)
    {
        var events = await GetEventsAsync<LatencyEvent>("metrics:latency:", from, to);
        var hourly = to.DayNumber - from.DayNumber <= 2;

        return events
            .GroupBy(e => e.Stage)
            .Select(stageGroup => new LatencyTrendSeries(
                stageGroup.Key.ToString(),
                stageGroup
                    .GroupBy(e => BucketTimestamp(e.Timestamp, hourly))
                    .OrderBy(b => b.Key)
                    .Select(b => new LatencyTrendPoint(
                        b.Key,
                        AggregateLatency(b.Select(e => (decimal)e.DurationMs), metric)))
                    .ToList()))
            .ToList();
    }

    private static DateTimeOffset BucketTimestamp(DateTimeOffset ts, bool hourly)
    {
        var u = ts.UtcDateTime;
        return hourly
            ? new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero)
            : new DateTimeOffset(u.Year, u.Month, u.Day, 0, 0, 0, TimeSpan.Zero);
    }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests"`
Expected: PASS (all latency + memory grouping tests).

- [ ] **Step 6: Commit**

```bash
git add Domain/DTOs/Metrics/LatencyTrend.cs Observability/Services/MetricsQueryService.cs Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs
git commit -m "feat(metrics): latency trend query with hourly/daily bucketing"
```

---

## Task 5: API endpoints

**Files:**
- Modify: `Observability/MetricsApiEndpoints.cs`
- Test: `Tests/Unit/Observability/MetricsApiEndpointsTests.cs` (create if absent; otherwise add to the existing endpoint test class)

- [ ] **Step 1: Write the failing test**

If no endpoint test class exists, create `Tests/Unit/Observability/MetricsApiEndpointsTests.cs` using `WebApplicationFactory`-free minimal hosting mirroring any existing endpoint test. If an endpoint test harness already exists, add these to it. Minimal standalone version:

```csharp
using System.Net;
using System.Net.Http.Json;
using Domain.DTOs.Metrics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Observability;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability;

public class MetricsApiEndpointsTests
{
    [Fact]
    public async Task LatencyRoutes_AreMappedAndReturnJson()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var redis = new Moq.Mock<IConnectionMultiplexer>();
        var db = new Moq.Mock<IDatabase>();
        redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(db.Object);
        db.Setup(d => d.SortedSetRangeByScoreAsync(It.IsAny<RedisKey>(), It.IsAny<double>(),
                It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(),
                It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);
        builder.Services.AddSingleton(redis.Object);
        builder.Services.AddSingleton<MetricsQueryService>();
        var app = builder.Build();
        app.MapMetricsApi();
        await app.StartAsync();
        var client = app.GetTestClient();

        var raw = await client.GetAsync("/api/metrics/latency");
        var grouped = await client.GetAsync("/api/metrics/latency/by/Stage?metric=P95");
        var trend = await client.GetAsync("/api/metrics/latency/trend?metric=P95");

        raw.StatusCode.ShouldBe(HttpStatusCode.OK);
        grouped.StatusCode.ShouldBe(HttpStatusCode.OK);
        trend.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await raw.Content.ReadFromJsonAsync<List<LatencyEvent>>()).ShouldNotBeNull();

        await app.StopAsync();
    }
}
```

> If the project already has an endpoint test pattern (search `Tests/` for `MapMetricsApi` or `GetTestClient`), copy that harness instead of the above and add the same three assertions. Add `Microsoft.AspNetCore.Mvc.Testing` to the test project only if not already referenced.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~MetricsApiEndpointsTests"`
Expected: FAIL — routes return 404 (not mapped).

- [ ] **Step 3: Add the endpoints**

In `MetricsApiEndpoints.cs`, add immediately after the `/memory/by/{dimension}` `MapGet` block:

```csharp
        api.MapGet("/latency", async (
            MetricsQueryService query,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetEventsAsync<LatencyEvent>("metrics:latency:", fromDate, toDate);
        });

        api.MapGet("/latency/by/{dimension}", async (
            MetricsQueryService query,
            LatencyDimension dimension,
            LatencyMetric metric,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetLatencyGroupedAsync(dimension, metric, fromDate, toDate);
        });

        api.MapGet("/latency/trend", async (
            MetricsQueryService query,
            LatencyMetric metric,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetLatencyTrendAsync(metric, fromDate, toDate);
        });
```

Add `using Domain.DTOs.Metrics.Enums;` to the file usings if absent.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~MetricsApiEndpointsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Observability/MetricsApiEndpoints.cs Tests/Unit/Observability/MetricsApiEndpointsTests.cs
git commit -m "feat(metrics): latency REST endpoints (raw/by/trend)"
```

---

## Task 6: Instrument MemoryRecall

**Files:**
- Modify: `Infrastructure/Memory/MemoryRecallHook.cs`
- Test: `Tests/Unit/Memory/MemoryRecallHookTests.cs`

`EnrichAsync` already has `var sw = Stopwatch.StartNew();`, `sw.Stop();`, the injected `metricsPublisher`, and `conversationId`/`agentId` parameters. Emit a `LatencyEvent` right after the existing `MemoryRecallEvent` publish, guarded so it can never fail the turn.

- [ ] **Step 1: Write the failing test**

Add to `MemoryRecallHookTests.cs` (constructor already builds `_hook` with `_metricsPublisher`; mirror `EnrichAsync_PublishesRecallMetricEvent`):

```csharp
    [Fact]
    public async Task EnrichAsync_AlsoPublishesLatencyEvent_WithMemoryRecallStage()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        var session = CreateSessionWithStateKey("state-test");
        _threadStateStore.Setup(s => s.GetMessagesAsync("state-test"))
            .ReturnsAsync((ChatMessage[]?)null);
        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MemorySearchResult>());

        LatencyEvent? captured = null;
        _metricsPublisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricsDTOs.LatencyEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => captured = e as LatencyEvent)
            .Returns(Task.CompletedTask);

        await _hook.EnrichAsync(message, "user1", "conv1", null, session, CancellationToken.None);

        captured.ShouldNotBeNull();
        captured.Stage.ShouldBe(LatencyStage.MemoryRecall);
        captured.ConversationId.ShouldBe("conv1");
        captured.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }
```

Match the file's existing alias/usings for metric types (it uses `MetricsDTOs.MemoryRecallEvent`; add `using Domain.DTOs.Metrics.Enums;` and reference `LatencyEvent`/`LatencyStage` consistently — if the file aliases `Domain.DTOs.Metrics` as `MetricsDTOs`, use `MetricsDTOs.LatencyEvent` and `MetricsDTOs.Enums.LatencyStage` or add the enum using).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~EnrichAsync_AlsoPublishesLatencyEvent"`
Expected: FAIL — no `LatencyEvent` captured.

- [ ] **Step 3: Emit the latency event**

In `MemoryRecallHook.EnrichAsync`, immediately after the existing success-path `await metricsPublisher.PublishAsync(new MemoryRecallEvent { ... }, ct);` block, add:

```csharp
        try
        {
            await metricsPublisher.PublishAsync(new LatencyEvent
            {
                Stage = LatencyStage.MemoryRecall,
                DurationMs = sw.ElapsedMilliseconds,
                ConversationId = conversationId,
                AgentId = agentId is not null ? agentDefinitionProvider.GetById(agentId)?.Name ?? agentId : null
            }, ct);
        }
        catch
        {
            // Latency emission is best-effort and must never fail or slow a recall.
        }
```

Add `using Domain.DTOs.Metrics.Enums;` (or the file's alias equivalent) to the usings.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~MemoryRecallHookTests"`
Expected: PASS (new test + existing recall tests).

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Memory/MemoryRecallHook.cs Tests/Unit/Memory/MemoryRecallHookTests.cs
git commit -m "feat(metrics): emit MemoryRecall LatencyEvent (best-effort)"
```

---

## Task 7: Instrument ToolExec

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/ToolApprovalChatClient.cs`
- Test: `Tests/Unit/Infrastructure/ToolApprovalChatClientMetricsTests.cs` (the class containing `InvokeFunctionAsync_ApprovedTool_PublishesSuccessEvent`)

`InvokeWithMetricsAsync` already has `sw`, the optional `_metricsPublisher`, and `toolName`. `AgentId` is auto-set by the `AgentMetricsPublisher` wrapper the factory injects; `ConversationId` is not available at this layer (acceptable per spec).

- [ ] **Step 1: Write the failing test**

Add (mirrors the existing approved-tool metrics test):

```csharp
    [Fact]
    public async Task InvokeFunctionAsync_ApprovedTool_PublishesToolExecLatencyEvent()
    {
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var function = AIFunctionFactory.Create(() => "result", "mcp__server__TestTool");
        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

        LatencyEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is LatencyEvent l) captured = l; })
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        captured.ShouldNotBeNull();
        captured.Stage.ShouldBe(LatencyStage.ToolExec);
        captured.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }
```

Add `using Domain.DTOs.Metrics;` and `using Domain.DTOs.Metrics.Enums;` if absent.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~InvokeFunctionAsync_ApprovedTool_PublishesToolExecLatencyEvent"`
Expected: FAIL — no `LatencyEvent` captured.

- [ ] **Step 3: Emit the latency event**

In `ToolApprovalChatClient.InvokeWithMetricsAsync`, in the **success path** immediately after the existing `await _metricsPublisher.PublishAsync(new ToolCallEvent { ... }, cancellationToken);` (still inside the `if (_metricsPublisher is not null)` block), add:

```csharp
                try
                {
                    await _metricsPublisher.PublishAsync(new LatencyEvent
                    {
                        Stage = LatencyStage.ToolExec,
                        DurationMs = sw.ElapsedMilliseconds
                    }, cancellationToken);
                }
                catch
                {
                    // Best-effort latency emission; never fail a tool call.
                }
```

Add the identical guarded block in the **catch path** immediately after the existing `ToolCallEvent` publish there (so a failed tool still records its exec latency).

Add `using Domain.DTOs.Metrics.Enums;` to the file usings.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~ToolApprovalChatClient"`
Expected: PASS (new + existing tool-metrics tests).

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/ToolApprovalChatClient.cs Tests/Unit/Infrastructure/ToolApprovalChatClientMetricsTests.cs
git commit -m "feat(metrics): emit ToolExec LatencyEvent (best-effort)"
```

---

## Task 8: Instrument HistoryStore (RedisChatMessageStore)

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/RedisChatMessageStore.cs`
- Modify: `Infrastructure/Agents/McpAgent.cs` (constructs `RedisChatMessageStore`)
- Test: `Tests/Unit/Infrastructure/RedisChatMessageStoreTests.cs`

`RedisChatMessageStore` currently takes only `IThreadStateStore`. Add an optional `IMetricsPublisher?` and optional `string? conversationId` so timing is emitted around `AppendMessagesAsync`. Optional with defaults keeps all existing constructions (and tests) compiling.

- [ ] **Step 1: Write the failing test**

Add to `RedisChatMessageStoreTests.cs` (mirror its existing append test setup; it builds the store with a fake/`Mock<IThreadStateStore>`):

```csharp
    [Fact]
    public async Task StoreChatHistoryAsync_PublishesHistoryStoreLatencyEvent()
    {
        var stateStore = new Mock<IThreadStateStore>();
        stateStore.Setup(s => s.AppendMessagesAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<ChatMessage>>()))
            .Returns(Task.CompletedTask);
        var publisher = new Mock<IMetricsPublisher>();
        LatencyEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => captured = e as LatencyEvent)
            .Returns(Task.CompletedTask);

        var store = new RedisChatMessageStore(stateStore.Object, publisher.Object, "conv1");
        var session = new AgentSession();
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, "k1");
        var context = TestInvokedContext(session,
            request: [new ChatMessage(ChatRole.User, "hi")],
            response: [new ChatMessage(ChatRole.Assistant, "yo")]);

        await store.StoreChatHistoryAsync(context);

        captured.ShouldNotBeNull();
        captured.Stage.ShouldBe(LatencyStage.HistoryStore);
        captured.ConversationId.ShouldBe("conv1");
        captured.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }
```

> `StoreChatHistoryAsync` is `protected`. If the test class already uses a test-visible wrapper/`InternalsVisibleTo`, follow that. Otherwise expose an `internal` pass-through `StoreChatHistoryForTestAsync(InvokedContext ctx) => StoreChatHistoryAsync(ctx)` only if the project already has `InternalsVisibleTo("Tests")` (check `Infrastructure.csproj`); if not, drive it through the existing public path the other tests in this file already use and assert via the `publisher` mock. Reuse the file's existing `InvokedContext` construction helper (named here `TestInvokedContext`) rather than inventing one.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~StoreChatHistoryAsync_PublishesHistoryStoreLatencyEvent"`
Expected: FAIL — constructor has no publisher parameter / no event emitted.

- [ ] **Step 3: Add constructor params + emit**

Change the `RedisChatMessageStore` declaration from:
```csharp
public sealed class RedisChatMessageStore(IThreadStateStore store) : ChatHistoryProvider
```
to:
```csharp
public sealed class RedisChatMessageStore(
    IThreadStateStore store,
    IMetricsPublisher? metricsPublisher = null,
    string? conversationId = null) : ChatHistoryProvider
```

In `StoreChatHistoryAsync`, wrap the existing locked append with a stopwatch and emit after release. Replace the existing `await _lock.WaitAsync...try/finally` block with:

```csharp
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await _lock.WaitAsync(cancellationToken);
        try
        {
            await store.AppendMessagesAsync(redisKey, newMessages);
        }
        finally
        {
            _lock.Release();
        }
        sw.Stop();

        if (metricsPublisher is not null)
        {
            try
            {
                await metricsPublisher.PublishAsync(new LatencyEvent
                {
                    Stage = LatencyStage.HistoryStore,
                    DurationMs = sw.ElapsedMilliseconds,
                    ConversationId = conversationId
                }, cancellationToken);
            }
            catch
            {
                // Best-effort; persistence already succeeded.
            }
        }
```

Add usings: `using Domain.DTOs.Metrics;` and `using Domain.DTOs.Metrics.Enums;`.

- [ ] **Step 4: Thread the publisher through McpAgent's construction**

In `McpAgent.cs` constructor, the inner agent is built with `ChatHistoryProvider = new RedisChatMessageStore(stateStore)`. This task only changes that call site to forward the publisher/conversationId McpAgent will receive in Task 9. To avoid a half-wired state, do the McpAgent constructor signature change in Task 9; here, leave `new RedisChatMessageStore(stateStore)` unchanged (the new params are optional, so HistoryStore emission is simply inactive until Task 9 wires it). The unit test above proves the store works when a publisher is supplied.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~RedisChatMessageStoreTests"`
Expected: PASS (new + existing append/migration tests).

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Agents/ChatClients/RedisChatMessageStore.cs Tests/Unit/Infrastructure/RedisChatMessageStoreTests.cs
git commit -m "feat(metrics): emit HistoryStore LatencyEvent (best-effort)"
```

---

## Task 9: Instrument SessionWarmup + LLM first-token/total (McpAgent) and wire the factory

**Files:**
- Modify: `Infrastructure/Agents/McpAgent.cs`
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs`
- Test: `Tests/Unit/Infrastructure/McpAgentLatencyTests.cs` (create)

McpAgent has no publisher/model today. Add optional constructor params `IMetricsPublisher? metricsPublisher`, `string? model`, `string? conversationId` (defaults keep existing constructions/tests compiling). The factory has `agentPublisher`, `definition.Model`, `agentKey.ConversationId` in scope. `AgentId` is set by the `AgentMetricsPublisher` wrapper.

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Infrastructure/McpAgentLatencyTests.cs`. Use the existing fake chat client used by other McpAgent tests (search `Tests/` for `FakeChatClient`/`FakeAiAgent` usage with `McpAgent`; reuse that exact harness). Test shape:

```csharp
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Infrastructure.Agents;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class McpAgentLatencyTests
{
    [Fact]
    public async Task RunStreaming_EmitsLlmFirstTokenAndLlmTotal_WithModel()
    {
        var publisher = new Mock<IMetricsPublisher>();
        var captured = new List<LatencyEvent>();
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is LatencyEvent l) lock (captured) captured.Add(l); })
            .Returns(Task.CompletedTask);

        // Build an McpAgent over a fake IChatClient that yields >=1 streamed update.
        // Reuse the existing McpAgent test harness/fake from the McpAgent test suite.
        var agent = McpAgentTestHarness.Create(
            metricsPublisher: publisher.Object, model: "anthropic/claude", conversationId: "conv1");
        var session = await agent.CreateSessionAsync(CancellationToken.None);

        await foreach (var _ in agent.RunStreamingAsync(
            [new ChatMessage(ChatRole.User, "hi")], session)) { }

        var stages = captured.Select(c => c.Stage).ToList();
        stages.ShouldContain(LatencyStage.LlmFirstToken);
        stages.ShouldContain(LatencyStage.LlmTotal);
        captured.First(c => c.Stage == LatencyStage.LlmTotal).Model.ShouldBe("anthropic/claude");
        captured.First(c => c.Stage == LatencyStage.LlmTotal).ConversationId.ShouldBe("conv1");
    }

    [Fact]
    public async Task WarmupSessionAsync_EmitsSessionWarmupLatency()
    {
        var publisher = new Mock<IMetricsPublisher>();
        LatencyEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is LatencyEvent { Stage: LatencyStage.SessionWarmup } l) captured = l; })
            .Returns(Task.CompletedTask);

        var agent = McpAgentTestHarness.Create(metricsPublisher: publisher.Object, conversationId: "conv1");
        var session = await agent.CreateSessionAsync(CancellationToken.None);

        await agent.WarmupSessionAsync(session);

        captured.ShouldNotBeNull();
        captured.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task PublisherThrowing_DoesNotFailWarmupOrTurn()
    {
        var publisher = new Mock<IMetricsPublisher>();
        publisher.Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var agent = McpAgentTestHarness.Create(metricsPublisher: publisher.Object);
        var session = await agent.CreateSessionAsync(CancellationToken.None);

        await Should.NotThrowAsync(async () =>
        {
            await agent.WarmupSessionAsync(session);
            await foreach (var _ in agent.RunStreamingAsync(
                [new ChatMessage(ChatRole.User, "hi")], session)) { }
        });
    }
}
```

> `McpAgentTestHarness.Create(...)` stands for the existing way McpAgent is constructed in the current McpAgent test suite (find it: `grep -rl "new McpAgent(" Tests/`). Add a small static factory in the test project that builds an `McpAgent` with a fake `IChatClient` (no real MCP endpoints — pass `endpoints: []`) and forwards `metricsPublisher`/`model`/`conversationId`. If the existing suite already has such a helper, use it and add the optional params.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~McpAgentLatencyTests"`
Expected: FAIL — constructor has no `metricsPublisher`/`model`/`conversationId`; no events emitted.

- [ ] **Step 3: Add McpAgent constructor params + fields**

In `McpAgent.cs` add fields near the other readonly fields:
```csharp
    private readonly IMetricsPublisher? _metricsPublisher;
    private readonly string? _model;
    private readonly string? _conversationId;
```

Add three optional parameters to the end of the constructor signature (after `TimeProvider? timeProvider = null`):
```csharp
        IMetricsPublisher? metricsPublisher = null,
        string? model = null,
        string? conversationId = null)
```

In the constructor body assign them:
```csharp
        _metricsPublisher = metricsPublisher;
        _model = model;
        _conversationId = conversationId;
```

Change the inner-agent construction's history provider from:
```csharp
            ChatHistoryProvider = new RedisChatMessageStore(stateStore)
```
to:
```csharp
            ChatHistoryProvider = new RedisChatMessageStore(stateStore, metricsPublisher, conversationId)
```

Add `using Domain.DTOs.Metrics;` and `using Domain.DTOs.Metrics.Enums;` if absent.

- [ ] **Step 4: Add the best-effort emit helper**

Add a private helper to `McpAgent`:
```csharp
    private async Task SafePublishLatencyAsync(LatencyStage stage, long durationMs)
    {
        if (_metricsPublisher is null) return;
        try
        {
            await _metricsPublisher.PublishAsync(new LatencyEvent
            {
                Stage = stage,
                DurationMs = durationMs,
                Model = stage is LatencyStage.LlmFirstToken or LatencyStage.LlmTotal ? _model : null,
                ConversationId = _conversationId
            });
        }
        catch
        {
            // Latency emission is best-effort and must never affect a turn.
        }
    }
```

- [ ] **Step 5: Time the warmup**

Replace the body of `WarmupSessionAsync` with:
```csharp
    public override async Task WarmupSessionAsync(AgentSession thread, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await GetOrCreateSessionAsync(thread, ct);
        sw.Stop();
        await SafePublishLatencyAsync(LatencyStage.SessionWarmup, sw.ElapsedMilliseconds);
    }
```

- [ ] **Step 6: Time LLM first-token / total across all streaming paths**

`RunCoreStreamingAsync` has multiple yield branches. Wrap them once. Rename the existing override body to a private inner iterator and delegate:

Change the existing `protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(...)` to `private async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingInnerAsync(...)` (same parameters and body, including the `[EnumeratorCancellation]` attribute on the `CancellationToken`).

Add the new override that wraps it with timing:
```csharp
    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
        IEnumerable<ChatMessage> messages,
        AgentSession? thread = null,
        AgentRunOptions? options = null,
        CancellationToken cancellationToken = default)
        => WithLlmLatencyAsync(
            RunCoreStreamingInnerAsync(messages, thread, options, cancellationToken),
            cancellationToken);

    private async IAsyncEnumerable<AgentResponseUpdate> WithLlmLatencyAsync(
        IAsyncEnumerable<AgentResponseUpdate> source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var firstEmitted = false;
        try
        {
            await foreach (var update in source.WithCancellation(ct))
            {
                if (!firstEmitted)
                {
                    firstEmitted = true;
                    await SafePublishLatencyAsync(LatencyStage.LlmFirstToken, sw.ElapsedMilliseconds);
                }
                yield return update;
            }
        }
        finally
        {
            sw.Stop();
            await SafePublishLatencyAsync(LatencyStage.LlmTotal, sw.ElapsedMilliseconds);
        }
    }
```

(`RunCoreAsync` already delegates to `RunCoreStreamingAsync`, so non-streaming turns are covered too.)

- [ ] **Step 7: Wire the factory**

In `MultiAgentFactory.CreateFromDefinition`, the `return new McpAgent(...)` currently ends with `reasoningEffort: definition.ReasoningEffort`. Append the three new arguments:
```csharp
            reasoningEffort: definition.ReasoningEffort,
            metricsPublisher: agentPublisher,
            model: definition.Model,
            conversationId: agentKey.ConversationId);
```
(`agentPublisher` is the already-built per-agent `AgentMetricsPublisher` wrapper; `agentKey` is the method parameter.)

- [ ] **Step 8: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~McpAgentLatencyTests"`
Expected: PASS. Then run the existing McpAgent suite to confirm no regression:
Run: `dotnet test --filter "FullyQualifiedName~McpAgent"`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add Infrastructure/Agents/McpAgent.cs Infrastructure/Agents/MultiAgentFactory.cs Tests/Unit/Infrastructure/McpAgentLatencyTests.cs
git commit -m "feat(metrics): emit SessionWarmup + LLM first-token/total LatencyEvents; wire factory"
```

---

## Task 10: Dashboard — hub + API client methods

**Files:**
- Modify: `Dashboard.Client/Services/MetricsHubService.cs`
- Modify: `Dashboard.Client/Services/MetricsApiService.cs`
- Test: `Tests/Unit/Dashboard.Client/Services/MetricsApiServiceTests.cs` (create if absent; otherwise add)

- [ ] **Step 1: Write the failing test**

Add a test that the three client methods hit the right URLs (mirror any existing `MetricsApiService` test; if none, use a stub `HttpMessageHandler` capturing the request URI):

```csharp
using System.Net;
using System.Net.Http.Json;
using Dashboard.Client.Services;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.Services;

public class MetricsApiServiceLatencyTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastUri;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri!.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Array.Empty<object>())
            });
        }
    }

    [Fact]
    public async Task LatencyClientMethods_CallExpectedRoutes()
    {
        var handler = new CapturingHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var api = new MetricsApiService(http);
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 2);

        await api.GetLatencyAsync(from, to);
        handler.LastUri.ShouldContain("api/metrics/latency?from=2026-03-01&to=2026-03-02");

        await api.GetLatencyGroupedAsync(LatencyDimension.Stage, LatencyMetric.P95, from, to);
        handler.LastUri.ShouldContain("api/metrics/latency/by/Stage?metric=P95");

        await api.GetLatencyTrendAsync(LatencyMetric.P95, from, to);
        handler.LastUri.ShouldContain("api/metrics/latency/trend?metric=P95");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~MetricsApiServiceLatencyTests"`
Expected: FAIL — methods don't exist.

- [ ] **Step 3: Add the client methods**

In `MetricsApiService.cs` add (mirrors `GetMemoryRecallAsync` / `GetMemoryGroupedAsync`):

```csharp
    public Task<List<LatencyEvent>?> GetLatencyAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<LatencyEvent>>($"api/metrics/latency?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<Dictionary<string, decimal>?> GetLatencyGroupedAsync(
        LatencyDimension dimension, LatencyMetric metric, DateOnly from, DateOnly to,
        CancellationToken ct = default) =>
        http.GetFromJsonAsync<Dictionary<string, decimal>>(
            $"api/metrics/latency/by/{dimension}?metric={metric}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);

    public Task<List<LatencyTrendSeries>?> GetLatencyTrendAsync(
        LatencyMetric metric, DateOnly from, DateOnly to,
        CancellationToken ct = default) =>
        http.GetFromJsonAsync<List<LatencyTrendSeries>>(
            $"api/metrics/latency/trend?metric={metric}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);
```

- [ ] **Step 4: Add the hub client handler**

In `MetricsHubService.cs` add next to `OnMemoryRecall`:
```csharp
    public virtual IDisposable OnLatency(Func<LatencyEvent, Task> handler) =>
        _connection!.On("OnLatency", handler);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~MetricsApiServiceLatencyTests"`
Expected: PASS. Also `dotnet build` to confirm `MetricsHubService` compiles.

- [ ] **Step 6: Commit**

```bash
git add Dashboard.Client/Services/MetricsHubService.cs Dashboard.Client/Services/MetricsApiService.cs Tests/Unit/Dashboard.Client/Services/MetricsApiServiceLatencyTests.cs
git commit -m "feat(dashboard): latency hub handler + API client methods"
```

---

## Task 11: Dashboard — LatencyState + LatencyStore + DI

**Files:**
- Create: `Dashboard.Client/State/Latency/LatencyState.cs`
- Create: `Dashboard.Client/State/Latency/LatencyStore.cs`
- Modify: `Dashboard.Client/Program.cs`
- Test: `Tests/Unit/Dashboard.Client/State/DashboardStoreTests.cs`

- [ ] **Step 1: Write the failing test**

In `DashboardStoreTests.cs`, add a `LatencyStore` row to the `StoreFactories` `TheoryData` so the existing `SetDateRange_UpdatesFromAndTo` / `InitialState_DefaultsToToday` theories cover it:

```csharp
            { "Latency", () => new LatencyStore(), (s, f, t) => ((LatencyStore)s).SetDateRange(f, t), s => ((LatencyStore)s).State.From, s => ((LatencyStore)s).State.To },
```

Add `using Dashboard.Client.State.Latency;`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~DashboardStoreTests"`
Expected: FAIL — `LatencyStore` not defined.

- [ ] **Step 3: Create the state**

`Dashboard.Client/State/Latency/LatencyState.cs`:
```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Latency;

public record LatencyState
{
    public IReadOnlyList<LatencyEvent> Events { get; init; } = [];
    public LatencyDimension GroupBy { get; init; } = LatencyDimension.Stage;
    public LatencyMetric Metric { get; init; } = LatencyMetric.P95;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public IReadOnlyList<LatencyTrendSeries> Trend { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}
```

- [ ] **Step 4: Create the store**

`Dashboard.Client/State/Latency/LatencyStore.cs` (mirrors `TokensStore`):
```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Latency;

public record SetLatencyEvents(IReadOnlyList<LatencyEvent> Events) : IAction;
public record SetLatencyBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetLatencyTrend(IReadOnlyList<LatencyTrendSeries> Trend) : IAction;
public record SetLatencyGroupBy(LatencyDimension GroupBy) : IAction;
public record SetLatencyMetric(LatencyMetric Metric) : IAction;
public record AppendLatencyEvent(LatencyEvent Event) : IAction;
public record SetLatencyDateRange(DateOnly From, DateOnly To) : IAction;

public sealed class LatencyStore : Store<LatencyState>
{
    public LatencyStore() : base(new LatencyState()) { }

    public void SetEvents(IReadOnlyList<LatencyEvent> events) =>
        Dispatch(new SetLatencyEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetLatencyBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetTrend(IReadOnlyList<LatencyTrendSeries> trend) =>
        Dispatch(new SetLatencyTrend(trend), static (s, a) => s with { Trend = a.Trend });

    public void SetGroupBy(LatencyDimension groupBy) =>
        Dispatch(new SetLatencyGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(LatencyMetric metric) =>
        Dispatch(new SetLatencyMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void AppendEvent(LatencyEvent evt) =>
        Dispatch(new AppendLatencyEvent(evt), static (s, a) => s with { Events = [.. s.Events, a.Event] });

    public void SetDateRange(DateOnly from, DateOnly to) =>
        Dispatch(new SetLatencyDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });
}
```

- [ ] **Step 5: Register in DI**

In `Dashboard.Client/Program.cs`, add next to the other `AddSingleton<…Store>()` lines:
```csharp
builder.Services.AddSingleton<LatencyStore>();
```
Add `using Dashboard.Client.State.Latency;` to `Program.cs` usings (or fully-qualify).

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~DashboardStoreTests"`
Expected: PASS (all store rows including Latency).

- [ ] **Step 7: Commit**

```bash
git add Dashboard.Client/State/Latency/ Dashboard.Client/Program.cs Tests/Unit/Dashboard.Client/State/DashboardStoreTests.cs
git commit -m "feat(dashboard): LatencyStore + LatencyState + DI registration"
```

---

## Task 12: Dashboard — DataLoadEffect + MetricsHubEffect wiring

**Files:**
- Modify: `Dashboard.Client/Effects/DataLoadEffect.cs`
- Modify: `Dashboard.Client/Effects/MetricsHubEffect.cs`
- Test: `Tests/Unit/Dashboard.Client/Effects/MetricsHubEffectTests.cs`

- [ ] **Step 1: Write the failing test**

`MetricsHubEffectTests` constructs `MetricsHubEffect` with positional stores. Add a `LatencyStore _latencyStore = new();` field, pass it into the constructor (it will become the last constructor arg, see Step 3), dispose it in `DisposeAsync`, and add:

```csharp
    [Fact]
    public async Task OnLatency_AppendsEventToLatencyStore()
    {
        _handler.EnqueueResponse(new Dictionary<string, decimal>(), TimeSpan.Zero); // breakdown refresh
        _handler.EnqueueResponse(new List<LatencyTrendSeries>(), TimeSpan.Zero);     // trend refresh
        await _effect.StartAsync();

        await _hub.RaiseLatency(new LatencyEvent { Stage = LatencyStage.LlmTotal, DurationMs = 5 });

        _latencyStore.State.Events.ShouldContain(e => e.Stage == LatencyStage.LlmTotal);
    }
```

`FakeMetricsHub` must support latency. Add to the test project's `FakeMetricsHub` (same file/region the other `On*`/`Raise*` fakes live in):
```csharp
    private Func<LatencyEvent, Task>? _latency;
    public override IDisposable OnLatency(Func<LatencyEvent, Task> handler)
    { _latency = handler; return new Noop(); }
    public Task RaiseLatency(LatencyEvent e) => _latency?.Invoke(e) ?? Task.CompletedTask;
```
(Use the existing `Noop`/disposable helper already present in `FakeMetricsHub`; match the existing `Raise*` naming.)

Add `using Domain.DTOs.Metrics.Enums;` to the test file.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "FullyQualifiedName~OnLatency_AppendsEventToLatencyStore"`
Expected: FAIL — `MetricsHubEffect` has no latency wiring / constructor doesn't accept `LatencyStore`.

- [ ] **Step 3: Wire DataLoadEffect**

In `DataLoadEffect.cs`:
- Add `LatencyStore latencyStore` as the last primary-constructor parameter.
- In `LoadAsync`, alongside `memoryStore.SetDateRange(from, to);` add `latencyStore.SetDateRange(from, to);`.
- Add tasks alongside the memory tasks:
```csharp
            var latencyTask = api.GetLatencyAsync(from, to);
            var latencyBreakdownTask = api.GetLatencyGroupedAsync(
                latencyStore.State.GroupBy, latencyStore.State.Metric, from, to);
            var latencyTrendTask = api.GetLatencyTrendAsync(latencyStore.State.Metric, from, to);
```
- Add these three tasks to the `await Task.WhenAll(...)` list.
- After the memory `SetBreakdown` calls, add:
```csharp
            latencyStore.SetEvents(await latencyTask ?? []);
            latencyStore.SetBreakdown(await latencyBreakdownTask ?? []);
            latencyStore.SetTrend(await latencyTrendTask ?? []);
```
Add `using Dashboard.Client.State.Latency;`.

- [ ] **Step 4: Wire MetricsHubEffect**

In `MetricsHubEffect.cs`:
- Add `LatencyStore latencyStore` as the last primary-constructor parameter.
- Add a `CancellationTokenSource? _latencyBreakdownCts;` field (mirror `_tokenBreakdownCts`).
- Add a refresh method mirroring `RefreshTokenBreakdownAsync`:
```csharp
    private async Task RefreshLatencyBreakdownAsync(CancellationToken ct)
    {
        try
        {
            var s = latencyStore.State;
            var breakdown = await api.GetLatencyGroupedAsync(s.GroupBy, s.Metric, s.From, s.To, ct);
            var trend = await api.GetLatencyTrendAsync(s.Metric, s.From, s.To, ct);
            ct.ThrowIfCancellationRequested();
            latencyStore.SetBreakdown(breakdown ?? []);
            latencyStore.SetTrend(trend ?? []);
        }
        catch (OperationCanceledException) { }
        catch { /* keep last known values */ }
    }
```
- In the handler-registration section (where `hub.OnTokenUsage(...)` etc. are added to `_subscriptions`), add:
```csharp
        _subscriptions.Add(hub.OnLatency(async evt =>
        {
            latencyStore.AppendEvent(evt);
            var ct = ResetCts(ref _latencyBreakdownCts);
            await RefreshLatencyBreakdownAsync(ct);
        }));
```
Add `using Dashboard.Client.State.Latency;`.

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test --filter "FullyQualifiedName~MetricsHubEffectTests"`
Expected: PASS (new + existing effect tests). Then `dotnet build` (DI in `Program.cs` resolves the new constructor params automatically since `LatencyStore` is registered).

- [ ] **Step 6: Commit**

```bash
git add Dashboard.Client/Effects/DataLoadEffect.cs Dashboard.Client/Effects/MetricsHubEffect.cs Tests/Unit/Dashboard.Client/Effects/MetricsHubEffectTests.cs
git commit -m "feat(dashboard): load + live-update latency state via effects"
```

---

## Task 13: Dashboard — LatencyTrendChart component (ApexCharts line)

**Files:**
- Create: `Dashboard.Client/Components/LatencyTrendChart.razor`

This is the one new UI component, built on the existing Blazor-ApexCharts dependency (same `ApexChart`/`ApexPointSeries` primitives as `DynamicChart.razor`), as a multi-series line chart. No new test (it is declarative markup over a third-party component, consistent with `DynamicChart.razor` which has no unit test); it is exercised via the page and the existing dashboard E2E.

- [ ] **Step 1: Create the component**

`Dashboard.Client/Components/LatencyTrendChart.razor`:
```razor
@using ApexCharts
@using Domain.DTOs.Metrics

<div class="@(HasData ? "chart-container" : "")">
@if (!HasData)
{
    <p class="no-data">No data available</p>
}
else
{
    <ApexChart @ref="_chart" @key="_structureKey" TItem="TrendDatum" Options="_options">
        @foreach (var series in Series)
        {
            <ApexPointSeries TItem="TrendDatum"
                             Items="series.Points.Select(p => new TrendDatum(p.Bucket.UtcDateTime, (double)p.Value, series.Stage)).ToList()"
                             Name="@series.Stage"
                             SeriesType="SeriesType.Line"
                             XValue="d => d.X"
                             YValue="d => (decimal)d.Y" />
        }
    </ApexChart>
}
</div>

@code {
    [Parameter] public IReadOnlyList<LatencyTrendSeries> Series { get; set; } = [];
    [Parameter] public string MetricLabel { get; set; } = "";

    private ApexChart<TrendDatum>? _chart;
    private int _structureKey;
    private string _prevKey = "";

    private bool HasData => Series.Any(s => s.Points.Count > 0);

    private static readonly List<string> Palette =
        ["#6366f1", "#22d3ee", "#f59e0b", "#ef4444", "#10b981", "#f472b6", "#a78bfa", "#fbbf24"];

    private readonly ApexChartOptions<TrendDatum> _options = new()
    {
        Chart = new Chart
        {
            Background = "transparent",
            ForeColor = "#a0a0b0",
            Height = "260",
            Toolbar = new Toolbar { Show = false },
            Animations = new Animations
            {
                Enabled = true,
                AnimateGradually = new AnimateGradually { Enabled = false }
            }
        },
        Stroke = new Stroke { Curve = Curve.Smooth, Width = 2 },
        Legend = new Legend { Position = LegendPosition.Bottom, Labels = new LegendLabels { Colors = "#a0a0b0" } },
        Colors = Palette,
        Theme = new Theme { Mode = Mode.Dark },
        Xaxis = new XAxis { Type = XAxisType.Datetime, Labels = new XAxisLabels { Style = new AxisLabelStyle { Colors = "#a0a0b0" } } },
        Grid = new Grid { BorderColor = "#2a2a3e" }
    };

    protected override void OnParametersSet()
    {
        var key = string.Join('|', Series.Select(s => $"{s.Stage}:{s.Points.Count}"));
        if (key != _prevKey)
        {
            _prevKey = key;
            _structureKey++;
        }
    }

    public sealed record TrendDatum(DateTime X, double Y, string Stage);
}
```

> If the project's Blazor-ApexCharts version names any option type differently (e.g. `AxisLabelStyle`), match `DynamicChart.razor`'s actual usage — it is the source of truth for the installed API surface. Only options already present in `DynamicChart.razor` (`Chart`, `Animations`, `Legend`, `LegendLabels`, `Theme`, `Tooltip`, `Grid`, `XAxis`) are guaranteed; adjust `Stroke`/`Toolbar`/`XAxisType` to whatever the installed package exposes (verify with `dotnet build`).

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: build succeeds (fix any ApexCharts option-name mismatches against the installed package / `DynamicChart.razor`).

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Components/LatencyTrendChart.razor
git commit -m "feat(dashboard): LatencyTrendChart multi-series line chart (ApexCharts)"
```

---

## Task 14: Dashboard — Latency page + nav

**Files:**
- Create: `Dashboard.Client/Pages/Latency.razor`
- Modify: `Dashboard.Client/Layout/MainLayout.razor`

Combined layout C: KPI chips (p50/p95/p99 all-stages-combined + slowest stage by p95) + per-stage bars (`DynamicChart` HorizontalBar) + trend (`LatencyTrendChart`) + recent-slow-turns table (top 50 by `DurationMs`). Mirrors `Tokens.razor` exactly for store/effect/storage wiring.

- [ ] **Step 1: Create the page**

`Dashboard.Client/Pages/Latency.razor`:
```razor
@page "/latency"
@using Dashboard.Client.State.Latency
@using Domain.DTOs.Metrics
@using Domain.DTOs.Metrics.Enums
@using static Dashboard.Client.Components.PillSelector
@implements IDisposable
@inject LatencyStore Store
@inject DataLoadEffect DataLoad
@inject MetricsApiService Api
@inject LocalStorageService Storage

<div class="tokens-page">
    <header class="page-header">
        <h2>Latency</h2>
        <div class="controls">
            <PillSelector Label="Group by" Options="DimensionOptions" Value="@_state.GroupBy.ToString()"
                          OnChanged="OnDimensionChanged" />
            <PillSelector Label="Metric" Options="MetricOptions" Value="@_state.Metric.ToString()"
                          OnChanged="OnMetricChanged" />
            <PillSelector Label="Time" Options="TimeOptions" Value="@_selectedDays.ToString()"
                          OnChanged="OnTimeChanged" />
        </div>
    </header>

    <section class="kpi-row">
        <KpiCard Label="p50 (all stages)" Value="@($"{_p50:N0} ms")" Color="var(--accent-green)" />
        <KpiCard Label="p95 (all stages)" Value="@($"{_p95:N0} ms")" Color="var(--accent-yellow)" />
        <KpiCard Label="p99 (all stages)" Value="@($"{_p99:N0} ms")" Color="var(--accent-purple)" />
        <KpiCard Label="Slowest stage (p95)" Value="@_slowestStage" Color="var(--accent-blue)" />
    </section>

    <section class="section">
        <DynamicChart Data="_state.Breakdown" ChartType="DynamicChart.ChartMode.HorizontalBar"
                      MetricLabel="@(MetricOptions.FirstOrDefault(o => o.Value == _state.Metric.ToString())?.Label ?? "")"
                      Unit="ms"
                      UseFullLabels="true" />
    </section>

    <section class="section">
        <h3>@(MetricOptions.FirstOrDefault(o => o.Value == _state.Metric.ToString())?.Label) trend</h3>
        <LatencyTrendChart Series="_state.Trend" />
    </section>

    <section class="section">
        <h3>Recent Slow Turns</h3>
        <div class="events-table">
            <div class="table-header">
                <span>Time</span>
                <span>Stage</span>
                <span>Duration</span>
                <span>Agent</span>
                <span>Conversation</span>
            </div>
            @foreach (var evt in GetSlowest())
            {
                <div class="table-row">
                    <span>@evt.Timestamp.ToString("dd/MM HH:mm:ss")</span>
                    <span>@evt.Stage</span>
                    <span>@evt.DurationMs.ToString("N0") ms</span>
                    <span>@(evt.AgentId ?? "-")</span>
                    <span>@(evt.ConversationId ?? "-")</span>
                </div>
            }
        </div>
    </section>
</div>

@code {
    private LatencyState _state = new();
    private int _selectedDays = 1;
    private DateOnly _from = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateOnly _to = DateOnly.FromDateTime(DateTime.UtcNow);
    private decimal _p50, _p95, _p99;
    private string _slowestStage = "-";
    private IDisposable? _sub;

    private static readonly IReadOnlyList<PillOption> DimensionOptions =
    [
        new("Stage", nameof(LatencyDimension.Stage)),
        new("Agent", nameof(LatencyDimension.Agent)),
        new("Model", nameof(LatencyDimension.Model)),
    ];

    private static readonly IReadOnlyList<PillOption> MetricOptions =
    [
        new("Avg", nameof(LatencyMetric.Avg)),
        new("P50", nameof(LatencyMetric.P50)),
        new("P95", nameof(LatencyMetric.P95)),
        new("P99", nameof(LatencyMetric.P99)),
    ];

    private static readonly IReadOnlyList<PillOption> TimeOptions =
    [
        new("Today", "1"),
        new("7d", "7"),
        new("30d", "30"),
    ];

    protected override async Task OnInitializedAsync()
    {
        var savedGroupBy = await Storage.GetAsync<LatencyDimension>("latency.groupBy");
        var savedMetric = await Storage.GetAsync<LatencyMetric>("latency.metric");
        var savedDays = await Storage.GetIntAsync("latency.days");

        if (savedGroupBy.HasValue) Store.SetGroupBy(savedGroupBy.Value);
        if (savedMetric.HasValue) Store.SetMetric(savedMetric.Value);
        if (savedDays.HasValue) _selectedDays = savedDays.Value;

        _sub = Store.StateObservable.Subscribe(s =>
        {
            _state = s;
            RecomputeKpis(s);
            InvokeAsync(StateHasChanged);
        });

        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to.AddDays(-(_selectedDays - 1));
        Store.SetDateRange(_from, _to);
        await DataLoad.LoadAsync(_from, _to);
    }

    private void RecomputeKpis(LatencyState s)
    {
        var all = s.Events.Select(e => (decimal)e.DurationMs).ToArray();
        _p50 = Percentile(all, 50);
        _p95 = Percentile(all, 95);
        _p99 = Percentile(all, 99);
        _slowestStage = s.Events
            .GroupBy(e => e.Stage)
            .Select(g => (Stage: g.Key, P95: Percentile(g.Select(e => (decimal)e.DurationMs).ToArray(), 95)))
            .OrderByDescending(x => x.P95)
            .Select(x => x.Stage.ToString())
            .FirstOrDefault() ?? "-";
    }

    private static decimal Percentile(decimal[] values, decimal q)
    {
        if (values.Length == 0) return 0m;
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 1) return sorted[0];
        var rank = (int)Math.Ceiling((double)q / 100m * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private IEnumerable<LatencyEvent> GetSlowest() =>
        _state.Events.OrderByDescending(e => e.DurationMs).Take(50);

    private async Task OnDimensionChanged(string value)
    {
        Store.SetGroupBy(Enum.Parse<LatencyDimension>(value));
        await Storage.SetAsync("latency.groupBy", value);
        await ReloadBreakdown();
    }

    private async Task OnMetricChanged(string value)
    {
        Store.SetMetric(Enum.Parse<LatencyMetric>(value));
        await Storage.SetAsync("latency.metric", value);
        await ReloadBreakdown();
    }

    private async Task OnTimeChanged(string value)
    {
        _selectedDays = int.Parse(value);
        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to.AddDays(-(_selectedDays - 1));
        Store.SetDateRange(_from, _to);
        await Storage.SetAsync("latency.days", value);
        await DataLoad.LoadAsync(_from, _to);
    }

    private async Task ReloadBreakdown()
    {
        var breakdown = await Api.GetLatencyGroupedAsync(
            Store.State.GroupBy, Store.State.Metric, _from, _to);
        Store.SetBreakdown(breakdown ?? []);
        var trend = await Api.GetLatencyTrendAsync(Store.State.Metric, _from, _to);
        Store.SetTrend(trend ?? []);
    }

    public void Dispose() => _sub?.Dispose();
}
```

> `KpiCard`, `PillSelector`, `DynamicChart`, `LocalStorageService.GetAsync<TEnum>`, `GetIntAsync`, `SetAsync` are all used exactly as in `Tokens.razor` — if any signature differs, match `Tokens.razor`'s actual usage (it is the reference implementation).

- [ ] **Step 2: Add the nav entry**

In `Dashboard.Client/Layout/MainLayout.razor`, add inside `<nav class="sidebar">` after the Memory `NavLink` block:
```razor
    <NavLink href="/latency" class="sidebar-icon" title="Latency">
        <span>&#9201;&#xFE0E;</span>
    </NavLink>
```

- [ ] **Step 3: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: build succeeds.

- [ ] **Step 4: Run the full dashboard unit suite**

Run: `dotnet test --filter "FullyQualifiedName~Dashboard.Client"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Dashboard.Client/Pages/Latency.razor Dashboard.Client/Layout/MainLayout.razor
git commit -m "feat(dashboard): combined Latency page + nav entry"
```

---

## Task 15: Full regression + spec coverage check

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build`
Expected: 0 errors, 0 new warnings.

- [ ] **Step 2: Run the full affected test surface**

Run: `dotnet test --filter "FullyQualifiedName~Latency|FullyQualifiedName~MetricsCollectorServiceTests|FullyQualifiedName~MetricsQueryServiceGroupingTests|FullyQualifiedName~MemoryRecallHookTests|FullyQualifiedName~ToolApprovalChatClient|FullyQualifiedName~RedisChatMessageStoreTests|FullyQualifiedName~McpAgent|FullyQualifiedName~MetricsHubEffectTests|FullyQualifiedName~DashboardStoreTests|FullyQualifiedName~RedisMetricsPublisherTests"`
Expected: all PASS.

- [ ] **Step 3: Spec coverage self-check**

Confirm each spec section maps to a task: data model (T1), collector (T2), query incl. percentile/grouped/trend (T3,T4), API (T5), instrumentation sites SessionWarmup/MemoryRecall/LlmFirstToken/LlmTotal/ToolExec/HistoryStore (T6,T7,T8,T9), dashboard page/store/effects/trend chart/nav/hub/api-client (T10–T14), best-effort emission asserted (T6,T7,T8,T9). No code/test gaps remain.

- [ ] **Step 4: Final commit (if any verification fixups were needed)**

```bash
git add -A
git commit -m "chore: latency instrumentation regression fixups"
```

---

## Self-Review Notes (resolved during planning)

- **Spec said SVG/no charting dep:** corrected — the dashboard already depends on Blazor-ApexCharts; `LatencyTrendChart` uses it (spec + this plan updated; user confirmed).
- **Spec said `RedisFixture` integration tests for collector/query:** the actual house pattern is `Mock<IDatabase>` unit tests (`MetricsCollectorServiceTests`, `MetricsQueryServiceGroupingTests`); plan follows the real pattern.
- **Spec said trend service returns enum-keyed dictionary:** plan returns `List<LatencyTrendSeries>` (serialization-safe, same information) — consistent with spec's "trend series DTO" for the API.
- **`McpAgent`/`RedisChatMessageStore` lacked publisher/model/conversationId:** added as optional constructor params wired from `MultiAgentFactory` (`agentPublisher`, `definition.Model`, `agentKey.ConversationId`); `AgentId` is supplied by the existing `AgentMetricsPublisher` wrapper.
- **`RunStreamingCoreAsync` has multiple yield paths:** instrumented at the `RunCoreStreamingAsync` override (wrapping an extracted inner iterator) so every streaming path — and `RunCoreAsync`, which delegates to it — is timed exactly once.
- **Type consistency:** `AggregateLatency`/`ComputePercentile` defined once in Task 3 and reused in Task 4; `LatencyTrendSeries`/`LatencyTrendPoint` defined in Task 4 and consumed in Tasks 10–14; `LatencyStore` API (`SetEvents/SetBreakdown/SetTrend/SetGroupBy/SetMetric/AppendEvent/SetDateRange`) defined in Task 11 and used consistently in Tasks 12 & 14.
