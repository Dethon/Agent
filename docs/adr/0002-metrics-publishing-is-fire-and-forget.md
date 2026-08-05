# 0002 — Metrics publishing is fire-and-forget

Status: accepted
Date: 2026-08-03

## Context

`IMetricsPublisher.PublishAsync` returned a `Task` that could fail, so about fifty
call sites each had to decide what a failed metric publish meant. Nine implemented
a guard — five named helpers with four different signatures, four inline
`try`/`catch` blocks — and the rest restated the rule in a comment or ignored it.

The guarantee lived in the host's registration, not in the type. The Agent host
registered `BufferedMetricsPublisher`, whose `PublishAsync` only does a `TryWrite`
and cannot throw, so all nine guards were dead code there. `McpChannelVoice`
registered `RedisMetricsPublisher` directly, so a publish was a live Redis round
trip. Two unguarded sites in the voice host were live defects: a Redis blip
discarded a good transcript (`WyomingSatelliteHost.cs:507`) and killed the
conversation task (`:548`).

## Decision

`IMetricsPublisher` is `void Publish(MetricEvent)`. No `Task`, no
`CancellationToken`. The guarantee is in the signature: a caller cannot await a
publish, cannot catch it, and cannot get it wrong.

Transports implement a separate `IMetricSink` (`Task SendAsync`, may throw), which
`BufferedMetricsPublisher` drains on a background reader. The sink interface lives
in `Infrastructure/Metrics/` because Domain never consumes it.

Hosts wire the whole surface with `AddMetricsPublishing(serviceName)`, which
registers the sink, the buffered publisher and the heartbeat together.

## Considered options

**A `BestEffort` decorator** over the existing `PublishAsync`. Removes the
duplication but leaves a `Task` at every call site that a caller can still await,
catch, or forget to register the decorator for. The registration is exactly what
went wrong in the voice host.

**Fix the voice registration only.** Cheapest, and the guarantee stays in a DI
line nobody reads.

**Document the guarantee and require each adapter to honour it.** This is what the
codebase already had, expressed as thirty-five comments.

## Consequences

- Nothing can observe a publish completing. Nothing did: no caller read the result,
  and `HeartbeatService` awaited only inside its own timer loop.
- A cancelled turn now records its metrics. The buffered publisher used to drop any
  event whose token was already cancelled, silently losing the schedule-execution
  event and the first-reply latency of every cancelled turn.
- Methods that existed only to guard-and-publish are gone, and sync-ness cascades
  outward until the first method that still awaits real work.
- `IMetricSink` has one adapter. Per ADR 0001, adapter count is not a reason to
  remove it.
- A reviewer will read a non-async publishing interface as a mistake. It is not.
