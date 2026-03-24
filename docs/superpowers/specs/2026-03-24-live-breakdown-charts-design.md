# Live Breakdown Charts on Dashboard Detail Pages

## Problem

The Dashboard detail pages (Tokens, Tools, Errors, Schedules) receive new events in real-time via SignalR, but their breakdown charts only refresh when the user manually changes a filter (groupBy, metric, or time range). This means the donut/bar charts go stale while the event tables update live.

## Solution

Re-fetch breakdown data from the server whenever a new SignalR event arrives for that domain. Store the active date range in each detail store's state so `MetricsHubEffect` can issue the correct API call without coupling to page components.

## Changes

### 1. Add date range to detail store states

Add `DateOnly From` and `DateOnly To` properties to each detail state record, defaulting to today:

- `TokensState` — add `From`, `To`
- `ToolsState` — add `From`, `To`
- `ErrorsState` — add `From`, `To`
- `SchedulesState` — add `From`, `To`

### 2. Add `SetDateRange` action to detail stores

Each store gets a `SetDateRange(DateOnly from, DateOnly to)` method that updates the state's `From`/`To`. New action records:

- `SetTokenDateRange(DateOnly From, DateOnly To)`
- `SetToolDateRange(DateOnly From, DateOnly To)`
- `SetErrorDateRange(DateOnly From, DateOnly To)`
- `SetScheduleDateRange(DateOnly From, DateOnly To)`

### 3. Pages set date range on load and time filter change

Each detail page calls `Store.SetDateRange(from, to)` in two places:

- `OnInitializedAsync` — after computing the initial `_from`/`_to`
- `OnTimeChanged` — after updating `_from`/`_to` from user selection

Files: `Tokens.razor`, `Tools.razor`, `Errors.razor`, `Schedules.razor`

### 4. MetricsHubEffect re-fetches breakdowns on events

Inject `MetricsApiService` into `MetricsHubEffect`. In each SignalR event handler, after the existing append/increment logic, add an async call to re-fetch the breakdown:

- `OnTokenUsage` → `api.GetTokenGroupedAsync(tokensStore.State.GroupBy, tokensStore.State.Metric, from, to)` → `tokensStore.SetBreakdown(result)`
- `OnToolCall` → `api.GetToolGroupedAsync(toolsStore.State.GroupBy, toolsStore.State.Metric, from, to)` → `toolsStore.SetBreakdown(result)`
- `OnError` → `api.GetErrorGroupedAsync(errorsStore.State.GroupBy, from, to)` → `errorsStore.SetBreakdown(result)`
- `OnScheduleExecution` → `api.GetScheduleGroupedAsync(schedulesStore.State.GroupBy, from, to)` → `schedulesStore.SetBreakdown(result)`

Each re-fetch reads `From`/`To` from the respective store's current state.

## Data Flow

```
SignalR event arrives
  → MetricsHubEffect handler
    → Append event to store (existing behavior)
    → Increment KPI counters (existing behavior, where applicable)
    → Read store's GroupBy, Metric, From, To
    → GET /api/metrics/{type}/by/{dimension}?metric=...&from=...&to=...
    → Store.SetBreakdown(result)
    → UI re-renders chart via store subscription
```

## Edge Cases

- **API failure**: Breakdown stays at its last known value. No error surfaced to the user (matches existing behavior when initial load fails silently for breakdowns).
- **Rapid events**: Each event triggers a re-fetch. The last `SetBreakdown` wins since it overwrites the entire dictionary. This is acceptable given the expected event volume (seconds between events, not milliseconds).
- **Page not visited yet**: Stores default `From`/`To` to today, so breakdowns will be fetched for today even if no page has set the range yet. This is correct since the default time filter is "Today".
- **SignalR disconnected**: No events arrive, so no re-fetches are attempted. When reconnected, the page can be refreshed manually or will update on the next event.

## Files Changed

| File | Change |
|------|--------|
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
| `Dashboard.Client/Effects/MetricsHubEffect.cs` | Inject `MetricsApiService`, re-fetch breakdowns in event handlers |

## Testing

- Unit tests for each store's `SetDateRange` action
- Unit test for `MetricsHubEffect` verifying breakdown re-fetch after event
- Manual E2E: open a detail page, trigger an event, observe chart updates without touching filters
