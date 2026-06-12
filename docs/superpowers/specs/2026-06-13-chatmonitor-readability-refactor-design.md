# ChatMonitor Readability Refactor — Design

**Date:** 2026-06-13
**Status:** Approved
**Type:** Behavior-preserving refactor (no functional change)

## Problem

`Domain/Monitor/ChatMonitor.cs` has grown to 414 lines mixing four concerns:

1. **Pipeline orchestration** (`Monitor`): merge channel streams, group by `(ConversationId, AgentId)`.
2. **Delivery-target resolution & turn announce** (`ResolveDeliveryTargetsAsync`, `AnnounceTurnStartAsync`): ~100 lines of static methods with heavy comment load (fan-out minting, attach-only ordering, shared-conversation anchoring).
3. **Turn processing** (`ProcessChatThread`, ~137 lines): group-anchor binding plus a giant per-message lambda (command parsing, per-message target resolution, announce, user-message building, memory recall, warmup await, streaming run with a schedule-metric completion fold).
4. **Reply delivery & mapping** (`DeliverUpdateAsync` / `DeliverToTargetAsync` / `MapResponseUpdate`) and metric-event building (`BuildScheduleEvent`).

The public static helpers and the nested `DeliveryTarget` record are used only by ChatMonitor itself and its tests — no other production code depends on them, so they are free to move.

## Design

Split into focused collaborators in `Domain/Monitor/`. ChatMonitor keeps its constructor signature (no DI or test-construction churn) and builds the collaborators internally as fields from its existing parameters.

### New files

**`DeliveryTarget.cs`** — the nested `ChatMonitor.DeliveryTarget` readonly record struct promoted to top-level; it is shared by resolver, dispatcher, and monitor.

**`DeliveryTargetResolver.cs`** — instance class, ctor `(IReadOnlyList<IChannelConnection> channels, ILogger logger)`:

- `ResolveAsync(message, originChannel, ct)` — today's `ResolveDeliveryTargetsAsync` unchanged; channels and logger become fields instead of threaded parameters (the optional-logger parameter disappears).
- `AnnounceTurnStartAsync(targets, message, skipMinted, ct)` — instance method; the other half of the target lifecycle.
- `BuildConversationContext(message, targets)` — stays static (pure, no dependencies).

**`ReplyDispatcher.cs`** — instance class, ctor `(IMetricsPublisher metricsPublisher, ILogger logger)`:

- `DeliverUpdateAsync(update, targets, ct)` → `bool` (delivered content), private `DeliverToTargetAsync`, private static `MapResponseUpdate`. Exactly today's logic including per-target failure isolation and error-event publishing.

### Moved

**`BuildScheduleEvent`** becomes a static factory on the DTO: `ScheduleExecutionEvent.FromMessage(message, durationMs, success, error)` → returns `null` for non-schedule origins. Removes the metrics-factory concern from the monitor.

### ChatMonitor.cs after (~170 lines, orchestration only)

- Constructor unchanged. Fields: `new DeliveryTargetResolver(channels, logger)`, `new ReplyDispatcher(metricsPublisher, logger)` (log category stays `Domain.Monitor.ChatMonitor`).
- `Monitor()` unchanged.
- `ProcessChatThread` decomposes into named private methods:
  - group-anchor resolution (first-message targets → approval handler → persistence key),
  - `RunTurnAsync` replacing the giant per-message lambda (command handling, per-message target resolution, announce for agent-initiated turns, user-message building + memory recall, the streaming run with its schedule-metric completion fold),
  - delivery loop with the FirstReply latency publish extracted.
- The anonymous `(Update, Targets, Tracker)` tuple becomes a small private record.
- All existing "why" comments are preserved at their new homes.

## Tests

Existing tests keep pinning the same behavior, re-pointed at new homes:

- `ChatMonitorDeliveryTests` → `DeliveryTargetResolverTests` (instance `ResolveAsync`).
- `ChatMonitorAnnounceTests` → announce tests on the resolver.
- `ChatMonitorConversationContextTests` → `DeliveryTargetResolver.BuildConversationContext`.
- `ChatMonitorScheduleMetricsTests` → `ScheduleExecutionEvent.FromMessage`.
- `MonitorTests` / `ChatMonitorPersistenceKeyTests` (full-pipeline, construct ChatMonitor directly) — unchanged apart from `DeliveryTarget` and `BuildScheduleEvent` references.
- Stale comment in `SendReplyToolTests` ("see ChatMonitor.MapResponseUpdate") updated to the new home.

## Invariants (must not change)

- Target resolution semantics: attach-only channels ordered last, shared-id anchoring across fan-out, skip-on-mint-failure.
- Announce semantics: channel-agnostic `create_conversation` call, skipMinted for group-opening messages, failures never abort the turn.
- Anchor semantics: persistence key and approval routing bound to `targets[0]`; per-message reply delivery routed to each message's own origin.
- Delivery semantics: per-target failure isolation, error-event publishing, `StreamComplete` excluded from delivered-content tracking.
- Metrics: FirstReply latency attribution to `replyTargets[0]`, schedule-execution event only for schedule origins.

## Verification

Full existing unit suite green before and after (`Category!=E2E`; the ~148 pre-existing DockerUnavailableException failures in this WSL environment are the baseline, not regressions).
