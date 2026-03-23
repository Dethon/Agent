# Dynamic Dashboard Visualizations

## Goal

Replace the dashboard's hardcoded chart groupings with dynamic controls that let users switch dimensions and metrics on the fly, and upgrade from pure-CSS bar charts to BlazorApexCharts.

## Pages & Controls

Each page gets a toolbar of pill-toggle selectors above the chart, consistent with the existing `TimeRangeSelector` component style. Pages that support multiple metrics get a second selector row.

### Tokens Page

| Selector | Options |
|----------|---------|
| Group by | User (sender) · Model · Agent |
| Metric   | Tokens (input+output) · Cost ($) |
| Time range | Today · 7d · 30d (existing) |

- **Chart type**: Donut (ApexCharts `ApexChart<T>` with `ApexChartType.Donut`)
- Default: Group by User, Metric Tokens, Time range Today
- KPI cards remain (Input Tokens, Output Tokens, Cost)

### Tools Page

| Selector | Options |
|----------|---------|
| Group by | Tool Name · Status (success/fail) |
| Metric   | Call Count · Avg Duration (ms) · Error Rate (%) |
| Time range | Today · 7d · 30d (existing) |

- **Chart type**: Horizontal bar (`ApexChartType.Bar` with `Horizontal = true`)
- When grouped by Status with Call Count metric: stacked bar (success/fail segments)
- KPI cards remain (Total Calls, Success Rate, Avg Duration)

### Errors Page

| Selector | Options |
|----------|---------|
| Group by | Service · Error Type |
| Time range | Today · 7d · 30d (add — currently uses `limit`) |

- **Chart type**: Horizontal bar
- Single metric (error count) — no metric selector needed
- KPI cards remain (Total Errors, Services Affected)

### Schedules Page

| Selector | Options |
|----------|---------|
| Group by | Schedule · Status (success/fail) |
| Time range | Today · 7d · 30d (existing) |

- **Chart type**: Horizontal bar
- When grouped by Status: stacked bar
- Single metric (execution count) — no metric selector needed
- KPI cards remain (Total Runs, Success Rate, Avg Duration)

## Control Style

Pill toggle buttons in a horizontal toolbar above the chart area. Layout:

```
[ Group by: (User) (Model) (Agent) ]  [ Metric: (Tokens) (Cost $) ]  [ Time: (Today) (7d) (30d) ]
```

Each selector group has a small uppercase label above the pills. Active pill uses the accent color (`#6366f1`), inactive pills use dark background with muted text. This matches the existing `TimeRangeSelector` pattern and extends it to a generic `PillSelector` component.

## Backend — Server-Side Aggregation

### New Generic API Pattern

Add grouped aggregation endpoints to `MetricsApiEndpoints.cs`:

| Endpoint | Returns |
|----------|---------|
| `GET /api/metrics/tokens/by/{dimension}?metric={metric}&from=&to=` | `Dictionary<string, decimal>` |
| `GET /api/metrics/tools/by/{dimension}?metric={metric}&from=&to=` | `Dictionary<string, decimal>` |
| `GET /api/metrics/errors/by/{dimension}?from=&to=` | `Dictionary<string, int>` |
| `GET /api/metrics/schedules/by/{dimension}?from=&to=` | `Dictionary<string, int>` |

**Tokens dimensions & metrics:**

| Dimension | Metric = `tokens` | Metric = `cost` |
|-----------|-------------------|-----------------|
| `user`    | Sum(InputTokens+OutputTokens) per Sender | Sum(Cost) per Sender |
| `model`   | Sum(InputTokens+OutputTokens) per Model | Sum(Cost) per Model |
| `agent`   | Sum(InputTokens+OutputTokens) per AgentId | Sum(Cost) per AgentId |

**Tools dimensions & metrics:**

| Dimension | Metric = `count` | Metric = `duration` | Metric = `errorrate` |
|-----------|------------------|---------------------|----------------------|
| `tool`    | Count per ToolName | Avg(DurationMs) per ToolName | (Failures/Total)*100 per ToolName |
| `status`  | Count per Success (true/false) | Avg(DurationMs) per Success | N/A (redundant) |

**Errors dimensions:** `service`, `errortype` → Count per group.

**Schedules dimensions:** `schedule`, `status` → Count per group.

### MetricsQueryService Changes

Add generic grouping methods that:
1. Retrieve the raw events from Redis sorted sets for the date range (reuse existing logic)
2. Deserialize and group by the requested dimension
3. Apply the requested aggregation (sum, avg, count, rate)
4. Return `Dictionary<string, decimal>`

### Existing Endpoints

Keep `/api/metrics/tokens/by-user` and `/api/metrics/tokens/by-model` for backward compatibility with the Overview page. The new `/by/{dimension}` endpoints serve the dynamic charts.

## Frontend Architecture

### New NuGet Package

Add `Blazor-ApexCharts` to `Dashboard.Client.csproj`. Register in `Program.cs`.

### New Components

**`PillSelector.razor`** — Generic pill toggle component:
- Parameters: `Label` (string), `Options` (list of name/value), `Value` (bound), `OnChanged` (EventCallback)
- Renders uppercase label + horizontal pill buttons
- Active pill highlighted with accent color
- Replaces the existing `TimeRangeSelector` (which becomes a specific instance of `PillSelector`)

**`DynamicChart.razor`** — Wrapper around ApexCharts:
- Parameters: `ChartType` (donut/bar), `Data` (Dictionary<string, decimal>), `Horizontal` (bool), `Stacked` (bool), `Title` (string)
- Handles ApexChart configuration and theming (dark mode, colors, tooltips)
- Responsive sizing

### State Changes

Each page store gets new state fields for the active dimension and metric:

**TokensState** — add: `GroupBy` (enum: User/Model/Agent), `Metric` (enum: Tokens/Cost), `Breakdown` (Dictionary<string, decimal>)

**ToolsState** — add: `GroupBy` (enum: ToolName/Status), `Metric` (enum: CallCount/AvgDuration/ErrorRate), `Breakdown` (Dictionary<string, decimal>)

**ErrorsState** — add: `GroupBy` (enum: Service/ErrorType), `Breakdown` (Dictionary<string, int>)

**SchedulesState** — add: `GroupBy` (enum: Schedule/Status), `Breakdown` (Dictionary<string, int>)

### Data Flow

1. User clicks a pill selector → page dispatches dimension/metric change to store
2. Effect triggers API call to `/api/metrics/{domain}/by/{dimension}?metric={metric}&from=&to=`
3. Response stored in `Breakdown` field
4. Chart component re-renders with new data

### Remove Old Components

- Remove `BarChart.razor` and `BarItem.cs` after migration (replaced by `DynamicChart`)
- Refactor `TimeRangeSelector` to use `PillSelector` internally, or replace entirely

## ApexCharts Theme

Dark theme to match the existing dashboard aesthetic:
- Background: transparent (inherits page dark background)
- Text/labels: `#a0a0b0`
- Chart colors: `["#6366f1", "#22d3ee", "#f59e0b", "#ef4444", "#10b981", "#f472b6"]`
- Tooltip: dark background, light text
- Grid lines: subtle (`#2a2a3e`)

## Testing

- Unit tests for new `MetricsQueryService` grouping methods (each dimension × metric combination)
- Unit tests for `PillSelector` component (renders options, fires callback on click, highlights active)
- Integration tests for new API endpoints (valid dimension, invalid dimension → 400, empty date range defaults)

## Out of Scope

- New pages (no Explorer/Analytics page)
- Time-series / trend line charts (future enhancement)
- Real-time chart updates via SignalR (charts refresh on dimension/metric/time-range change only; live event append to tables remains as-is)
