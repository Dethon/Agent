# Dynamic Dashboard Visualizations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace hardcoded chart groupings with dynamic dimension/metric selectors on each dashboard page, backed by server-side aggregation endpoints, and migrate from pure-CSS bar charts to BlazorApexCharts.

**Architecture:** Server-side aggregation endpoints group metric events by requested dimension and metric. The Dashboard.Client stores hold the active grouping state and breakdown data. Pages render pill-toggle selectors that trigger API calls, with results displayed in ApexCharts (donut for Tokens, horizontal bar for Tools/Errors/Schedules).

**Tech Stack:** .NET 10, Blazor WebAssembly, BlazorApexCharts, StackExchange.Redis, System.Reactive, xUnit + Moq + Shouldly

**Spec:** `docs/superpowers/specs/2026-03-23-dynamic-dashboard-visualizations-design.md`

---

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `Domain/DTOs/Metrics/Enums/TokenDimension.cs` | Enum: User, Model, Agent |
| Create | `Domain/DTOs/Metrics/Enums/TokenMetric.cs` | Enum: Tokens, Cost |
| Create | `Domain/DTOs/Metrics/Enums/ToolDimension.cs` | Enum: ToolName, Status |
| Create | `Domain/DTOs/Metrics/Enums/ToolMetric.cs` | Enum: CallCount, AvgDuration, ErrorRate |
| Create | `Domain/DTOs/Metrics/Enums/ErrorDimension.cs` | Enum: Service, ErrorType |
| Create | `Domain/DTOs/Metrics/Enums/ScheduleDimension.cs` | Enum: Schedule, Status |
| Modify | `Observability/Services/MetricsQueryService.cs` | Add grouped aggregation methods |
| Modify | `Observability/MetricsApiEndpoints.cs` | Add `/by/{dimension}` endpoints |
| Create | `Dashboard.Client/Components/PillSelector.razor` | Generic pill-toggle control |
| Create | `Dashboard.Client/Components/DynamicChart.razor` | ApexCharts wrapper |
| Modify | `Dashboard.Client/State/Tokens/TokensState.cs` | Replace ByUser/ByModel with GroupBy+Metric+Breakdown |
| Modify | `Dashboard.Client/State/Tokens/TokensStore.cs` | Replace SetBreakdowns with SetBreakdown+SetGroupBy+SetMetric |
| Modify | `Dashboard.Client/State/Tools/ToolsState.cs` | Add GroupBy+Metric+Breakdown |
| Modify | `Dashboard.Client/State/Tools/ToolsStore.cs` | Add SetBreakdown+SetGroupBy+SetMetric |
| Modify | `Dashboard.Client/State/Errors/ErrorsState.cs` | Add GroupBy+Breakdown |
| Modify | `Dashboard.Client/State/Errors/ErrorsStore.cs` | Add SetBreakdown+SetGroupBy |
| Modify | `Dashboard.Client/State/Schedules/SchedulesState.cs` | Add GroupBy+Breakdown |
| Modify | `Dashboard.Client/State/Schedules/SchedulesStore.cs` | Add SetBreakdown+SetGroupBy |
| Modify | `Dashboard.Client/Services/MetricsApiService.cs` | Add generic grouped endpoint methods |
| Modify | `Dashboard.Client/Effects/DataLoadEffect.cs` | Load breakdowns via new endpoints |
| Modify | `Dashboard.Client/Pages/Tokens.razor` | Use PillSelector + DynamicChart (donut) |
| Modify | `Dashboard.Client/Pages/Tools.razor` | Use PillSelector + DynamicChart (hbar) |
| Modify | `Dashboard.Client/Pages/Errors.razor` | Add TimeRangeSelector + PillSelector + DynamicChart |
| Modify | `Dashboard.Client/Pages/Schedules.razor` | Use PillSelector + DynamicChart (hbar) |
| Modify | `Dashboard.Client/Pages/Overview.razor` | Use new breakdown state |
| Modify | `Dashboard.Client/Dashboard.Client.csproj` | Add Blazor-ApexCharts package |
| Modify | `Dashboard.Client/Program.cs` | No change needed (stores already registered) |
| Modify | `Dashboard.Client/_Imports.razor` | Add ApexCharts using |
| Delete | `Dashboard.Client/Components/BarChart.razor` | Replaced by DynamicChart |
| Delete | `Dashboard.Client/Components/BarItem.cs` | No longer needed |
| Delete | `Dashboard.Client/Components/TimeRangeSelector.razor` | Replaced by PillSelector |
| Create | `Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs` | Tests for new grouping methods |
| Create | `Tests/Unit/Dashboard.Client/State/TokensStoreTests.cs` | Tests for new Tokens state |
| Create | `Tests/Unit/Dashboard.Client/State/ToolsStoreTests.cs` | Tests for new Tools state |

---

## Task 1: Add BlazorApexCharts Package

**Files:**
- Modify: `Dashboard.Client/Dashboard.Client.csproj:15-18`
- Modify: `Dashboard.Client/_Imports.razor:13`

- [ ] **Step 1: Add the NuGet package**

```bash
cd /home/dethon/repos/agent && dotnet add Dashboard.Client/Dashboard.Client.csproj package Blazor-ApexCharts
```

- [ ] **Step 2: Add the using directive to _Imports.razor**

Add to `Dashboard.Client/_Imports.razor` after line 13:

```razor
@using ApexCharts
```

- [ ] **Step 3: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Dashboard.Client/Dashboard.Client.csproj Dashboard.Client/_Imports.razor
git commit -m "feat(dashboard): add Blazor-ApexCharts package"
```

---

## Task 2: Create Domain Enums

**Files:**
- Create: `Domain/DTOs/Metrics/Enums/TokenDimension.cs`
- Create: `Domain/DTOs/Metrics/Enums/TokenMetric.cs`
- Create: `Domain/DTOs/Metrics/Enums/ToolDimension.cs`
- Create: `Domain/DTOs/Metrics/Enums/ToolMetric.cs`
- Create: `Domain/DTOs/Metrics/Enums/ErrorDimension.cs`
- Create: `Domain/DTOs/Metrics/Enums/ScheduleDimension.cs`

- [ ] **Step 1: Create TokenDimension enum**

```csharp
// Domain/DTOs/Metrics/Enums/TokenDimension.cs
namespace Domain.DTOs.Metrics.Enums;

public enum TokenDimension { User, Model, Agent }
```

- [ ] **Step 2: Create TokenMetric enum**

```csharp
// Domain/DTOs/Metrics/Enums/TokenMetric.cs
namespace Domain.DTOs.Metrics.Enums;

public enum TokenMetric { Tokens, Cost }
```

- [ ] **Step 3: Create ToolDimension enum**

```csharp
// Domain/DTOs/Metrics/Enums/ToolDimension.cs
namespace Domain.DTOs.Metrics.Enums;

public enum ToolDimension { ToolName, Status }
```

- [ ] **Step 4: Create ToolMetric enum**

```csharp
// Domain/DTOs/Metrics/Enums/ToolMetric.cs
namespace Domain.DTOs.Metrics.Enums;

public enum ToolMetric { CallCount, AvgDuration, ErrorRate }
```

- [ ] **Step 5: Create ErrorDimension enum**

```csharp
// Domain/DTOs/Metrics/Enums/ErrorDimension.cs
namespace Domain.DTOs.Metrics.Enums;

public enum ErrorDimension { Service, ErrorType }
```

- [ ] **Step 6: Create ScheduleDimension enum**

```csharp
// Domain/DTOs/Metrics/Enums/ScheduleDimension.cs
namespace Domain.DTOs.Metrics.Enums;

public enum ScheduleDimension { Schedule, Status }
```

- [ ] **Step 7: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Domain/Domain.csproj
```

- [ ] **Step 8: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/
git commit -m "feat(domain): add dimension and metric enums for dynamic dashboard grouping"
```

---

## Task 3: Backend — Token Grouped Aggregation (TDD)

**Files:**
- Create: `Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs`
- Modify: `Observability/Services/MetricsQueryService.cs:101-124`

- [ ] **Step 1: Write failing tests for GetTokenGroupedAsync**

Create `Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs`:

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Moq;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;
using System.Text.Json;

namespace Tests.Unit.Observability.Services;

public class MetricsQueryServiceGroupingTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly MetricsQueryService _sut;

    public MetricsQueryServiceGroupingTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _sut = new MetricsQueryService(_redis.Object);
    }

    private void SetupSortedSet(string key, params MetricEvent[] events)
    {
        var entries = events
            .Select(e => (RedisValue)JsonSerializer.Serialize<MetricEvent>(e, JsonOptions))
            .ToArray();
        _db.Setup(d => d.SortedSetRangeByScoreAsync(
                key, It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<Exclude>(), It.IsAny<Order>(),
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync(entries);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_ByUser_Tokens_SumsTokensPerSender()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tokens:2026-03-15",
            new TokenUsageEvent { Sender = "alice", Model = "gpt-4", InputTokens = 100, OutputTokens = 50, Cost = 0.01m },
            new TokenUsageEvent { Sender = "alice", Model = "gpt-4", InputTokens = 200, OutputTokens = 100, Cost = 0.02m },
            new TokenUsageEvent { Sender = "bob", Model = "claude", InputTokens = 300, OutputTokens = 150, Cost = 0.03m });

        var result = await _sut.GetTokenGroupedAsync(TokenDimension.User, TokenMetric.Tokens, date, date);

        result["alice"].ShouldBe(450m);
        result["bob"].ShouldBe(450m);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_ByModel_Cost_SumsCostPerModel()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tokens:2026-03-15",
            new TokenUsageEvent { Sender = "alice", Model = "gpt-4", InputTokens = 100, OutputTokens = 50, Cost = 0.05m },
            new TokenUsageEvent { Sender = "bob", Model = "gpt-4", InputTokens = 200, OutputTokens = 100, Cost = 0.10m },
            new TokenUsageEvent { Sender = "alice", Model = "claude", InputTokens = 300, OutputTokens = 150, Cost = 0.03m });

        var result = await _sut.GetTokenGroupedAsync(TokenDimension.Model, TokenMetric.Cost, date, date);

        result["gpt-4"].ShouldBe(0.15m);
        result["claude"].ShouldBe(0.03m);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_ByAgent_Tokens_GroupsByAgentId()
    {
        var date = new DateOnly(2026, 3, 15);
        SetupSortedSet("metrics:tokens:2026-03-15",
            new TokenUsageEvent { AgentId = "agent-1", Sender = "alice", Model = "gpt-4", InputTokens = 100, OutputTokens = 50, Cost = 0.01m },
            new TokenUsageEvent { AgentId = "agent-2", Sender = "alice", Model = "gpt-4", InputTokens = 200, OutputTokens = 100, Cost = 0.02m });

        var result = await _sut.GetTokenGroupedAsync(TokenDimension.Agent, TokenMetric.Tokens, date, date);

        result["agent-1"].ShouldBe(150m);
        result["agent-2"].ShouldBe(300m);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_EmptyData_ReturnsEmptyDictionary()
    {
        var date = new DateOnly(2026, 3, 15);
        _db.Setup(d => d.SortedSetRangeByScoreAsync(
                "metrics:tokens:2026-03-15", It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<Exclude>(), It.IsAny<Order>(),
                It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
            .ReturnsAsync([]);

        var result = await _sut.GetTokenGroupedAsync(TokenDimension.User, TokenMetric.Tokens, date, date);

        result.ShouldBeEmpty();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd /home/dethon/repos/agent && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests.GetToken" --no-restore -v minimal
```

Expected: FAIL — `MetricsQueryService` does not have `GetTokenGroupedAsync` method.

- [ ] **Step 3: Implement GetTokenGroupedAsync**

Add to `Observability/Services/MetricsQueryService.cs` before the `EnumerateDates` method (before line 126):

```csharp
public async Task<Dictionary<string, decimal>> GetTokenGroupedAsync(
    TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to)
{
    var events = await GetEventsAsync<TokenUsageEvent>("metrics:tokens:", from, to);

    return events
        .GroupBy(e => dimension switch
        {
            TokenDimension.User => e.Sender,
            TokenDimension.Model => e.Model,
            TokenDimension.Agent => e.AgentId ?? "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        })
        .ToDictionary(
            g => g.Key,
            g => metric switch
            {
                TokenMetric.Tokens => g.Sum(e => (decimal)(e.InputTokens + e.OutputTokens)),
                TokenMetric.Cost => g.Sum(e => e.Cost),
                _ => throw new ArgumentOutOfRangeException(nameof(metric))
            });
}
```

Add the using at the top of the file:

```csharp
using Domain.DTOs.Metrics.Enums;
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd /home/dethon/repos/agent && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests.GetToken" --no-restore -v minimal
```

Expected: All 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Observability/Services/MetricsQueryService.cs Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs
git commit -m "feat(observability): add GetTokenGroupedAsync for dynamic token breakdowns"
```

---

## Task 4: Backend — Tools Grouped Aggregation (TDD)

**Files:**
- Modify: `Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs`
- Modify: `Observability/Services/MetricsQueryService.cs`

- [ ] **Step 1: Write failing tests for GetToolGroupedAsync**

Append to `MetricsQueryServiceGroupingTests.cs`:

```csharp
[Fact]
public async Task GetToolGroupedAsync_ByTool_CallCount_CountsPerTool()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:tools:2026-03-15",
        new ToolCallEvent { ToolName = "search", DurationMs = 100, Success = true },
        new ToolCallEvent { ToolName = "search", DurationMs = 200, Success = false },
        new ToolCallEvent { ToolName = "read", DurationMs = 50, Success = true });

    var result = await _sut.GetToolGroupedAsync(ToolDimension.ToolName, ToolMetric.CallCount, date, date);

    result["search"].ShouldBe(2m);
    result["read"].ShouldBe(1m);
}

[Fact]
public async Task GetToolGroupedAsync_ByTool_AvgDuration_AveragesPerTool()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:tools:2026-03-15",
        new ToolCallEvent { ToolName = "search", DurationMs = 100, Success = true },
        new ToolCallEvent { ToolName = "search", DurationMs = 300, Success = true },
        new ToolCallEvent { ToolName = "read", DurationMs = 50, Success = true });

    var result = await _sut.GetToolGroupedAsync(ToolDimension.ToolName, ToolMetric.AvgDuration, date, date);

    result["search"].ShouldBe(200m);
    result["read"].ShouldBe(50m);
}

[Fact]
public async Task GetToolGroupedAsync_ByTool_ErrorRate_CalculatesPercentage()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:tools:2026-03-15",
        new ToolCallEvent { ToolName = "search", DurationMs = 100, Success = true },
        new ToolCallEvent { ToolName = "search", DurationMs = 200, Success = false },
        new ToolCallEvent { ToolName = "read", DurationMs = 50, Success = true });

    var result = await _sut.GetToolGroupedAsync(ToolDimension.ToolName, ToolMetric.ErrorRate, date, date);

    result["search"].ShouldBe(50m);
    result["read"].ShouldBe(0m);
}

[Fact]
public async Task GetToolGroupedAsync_ByStatus_CallCount_CountsPerStatus()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:tools:2026-03-15",
        new ToolCallEvent { ToolName = "search", DurationMs = 100, Success = true },
        new ToolCallEvent { ToolName = "read", DurationMs = 200, Success = false },
        new ToolCallEvent { ToolName = "read", DurationMs = 50, Success = true });

    var result = await _sut.GetToolGroupedAsync(ToolDimension.Status, ToolMetric.CallCount, date, date);

    result["Success"].ShouldBe(2m);
    result["Failure"].ShouldBe(1m);
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd /home/dethon/repos/agent && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests.GetTool" --no-restore -v minimal
```

Expected: FAIL — `GetToolGroupedAsync` does not exist.

- [ ] **Step 3: Implement GetToolGroupedAsync**

Add to `Observability/Services/MetricsQueryService.cs` after `GetTokenGroupedAsync`:

```csharp
public async Task<Dictionary<string, decimal>> GetToolGroupedAsync(
    ToolDimension dimension, ToolMetric metric, DateOnly from, DateOnly to)
{
    var events = await GetEventsAsync<ToolCallEvent>("metrics:tools:", from, to);

    return events
        .GroupBy(e => dimension switch
        {
            ToolDimension.ToolName => e.ToolName,
            ToolDimension.Status => e.Success ? "Success" : "Failure",
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        })
        .ToDictionary(
            g => g.Key,
            g => metric switch
            {
                ToolMetric.CallCount => (decimal)g.Count(),
                ToolMetric.AvgDuration => (decimal)g.Average(e => e.DurationMs),
                ToolMetric.ErrorRate => g.Count() > 0
                    ? (decimal)g.Count(e => !e.Success) / g.Count() * 100m
                    : 0m,
                _ => throw new ArgumentOutOfRangeException(nameof(metric))
            });
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd /home/dethon/repos/agent && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests.GetTool" --no-restore -v minimal
```

Expected: All 4 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Observability/Services/MetricsQueryService.cs Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs
git commit -m "feat(observability): add GetToolGroupedAsync for dynamic tool breakdowns"
```

---

## Task 5: Backend — Errors & Schedules Grouped Aggregation (TDD)

**Files:**
- Modify: `Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs`
- Modify: `Observability/Services/MetricsQueryService.cs`

- [ ] **Step 1: Write failing tests**

Append to `MetricsQueryServiceGroupingTests.cs`:

```csharp
[Fact]
public async Task GetErrorGroupedAsync_ByService_CountsPerService()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:errors:2026-03-15",
        new ErrorEvent { Service = "agent", ErrorType = "Timeout", Message = "err1" },
        new ErrorEvent { Service = "agent", ErrorType = "NullRef", Message = "err2" },
        new ErrorEvent { Service = "mcp-text", ErrorType = "Timeout", Message = "err3" });

    var result = await _sut.GetErrorGroupedAsync(ErrorDimension.Service, date, date);

    result["agent"].ShouldBe(2);
    result["mcp-text"].ShouldBe(1);
}

[Fact]
public async Task GetErrorGroupedAsync_ByErrorType_CountsPerType()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:errors:2026-03-15",
        new ErrorEvent { Service = "agent", ErrorType = "Timeout", Message = "err1" },
        new ErrorEvent { Service = "mcp-text", ErrorType = "Timeout", Message = "err2" },
        new ErrorEvent { Service = "agent", ErrorType = "NullRef", Message = "err3" });

    var result = await _sut.GetErrorGroupedAsync(ErrorDimension.ErrorType, date, date);

    result["Timeout"].ShouldBe(2);
    result["NullRef"].ShouldBe(1);
}

[Fact]
public async Task GetScheduleGroupedAsync_BySchedule_CountsPerScheduleId()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:schedules:2026-03-15",
        new ScheduleExecutionEvent { ScheduleId = "daily-backup", Prompt = "backup", DurationMs = 100, Success = true },
        new ScheduleExecutionEvent { ScheduleId = "daily-backup", Prompt = "backup", DurationMs = 200, Success = false },
        new ScheduleExecutionEvent { ScheduleId = "hourly-check", Prompt = "check", DurationMs = 50, Success = true });

    var result = await _sut.GetScheduleGroupedAsync(ScheduleDimension.Schedule, date, date);

    result["daily-backup"].ShouldBe(2);
    result["hourly-check"].ShouldBe(1);
}

[Fact]
public async Task GetScheduleGroupedAsync_ByStatus_CountsPerStatus()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:schedules:2026-03-15",
        new ScheduleExecutionEvent { ScheduleId = "daily-backup", Prompt = "backup", DurationMs = 100, Success = true },
        new ScheduleExecutionEvent { ScheduleId = "daily-backup", Prompt = "backup", DurationMs = 200, Success = false },
        new ScheduleExecutionEvent { ScheduleId = "hourly-check", Prompt = "check", DurationMs = 50, Success = true });

    var result = await _sut.GetScheduleGroupedAsync(ScheduleDimension.Status, date, date);

    result["Success"].ShouldBe(2);
    result["Failure"].ShouldBe(1);
}
```

- [ ] **Step 2: Run tests to verify they fail**

```bash
cd /home/dethon/repos/agent && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests.GetError|FullyQualifiedName~MetricsQueryServiceGroupingTests.GetSchedule" --no-restore -v minimal
```

Expected: FAIL — methods do not exist.

- [ ] **Step 3: Implement GetErrorGroupedAsync and GetScheduleGroupedAsync**

Add to `Observability/Services/MetricsQueryService.cs`:

```csharp
public async Task<Dictionary<string, int>> GetErrorGroupedAsync(
    ErrorDimension dimension, DateOnly from, DateOnly to)
{
    var events = await GetEventsAsync<ErrorEvent>("metrics:errors:", from, to);

    return events
        .GroupBy(e => dimension switch
        {
            ErrorDimension.Service => e.Service,
            ErrorDimension.ErrorType => e.ErrorType,
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        })
        .ToDictionary(g => g.Key, g => g.Count());
}

public async Task<Dictionary<string, int>> GetScheduleGroupedAsync(
    ScheduleDimension dimension, DateOnly from, DateOnly to)
{
    var events = await GetEventsAsync<ScheduleExecutionEvent>("metrics:schedules:", from, to);

    return events
        .GroupBy(e => dimension switch
        {
            ScheduleDimension.Schedule => e.ScheduleId,
            ScheduleDimension.Status => e.Success ? "Success" : "Failure",
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        })
        .ToDictionary(g => g.Key, g => g.Count());
}
```

- [ ] **Step 4: Run tests to verify they pass**

```bash
cd /home/dethon/repos/agent && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests" --no-restore -v minimal
```

Expected: All 12 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add Observability/Services/MetricsQueryService.cs Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs
git commit -m "feat(observability): add error and schedule grouped aggregation methods"
```

---

## Task 6: Backend — API Endpoints

**Files:**
- Modify: `Observability/MetricsApiEndpoints.cs:64-83`

- [ ] **Step 1: Add new grouped endpoints**

Add the following endpoints to `MetricsApiEndpoints.cs` after the existing `/tokens/by-model` endpoint (after line 83), before the closing `}`:

```csharp
api.MapGet("/tokens/by/{dimension}", async (
    MetricsQueryService query,
    TokenDimension dimension,
    TokenMetric? metric,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return await query.GetTokenGroupedAsync(dimension, metric ?? TokenMetric.Tokens, fromDate, toDate);
});

api.MapGet("/tools/by/{dimension}", async (
    MetricsQueryService query,
    ToolDimension dimension,
    ToolMetric? metric,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return await query.GetToolGroupedAsync(dimension, metric ?? ToolMetric.CallCount, fromDate, toDate);
});

api.MapGet("/errors/by/{dimension}", async (
    MetricsQueryService query,
    ErrorDimension dimension,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return await query.GetErrorGroupedAsync(dimension, fromDate, toDate);
});

api.MapGet("/schedules/by/{dimension}", async (
    MetricsQueryService query,
    ScheduleDimension dimension,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return await query.GetScheduleGroupedAsync(dimension, fromDate, toDate);
});
```

Add the using at the top:

```csharp
using Domain.DTOs.Metrics.Enums;
```

- [ ] **Step 2: Add the errors date-range endpoint**

Add a new endpoint for errors with date range (the old `/errors` with `limit` stays for backward compatibility). Add after the existing `/errors` endpoint:

```csharp
api.MapGet("/errors/range", async (
    MetricsQueryService query,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return await query.GetEventsAsync<ErrorEvent>("metrics:errors:", fromDate, toDate);
});
```

- [ ] **Step 3: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Observability/Observability.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Observability/MetricsApiEndpoints.cs
git commit -m "feat(observability): add grouped aggregation API endpoints"
```

---

## Task 7: Frontend — PillSelector Component

**Files:**
- Create: `Dashboard.Client/Components/PillSelector.razor`

- [ ] **Step 1: Create PillSelector component**

Create `Dashboard.Client/Components/PillSelector.razor`:

```razor
<div class="pill-selector">
    <span class="pill-label">@Label</span>
    <div class="pill-group">
        @foreach (var option in Options)
        {
            var isActive = option.Value == Value;
            var isDisabled = DisabledValues.Contains(option.Value);
            <button class="pill @(isActive ? "active" : "") @(isDisabled ? "disabled" : "")"
                    disabled="@isDisabled"
                    @onclick="() => SelectValue(option.Value)">
                @option.Label
            </button>
        }
    </div>
</div>

@code {
    [Parameter, EditorRequired] public string Label { get; set; } = "";
    [Parameter, EditorRequired] public IReadOnlyList<PillOption> Options { get; set; } = [];
    [Parameter] public string Value { get; set; } = "";
    [Parameter] public EventCallback<string> OnChanged { get; set; }
    [Parameter] public IReadOnlySet<string> DisabledValues { get; set; } = new HashSet<string>();

    private async Task SelectValue(string value)
    {
        if (value != Value)
            await OnChanged.InvokeAsync(value);
    }

    public record PillOption(string Label, string Value);
}

<style>
    .pill-selector { display: flex; flex-direction: column; gap: 0.25rem; }
    .pill-label { font-size: 0.65rem; color: var(--text-secondary); text-transform: uppercase; letter-spacing: 0.08em; }
    .pill-group { display: flex; gap: 0.3rem; }
    .pill {
        background: var(--bg-card);
        color: var(--text-secondary);
        border: 1px solid rgba(255,255,255,0.1);
        border-radius: 12px;
        padding: 0.25rem 0.75rem;
        cursor: pointer;
        font-size: 0.75rem;
        transition: all 0.2s;
        white-space: nowrap;
    }
    .pill:hover:not(.disabled) { color: var(--text-primary); border-color: var(--accent-blue); }
    .pill.active { background: rgba(0,210,255,0.15); color: var(--accent-blue); border-color: var(--accent-blue); }
    .pill.disabled { opacity: 0.4; cursor: not-allowed; }

    @@media (max-width: 768px) {
        .pill { padding: 0.2rem 0.5rem; font-size: 0.7rem; }
    }
</style>
```

- [ ] **Step 2: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Components/PillSelector.razor
git commit -m "feat(dashboard): add PillSelector component for dynamic grouping controls"
```

---

## Task 8: Frontend — DynamicChart Component

**Files:**
- Create: `Dashboard.Client/Components/DynamicChart.razor`

- [ ] **Step 1: Create DynamicChart component**

Create `Dashboard.Client/Components/DynamicChart.razor`:

```razor
@using ApexCharts

@if (Data is null || Data.Count == 0)
{
    <p class="no-data">No data available</p>
}
else if (ChartType == ChartMode.Donut)
{
    <ApexChart TItem="ChartEntry"
               Options="_donutOptions">
        <ApexPointSeries TItem="ChartEntry"
                         Items="_entries"
                         SeriesType="SeriesType.Donut"
                         XValue="e => e.Label"
                         YValue="e => e.Value"
                         OrderByDescending="e => e.x" />
    </ApexChart>
}
else
{
    <ApexChart TItem="ChartEntry"
               Options="_barOptions">
        <ApexPointSeries TItem="ChartEntry"
                         Items="_entries"
                         SeriesType="SeriesType.Bar"
                         XValue="e => e.Label"
                         YValue="e => e.Value"
                         OrderByDescending="e => e.x" />
    </ApexChart>
}

@code {
    [Parameter] public Dictionary<string, decimal>? Data { get; set; }
    [Parameter] public ChartMode ChartType { get; set; } = ChartMode.HorizontalBar;
    [Parameter] public string ValueFormat { get; set; } = "N0";

    private List<ChartEntry> _entries = [];

    private static readonly string[] Palette =
        ["#6366f1", "#22d3ee", "#f59e0b", "#ef4444", "#10b981", "#f472b6", "#a78bfa", "#fbbf24"];

    private readonly ApexChartOptions<ChartEntry> _donutOptions = new()
    {
        Chart = new Chart
        {
            Background = "transparent",
            ForeColor = "#a0a0b0"
        },
        Legend = new Legend { Position = LegendPosition.Bottom, Labels = new LegendLabels { Colors = new Color("#a0a0b0") } },
        Colors = Palette,
        Theme = new Theme { Mode = Mode.Dark }
    };

    private readonly ApexChartOptions<ChartEntry> _barOptions = new()
    {
        Chart = new Chart
        {
            Background = "transparent",
            ForeColor = "#a0a0b0"
        },
        PlotOptions = new PlotOptions
        {
            Bar = new PlotOptionsBar { Horizontal = true, BarHeight = "60%" }
        },
        Colors = Palette,
        Theme = new Theme { Mode = Mode.Dark },
        Grid = new Grid
        {
            BorderColor = "#2a2a3e"
        }
    };

    protected override void OnParametersSet()
    {
        _entries = Data?
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new ChartEntry(kv.Key, kv.Value))
            .ToList() ?? [];
    }

    public record ChartEntry(string Label, decimal Value);

    public enum ChartMode { Donut, HorizontalBar }
}

<style>
    .no-data { color: var(--text-secondary); font-size: 0.85rem; padding: 1rem 0; }
</style>
```

- [ ] **Step 2: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Components/DynamicChart.razor
git commit -m "feat(dashboard): add DynamicChart component wrapping BlazorApexCharts"
```

---

## Task 9: Frontend — Update State Stores

**Files:**
- Modify: `Dashboard.Client/State/Tokens/TokensState.cs`
- Modify: `Dashboard.Client/State/Tokens/TokensStore.cs`
- Modify: `Dashboard.Client/State/Tools/ToolsState.cs`
- Modify: `Dashboard.Client/State/Tools/ToolsStore.cs`
- Modify: `Dashboard.Client/State/Errors/ErrorsState.cs`
- Modify: `Dashboard.Client/State/Errors/ErrorsStore.cs`
- Modify: `Dashboard.Client/State/Schedules/SchedulesState.cs`
- Modify: `Dashboard.Client/State/Schedules/SchedulesStore.cs`

- [ ] **Step 1: Update TokensState**

Replace `Dashboard.Client/State/Tokens/TokensState.cs` entirely:

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tokens;

public record TokensState
{
    public IReadOnlyList<TokenUsageEvent> Events { get; init; } = [];
    public TokenDimension GroupBy { get; init; } = TokenDimension.User;
    public TokenMetric Metric { get; init; } = TokenMetric.Tokens;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
}
```

- [ ] **Step 2: Update TokensStore**

Replace `Dashboard.Client/State/Tokens/TokensStore.cs` entirely:

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tokens;

public record SetTokenEvents(IReadOnlyList<TokenUsageEvent> Events) : IAction;
public record SetTokenBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetTokenGroupBy(TokenDimension GroupBy) : IAction;
public record SetTokenMetric(TokenMetric Metric) : IAction;
public record AppendTokenEvent(TokenUsageEvent Event) : IAction;

public sealed class TokensStore : Store<TokensState>
{
    public TokensStore() : base(new TokensState()) { }

    public void SetEvents(IReadOnlyList<TokenUsageEvent> events) =>
        Dispatch(new SetTokenEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetTokenBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(TokenDimension groupBy) =>
        Dispatch(new SetTokenGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(TokenMetric metric) =>
        Dispatch(new SetTokenMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void AppendEvent(TokenUsageEvent evt) =>
        Dispatch(new AppendTokenEvent(evt), static (s, a) => s with
        {
            Events = [..s.Events, a.Event],
        });
}
```

- [ ] **Step 3: Update ToolsState**

Replace `Dashboard.Client/State/Tools/ToolsState.cs` entirely:

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tools;

public record ToolsState
{
    public IReadOnlyList<ToolCallEvent> Events { get; init; } = [];
    public ToolDimension GroupBy { get; init; } = ToolDimension.ToolName;
    public ToolMetric Metric { get; init; } = ToolMetric.CallCount;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
}
```

- [ ] **Step 4: Update ToolsStore**

Replace `Dashboard.Client/State/Tools/ToolsStore.cs` entirely:

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tools;

public record SetToolEvents(IReadOnlyList<ToolCallEvent> Events) : IAction;
public record SetToolBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetToolGroupBy(ToolDimension GroupBy) : IAction;
public record SetToolMetric(ToolMetric Metric) : IAction;
public record AppendToolEvent(ToolCallEvent Event) : IAction;

public sealed class ToolsStore : Store<ToolsState>
{
    public ToolsStore() : base(new ToolsState()) { }

    public void SetEvents(IReadOnlyList<ToolCallEvent> events) =>
        Dispatch(new SetToolEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetToolBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(ToolDimension groupBy) =>
        Dispatch(new SetToolGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(ToolMetric metric) =>
        Dispatch(new SetToolMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void AppendEvent(ToolCallEvent evt) =>
        Dispatch(new AppendToolEvent(evt), static (s, a) => s with
        {
            Events = [..s.Events, a.Event],
        });
}
```

- [ ] **Step 5: Update ErrorsState**

Replace `Dashboard.Client/State/Errors/ErrorsState.cs` entirely:

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Errors;

public record ErrorsState
{
    public IReadOnlyList<ErrorEvent> Events { get; init; } = [];
    public ErrorDimension GroupBy { get; init; } = ErrorDimension.Service;
    public Dictionary<string, int> Breakdown { get; init; } = [];
}
```

- [ ] **Step 6: Update ErrorsStore**

Replace `Dashboard.Client/State/Errors/ErrorsStore.cs` entirely:

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Errors;

public record SetErrorEvents(IReadOnlyList<ErrorEvent> Events) : IAction;
public record SetErrorBreakdown(Dictionary<string, int> Breakdown) : IAction;
public record SetErrorGroupBy(ErrorDimension GroupBy) : IAction;
public record AppendErrorEvent(ErrorEvent Event) : IAction;

public sealed class ErrorsStore : Store<ErrorsState>
{
    public ErrorsStore() : base(new ErrorsState()) { }

    public void SetEvents(IReadOnlyList<ErrorEvent> events) =>
        Dispatch(new SetErrorEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, int> breakdown) =>
        Dispatch(new SetErrorBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(ErrorDimension groupBy) =>
        Dispatch(new SetErrorGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void AppendEvent(ErrorEvent evt) =>
        Dispatch(new AppendErrorEvent(evt), static (s, a) => s with
        {
            Events = [..s.Events, a.Event],
        });
}
```

- [ ] **Step 7: Update SchedulesState**

Replace `Dashboard.Client/State/Schedules/SchedulesState.cs` entirely:

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Schedules;

public record SchedulesState
{
    public IReadOnlyList<ScheduleExecutionEvent> Events { get; init; } = [];
    public ScheduleDimension GroupBy { get; init; } = ScheduleDimension.Schedule;
    public Dictionary<string, int> Breakdown { get; init; } = [];
}
```

- [ ] **Step 8: Update SchedulesStore**

Replace `Dashboard.Client/State/Schedules/SchedulesStore.cs` entirely:

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Schedules;

public record SetScheduleEvents(IReadOnlyList<ScheduleExecutionEvent> Events) : IAction;
public record SetScheduleBreakdown(Dictionary<string, int> Breakdown) : IAction;
public record SetScheduleGroupBy(ScheduleDimension GroupBy) : IAction;
public record AppendScheduleEvent(ScheduleExecutionEvent Event) : IAction;

public sealed class SchedulesStore : Store<SchedulesState>
{
    public SchedulesStore() : base(new SchedulesState()) { }

    public void SetEvents(IReadOnlyList<ScheduleExecutionEvent> events) =>
        Dispatch(new SetScheduleEvents(events), static (s, a) => s with { Events = a.Events });

    public void SetBreakdown(Dictionary<string, int> breakdown) =>
        Dispatch(new SetScheduleBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(ScheduleDimension groupBy) =>
        Dispatch(new SetScheduleGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void AppendEvent(ScheduleExecutionEvent evt) =>
        Dispatch(new AppendScheduleEvent(evt), static (s, a) => s with
        {
            Events = [..s.Events, a.Event],
        });
}
```

- [ ] **Step 9: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

Expected: Build will fail because `DataLoadEffect.cs` references `SetBreakdowns` which no longer exists. That's expected — we fix it in the next task.

- [ ] **Step 10: Commit**

```bash
git add Dashboard.Client/State/
git commit -m "feat(dashboard): update state stores with dynamic grouping fields"
```

---

## Task 10: Frontend — Update MetricsApiService & DataLoadEffect

**Files:**
- Modify: `Dashboard.Client/Services/MetricsApiService.cs`
- Modify: `Dashboard.Client/Effects/DataLoadEffect.cs`

- [ ] **Step 1: Update MetricsApiService**

Replace `Dashboard.Client/Services/MetricsApiService.cs` entirely:

```csharp
using System.Net.Http.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.Services;

public record MetricsSummary(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    decimal Cost,
    long ToolCalls,
    long ToolErrors);

public record ServiceHealthResponse(string Service, bool IsHealthy, string LastSeen);

public sealed class MetricsApiService(HttpClient http)
{
    public Task<MetricsSummary?> GetSummaryAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<MetricsSummary>($"api/metrics/summary?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<TokenUsageEvent>?> GetTokensAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<TokenUsageEvent>>($"api/metrics/tokens?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ToolCallEvent>?> GetToolsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ToolCallEvent>>($"api/metrics/tools?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ErrorEvent>?> GetErrorsAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ErrorEvent>>($"api/metrics/errors/range?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ScheduleExecutionEvent>?> GetSchedulesAsync(DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<List<ScheduleExecutionEvent>>($"api/metrics/schedules?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<List<ServiceHealthResponse>?> GetHealthAsync() =>
        http.GetFromJsonAsync<List<ServiceHealthResponse>>("api/metrics/health");

    public Task<Dictionary<string, decimal>?> GetTokenGroupedAsync(
        TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<Dictionary<string, decimal>>(
            $"api/metrics/tokens/by/{dimension}?metric={metric}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<Dictionary<string, decimal>?> GetToolGroupedAsync(
        ToolDimension dimension, ToolMetric metric, DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<Dictionary<string, decimal>>(
            $"api/metrics/tools/by/{dimension}?metric={metric}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<Dictionary<string, int>?> GetErrorGroupedAsync(
        ErrorDimension dimension, DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<Dictionary<string, int>>(
            $"api/metrics/errors/by/{dimension}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

    public Task<Dictionary<string, int>?> GetScheduleGroupedAsync(
        ScheduleDimension dimension, DateOnly from, DateOnly to) =>
        http.GetFromJsonAsync<Dictionary<string, int>>(
            $"api/metrics/schedules/by/{dimension}?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
}
```

- [ ] **Step 2: Update DataLoadEffect**

Replace `Dashboard.Client/Effects/DataLoadEffect.cs` entirely:

```csharp
using Dashboard.Client.Services;
using Dashboard.Client.State.Connection;
using Dashboard.Client.State.Errors;
using Dashboard.Client.State.Health;
using Dashboard.Client.State.Metrics;
using Dashboard.Client.State.Schedules;
using Dashboard.Client.State.Tokens;
using Dashboard.Client.State.Tools;

namespace Dashboard.Client.Effects;

public sealed class DataLoadEffect(
    MetricsApiService api,
    MetricsStore metricsStore,
    HealthStore healthStore,
    TokensStore tokensStore,
    ToolsStore toolsStore,
    ErrorsStore errorsStore,
    SchedulesStore schedulesStore,
    ConnectionStore connectionStore)
{
    public async Task LoadAsync(DateOnly from, DateOnly to)
    {
        try
        {
            var summaryTask = api.GetSummaryAsync(from, to);
            var tokensTask = api.GetTokensAsync(from, to);
            var toolsTask = api.GetToolsAsync(from, to);
            var errorsTask = api.GetErrorsAsync(from, to);
            var schedulesTask = api.GetSchedulesAsync(from, to);
            var healthTask = api.GetHealthAsync();

            var tokenBreakdownTask = api.GetTokenGroupedAsync(
                tokensStore.State.GroupBy, tokensStore.State.Metric, from, to);
            var toolBreakdownTask = api.GetToolGroupedAsync(
                toolsStore.State.GroupBy, toolsStore.State.Metric, from, to);
            var errorBreakdownTask = api.GetErrorGroupedAsync(
                errorsStore.State.GroupBy, from, to);
            var scheduleBreakdownTask = api.GetScheduleGroupedAsync(
                schedulesStore.State.GroupBy, from, to);

            await Task.WhenAll(summaryTask, tokensTask, toolsTask, errorsTask,
                schedulesTask, healthTask, tokenBreakdownTask, toolBreakdownTask,
                errorBreakdownTask, scheduleBreakdownTask);

            var summary = await summaryTask;
            if (summary is not null)
            {
                metricsStore.UpdateSummary(new MetricsState
                {
                    InputTokens = summary.InputTokens,
                    OutputTokens = summary.OutputTokens,
                    Cost = summary.Cost,
                    ToolCalls = summary.ToolCalls,
                    ToolErrors = summary.ToolErrors,
                });
            }

            tokensStore.SetEvents(await tokensTask ?? []);
            toolsStore.SetEvents(await toolsTask ?? []);
            errorsStore.SetEvents(await errorsTask ?? []);
            schedulesStore.SetEvents(await schedulesTask ?? []);

            tokensStore.SetBreakdown(await tokenBreakdownTask ?? []);
            toolsStore.SetBreakdown(await toolBreakdownTask ?? []);
            errorsStore.SetBreakdown(await errorBreakdownTask ?? []);
            schedulesStore.SetBreakdown(await scheduleBreakdownTask ?? []);

            var health = await healthTask;
            if (health is not null)
            {
                healthStore.UpdateHealth(health
                    .Select(h => new ServiceHealth(h.Service, h.IsHealthy, h.LastSeen))
                    .ToList());
            }

            connectionStore.SetConnected(true);
        }
        catch
        {
            connectionStore.SetConnected(false);
        }
    }

    public async Task LoadBreakdownAsync(DateOnly from, DateOnly to)
    {
        try
        {
            var tokenBreakdownTask = api.GetTokenGroupedAsync(
                tokensStore.State.GroupBy, tokensStore.State.Metric, from, to);
            var toolBreakdownTask = api.GetToolGroupedAsync(
                toolsStore.State.GroupBy, toolsStore.State.Metric, from, to);
            var errorBreakdownTask = api.GetErrorGroupedAsync(
                errorsStore.State.GroupBy, from, to);
            var scheduleBreakdownTask = api.GetScheduleGroupedAsync(
                schedulesStore.State.GroupBy, from, to);

            await Task.WhenAll(tokenBreakdownTask, toolBreakdownTask,
                errorBreakdownTask, scheduleBreakdownTask);

            tokensStore.SetBreakdown(await tokenBreakdownTask ?? []);
            toolsStore.SetBreakdown(await toolBreakdownTask ?? []);
            errorsStore.SetBreakdown(await errorBreakdownTask ?? []);
            schedulesStore.SetBreakdown(await scheduleBreakdownTask ?? []);
        }
        catch
        {
            // Breakdown load failure is non-fatal
        }
    }
}
```

- [ ] **Step 3: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add Dashboard.Client/Services/MetricsApiService.cs Dashboard.Client/Effects/DataLoadEffect.cs
git commit -m "feat(dashboard): update API service and effects for dynamic breakdowns"
```

---

## Task 11: Frontend — Update Tokens Page

**Files:**
- Modify: `Dashboard.Client/Pages/Tokens.razor`

- [ ] **Step 1: Replace Tokens.razor**

Replace `Dashboard.Client/Pages/Tokens.razor` entirely:

```razor
@page "/tokens"
@using Dashboard.Client.State.Tokens
@using Domain.DTOs.Metrics.Enums
@using static Dashboard.Client.Components.PillSelector
@implements IDisposable
@inject TokensStore Store
@inject DataLoadEffect DataLoad

<div class="tokens-page">
    <header class="page-header">
        <h2>Token Usage</h2>
        <div class="controls">
            <PillSelector Label="Group by" Options="_dimensionOptions" Value="@_state.GroupBy.ToString()"
                          OnChanged="OnDimensionChanged" />
            <PillSelector Label="Metric" Options="_metricOptions" Value="@_state.Metric.ToString()"
                          OnChanged="OnMetricChanged" />
            <PillSelector Label="Time" Options="_timeOptions" Value="@_selectedDays.ToString()"
                          OnChanged="OnTimeChanged" />
        </div>
    </header>

    <section class="section">
        <DynamicChart Data="_state.Breakdown" ChartType="DynamicChart.ChartMode.Donut"
                      ValueFormat="@(_state.Metric == TokenMetric.Cost ? "F4" : "N0")" />
    </section>

    <section class="section">
        <h3>Recent Token Events</h3>
        <div class="events-table">
            <div class="table-header">
                <span>Time</span>
                <span>Sender</span>
                <span>Model</span>
                <span>Input</span>
                <span>Output</span>
                <span>Cost</span>
            </div>
            @foreach (var evt in _state.Events.TakeLast(50).Reverse())
            {
                <div class="table-row">
                    <span>@evt.Timestamp.ToString("HH:mm:ss")</span>
                    <span>@evt.Sender</span>
                    <span>@evt.Model</span>
                    <span>@evt.InputTokens.ToString("N0")</span>
                    <span>@evt.OutputTokens.ToString("N0")</span>
                    <span>@($"${evt.Cost:F4}")</span>
                </div>
            }
        </div>
    </section>
</div>

@code {
    private TokensState _state = new();
    private int _selectedDays = 1;
    private DateOnly _from;
    private DateOnly _to;
    private IDisposable? _sub;

    private static readonly IReadOnlyList<PillOption> _dimensionOptions =
    [
        new("User", nameof(TokenDimension.User)),
        new("Model", nameof(TokenDimension.Model)),
        new("Agent", nameof(TokenDimension.Agent)),
    ];

    private static readonly IReadOnlyList<PillOption> _metricOptions =
    [
        new("Tokens", nameof(TokenMetric.Tokens)),
        new("Cost ($)", nameof(TokenMetric.Cost)),
    ];

    private static readonly IReadOnlyList<PillOption> _timeOptions =
    [
        new("Today", "1"),
        new("7d", "7"),
        new("30d", "30"),
    ];

    protected override async Task OnInitializedAsync()
    {
        _sub = Store.StateObservable.Subscribe(s =>
        {
            _state = s;
            InvokeAsync(StateHasChanged);
        });

        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to;
        await DataLoad.LoadAsync(_from, _to);
    }

    private async Task OnDimensionChanged(string value)
    {
        Store.SetGroupBy(Enum.Parse<TokenDimension>(value));
        await ReloadBreakdown();
    }

    private async Task OnMetricChanged(string value)
    {
        Store.SetMetric(Enum.Parse<TokenMetric>(value));
        await ReloadBreakdown();
    }

    private async Task OnTimeChanged(string value)
    {
        _selectedDays = int.Parse(value);
        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to.AddDays(-(_selectedDays - 1));
        await DataLoad.LoadAsync(_from, _to);
    }

    private async Task ReloadBreakdown()
    {
        var breakdown = await new MetricsApiService(
            ((MetricsApiService)null!).GetType() == typeof(object) ? null! : null!).GetTokenGroupedAsync(
            Store.State.GroupBy, Store.State.Metric, _from, _to);
        Store.SetBreakdown(breakdown ?? []);
    }

    public void Dispose() => _sub?.Dispose();
}

<style>
    .tokens-page { display: flex; flex-direction: column; gap: 1.5rem; }
    .page-header { display: flex; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; gap: 1rem; }
    .page-header h2 { font-size: 1.4rem; font-weight: 600; }
    .controls { display: flex; gap: 1.5rem; flex-wrap: wrap; align-items: flex-end; }
    .section h3 { font-size: 1rem; margin-bottom: 0.8rem; color: var(--text-secondary); }
    .events-table { display: flex; flex-direction: column; gap: 2px; }
    .table-header, .table-row { display: grid; grid-template-columns: 80px minmax(0, 1fr) minmax(0, 1fr) 90px 90px 90px; gap: 0.5rem; padding: 0.4rem 0.8rem; font-size: 0.82rem; }
    .table-header { background: var(--bg-secondary); color: var(--text-secondary); font-weight: 600; border-radius: 4px; text-transform: uppercase; font-size: 0.7rem; }
    .table-row { background: var(--bg-card); border-radius: 4px; }
    .table-row:hover { background: rgba(255,255,255,0.03); }

    @@media (max-width: 768px) {
        .page-header { flex-direction: column; }
        .page-header h2 { font-size: 1.1rem; }
        .events-table { overflow-x: auto; -webkit-overflow-scrolling: touch; }
        .table-header, .table-row { grid-template-columns: 65px minmax(0, 1fr) minmax(0, 1fr) 70px 70px 70px; font-size: 0.72rem; padding: 0.4rem 0.5rem; min-width: 480px; }
        .table-row span { word-break: break-word; }
    }
</style>
```

Wait — the `ReloadBreakdown` method above has a bug. Let me fix that. The page should inject `MetricsApiService` directly. Let me correct the approach.

Actually, the proper pattern is for pages to call `DataLoad.LoadBreakdownAsync` or inject the API service directly. Let me fix the page to inject `MetricsApiService`:

Replace the `ReloadBreakdown` method and the inject section:

After `@inject DataLoadEffect DataLoad`, add:
```razor
@inject MetricsApiService Api
```

Replace the `ReloadBreakdown` method:
```csharp
private async Task ReloadBreakdown()
{
    var breakdown = await Api.GetTokenGroupedAsync(
        Store.State.GroupBy, Store.State.Metric, _from, _to);
    Store.SetBreakdown(breakdown ?? []);
}
```

- [ ] **Step 2: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Pages/Tokens.razor
git commit -m "feat(dashboard): update Tokens page with dynamic dimension/metric selectors and donut chart"
```

---

## Task 12: Frontend — Update Tools Page

**Files:**
- Modify: `Dashboard.Client/Pages/Tools.razor`

- [ ] **Step 1: Replace Tools.razor**

Replace `Dashboard.Client/Pages/Tools.razor` entirely:

```razor
@page "/tools"
@using Dashboard.Client.State.Tools
@using Domain.DTOs.Metrics.Enums
@using static Dashboard.Client.Components.PillSelector
@implements IDisposable
@inject ToolsStore Store
@inject DataLoadEffect DataLoad
@inject MetricsApiService Api

<div class="tools-page">
    <header class="page-header">
        <h2>Tool Calls</h2>
        <div class="controls">
            <PillSelector Label="Group by" Options="_dimensionOptions" Value="@_state.GroupBy.ToString()"
                          OnChanged="OnDimensionChanged" />
            <PillSelector Label="Metric" Options="_metricOptions" Value="@_state.Metric.ToString()"
                          OnChanged="OnMetricChanged" DisabledValues="_disabledMetrics" />
            <PillSelector Label="Time" Options="_timeOptions" Value="@_selectedDays.ToString()"
                          OnChanged="OnTimeChanged" />
        </div>
    </header>

    <section class="kpi-row">
        <KpiCard Label="Total Calls" Value="@_totalCalls.ToString("N0")" Color="var(--accent-yellow)" />
        <KpiCard Label="Success Rate" Value="@($"{_successRate:F1}%")" Color="var(--accent-green)" />
        <KpiCard Label="Avg Duration" Value="@($"{_avgDuration:F0}ms")" Color="var(--accent-blue)" />
    </section>

    <section class="section">
        <DynamicChart Data="_state.Breakdown" ChartType="DynamicChart.ChartMode.HorizontalBar" />
    </section>

    <section class="section">
        <h3>Recent Tool Calls</h3>
        <div class="events-table">
            <div class="table-header">
                <span>Time</span>
                <span>Tool</span>
                <span>Duration</span>
                <span>Status</span>
            </div>
            @foreach (var evt in _state.Events.TakeLast(50).Reverse())
            {
                <div class="table-row">
                    <span>@evt.Timestamp.ToString("HH:mm:ss")</span>
                    <span class="tool-name" title="@evt.ToolName">
                        <span class="tool-name-full">@evt.ToolName</span>
                        <span class="tool-name-short">@ShortToolName(evt.ToolName)</span>
                    </span>
                    <span>@evt.DurationMs ms</span>
                    <span class="@(evt.Success ? "status-ok" : "status-err")">@(evt.Success ? "OK" : evt.Error ?? "FAIL")</span>
                </div>
            }
        </div>
    </section>
</div>

@code {
    private ToolsState _state = new();
    private int _selectedDays = 1;
    private DateOnly _from;
    private DateOnly _to;
    private long _totalCalls;
    private double _successRate;
    private double _avgDuration;
    private IDisposable? _sub;

    private IReadOnlySet<string> _disabledMetrics = new HashSet<string>();

    private static readonly IReadOnlyList<PillOption> _dimensionOptions =
    [
        new("Tool Name", nameof(ToolDimension.ToolName)),
        new("Status", nameof(ToolDimension.Status)),
    ];

    private static readonly IReadOnlyList<PillOption> _metricOptions =
    [
        new("Call Count", nameof(ToolMetric.CallCount)),
        new("Avg Duration", nameof(ToolMetric.AvgDuration)),
        new("Error Rate", nameof(ToolMetric.ErrorRate)),
    ];

    private static readonly IReadOnlyList<PillOption> _timeOptions =
    [
        new("Today", "1"),
        new("7d", "7"),
        new("30d", "30"),
    ];

    protected override async Task OnInitializedAsync()
    {
        _sub = Store.StateObservable.Subscribe(s =>
        {
            _state = s;
            _totalCalls = s.Events.Count;
            _successRate = s.Events.Count > 0 ? s.Events.Count(e => e.Success) * 100.0 / s.Events.Count : 0;
            _avgDuration = s.Events.Count > 0 ? s.Events.Average(e => e.DurationMs) : 0;
            UpdateDisabledMetrics(s.GroupBy);
            InvokeAsync(StateHasChanged);
        });

        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to;
        await DataLoad.LoadAsync(_from, _to);
    }

    private void UpdateDisabledMetrics(ToolDimension groupBy)
    {
        _disabledMetrics = groupBy == ToolDimension.Status
            ? new HashSet<string> { nameof(ToolMetric.ErrorRate) }
            : new HashSet<string>();
    }

    private async Task OnDimensionChanged(string value)
    {
        var dim = Enum.Parse<ToolDimension>(value);
        Store.SetGroupBy(dim);
        if (dim == ToolDimension.Status && _state.Metric == ToolMetric.ErrorRate)
            Store.SetMetric(ToolMetric.CallCount);
        await ReloadBreakdown();
    }

    private async Task OnMetricChanged(string value)
    {
        Store.SetMetric(Enum.Parse<ToolMetric>(value));
        await ReloadBreakdown();
    }

    private async Task OnTimeChanged(string value)
    {
        _selectedDays = int.Parse(value);
        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to.AddDays(-(_selectedDays - 1));
        await DataLoad.LoadAsync(_from, _to);
    }

    private async Task ReloadBreakdown()
    {
        var breakdown = await Api.GetToolGroupedAsync(
            Store.State.GroupBy, Store.State.Metric, _from, _to);
        Store.SetBreakdown(breakdown ?? []);
    }

    private static string ShortToolName(string name)
    {
        var idx = name.LastIndexOf(':');
        return idx >= 0 ? name[(idx + 1)..] : name;
    }

    public void Dispose() => _sub?.Dispose();
}

<style>
    .tools-page { display: flex; flex-direction: column; gap: 1.5rem; }
    .page-header { display: flex; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; gap: 1rem; }
    .page-header h2 { font-size: 1.4rem; font-weight: 600; }
    .controls { display: flex; gap: 1.5rem; flex-wrap: wrap; align-items: flex-end; }
    .kpi-row { display: flex; gap: 1rem; flex-wrap: wrap; }
    .section h3 { font-size: 1rem; margin-bottom: 0.8rem; color: var(--text-secondary); }
    .events-table { display: flex; flex-direction: column; gap: 2px; }
    .table-header, .table-row { display: grid; grid-template-columns: 80px minmax(0, 2fr) 100px minmax(0, 1fr); gap: 0.5rem; padding: 0.4rem 0.8rem; font-size: 0.82rem; }
    .table-header { background: var(--bg-secondary); color: var(--text-secondary); font-weight: 600; border-radius: 4px; text-transform: uppercase; font-size: 0.7rem; }
    .table-row { background: var(--bg-card); border-radius: 4px; }
    .table-row:hover { background: rgba(255,255,255,0.03); }
    .tool-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .tool-name-short { display: none; }
    .status-ok { color: var(--accent-green); }
    .status-err { color: var(--accent-red); }

    @@media (max-width: 768px) {
        .page-header { flex-direction: column; }
        .page-header h2 { font-size: 1.1rem; }
        .events-table { overflow-x: auto; -webkit-overflow-scrolling: touch; }
        .table-header, .table-row { grid-template-columns: 65px minmax(0, 2fr) 80px minmax(0, 1fr); font-size: 0.72rem; padding: 0.4rem 0.5rem; min-width: 360px; }
        .tool-name-full { display: none; }
        .tool-name-short { display: inline; }
    }
</style>
```

- [ ] **Step 2: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Pages/Tools.razor
git commit -m "feat(dashboard): update Tools page with dynamic dimension/metric selectors and horizontal bar chart"
```

---

## Task 13: Frontend — Update Errors Page

**Files:**
- Modify: `Dashboard.Client/Pages/Errors.razor`

- [ ] **Step 1: Replace Errors.razor**

Replace `Dashboard.Client/Pages/Errors.razor` entirely:

```razor
@page "/errors"
@using Dashboard.Client.State.Errors
@using Domain.DTOs.Metrics.Enums
@using static Dashboard.Client.Components.PillSelector
@implements IDisposable
@inject ErrorsStore Store
@inject DataLoadEffect DataLoad
@inject MetricsApiService Api

<div class="errors-page">
    <header class="page-header">
        <h2>Errors</h2>
        <div class="controls">
            <PillSelector Label="Group by" Options="_dimensionOptions" Value="@_state.GroupBy.ToString()"
                          OnChanged="OnDimensionChanged" />
            <PillSelector Label="Time" Options="_timeOptions" Value="@_selectedDays.ToString()"
                          OnChanged="OnTimeChanged" />
        </div>
    </header>

    <section class="kpi-row">
        <KpiCard Label="Total Errors" Value="@_state.Events.Count.ToString("N0")" Color="var(--accent-red)" />
        <KpiCard Label="Services Affected" Value="@_servicesAffected.ToString()" Color="var(--accent-yellow)" />
    </section>

    <section class="section">
        <DynamicChart Data="_breakdownDecimal" ChartType="DynamicChart.ChartMode.HorizontalBar" />
    </section>

    <section class="section">
        <h3>Error List</h3>
        <div class="events-table">
            <div class="table-header">
                <span>Time</span>
                <span>Service</span>
                <span>Type</span>
                <span>Message</span>
            </div>
            @foreach (var evt in _state.Events.TakeLast(100).Reverse())
            {
                <div class="table-row">
                    <span>@evt.Timestamp.ToString("HH:mm:ss")</span>
                    <span class="svc-badge">@evt.Service</span>
                    <span class="err-type">@evt.ErrorType</span>
                    <span class="err-msg" title="@evt.Message">@Truncate(evt.Message, 80)</span>
                </div>
            }
            @if (!_state.Events.Any())
            {
                <p class="no-data">No errors recorded</p>
            }
        </div>
    </section>
</div>

@code {
    private ErrorsState _state = new();
    private int _selectedDays = 1;
    private int _servicesAffected;
    private DateOnly _from;
    private DateOnly _to;
    private IDisposable? _sub;

    private Dictionary<string, decimal> _breakdownDecimal = [];

    private static readonly IReadOnlyList<PillOption> _dimensionOptions =
    [
        new("Service", nameof(ErrorDimension.Service)),
        new("Error Type", nameof(ErrorDimension.ErrorType)),
    ];

    private static readonly IReadOnlyList<PillOption> _timeOptions =
    [
        new("Today", "1"),
        new("7d", "7"),
        new("30d", "30"),
    ];

    protected override async Task OnInitializedAsync()
    {
        _sub = Store.StateObservable.Subscribe(s =>
        {
            _state = s;
            _servicesAffected = s.Events.Select(e => e.Service).Distinct().Count();
            _breakdownDecimal = s.Breakdown.ToDictionary(kv => kv.Key, kv => (decimal)kv.Value);
            InvokeAsync(StateHasChanged);
        });

        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to;
        await DataLoad.LoadAsync(_from, _to);
    }

    private async Task OnDimensionChanged(string value)
    {
        Store.SetGroupBy(Enum.Parse<ErrorDimension>(value));
        var breakdown = await Api.GetErrorGroupedAsync(Store.State.GroupBy, _from, _to);
        Store.SetBreakdown(breakdown ?? []);
    }

    private async Task OnTimeChanged(string value)
    {
        _selectedDays = int.Parse(value);
        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to.AddDays(-(_selectedDays - 1));
        await DataLoad.LoadAsync(_from, _to);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");

    public void Dispose() => _sub?.Dispose();
}

<style>
    .errors-page { display: flex; flex-direction: column; gap: 1.5rem; }
    .page-header { display: flex; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; gap: 1rem; }
    .page-header h2 { font-size: 1.4rem; font-weight: 600; }
    .controls { display: flex; gap: 1.5rem; flex-wrap: wrap; align-items: flex-end; }
    .kpi-row { display: flex; gap: 1rem; flex-wrap: wrap; }
    .section h3 { font-size: 1rem; margin-bottom: 0.8rem; color: var(--text-secondary); }
    .events-table { display: flex; flex-direction: column; gap: 2px; }
    .table-header, .table-row { display: grid; grid-template-columns: 80px 120px 150px 1fr; gap: 0.5rem; padding: 0.4rem 0.8rem; font-size: 0.82rem; }
    .table-header { background: var(--bg-secondary); color: var(--text-secondary); font-weight: 600; border-radius: 4px; text-transform: uppercase; font-size: 0.7rem; }
    .table-row { background: var(--bg-card); border-radius: 4px; }
    .table-row:hover { background: rgba(255,255,255,0.03); }
    .svc-badge { color: var(--accent-yellow); }
    .err-type { color: var(--accent-red); font-weight: 600; }
    .err-msg { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .no-data { color: var(--text-secondary); font-size: 0.85rem; padding: 0.5rem 0.8rem; }

    @@media (max-width: 768px) {
        .page-header { flex-direction: column; }
        .page-header h2 { font-size: 1.1rem; }
        .events-table { overflow-x: auto; -webkit-overflow-scrolling: touch; }
        .table-header, .table-row { grid-template-columns: 65px 90px 110px 1fr; font-size: 0.72rem; padding: 0.4rem 0.5rem; min-width: 400px; }
        .err-msg { white-space: normal; word-break: break-word; }
    }
</style>
```

- [ ] **Step 2: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Pages/Errors.razor
git commit -m "feat(dashboard): update Errors page with dynamic grouping and time range selectors"
```

---

## Task 14: Frontend — Update Schedules Page

**Files:**
- Modify: `Dashboard.Client/Pages/Schedules.razor`

- [ ] **Step 1: Replace Schedules.razor**

Replace `Dashboard.Client/Pages/Schedules.razor` entirely:

```razor
@page "/schedules"
@using Dashboard.Client.State.Schedules
@using Domain.DTOs.Metrics.Enums
@using static Dashboard.Client.Components.PillSelector
@implements IDisposable
@inject SchedulesStore Store
@inject DataLoadEffect DataLoad
@inject MetricsApiService Api

<div class="schedules-page">
    <header class="page-header">
        <h2>Schedule Executions</h2>
        <div class="controls">
            <PillSelector Label="Group by" Options="_dimensionOptions" Value="@_state.GroupBy.ToString()"
                          OnChanged="OnDimensionChanged" />
            <PillSelector Label="Time" Options="_timeOptions" Value="@_selectedDays.ToString()"
                          OnChanged="OnTimeChanged" />
        </div>
    </header>

    <section class="kpi-row">
        <KpiCard Label="Total Runs" Value="@_totalRuns.ToString("N0")" Color="var(--accent-purple)" />
        <KpiCard Label="Success Rate" Value="@($"{_successRate:F1}%")" Color="var(--accent-green)" />
        <KpiCard Label="Avg Duration" Value="@($"{_avgDuration:F0}ms")" Color="var(--accent-blue)" />
    </section>

    <section class="section">
        <DynamicChart Data="_breakdownDecimal" ChartType="DynamicChart.ChartMode.HorizontalBar" />
    </section>

    <section class="section">
        <h3>Execution History</h3>
        <div class="events-table">
            <div class="table-header">
                <span>Time</span>
                <span>Schedule</span>
                <span>Duration</span>
                <span>Status</span>
                <span>Details</span>
            </div>
            @foreach (var evt in _state.Events.TakeLast(50).Reverse())
            {
                <div class="table-row">
                    <span>@evt.Timestamp.ToString("HH:mm:ss")</span>
                    <span>@evt.ScheduleId</span>
                    <span>@evt.DurationMs ms</span>
                    <span class="@(evt.Success ? "status-ok" : "status-err")">@(evt.Success ? "OK" : "FAIL")</span>
                    <span class="detail-text" title="@(evt.Error ?? evt.Prompt)">@Truncate(evt.Error ?? evt.Prompt, 60)</span>
                </div>
            }
            @if (!_state.Events.Any())
            {
                <p class="no-data">No schedule executions recorded</p>
            }
        </div>
    </section>
</div>

@code {
    private SchedulesState _state = new();
    private int _selectedDays = 1;
    private DateOnly _from;
    private DateOnly _to;
    private long _totalRuns;
    private double _successRate;
    private double _avgDuration;
    private IDisposable? _sub;

    private Dictionary<string, decimal> _breakdownDecimal = [];

    private static readonly IReadOnlyList<PillOption> _dimensionOptions =
    [
        new("Schedule", nameof(ScheduleDimension.Schedule)),
        new("Status", nameof(ScheduleDimension.Status)),
    ];

    private static readonly IReadOnlyList<PillOption> _timeOptions =
    [
        new("Today", "1"),
        new("7d", "7"),
        new("30d", "30"),
    ];

    protected override async Task OnInitializedAsync()
    {
        _sub = Store.StateObservable.Subscribe(s =>
        {
            _state = s;
            _totalRuns = s.Events.Count;
            _successRate = s.Events.Count > 0 ? s.Events.Count(e => e.Success) * 100.0 / s.Events.Count : 0;
            _avgDuration = s.Events.Count > 0 ? s.Events.Average(e => e.DurationMs) : 0;
            _breakdownDecimal = s.Breakdown.ToDictionary(kv => kv.Key, kv => (decimal)kv.Value);
            InvokeAsync(StateHasChanged);
        });

        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to;
        await DataLoad.LoadAsync(_from, _to);
    }

    private async Task OnDimensionChanged(string value)
    {
        Store.SetGroupBy(Enum.Parse<ScheduleDimension>(value));
        var breakdown = await Api.GetScheduleGroupedAsync(Store.State.GroupBy, _from, _to);
        Store.SetBreakdown(breakdown ?? []);
    }

    private async Task OnTimeChanged(string value)
    {
        _selectedDays = int.Parse(value);
        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to.AddDays(-(_selectedDays - 1));
        await DataLoad.LoadAsync(_from, _to);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : string.Concat(s.AsSpan(0, max), "...");

    public void Dispose() => _sub?.Dispose();
}

<style>
    .schedules-page { display: flex; flex-direction: column; gap: 1.5rem; }
    .page-header { display: flex; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; gap: 1rem; }
    .page-header h2 { font-size: 1.4rem; font-weight: 600; }
    .controls { display: flex; gap: 1.5rem; flex-wrap: wrap; align-items: flex-end; }
    .kpi-row { display: flex; gap: 1rem; flex-wrap: wrap; }
    .section h3 { font-size: 1rem; margin-bottom: 0.8rem; color: var(--text-secondary); }
    .events-table { display: flex; flex-direction: column; gap: 2px; }
    .table-header, .table-row { display: grid; grid-template-columns: 80px 140px 100px 70px 1fr; gap: 0.5rem; padding: 0.4rem 0.8rem; font-size: 0.82rem; }
    .table-header { background: var(--bg-secondary); color: var(--text-secondary); font-weight: 600; border-radius: 4px; text-transform: uppercase; font-size: 0.7rem; }
    .table-row { background: var(--bg-card); border-radius: 4px; }
    .table-row:hover { background: rgba(255,255,255,0.03); }
    .status-ok { color: var(--accent-green); font-weight: 600; }
    .status-err { color: var(--accent-red); font-weight: 600; }
    .detail-text { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; color: var(--text-secondary); }
    .no-data { color: var(--text-secondary); font-size: 0.85rem; padding: 0.5rem 0.8rem; }

    @@media (max-width: 768px) {
        .page-header { flex-direction: column; }
        .page-header h2 { font-size: 1.1rem; }
        .events-table { overflow-x: auto; -webkit-overflow-scrolling: touch; }
        .table-header, .table-row { grid-template-columns: 65px 110px 80px 55px 1fr; font-size: 0.72rem; padding: 0.4rem 0.5rem; min-width: 440px; }
        .detail-text { white-space: normal; word-break: break-word; }
    }
</style>
```

- [ ] **Step 2: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Pages/Schedules.razor
git commit -m "feat(dashboard): update Schedules page with dynamic grouping and horizontal bar chart"
```

---

## Task 15: Frontend — Update Overview Page

**Files:**
- Modify: `Dashboard.Client/Pages/Overview.razor:73-76,86-98`

The Overview page subscribes to `Tokens.StateObservable` for the recent activity feed. Since we removed `ByUser`/`ByModel`, it no longer needs those fields. The Overview page doesn't display token breakdown charts — it only shows KPI cards, health grid, and recent activity. No changes needed beyond ensuring it still compiles (the `RebuildActivity` method only uses `Events`, which still exists).

- [ ] **Step 1: Verify Overview page still compiles**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

If this builds, no changes needed. If not, fix any compile errors.

- [ ] **Step 2: Commit (only if changes were needed)**

```bash
git add Dashboard.Client/Pages/Overview.razor
git commit -m "fix(dashboard): update Overview page for new state shape"
```

---

## Task 16: Remove Old Components

**Files:**
- Delete: `Dashboard.Client/Components/BarChart.razor`
- Delete: `Dashboard.Client/Components/BarItem.cs`
- Delete: `Dashboard.Client/Components/TimeRangeSelector.razor`

- [ ] **Step 1: Delete old components**

```bash
cd /home/dethon/repos/agent && rm Dashboard.Client/Components/BarChart.razor Dashboard.Client/Components/BarItem.cs Dashboard.Client/Components/TimeRangeSelector.razor
```

- [ ] **Step 2: Search for remaining references**

```bash
cd /home/dethon/repos/agent && grep -r "BarChart\|BarItem\|TimeRangeSelector" Dashboard.Client/ --include="*.razor" --include="*.cs"
```

Expected: No results. If any references remain, update those files to remove them.

- [ ] **Step 3: Verify it builds**

```bash
cd /home/dethon/repos/agent && dotnet build Dashboard.Client/Dashboard.Client.csproj
```

- [ ] **Step 4: Commit**

```bash
git add -u Dashboard.Client/Components/
git commit -m "refactor(dashboard): remove BarChart, BarItem, and TimeRangeSelector (replaced by DynamicChart and PillSelector)"
```

---

## Task 17: Run Full Test Suite

- [ ] **Step 1: Run all tests**

```bash
cd /home/dethon/repos/agent && dotnet test Tests/Tests.csproj --no-restore -v minimal
```

Expected: All tests pass, including the 12 new grouping tests.

- [ ] **Step 2: Build the full solution**

```bash
cd /home/dethon/repos/agent && dotnet build
```

Expected: Build succeeded with no warnings related to our changes.

- [ ] **Step 3: Fix any issues and commit**

If any tests fail or build issues exist, fix them and commit:

```bash
git add -A
git commit -m "fix: resolve test/build issues from dynamic dashboard migration"
```
