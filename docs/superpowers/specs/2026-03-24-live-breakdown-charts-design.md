# Live Breakdown Charts on Dashboard Detail Pages

## Problem

The Dashboard detail pages (Tokens, Tools, Errors, Schedules) receive new events in real-time via SignalR, but their breakdown charts only refresh when the user manually changes a filter (groupBy, metric, or time range). This means the donut/bar charts go stale while the event tables update live.

## Solution

Re-fetch breakdown data from the server whenever a new SignalR event arrives for that domain. Store the active date range in each detail store's state so `MetricsHubEffect` can issue the correct API call without coupling to page components.

## Changes

### 1. Change MetricsHubService handlers from sync to async

The current `On*` methods accept `Action<T>` (synchronous). Change them to accept `Func<T, Task>` so the hub effect can `await` API calls inside the handlers. SignalR's `.On()` supports `Func<T, Task>` natively.

- `OnTokenUsage(Action<TokenUsageEvent>)` → `OnTokenUsage(Func<TokenUsageEvent, Task>)`
- `OnToolCall(Action<ToolCallEvent>)` → `OnToolCall(Func<ToolCallEvent, Task>)`
- `OnError(Action<ErrorEvent>)` → `OnError(Func<ErrorEvent, Task>)`
- `OnScheduleExecution(Action<ScheduleExecutionEvent>)` → `OnScheduleExecution(Func<ScheduleExecutionEvent, Task>)`
- `OnHealthUpdate(Action<ServiceHealthUpdate>)` → `OnHealthUpdate(Func<ServiceHealthUpdate, Task>)`

File: `Dashboard.Client/Services/MetricsHubService.cs`

### 2. Add date range to detail store states

Add `DateOnly From` and `DateOnly To` properties to each detail state record, defaulting to today:

- `TokensState` — add `From`, `To`
- `ToolsState` — add `From`, `To`
- `ErrorsState` — add `From`, `To`
- `SchedulesState` — add `From`, `To`

### 3. Add `SetDateRange` action to detail stores

Each store gets a `SetDateRange(DateOnly from, DateOnly to)` method that updates the state's `From`/`To`. New action records:

- `SetTokenDateRange(DateOnly From, DateOnly To)`
- `SetToolDateRange(DateOnly From, DateOnly To)`
- `SetErrorDateRange(DateOnly From, DateOnly To)`
- `SetScheduleDateRange(DateOnly From, DateOnly To)`

### 4. Pages set date range on load and time filter change

Each detail page calls `Store.SetDateRange(from, to)` in two places:

- `OnInitializedAsync` — after computing the initial `_from`/`_to`
- `OnTimeChanged` — after updating `_from`/`_to` from user selection

`DataLoadEffect.LoadAsync` also calls `SetDateRange` on all four stores so that stores for pages not yet visited stay in sync when another page triggers a full load.

Files: `Tokens.razor`, `Tools.razor`, `Errors.razor`, `Schedules.razor`, `DataLoadEffect.cs`

### 5. MetricsHubEffect re-fetches breakdowns on events

Inject `MetricsApiService` into `MetricsHubEffect`. Update handlers to be async. In each handler, after the existing append/increment logic, call a private helper to re-fetch the breakdown:

- `OnTokenUsage` → `RefreshTokenBreakdownAsync()` → `api.GetTokenGroupedAsync(...)` → `tokensStore.SetBreakdown(result)`
- `OnToolCall` → `RefreshToolBreakdownAsync()` → `api.GetToolGroupedAsync(...)` → `toolsStore.SetBreakdown(result)`
- `OnError` → `RefreshErrorBreakdownAsync()` → `api.GetErrorGroupedAsync(...)` → `errorsStore.SetBreakdown(result)`
- `OnScheduleExecution` → `RefreshScheduleBreakdownAsync()` → `api.GetScheduleGroupedAsync(...)` → `schedulesStore.SetBreakdown(result)`

Each helper reads `GroupBy`, `Metric`, `From`, `To` from the respective store's current state. Each helper wraps the API call in try/catch — on failure, the breakdown stays at its last known value (silent failure, matching existing behavior).

## Data Flow

```
SignalR event arrives
  → MetricsHubEffect async handler
    → Append event to store (existing behavior)
    → Increment KPI counters (existing behavior, where applicable)
    → Read store's GroupBy, Metric, From, To
    → GET /api/metrics/{type}/by/{dimension}?metric=...&from=...&to=...
    → Store.SetBreakdown(result)
    → UI re-renders chart via store subscription
```

## Edge Cases

- **API failure**: Each refresh helper catches exceptions silently. Breakdown stays at its last known value.
- **Rapid events**: Each event triggers a re-fetch. The last `SetBreakdown` wins since it overwrites the entire dictionary. Responses may arrive out of order for rapid-fire events, but given the expected event volume (seconds between events) this is acceptable. If this proves problematic in practice, a debounce or cancellation token can be added as a follow-up.
- **Page not visited yet**: Stores default `From`/`To` to today, and `DataLoadEffect.LoadAsync` syncs all stores' date ranges, so breakdowns are always fetched for the correct range.
- **SignalR disconnected**: No events arrive, so no re-fetches. On reconnect, the next arriving event triggers a fresh breakdown fetch. A full data reload on reconnect is a potential follow-up improvement.

## Files Changed

| File | Change |
|------|--------|
| `Dashboard.Client/Services/MetricsHubService.cs` | Change `Action<T>` handlers to `Func<T, Task>` |
| `Dashboard.Client/State/Tokens/TokensState.cs` | Add `From`, `To` properties |
| `Dashboard.Client/State/Tools/ToolsState.cs` | Add `From`, `To` properties |
| `Dashboard.Client/State/Errors/ErrorsState.cs` | Add `From`, `To` properties |
| `Dashboard.Client/State/Schedules/SchedulesState.cs` | Add `From`, `To` properties |
| `Dashboard.Client/State/Tokens/TokensStore.cs` | Add `SetDateRange` action and method |
| `Dashboard.Client/State/Tools/ToolsStore.cs` | Add `SetDateRange` action and method |
| `Dashboard.Client/State/Errors/ErrorsStore.cs` | Add `SetDateRange` action and method |
| `Dashboard.Client/State/Schedules/SchedulesStore.cs` | Add `SetDateRange` action and method |
| `Dashboard.Client/Pages/Tokens.razor` | Call `SetDateRange` on init and time change |
| `Dashboard.Client/Pages/Tools.razor` | Call `SetDateRange` on init and time change |
| `Dashboard.Client/Pages/Errors.razor` | Call `SetDateRange` on init and time change |
| `Dashboard.Client/Pages/Schedules.razor` | Call `SetDateRange` on init and time change |
| `Dashboard.Client/Effects/DataLoadEffect.cs` | Call `SetDateRange` on all stores during load |
| `Dashboard.Client/Effects/MetricsHubEffect.cs` | Inject `MetricsApiService`, async handlers, private refresh helpers |

## Testing

- Unit tests for each store's `SetDateRange` action
- Unit tests for `MetricsHubEffect` breakdown re-fetch: mock `MetricsApiService` to verify it's called with correct parameters after a simulated event, and verify `SetBreakdown` is called on the store. Test error case (API throws) to verify silent failure.
- Manual E2E: open a detail page, trigger an event, observe chart updates without touching filters
