# Live Breakdown Charts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make breakdown charts on all dashboard detail pages (Tokens, Tools, Errors, Schedules) refresh automatically when new SignalR events arrive.

**Architecture:** Add `From`/`To` date range to each detail store's state. Change `MetricsHubService` handlers from `Action<T>` to `Func<T, Task>`. In `MetricsHubEffect`, after appending each event, re-fetch the breakdown from the API using the store's current filters.

**Tech Stack:** Blazor WASM, SignalR, C# records, Redux-like stores (Rx.NET BehaviorSubject)

**Spec:** `docs/superpowers/specs/2026-03-24-live-breakdown-charts-design.md`

**Parallelism:** Tasks 1-4 are fully independent and can be executed in parallel by separate agents.

**Tech debt:** MetricsHubEffect unit tests are deferred — `MetricsHubService` is a sealed class wrapping `HubConnection`, making it hard to mock. The effect logic is verified via build checks and manual E2E testing.

---

### Task 1: Add date range to TokensState and TokensStore

**Files:**
- Modify: `Dashboard.Client/State/Tokens/TokensState.cs`
- Modify: `Dashboard.Client/State/Tokens/TokensStore.cs`
- Create: `Tests/Unit/Dashboard.Client/State/TokensStoreTests.cs`

- [ ] **Step 1: Write failing test for SetDateRange**

```csharp
// Tests/Unit/Dashboard.Client/State/TokensStoreTests.cs
using Dashboard.Client.State.Tokens;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.State;

public class TokensStoreTests : IDisposable
{
    private readonly TokensStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void SetDateRange_UpdatesFromAndTo()
    {
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 24);

        _store.SetDateRange(from, to);

        _store.State.From.ShouldBe(from);
        _store.State.To.ShouldBe(to);
    }

    [Fact]
    public void InitialState_DefaultsToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        _store.State.From.ShouldBe(today);
        _store.State.To.ShouldBe(today);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~TokensStoreTests" -v minimal`
Expected: FAIL — `SetDateRange` method and `From`/`To` properties don't exist

- [ ] **Step 3: Add From/To to TokensState**

```csharp
// Dashboard.Client/State/Tokens/TokensState.cs
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tokens;

public record TokensState
{
    public IReadOnlyList<TokenUsageEvent> Events { get; init; } = [];
    public TokenDimension GroupBy { get; init; } = TokenDimension.User;
    public TokenMetric Metric { get; init; } = TokenMetric.Tokens;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}
```

- [ ] **Step 4: Add SetDateRange action and method to TokensStore**

Add to `Dashboard.Client/State/Tokens/TokensStore.cs`, after the existing `AppendTokenEvent` record:

```csharp
public record SetTokenDateRange(DateOnly From, DateOnly To) : IAction;
```

Add to the `TokensStore` class:

```csharp
public void SetDateRange(DateOnly from, DateOnly to) =>
    Dispatch(new SetTokenDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~TokensStoreTests" -v minimal`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Dashboard.Client/State/Tokens/ Tests/Unit/Dashboard.Client/State/TokensStoreTests.cs
git commit -m "feat: add date range to TokensState and TokensStore"
```

---

### Task 2: Add date range to ToolsState and ToolsStore

**Files:**
- Modify: `Dashboard.Client/State/Tools/ToolsState.cs`
- Modify: `Dashboard.Client/State/Tools/ToolsStore.cs`
- Create: `Tests/Unit/Dashboard.Client/State/ToolsStoreTests.cs`

- [ ] **Step 1: Write failing test for SetDateRange**

```csharp
// Tests/Unit/Dashboard.Client/State/ToolsStoreTests.cs
using Dashboard.Client.State.Tools;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.State;

public class ToolsStoreTests : IDisposable
{
    private readonly ToolsStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void SetDateRange_UpdatesFromAndTo()
    {
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 24);

        _store.SetDateRange(from, to);

        _store.State.From.ShouldBe(from);
        _store.State.To.ShouldBe(to);
    }

    [Fact]
    public void InitialState_DefaultsToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        _store.State.From.ShouldBe(today);
        _store.State.To.ShouldBe(today);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ToolsStoreTests" -v minimal`
Expected: FAIL

- [ ] **Step 3: Add From/To to ToolsState**

```csharp
// Dashboard.Client/State/Tools/ToolsState.cs
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Tools;

public record ToolsState
{
    public IReadOnlyList<ToolCallEvent> Events { get; init; } = [];
    public ToolDimension GroupBy { get; init; } = ToolDimension.ToolName;
    public ToolMetric Metric { get; init; } = ToolMetric.CallCount;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}
```

- [ ] **Step 4: Add SetDateRange action and method to ToolsStore**

Add to `Dashboard.Client/State/Tools/ToolsStore.cs`, after `AppendToolEvent`:

```csharp
public record SetToolDateRange(DateOnly From, DateOnly To) : IAction;
```

Add to the `ToolsStore` class:

```csharp
public void SetDateRange(DateOnly from, DateOnly to) =>
    Dispatch(new SetToolDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ToolsStoreTests" -v minimal`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Dashboard.Client/State/Tools/ Tests/Unit/Dashboard.Client/State/ToolsStoreTests.cs
git commit -m "feat: add date range to ToolsState and ToolsStore"
```

---

### Task 3: Add date range to ErrorsState and ErrorsStore

**Files:**
- Modify: `Dashboard.Client/State/Errors/ErrorsState.cs`
- Modify: `Dashboard.Client/State/Errors/ErrorsStore.cs`
- Create: `Tests/Unit/Dashboard.Client/State/ErrorsStoreTests.cs`

- [ ] **Step 1: Write failing test for SetDateRange**

```csharp
// Tests/Unit/Dashboard.Client/State/ErrorsStoreTests.cs
using Dashboard.Client.State.Errors;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.State;

public class ErrorsStoreTests : IDisposable
{
    private readonly ErrorsStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void SetDateRange_UpdatesFromAndTo()
    {
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 24);

        _store.SetDateRange(from, to);

        _store.State.From.ShouldBe(from);
        _store.State.To.ShouldBe(to);
    }

    [Fact]
    public void InitialState_DefaultsToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        _store.State.From.ShouldBe(today);
        _store.State.To.ShouldBe(today);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ErrorsStoreTests" -v minimal`
Expected: FAIL

- [ ] **Step 3: Add From/To to ErrorsState**

```csharp
// Dashboard.Client/State/Errors/ErrorsState.cs
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Errors;

public record ErrorsState
{
    public IReadOnlyList<ErrorEvent> Events { get; init; } = [];
    public ErrorDimension GroupBy { get; init; } = ErrorDimension.Service;
    public Dictionary<string, int> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}
```

- [ ] **Step 4: Add SetDateRange action and method to ErrorsStore**

Add to `Dashboard.Client/State/Errors/ErrorsStore.cs`, after `AppendErrorEvent`:

```csharp
public record SetErrorDateRange(DateOnly From, DateOnly To) : IAction;
```

Add to the `ErrorsStore` class:

```csharp
public void SetDateRange(DateOnly from, DateOnly to) =>
    Dispatch(new SetErrorDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ErrorsStoreTests" -v minimal`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Dashboard.Client/State/Errors/ Tests/Unit/Dashboard.Client/State/ErrorsStoreTests.cs
git commit -m "feat: add date range to ErrorsState and ErrorsStore"
```

---

### Task 4: Add date range to SchedulesState and SchedulesStore

**Files:**
- Modify: `Dashboard.Client/State/Schedules/SchedulesState.cs`
- Modify: `Dashboard.Client/State/Schedules/SchedulesStore.cs`
- Create: `Tests/Unit/Dashboard.Client/State/SchedulesStoreTests.cs`

- [ ] **Step 1: Write failing test for SetDateRange**

```csharp
// Tests/Unit/Dashboard.Client/State/SchedulesStoreTests.cs
using Dashboard.Client.State.Schedules;
using Shouldly;

namespace Tests.Unit.Dashboard.Client.State;

public class SchedulesStoreTests : IDisposable
{
    private readonly SchedulesStore _store = new();

    public void Dispose() => _store.Dispose();

    [Fact]
    public void SetDateRange_UpdatesFromAndTo()
    {
        var from = new DateOnly(2026, 3, 1);
        var to = new DateOnly(2026, 3, 24);

        _store.SetDateRange(from, to);

        _store.State.From.ShouldBe(from);
        _store.State.To.ShouldBe(to);
    }

    [Fact]
    public void InitialState_DefaultsToToday()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        _store.State.From.ShouldBe(today);
        _store.State.To.ShouldBe(today);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~SchedulesStoreTests" -v minimal`
Expected: FAIL

- [ ] **Step 3: Add From/To to SchedulesState**

```csharp
// Dashboard.Client/State/Schedules/SchedulesState.cs
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Schedules;

public record SchedulesState
{
    public IReadOnlyList<ScheduleExecutionEvent> Events { get; init; } = [];
    public ScheduleDimension GroupBy { get; init; } = ScheduleDimension.Schedule;
    public Dictionary<string, int> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}
```

- [ ] **Step 4: Add SetDateRange action and method to SchedulesStore**

Add to `Dashboard.Client/State/Schedules/SchedulesStore.cs`, after `AppendScheduleEvent`:

```csharp
public record SetScheduleDateRange(DateOnly From, DateOnly To) : IAction;
```

Add to the `SchedulesStore` class:

```csharp
public void SetDateRange(DateOnly from, DateOnly to) =>
    Dispatch(new SetScheduleDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~SchedulesStoreTests" -v minimal`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Dashboard.Client/State/Schedules/ Tests/Unit/Dashboard.Client/State/SchedulesStoreTests.cs
git commit -m "feat: add date range to SchedulesState and SchedulesStore"
```

---

### Task 5: Change MetricsHubService handlers to async

**Files:**
- Modify: `Dashboard.Client/Services/MetricsHubService.cs`
- Modify: `Dashboard.Client/Effects/MetricsHubEffect.cs` (update lambdas to return Task)

- [ ] **Step 1: Change handler signatures in MetricsHubService**

Replace all `Action<T>` with `Func<T, Task>` in `Dashboard.Client/Services/MetricsHubService.cs`:

```csharp
public IDisposable OnTokenUsage(Func<TokenUsageEvent, Task> handler) =>
    _connection.On("OnTokenUsage", handler);

public IDisposable OnToolCall(Func<ToolCallEvent, Task> handler) =>
    _connection.On("OnToolCall", handler);

public IDisposable OnError(Func<ErrorEvent, Task> handler) =>
    _connection.On("OnError", handler);

public IDisposable OnScheduleExecution(Func<ScheduleExecutionEvent, Task> handler) =>
    _connection.On("OnScheduleExecution", handler);

public IDisposable OnHealthUpdate(Func<ServiceHealthUpdate, Task> handler) =>
    _connection.On("OnHealthUpdate", handler);
```

- [ ] **Step 2: Update MetricsHubEffect handlers to return Task**

In `Dashboard.Client/Effects/MetricsHubEffect.cs`, change all handler lambdas from `Action<T>` to `Func<T, Task>`. The sync logic stays the same but each lambda now returns `Task.CompletedTask`:

```csharp
_subscriptions.Add(hub.OnTokenUsage(evt =>
{
    metricsStore.IncrementFromTokenUsage(evt);
    tokensStore.AppendEvent(evt);
    return Task.CompletedTask;
}));

_subscriptions.Add(hub.OnToolCall(evt =>
{
    metricsStore.IncrementToolCall(!evt.Success);
    toolsStore.AppendEvent(evt);
    return Task.CompletedTask;
}));

_subscriptions.Add(hub.OnError(evt =>
{
    errorsStore.AppendEvent(evt);
    return Task.CompletedTask;
}));

_subscriptions.Add(hub.OnScheduleExecution(evt =>
{
    schedulesStore.AppendEvent(evt);
    return Task.CompletedTask;
}));

_subscriptions.Add(hub.OnHealthUpdate(evt =>
{
    var current = healthStore.State.Services.ToList();
    var idx = current.FindIndex(s => s.Service == evt.Service);
    var entry = new ServiceHealth(evt.Service, evt.IsHealthy, evt.Timestamp.ToString("o"));

    if (idx >= 0)
    {
        current[idx] = entry;
    }
    else
    {
        current.Add(entry);
    }

    healthStore.UpdateHealth(current);
    return Task.CompletedTask;
}));
```

- [ ] **Step 3: Verify build succeeds**

Run: `dotnet build Dashboard.Client/`
Expected: Build succeeded

- [ ] **Step 4: Run all existing tests**

Run: `dotnet test Tests/ --filter "Category!=E2E" -v minimal`
Expected: All pass (no behavioral change yet)

- [ ] **Step 5: Commit**

```bash
git add Dashboard.Client/Services/MetricsHubService.cs Dashboard.Client/Effects/MetricsHubEffect.cs
git commit -m "refactor: change MetricsHubService handlers from Action to Func<T, Task>"
```

---

### Task 6: Add breakdown re-fetch to MetricsHubEffect

**Files:**
- Modify: `Dashboard.Client/Effects/MetricsHubEffect.cs`

- [ ] **Step 1: Inject MetricsApiService into MetricsHubEffect**

Update the primary constructor of `MetricsHubEffect` to include `MetricsApiService api`:

```csharp
public sealed class MetricsHubEffect(
    MetricsHubService hub,
    MetricsApiService api,
    MetricsStore metricsStore,
    HealthStore healthStore,
    TokensStore tokensStore,
    ToolsStore toolsStore,
    ErrorsStore errorsStore,
    SchedulesStore schedulesStore,
    ConnectionStore connectionStore) : IAsyncDisposable
```

- [ ] **Step 2: Add private refresh helper methods**

Add these methods to the `MetricsHubEffect` class:

```csharp
private async Task RefreshTokenBreakdownAsync()
{
    try
    {
        var s = tokensStore.State;
        var result = await api.GetTokenGroupedAsync(s.GroupBy, s.Metric, s.From, s.To);
        tokensStore.SetBreakdown(result ?? []);
    }
    catch { /* Breakdown stays at last known value */ }
}

private async Task RefreshToolBreakdownAsync()
{
    try
    {
        var s = toolsStore.State;
        var result = await api.GetToolGroupedAsync(s.GroupBy, s.Metric, s.From, s.To);
        toolsStore.SetBreakdown(result ?? []);
    }
    catch { /* Breakdown stays at last known value */ }
}

private async Task RefreshErrorBreakdownAsync()
{
    try
    {
        var s = errorsStore.State;
        var result = await api.GetErrorGroupedAsync(s.GroupBy, s.From, s.To);
        errorsStore.SetBreakdown(result ?? []);
    }
    catch { /* Breakdown stays at last known value */ }
}

private async Task RefreshScheduleBreakdownAsync()
{
    try
    {
        var s = schedulesStore.State;
        var result = await api.GetScheduleGroupedAsync(s.GroupBy, s.From, s.To);
        schedulesStore.SetBreakdown(result ?? []);
    }
    catch { /* Breakdown stays at last known value */ }
}
```

- [ ] **Step 3: Wire refresh calls into event handlers**

Update the handlers in `StartAsync` to call the refresh methods. Change each handler from `return Task.CompletedTask` to `await` the refresh:

```csharp
_subscriptions.Add(hub.OnTokenUsage(async evt =>
{
    metricsStore.IncrementFromTokenUsage(evt);
    tokensStore.AppendEvent(evt);
    await RefreshTokenBreakdownAsync();
}));

_subscriptions.Add(hub.OnToolCall(async evt =>
{
    metricsStore.IncrementToolCall(!evt.Success);
    toolsStore.AppendEvent(evt);
    await RefreshToolBreakdownAsync();
}));

_subscriptions.Add(hub.OnError(async evt =>
{
    errorsStore.AppendEvent(evt);
    await RefreshErrorBreakdownAsync();
}));

_subscriptions.Add(hub.OnScheduleExecution(async evt =>
{
    schedulesStore.AppendEvent(evt);
    await RefreshScheduleBreakdownAsync();
}));
```

The `OnHealthUpdate` handler stays unchanged (returns `Task.CompletedTask`).

- [ ] **Step 4: Verify build succeeds**

Run: `dotnet build Dashboard.Client/`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add Dashboard.Client/Effects/MetricsHubEffect.cs
git commit -m "feat: re-fetch breakdowns in MetricsHubEffect on SignalR events"
```

---

### Task 7: Pages set date range on stores

**Files:**
- Modify: `Dashboard.Client/Pages/Tokens.razor`
- Modify: `Dashboard.Client/Pages/Tools.razor`
- Modify: `Dashboard.Client/Pages/Errors.razor`
- Modify: `Dashboard.Client/Pages/Schedules.razor`

- [ ] **Step 1: Update Tokens.razor**

In `OnInitializedAsync`, after line `_from = _to.AddDays(-(_selectedDays - 1));` (line 113), add:

```csharp
Store.SetDateRange(_from, _to);
```

In `OnTimeChanged`, after line `_from = _to.AddDays(-(_selectedDays - 1));` (line 135), add:

```csharp
Store.SetDateRange(_from, _to);
```

- [ ] **Step 2: Update Tools.razor**

In `OnInitializedAsync`, after line `_from = _to.AddDays(-(_selectedDays - 1));` (line 114), add:

```csharp
Store.SetDateRange(_from, _to);
```

In `OnTimeChanged`, after line `_from = _to.AddDays(-(_selectedDays - 1));` (line 146), add:

```csharp
Store.SetDateRange(_from, _to);
```

- [ ] **Step 3: Update Errors.razor**

In `OnInitializedAsync`, after line `_from = _to.AddDays(-(_selectedDays - 1));` (line 98), add:

```csharp
Store.SetDateRange(_from, _to);
```

In `OnTimeChanged`, after line `_from = _to.AddDays(-(_selectedDays - 1));` (line 115), add:

```csharp
Store.SetDateRange(_from, _to);
```

- [ ] **Step 4: Update Schedules.razor**

In `OnInitializedAsync`, after line `_from = _to.AddDays(-(_selectedDays - 1));` (line 105), add:

```csharp
Store.SetDateRange(_from, _to);
```

In `OnTimeChanged`, after line `_from = _to.AddDays(-(_selectedDays - 1));` (line 122), add:

```csharp
Store.SetDateRange(_from, _to);
```

- [ ] **Step 5: Verify build succeeds**

Run: `dotnet build Dashboard.Client/`
Expected: Build succeeded

- [ ] **Step 6: Commit**

```bash
git add Dashboard.Client/Pages/Tokens.razor Dashboard.Client/Pages/Tools.razor Dashboard.Client/Pages/Errors.razor Dashboard.Client/Pages/Schedules.razor
git commit -m "feat: pages set date range on stores for live breakdown refresh"
```

---

### Task 8: DataLoadEffect syncs date range on all stores

**Files:**
- Modify: `Dashboard.Client/Effects/DataLoadEffect.cs`

- [ ] **Step 1: Add SetDateRange calls to DataLoadEffect.LoadAsync**

In `Dashboard.Client/Effects/DataLoadEffect.cs`, at the beginning of the `try` block in `LoadAsync` (after line 25), add:

```csharp
tokensStore.SetDateRange(from, to);
toolsStore.SetDateRange(from, to);
errorsStore.SetDateRange(from, to);
schedulesStore.SetDateRange(from, to);
```

- [ ] **Step 2: Verify build succeeds**

Run: `dotnet build Dashboard.Client/`
Expected: Build succeeded

- [ ] **Step 3: Run all unit tests**

Run: `dotnet test Tests/ --filter "Category!=E2E" -v minimal`
Expected: All pass

- [ ] **Step 4: Commit**

```bash
git add Dashboard.Client/Effects/DataLoadEffect.cs
git commit -m "feat: DataLoadEffect syncs date range on all stores during load"
```
