# Spec — The Metric Family

Status: ready-for-agent

Grilled from candidate 9 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the exact file and line evidence for every claim below. Decision recorded
as `docs/adr/0007-a-metric-family-is-named-not-typed.md`. Vocabulary follows
`CONTEXT.md`: a **metric family** is one kind of metric as the dashboard shows it, a
**breakdown** is a family's numbers grouped by one dimension over one date range, a
**dimension** is what a breakdown groups by, and an **aggregation** is how a set of
durations is reduced to one number.

## Problem Statement

Adding a metric family to the dashboard means editing eight places, and nothing
catches you if you miss one.

The dashboard shows seven families: tokens, tools, errors, schedules, memory, latency
and voice. Each is declared in the live-update effect as a refresh method and a
cancellation token source, again in the page-load effect as a pair of calls and a pair
of assignments, again in the API service as a URL builder, again in the endpoint group
as a route map, and again as a page. Voice, the most recent family, was added by
editing all of them. There is no list of families anywhere, so there is nothing to
check a new one against.

Two of those places build the same request from the same state independently, and that
is how the aggregation bug happened. The voice page lets the user choose how durations
are reduced. The live-update path and the page-load path each construct that request
themselves, and one of them had a default value for the choice. Picking the 95th
percentile and then receiving a live event silently reverted the chart to the average.
The fix was to delete the default, and the comment recording why it must never come
back is now the clearest documentation of the problem the fan-out creates.

Live updates cost the server far more than they need to. Every event arriving over the
hub immediately refetches that family's whole breakdown and cancels the request before
it. Cancelling a request from a WebAssembly client does not stop the server, which has
already begun reading every event in the range out of Redis and grouping them in
memory. A single busy turn emits many events, so the server performs one full
aggregation per event and the browser reads the last one.

The vocabulary has drifted in a way that makes all of this harder to reason about. One
enum names a reduction over a set of durations — average, percentiles, maximum, count.
On the latency page it is the user's *metric* choice. On the voice page the same enum
is the user's *aggregate* choice, sitting beside a different enum that is also called a
metric but actually names which kind of event happened. Three concepts, two names.

Six of the seven families have no unit coverage. The per-page work — reading saved
preferences, deriving the date range from the selected number of days, persisting a
choice and reloading — is written out separately on every page and is reachable only
by driving a real browser against a real stack.

The result is that a metric family, the thing the dashboard is actually made of, is not
a thing anywhere. It is a name repeated in eight files and a set of rules that live in
prose.

## Solution

Make the metric family a value, and let the dashboard be a list of them.

A family knows its own name, where its preferences are saved, how to load its events
and how to refresh its breakdown. That is everything anyone asks of one. Both effects
stop knowing about tokens and tools and voice, and iterate the family table instead.
The pages stop implementing preference persistence and date derivation, and share a
control wrapper that does it once.

Refreshing a family becomes a real operation with a stated contract rather than seven
copies of a method. Awaiting it means the family's breakdown reflects the state at or
after the call. Concurrent callers share the run in flight, and it repeats once if the
state moved while it was running, so a burst of live events costs two server
aggregations instead of twenty and adds no lag. It reports failure by throwing, and
each of its two callers applies its own policy in one place: the live-update path keeps
the last known breakdown, the page-load path reports the dashboard disconnected.

The family is identified by name, not typed by its dimension and metric enums. The
seven families have four different call shapes, and the generic version fits three of
them. The ADR records what the generic version would have cost.

On the server, the date range stops being defaulted twenty-two times. It becomes a
bound parameter with one default drawn from the time provider that the query service
already takes and the endpoints ignore.

The reduction enum is renamed to say what it is. It is an **aggregation**, and the
latency page's metric pill and the voice page's aggregate pill are choosing the same
thing.

## User Stories

1. As a dashboard user, I want to group a family's numbers by a dimension I choose, so
   that I can see which sender, model, stage or satellite is responsible.
2. As a dashboard user, I want to choose which quantity a family charts, so that I can
   look at cost one moment and token count the next without changing pages.
3. As a dashboard user, I want to choose how durations are reduced on the latency and
   voice pages, so that I can look at the 95th percentile rather than an average that
   hides the tail.
4. As a dashboard user, I want the aggregation I picked to be the one used, so that a
   live event arriving does not quietly put the chart back to an average.
5. As a dashboard user, I want my group-by, metric, aggregation and time-range choices
   remembered when I come back, so that I do not reset the same four pills every visit.
6. As a dashboard user, I want each family to remember its own choices, so that
   grouping tokens by model does not change how errors are grouped.
7. As a dashboard user, I want the chart to update when events arrive, so that I can
   watch a turn happen without reloading.
8. As a dashboard user, I want a burst of activity not to make the dashboard
   unresponsive, so that the busiest moments are the ones I can still watch.
9. As a dashboard user, I want the observability service not to do twenty redundant
   aggregations to answer one question, so that watching the dashboard does not slow
   down the thing it is watching.
10. As a dashboard user, I want the chart to keep its last known values when a refresh
    fails, so that a transient error does not blank a chart I am reading.
11. As a dashboard user, I want the connection indicator to go red when the page cannot
    load its data, so that I can tell stale numbers from current ones.
12. As a dashboard user, I want the latency trend to move together with the latency
    breakdown, so that the two panels on one page never disagree.
13. As a dashboard user, I want to keep the choices I have already saved after this
    change ships, so that an internal refactor does not reset my dashboard.
14. As a dashboard user, I want each page to keep the panels it has today — its own
    headline figures, its own chart type and its own event table — so that nothing I
    use disappears in the name of uniformity.
15. As a dashboard user, I want the voice page to keep its separate metric and
    aggregate pills, so that I can still choose an event kind and a reduction
    independently.
16. As a dashboard user, I want an endpoint asked without a date range to answer for
    today, so that a hand-typed URL behaves the way it always has.
17. As a developer, I want one list of every metric family, so that I can see what the
    dashboard shows without reading five files.
18. As a developer, I want to add a family by adding one entry to that list, so that
    adding one is a decision rather than an exercise in not missing a site.
19. As a developer, I want the family to be the only thing that knows how its request
    is built, so that two call sites cannot disagree about a default the way they did
    over the voice aggregation.
20. As a developer, I want the refresh contract stated once, so that I do not have to
    infer the cancellation and error rules from seven copies of a method.
21. As a developer, I want failure handling to live with the caller that has a policy,
    so that "keep the last value" and "report disconnected" are each written once.
22. As a developer, I want every family covered by one parameterised test, so that the
    six families with no coverage stop being six families with no coverage.
23. As a developer, I want to test preference persistence and date derivation without a
    browser, so that a change to that logic does not depend on Docker and Playwright.
24. As a developer, I want the endpoint date default in one place and driven by the time
    provider, so that it can be tested and cannot drift between routes.
25. As a developer, I want the reduction enum named for what it is, so that reading a
    signature tells me whether a parameter chooses a quantity or a way of reducing one.
26. As a developer, I want the event-kind enum left alone, so that historical voice
    metrics stored by integer value stay readable.
27. As a developer, I want the page-load path and the live-update path to share the
    family's definition, so that the next family cannot be half-added.
28. As a developer, I want the shared control header to allow a family its own extra
    control, so that voice's aggregate pill does not force a special case into every
    other page.
29. As a developer, I want a family able to declare which metric choices are currently
    unavailable, so that the tools and memory pages keep disabling the pills that do not
    apply.
30. As a developer, I want this change to shrink the surface of the dashboard
    live-connection work that follows it, so that candidate 11 adds catch-up as a walk
    of the family table rather than a second eleven-store fan-out.

## Implementation Decisions

**The metric family is a value in the dashboard client.** A non-generic record carries
the family's name, the prefix its preferences are saved under, and two operations:
load my events, and refresh my breakdown. A generic subtype adds the family's store,
which the pages and the control wrapper need typed. The record is not generic over the
family's dimension and metric enums. Each family's call shape is closed over at the
single site where the table is built. `docs/adr/0007-a-metric-family-is-named-not-typed.md`
records the rejected alternative.

**One registration site builds the table.** It is constructed from the API service and
the seven stores, registered as a single service, and is the only place a family is
declared. Both effects take it in place of their eleven injected stores; a page names
the one family it shows.

**Refresh has a stated contract.** Awaiting a family's refresh means its breakdown
reflects the store state at or after the call. Concurrent callers share the run already
in flight, and the run repeats once if the state changed while it was running. There is
no timer and no debounce: this adds no latency. It reports failure by throwing to
everyone awaiting it, and does not swallow.

**Each caller owns its failure policy.** The live-update effect wraps the table in one
try/catch: cancellation is ignored and anything else leaves the breakdown at its last
known value. The page-load effect keeps its existing catch, so any failure across the
whole load still reports the dashboard disconnected. Neither behaviour changes.

**The live-update effect becomes a table lookup.** Each hub subscription updates its
store from the event and then refreshes its family. The seven cancellation token source
fields and the seven refresh methods go; the coalescing lives in the family and needs
no field on the effect.

**The page-load effect keeps only what is not per-family.** The family absorbs its raw
event load as well as its breakdown, so the page-load effect sets the date range on
every family, starts both operations for each, and adds the summary and health calls.
Everything still runs in parallel, as it does today. Memory keeps its three event
sources inside its own load operation.

**The API service gets one grouped call.** The seven URL builders become one method
taking the route segment and the query values. The comment recording the aggregation
default bug is deleted, because with the family as the only place a request is built
there is no second call path to revert the user's choice.

**Pages keep everything below the control header.** A control wrapper component owns the
header markup, the group-by, metric and time pills, preference loading and saving, and
the derivation of the date range from the selected number of days. It exposes the
family's state to its child content typed. Each page keeps its own headline figures,
chart type, event table and sorting. The voice page's aggregate pill is supplied as an
extra-controls fragment. The tools and memory pages keep their disabled metric values,
supplied as a parameter.

**The wrapper's logic sits in a plain class.** Preference loading, preference saving,
date derivation and change handling live in a session type constructed from a family,
the local storage service and the time provider. The component forwards to it and holds
markup. This keeps the logic testable without a Blazor component testing library.

**Preference keys are unchanged.** A family's preference prefix is its name followed by
a dot, which is exactly the convention the pages already use, so every saved choice
survives the change. The overview page keeps its own day-count key and stays as it is;
it has the time pill but no breakdown.

**The endpoint date range becomes a bound parameter.** A record with a custom binder
reads the from and to query values and fills either default from the time provider
resolved from the request services. Every endpoint in the metrics group takes it in
place of two nullable parameters. The query string is unchanged, so no client changes
and no route changes. There is no OpenAPI document in the observability service, so
hiding the parameters behind a binder costs nothing.

**The reduction enum is renamed to `Aggregation`.** It is never persisted — it appears
only in query strings and in saved preference values, and renaming the type does not
change its member names, so both keep working. The latency page's metric pill and the
voice page's aggregate pill are both choosing an aggregation. The query string parameter
names are unchanged.

**The voice event-kind enum keeps its name.** It is persisted by integer value in Redis
and its members are pinned. Renaming it would reach into the voice channel server, which
is not what this change is about.

**The grouping service is untouched.** Its seven grouping methods stay per-family. Token
grouping switches between two event streams depending on the metric, memory grouping
merges three streams and type-switches over them, and voice grouping pre-filters by
event kind and decides whether a metric is a duration from its name. There is no shared
shape there to extract, and pretending otherwise would push those differences into
escape hatches.

## Testing Decisions

A good test here asserts on what left the dashboard client and what the stores hold: the
request that went out, and the state a user would see. It does not assert on the family
table's internals, on how coalescing is implemented, or on which method was called.
Fakes go at the edges of the client — HTTP out and JavaScript interop out — and
everything inside stays real.

**The existing seam is the primary one and it is reused.** `MetricsHubEffectTests`
already fakes exactly the two edges that matter: an HTTP message handler that records
request URIs and stages responses with configurable delays, and a fake hub that fires
events into the registered handlers. The stores, the API service and the effects are
real. Every new behaviour tests through it.

**One fake is added, at the other edge.** A dictionary-backed JavaScript runtime fake,
so the local storage service stays real and the session type can be tested with a real
family and a fake time provider. This deliberately introduces no storage interface, and
none is coming: `docs/adr/0008-the-two-browser-clients-stay-separate.md` rules out
sharing the chat client's.

**The endpoint date default gets a narrow seam.** The binder is tested directly against
a request context carrying query values and a fake time provider, covering both values
absent, one present, both present and unparseable input. The repository has no
`WebApplicationFactory` usage and booting the observability service would require Redis,
which is not worth it for a defaulting rule.

Modules covered:

- **The family table**, parameterised over all seven families: refreshing a family sends
  every choice currently in its store, and writes the response into its breakdown. This
  is the general form of the aggregation bug, and it is the test the six uncovered
  families gain.
- **The refresh contract**: triggering a family repeatedly while a response is delayed
  produces two requests, not one per trigger, and the breakdown ends at the value from
  the last request. This replaces the existing rapid-events test, which pins the
  cancel-stale behaviour being removed. The new test keeps the same parameterisation
  over families and the same staged-delay technique.
- **The failure policy**: a failing refresh leaves the breakdown at its previous value
  when it arrives through the live-update path, and reports disconnected when it arrives
  through the page-load path.
- **The page-load effect**: loading sets the date range on every family and issues both
  the event request and the breakdown request for each, plus summary and health.
- **The session type**: a saved preference is applied on initialisation, a changed pill
  is persisted under the family's prefix and triggers a refresh, and the date range is
  derived from the selected day count against a fixed time provider.
- **The date range binder**, as above.

Prior art to follow: `Tests/Unit/Dashboard.Client/Effects/MetricsHubEffectTests.cs` for
the client seam, the fakes and the `TheoryData` parameterisation style;
`Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs` for observability
unit tests; the repository testing rules for naming and for Shouldly assertions.
`Microsoft.Extensions.TimeProvider.Testing` is already referenced.

Which pills a family renders stays covered by the existing Playwright suite under
`Tests/E2E/Dashboard/`. Those tests should not need changes; if the real-time test
becomes flaky, that is a signal about the coalescing behaviour and not a reason to
lengthen its waits.

## Out of Scope

- **The grouping service.** Its seven methods stay as they are, for the reasons above.
- **The page markup below the control header.** Headline figures, chart types, event
  tables and sorting stay per page.
- **The overview page.** It has the time pill but no breakdown and is left alone.
- **Renaming the voice event-kind enum**, which is persisted by value and would pull the
  voice channel server into a dashboard change.
- **A Blazor component testing library.** Not added.
- **The dashboard's live connection** — its retry policy, its initial-start loop, its
  catch-up after an outage and its status indicator. That is candidate 11, and it waits
  on this change and on candidate 2. It was surveyed as a shared Blazor client library;
  `docs/adr/0008-the-two-browser-clients-stay-separate.md` records why that half was
  dropped.
- **A debounce.** There is none today and none is added; coalescing achieves the same
  reduction in server work without adding lag.
- **Route shapes and query parameter names.** Unchanged, so no client or endpoint
  contract moves.
- **Reporting why a page load failed.** The page-load effect swallows the reason today
  and continues to.

## Further Notes

This change is sequenced **before** candidate 11, the dashboard's live connection. They
contact in three places: both rewrite the live-update effect's subscription block and
its test file, and candidate 11's catch-up reloads through the page-load effect, which
this change turns from eleven injected stores into a walk of the family table. Running
this first shrinks the subscription block from roughly one hundred and twenty lines to
thirty and gives catch-up one call to make instead of nineteen. This change has no
dependency on candidate 2 and can start immediately.

It touches no file that candidates 1, 10 or 12 touch. Those are all on the publishing
side of observability; this is entirely on the reading side.

The comment in the API service that documents the aggregation default bug is deleted
rather than moved. It exists because two call sites could disagree about a default, and
after this change there is one call site. The regression test that guards the same bug
stays and is generalised to every family.

The endpoint date defaulting is a small fix riding along with a client-side change. It
is included because the twenty-two copies are in the same fan-out this change exists to
remove, and because the time provider they should be using is already injected into the
service sitting behind them.
