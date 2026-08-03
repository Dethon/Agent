# 0007 — A metric family is named, not typed

Status: accepted
Date: 2026-08-03

## Context

The dashboard shows seven metric families: tokens, tools, errors, schedules, memory,
latency and voice. Each one is declared five times over — a `Refresh*BreakdownAsync`
method and a `CancellationTokenSource` field in `Dashboard.Client/Effects/MetricsHubEffect.cs`,
a second fan-out in `Dashboard.Client/Effects/DataLoadEffect.cs`, a `Get*GroupedAsync`
in `Dashboard.Client/Services/MetricsApiService.cs`, an endpoint map in
`Observability/MetricsApiEndpoints.cs`, and a page under `Dashboard.Client/Pages/`.
Adding voice, the most recent family, meant editing all five with no compiler help.

The obvious consolidation is one descriptor per family, generic over the family's
dimension and metric enums, since both already exist in `Domain/DTOs/Metrics/Enums/`.
That is what the audit proposed: `MetricBreakdown<TDimension, TMetric>`.

The seven families do not have one call shape. They have four:

- Tokens, tools and memory take a dimension and a metric.
- Errors and schedules take a dimension only, and return `Dictionary<string, int>`
  rather than `Dictionary<string, decimal>`. There is no `ErrorMetric` or
  `ScheduleMetric` enum, because there is nothing for one to name.
- Voice takes a third argument, the aggregation applied to its duration events.
- Latency makes a second call for its trend series and writes two store slots.

`MetricBreakdown<TDimension, TMetric>` fits three of the seven.

## Decision

A metric family is identified by name, not by its enum types.

```csharp
record BreakdownFamily(
    string Name,
    string PreferenceKeyPrefix,
    Func<CancellationToken, Task> RefreshAsync,
    Func<CancellationToken, Task> LoadEventsAsync);

record BreakdownFamily<TState>(...) : BreakdownFamily
{
    Store<TState> Store { get; }
}
```

The generic parameter carries the store, which the pages and the wrapper component
need typed. It does not carry the dimension or the metric. Each family's call shape
lives in the closure that builds its `RefreshAsync`, at the single registration site
where the family table is constructed.

`MetricsHubEffect` and `DataLoadEffect` iterate the non-generic base. A page names
one family and gets its state typed.

## Considered options

**Widen the generic so every family fits.** Give errors and schedules a one-member
`Count` metric enum, put Voice's aggregation and Latency's trend behind optional slots
on the descriptor. Rejected because the two enum members would exist for no reason
other than to satisfy a type parameter, and would show up in `Domain` next to
`TokenMetric` and `VoiceMetric`, which name real things. Six of seven families would
carry two slots they never read. The type would be uniform and the meaning would not.

**One class per family implementing a common interface.** `VoiceBreakdownFamily`,
`LatencyBreakdownFamily` and five more. Each quirk gets its own home with no closure
and no optional slot, and each is independently testable. Rejected because it keeps
the seven declarations that are the problem, moves them into seven new files, and
gives up the single registration site that makes a missing family visible.

**Leave the five layers alone and fix only the endpoint date defaulting.** The
cheapest change that removes a real duplication. Rejected because it leaves the two
effects building the same query from the same store state in two places, which is how
the aggregation-default bug documented at `MetricsApiService.cs:100-102` happened.

## Consequences

- The family table is the only place a family is declared, but nothing enforces that
  a new family is added to it. A family omitted from the table is visibly absent from
  a seven-entry list rather than caught by the compiler. This is the price of the
  decision and the reason it is recorded here.
- One parameterised test over the table covers all seven families. Six of them have no
  unit coverage today.
- `MetricsQueryService` is unaffected. Its seven grouping methods stay per-family:
  `GetTokenGroupedAsync` switches between two event streams by metric,
  `GetMemoryGroupedAsync` merges three and type-switches over them, and
  `GetVoiceGroupedAsync` pre-filters by event kind and infers duration from the enum
  name suffix. There is no shared shape there to extract.
- `LatencyMetric` is renamed `Aggregation`. It is a reduction over a set of durations
  and is the "Metric" pill on the latency page and the "Aggregate" pill on the voice
  page. It is query-string only and never persisted, so the rename is value-safe.
  `VoiceMetric` keeps its name: it is persisted by integer value in Redis and its
  members are pinned.
- Sequenced before candidate 11. That candidate was surveyed as a shared Blazor client
  seam and regrilled into a dashboard live-connection fix;
  `docs/adr/0008-the-two-browser-clients-stay-separate.md` records why the sharing half
  was dropped. The sequencing survives the reframing: this change rewrites
  `MetricsHubEffect.StartAsync`, `DataLoadEffect` and `MetricsHubEffectTests`, and the
  family table is what candidate 11's catch-up walks.
