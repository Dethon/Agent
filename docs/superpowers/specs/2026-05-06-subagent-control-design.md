# Subagent Control — Design Spec

**Date:** 2026-05-06
**Branch:** `subagent-control`
**Status:** Approved for implementation planning

## Problem

The current `run_subagent` tool is fully blocking on the parent agent. Once invoked, the parent waits for the subagent to finish and gets back only a final text result. There is:

- No way to dispatch and continue (the parent's turn stalls until the subagent ends).
- No way to cancel a running subagent — neither the parent nor the user can stop one mid-flight.
- No way to inspect partial progress or stream intermediate results.

The only existing safety net is a profile-level `MaxExecutionSeconds` upper bound.

## Goals

- Let the parent agent fan out work asynchronously: start a subagent, get a handle back immediately, do other things, then collect the result later.
- Let the parent and human user cancel a running subagent.
- Let the parent and human user inspect what a running subagent has done so far.
- Notify the parent proactively when a backgrounded subagent finishes while the parent is idle.
- Preserve the existing one-call blocking behavior unchanged for parents that don't need any of this.

## Non-goals (v1)

- No persistence of subagent state across agent process restarts. Sessions are bound to the parent thread's in-memory lifetime.
- No live token streaming. Per-turn snapshots are the granularity for partial visibility.
- No new dashboard surface. The existing observability dashboard receives metric events for free; no dedicated subagent page in v1.
- No human UI on the ServiceBus channel. ServiceBus stays auto-approval, log-only.
- No recursion. Subagents still cannot enable the `subagents` feature.

## High-level decisions

| Decision | Choice |
|---|---|
| Tool surface shape | Single `run_subagent` with `run_in_background` flag (default `false`); helper tools for async control |
| Partial visibility granularity | Per-turn snapshots |
| Human surface | Inline cards in WebChat (SignalR) and Telegram chat threads only |
| Subagent lifecycle | Bound to the parent thread; in-memory; no Redis persistence |
| Completion notification model | Always push: completed backgrounded subagents trigger a fresh parent turn when the parent is idle |
| Silent backgrounded subagents | Supported via `silent: bool = false` on `run_subagent` — runs in the background but skips the chat card |

## Architecture

### `SubAgentSession`

One per running subagent. Owns:
- The `DisposableAgent` instance produced by the existing `featureConfig.SubAgentFactory(profile)`.
- A `CancellationTokenSource` used for cancel propagation.
- A bounded list of per-turn snapshots (cap: 50; oldest non-terminal snapshots dropped if exceeded).
- A `TaskCompletionSource<JsonNode>` for the final result.
- An atomic `terminalState` field set via `Interlocked.CompareExchange` so cancel-vs-natural-completion races resolve to exactly one terminal state.
- Attribution: `cancelled_by` ∈ `{ "parent", "user", "system" }` and an optional `error: { code, message }`.

### `SubAgentSessionManager`

Per-thread registry of `SubAgentSession`. One instance per parent agent thread, registered in the agent's per-thread DI scope (matching how other per-thread services are wired today). Responsibilities:

- `Start(profile, prompt, mode, silent) → handle` — creates the session, kicks off `RunAsync` on a background task, returns the new handle (short ULID-ish string).
- `Get(handle)`, `Cancel(handle, source)`, `List()`, `Release(handle)`.
- Tracks `isParentTurnActive: bool` and a `pendingWakeBuffer: Set<handle>` for the push flow (see below).
- Disposes all sessions when the parent thread is torn down (existing thread-disposal hook). Each disposal is a system-attributed cancel with code `ThreadClosed`.
- Enforces concurrency cap (8 concurrent sessions per thread, see Limits).

The Domain layer sees the manager through a new interface `ISubAgentSessions` so the Domain tools call it without crossing layer boundaries.

### Layer placement

- Domain: `ISubAgentSessions` interface, all tool definitions (`SubAgentRunTool` extended; new `SubAgentCheckTool`, `SubAgentWaitTool`, `SubAgentCancelTool`, `SubAgentListTool`, `SubAgentReleaseTool`), DTOs for snapshots and session state.
- Agent: `SubAgentSessionManager`, per-thread DI wiring, hook into thread disposal, registration of `ISubAgentSessions`.
- Infrastructure: snapshot recorder integration with the existing `Microsoft.Extensions.AI` chat loop (lives next to `McpAgent`).
- Channel servers (`McpChannelSignalR`, `McpChannelTelegram`): new outbound tools `subagent_announce`, `subagent_update` and inbound notification `subagent/cancel_requested`.

### Per-thread scoping

`featureConfig` is extended with the parent thread/session id alongside `UserId`. The Domain tools use this to resolve the correct manager scope. Handles from one thread are not visible from another — `subagent_check` on a foreign handle returns `NotFound` (not "Forbidden") so handle existence does not leak across threads.

## Parent agent tool surface

All tools registered under `domain__subagents__*`.

### `run_subagent` (extended)

```
run_subagent(
  subagent_id: string,
  prompt: string,
  run_in_background: bool = false,
  silent: bool = false
)
```

- `run_in_background=false` (default): identical to current behavior. Blocks, returns `{ status: "completed", result: string }`.
- `run_in_background=true`: returns immediately with `{ status: "started", handle: string, subagent_id: string }`.
- `silent=true` is only meaningful with `run_in_background=true`: suppresses the chat card. Push notifications on completion still fire.
- Existing `MaxExecutionSeconds` profile cap applies in both modes.

### `subagent_check(handle)`

Non-consuming. Multiple calls return the same data. Result remains in the registry until the thread closes or `subagent_release` is called.

```
{
  status: "running" | "completed" | "failed" | "cancelled",
  handle, subagent_id,
  started_at, elapsed_seconds,
  turns: [
    {
      index: 0,
      assistant_text: "...",
      tool_calls: [{ name, args_summary }],
      tool_results: [{ name, ok: bool, summary }],
      started_at, completed_at
    }
  ],
  // present only on terminal states:
  result?: string,
  error?: { code, message },
  cancelled_by?: "parent" | "user" | "system"
}
```

`args_summary` truncates each arg value to ~200 chars; `summary` truncates each tool result body to ~500 chars.

### `subagent_wait(handles, mode, timeout_seconds)`

```
subagent_wait(
  handles: string[],
  mode: "any" | "all" = "all",
  timeout_seconds: int = 60
)
→ { completed: string[], still_running: string[] }
```

Blocks the parent's tool call until the named handles reach a terminal state per `mode`, or the timeout elapses. Timeout is not an error — it just returns the partition. If the parent's own turn is cancelled while inside `subagent_wait`, the wait returns via `OperationCanceledException`; the underlying subagents are *not* cancelled (decoupled by design).

### `subagent_cancel(handle)`

Best-effort. Returns immediately with `{ status: "cancelling" }`. Subsequent `subagent_check` reaches `cancelled` once the underlying loop observes cancellation. Idempotent: cancelling an already-terminal session returns `{ status: "<current_terminal_state>" }`.

### `subagent_list()`

Returns `[{ handle, subagent_id, status, elapsed_seconds, started_at }]` for all sessions in this thread (running and terminal). Useful when the parent wakes from a push and wants to enumerate.

### `subagent_release(handle)`

Drops a terminal session from the registry. Calling on a still-running session returns an error (`InvalidOperation`). Optional convenience — sessions clean up at thread close anyway.

## Per-turn snapshots

A "turn" = one cycle of *(assistant generates response → if response contains tool calls, run them → feed results back to model)*. This maps onto each iteration of the existing `Microsoft.Extensions.AI` tool-call loop. The snapshot recorder hooks into the chat client's message pipeline (no new streaming hook on `DisposableAgent.RunAsync` is added — we read the message list the agent already builds) and appends one snapshot per completed turn.

Snapshots are in-memory only, on `SubAgentSession`. They are not persisted. Each snapshot append also publishes a `MetricEvent` (subagent turn completed) via the existing `IMetricsPublisher`, giving the observability dashboard coverage without any new dashboard work.

## Human chat surface

Cards appear only for backgrounded subagents with `silent=false`. Blocking `run_subagent` calls happen inside the parent's "thinking" interval and do not get a card.

### Card contents

- Header: subagent ID + first ~80 chars of prompt
- Status: `Running` / `Completed` / `Failed` / `Cancelled` + elapsed time
- One button: `Cancel` (only while `Running`; removed on terminal)

### Channel protocol additions (mirror the existing approval pattern)

Outbound (agent → channel):

- `subagent_announce(handle, subagent_id, prompt_summary)` — posts a new card.
- `subagent_update(handle, status, terminal_summary?)` — mutates the card in place. `terminal_summary` is the first ~200 chars of `result` or `error.message`. On terminal states, the cancel button is removed.

Inbound (channel → agent):

- `subagent/cancel_requested(handle)` notification — fired when the user clicks Cancel. Routed to `SubAgentSessionManager.Cancel(handle, source: "user")` for the originating thread.

### WebChat (SignalR)

New card component in `WebChat.Client`, driven by the existing Redux store. Card subscribes to a `subagent` SignalR event for in-place updates. Cancel button invokes a hub method that the channel server forwards to the agent as the `subagent/cancel_requested` notification.

### Telegram

Uses the existing inline-keyboard plumbing. `subagent_announce` sends a message with one inline button (`callback_data: "subagent_cancel:{handle}"`). `subagent_update` calls `editMessageText` + `editMessageReplyMarkup` (button removed once terminal). The cancel callback resolves to the notification.

### ServiceBus

No protocol additions. Confirmed log-only, auto-approval flow remains unchanged.

### Why not reuse `request_approval`

Approval semantics are "wait for the user before doing X". Subagent cards have inverted semantics — the work is already running and the button stops it. Overloading approval would muddle both contracts; the subagent protocol is a small, focused addition.

### Result delivery

The card shows a *summary* on completion. The actual result still flows back to the parent via `subagent_check` / push and is incorporated into the parent's next assistant message — not posted as a separate user-facing chat bubble. This avoids "two messages for one answer" confusion.

## Push completion flow

**Goal:** when a backgrounded subagent finishes while the parent has no active turn, wake the parent so it can react. While the parent *is* mid-turn, completions queue silently and feed `subagent_check` / `subagent_wait`.

### Per-thread state on `SubAgentSessionManager`

- `isParentTurnActive: bool` — flipped `true` on parent turn start, flipped `false` immediately after the parent emits its final assistant text (no grace window).
- `pendingWakeBuffer: Set<handle>` — completions waiting to trigger a wake.

### Completion path (per session)

1. Subagent reaches terminal state (completed / failed / cancelled).
2. Manager updates the session, fires `subagent_update` (card → terminal). Skip the card update if `silent=true`.
3. **If `cancelled_by == "parent"`:** stop here. No wake — the parent already knows; it cancelled the session itself.
4. Otherwise add the handle to `pendingWakeBuffer` and start a 250 ms debounce timer. New completions inside the window join the same buffer.
5. When the debounce timer fires:
   - If `isParentTurnActive == true`: do nothing. The active turn will consume completions via `check`/`wait` if it cares.
   - If `isParentTurnActive == false`: emit one wake event carrying the buffered handles; clear the buffer.

### Wake event

A synthetic system message injected directly into the parent thread's input queue, reusing the same code path channel inbound messages take (`ChatMonitor` or equivalent dispatcher). Message body:

```
[system] Background subagents have completed and are awaiting your attention:
  - handle=01HZX..., subagent=researcher, status=completed
  - handle=01HZY..., subagent=scraper,    status=cancelled (by user)
Use subagent_check on each handle to retrieve results.
```

Full results are *not* inlined — the parent calls `subagent_check` per handle. This avoids bloating the synthetic message and lets the parent skip results that have become stale.

### Multiple back-to-back wakes

If a turn ends and there are still buffered completions, the next wake fires immediately. The parent may have several short consecutive turns — that's intended; it's catching up.

### Channel involvement

None. Push is intra-process. The wake injects directly into the parent thread's message queue. The user observes an extra assistant message appear unprompted in their chat — an intended UX change ("the agent is following up on its own work"). Cards have already flipped to terminal state by the time the parent's follow-up assistant message lands, so the user has visual context for the unprompted message.

## Cancellation semantics

### Sources

| Source | Attribution |
|---|---|
| `subagent_cancel(handle)` from parent | `cancelled_by="parent"` |
| Chat-card Cancel button | `cancelled_by="user"` |
| `MaxExecutionSeconds` elapsed | `cancelled_by="system"`, `error.code="Timeout"` |
| Parent thread teardown / process shutdown | `cancelled_by="system"`, `error.code="ThreadClosed"` |

All routes converge on `SubAgentSession.Cancel(reason)`, which cancels the session's `CancellationTokenSource`.

### Propagation

The session's CTS is threaded into `DisposableAgent.RunAsync(messages, cancellationToken)`. Cancellation is observed:

- Between tool-loop iterations → loop exits cleanly with `OperationCanceledException`.
- Inside an HTTP/LLM call → underlying `HttpClient` cancels the request.
- Inside a tool call → the tool's own `CancellationToken` (which it must respect; existing domain/MCP tools already do).

### Best-effort, not instantaneous

`subagent_cancel` returns immediately with `{ status: "cancelling" }`. Terminal `cancelled` state is reached "soon" — bounded by the slowest non-cancellable tool currently running. Worst case: the tool finishes, then the loop exits. We accept this; forced thread aborts are out of scope.

### Snapshot preservation

Turn snapshots accumulated before cancel are kept. `subagent_check` after cancel returns `status: "cancelled"`, full `turns[]`, `cancelled_by`, and `error: { code: "Cancelled" | "Timeout" | "ThreadClosed", message }`. No `result` field.

### Race resolution

The session has a single `terminalState` field set via `Interlocked.CompareExchange`. Whoever wins the race (cancel or natural completion) sets the state; the loser becomes a no-op.

### Idempotency

Calling `subagent_cancel` on an already-terminal session returns `{ status: "<current_terminal_state>" }` — no error, no extra work. The user's Cancel-button-after-terminal callback is similarly tolerated (button is hidden post-terminal anyway).

### `MaxExecutionSeconds`

Preserved exactly as today, routed through the new CTS path. The profile-level timer is started on session creation. For backgrounded subagents this is the only thing that bounds runaway sessions — there is no parent-turn timeout, since the parent's turn ended already.

### Decoupling: parent's `subagent_wait` cancel does not cascade

If the parent's own turn is cancelled while inside `subagent_wait`, the wait exits via `OperationCanceledException` but the waited-on subagents keep running independently. They will emit pushes later as normal. Backgrounded subagents are explicitly long-lived and shouldn't die because the parent's turn was abandoned.

## Error envelopes

All errors use the existing `ToolError` shape.

| Situation | Tool | Code | Retryable |
|---|---|---|---|
| Unknown subagent ID | `run_subagent` | `NotFound` | no |
| `SubAgentFactory` not configured | `run_subagent` | `Unavailable` | no |
| Unknown handle | `subagent_check`/`wait`/`cancel`/`release` | `NotFound` | no |
| Handle from a different thread | same | `NotFound` | no (deliberate — don't leak existence) |
| Subagent threw mid-run | reflected in `subagent_check` (`status: "failed"`, `error.code: InternalError`) | — | no |
| `MaxExecutionSeconds` hit | reflected in `subagent_check` (`status: "cancelled"`, `error.code: Timeout`) | — | yes (parent could retry with smaller scope) |
| `subagent_wait` timeout | returns `{ completed, still_running }` — not an error | — | n/a |
| Concurrency cap exceeded | `run_subagent` | `Unavailable` (`"Too many active subagents in this thread (8 max). Cancel or wait on existing handles first."`) | yes |
| `subagent_release` on running session | `subagent_release` | `InvalidOperation` | no |
| Channel call to update card fails | logged + retried once; if still failing, dropped silently — never blocks the subagent itself | — | n/a |

## Limits (v1)

- Max concurrent sessions per thread: **8**.
- Max accumulated turn snapshots per session: **50**. Oldest non-terminal snapshots dropped beyond that. Final `result` is unaffected.
- Wake debounce window: **250 ms**.
- Default `subagent_wait` timeout: **60 s**.

These are constants in v1. Promote to config if real usage demands.

## Concurrency & thread safety

- `SubAgentSessionManager` is per-thread; one instance per parent agent thread.
- Internal collections use `ConcurrentDictionary<handle, SubAgentSession>`.
- Terminal-state race: `Interlocked.CompareExchange` on `SubAgentSession.terminalState`.
- Wake-buffer flush + `isParentTurnActive` flip: guarded by a single `lock`. Both are O(1); contention is negligible.

## Backwards compatibility

- `run_subagent(subagent_id, prompt)` with no extra params behaves byte-identically to today.
- New parameters (`run_in_background`, `silent`) default to `false`. Existing tests, telemetry, and prompts continue to work unchanged.
- `SubAgentPrompt.SystemPrompt` gains new sections describing the background flag, `silent`, and the helper tools. Existing guidance is preserved.

## Observability

Each of the following emits a `MetricEvent` via the existing `IMetricsPublisher`:

- Subagent session start (with `subagent_id`, `mode = "blocking" | "background"`, `silent`)
- Per-turn snapshot append
- Terminal transition (with terminal state and `cancelled_by` if cancelled)

The dashboard's existing tool/agent analytics absorb these for free. No new dashboard page in v1.

## Test strategy (TDD)

Per the project's CLAUDE.md: RED → GREEN → REVIEW per triplet, commit after each triplet.

### Unit (`Tests/Unit/`)

- `SubAgentSession` terminal-state race (`Interlocked.CompareExchange` invariants).
- `SubAgentSessionManager` per-thread isolation (handle from thread A invisible to thread B).
- Wake-buffer debounce coalescing (2 completions inside 250 ms → 1 wake).
- `cancelled_by="parent"` suppresses the wake; `"user"` and `"system"` do not.
- Tool-surface error envelopes (NotFound, Unavailable, max-session limit, InvalidOperation).
- Snapshot recorder turn-boundary detection.
- Snapshot drop policy at the 50-snapshot cap.

### Integration (`Tests/Integration/`)

- End-to-end `run_subagent(run_in_background=true)` → `subagent_check` returns running → simulate completion → `subagent_check` returns completed result.
- `subagent_wait(mode="all")` blocks until all handles terminal; `mode="any"` returns on first.
- Cancel-via-user flow: post `subagent/cancel_requested` notification → session ends with `cancelled_by="user"` → wake fires → parent receives synthetic system message.
- Channel card lifecycle: announce → update → terminal — for both SignalR and Telegram channel servers.
- `silent=true` produces no card but still pushes on completion.
- Parent thread close cancels all in-flight sessions and produces no wake (sessions cancel with `cancelled_by="system"`).

### E2E (`Tests/E2E/WebChat/`)

- Card appears in WebChat, Cancel button click cancels the subagent, card flips to "Cancelled by you", parent's follow-up assistant message reflects the cancellation.

### Suggested triplet sequence

1. `SubAgentSession` core (state machine, snapshots, cancel).
2. `SubAgentSessionManager` (per-thread registry, max-session enforcement).
3. `run_subagent` background-mode return.
4. `subagent_check`, `subagent_release`.
5. `subagent_cancel` + cancellation propagation.
6. `subagent_wait` (any/all/timeout).
7. `subagent_list`.
8. Wake buffer + push trigger (intra-process injection into thread message queue).
9. Channel protocol — SignalR side (server + WebChat.Client card component).
10. Channel protocol — Telegram side (announce/update + callback handler).
11. `SubAgentPrompt` updates.
12. Metrics events for snapshot/terminal transitions.

## Out of scope (deferred)

- Redis-backed persistence of subagent state across process restarts.
- Live token streaming for partial visibility.
- Dashboard page listing subagents across users.
- ServiceBus channel UI for subagent control.
- Recursive subagents (subagents spawning subagents).
- Configurable concurrency / snapshot / debounce limits (currently constants).
