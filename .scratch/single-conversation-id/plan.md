# One Turn, One Conversation Id Implementation Plan

**Goal:** Three names exist for one concept, so a scheduled turn's telemetry splits across two ids and the dashboard shows half a turn per row. Resolve one id per turn and use it everywhere.

**Why now:** `ChatMonitor` builds the agent from `agentKey` (`:71`), restores history under `anchors.PersistenceKey` (`:73`), and attributes first-reply latency to `targets[0].ConversationId` (`:252`). For a scheduled fire that mints a WebChat conversation, `LatencyStage.FirstReply` lands on the minted id while `ToolCallEvent`, `LatencyStage.ToolExec`, `LlmFirstToken`, `LlmTotal` and `HistoryStore` land on the synthetic scheduling id. The comment at `:248-251` reasons carefully about attribution for exactly one event and the same reasoning is silently not applied to the other six.

**Source:** architecture review 2026-08-02, candidate 5.

## Global Constraints

- TDD per task. `dotnet test Tests/Unit --nologo -v q`.
- `.cs` files have no trailing newline; pre-commit runs `dotnet format` and re-stages whole files.
- File-scoped namespaces, primary constructors, no XML doc comments.
- Domain never imports Infrastructure/Agent.
- Commit after each task.

## Locked decisions

**One id, no carve-outs.** `ResolveGroupAnchorsAsync` computes `DeliveryConversationId` once and it drives every downstream use.

`agentKey.ConversationId` currently drives five things in `MultiAgentFactory`. All five move:

| use | site |
|---|---|
| OpenRouter `session_id` | `MultiAgentFactory.cs:105` |
| agent name | `:109` |
| `ToolApprovalChatClient` metric id | `:110` |
| `McpAgent._conversationId` | `:141` |
| history persistence key | `ChatMonitor.cs:73` (already the delivery id) |

**`ChannelToolApprovalHandler` is deleted.** Its two methods are one-line forwards to identically-named members on `IChannelConnection` with `conversationId` curried, and `ToolApprovalChatClient` already takes a `conversationId`. Carrying the string privately is what made the divergence between "where approvals go" and "what metrics are stamped with" invisible.

## Accepted tradeoff

`session_id` moves with everything else, and a scheduled fire mints a fresh delivery conversation each run. So **a scheduled agent's `session_id` changes every fire and each scheduled turn is a full OpenRouter prompt-cache miss.**

This was raised twice and accepted twice. The alternative — deriving `session_id` from the stable group key while everything observable used the single id — was offered and declined in favour of one concept with no exceptions. Recorded in memory as `scheduled-agents-accept-cache-miss` so a future session does not treat it as a regression.

If cache cost later becomes a real problem, reopen it as a deliberate tradeoff, not a bug.

## Tasks

1. **`GroupAnchors` gains `DeliveryConversationId`.** Resolved in `ResolveGroupAnchorsAsync` alongside the existing targets, approval handler and persistence key. Its fallback stays `first.Message.ConversationId` when there are no targets, matching today.
2. **Feed it to `agentFactory.Create`.** Failing test first: a scheduled fire that mints a WebChat target publishes `ToolCallEvent`, `LatencyStage.ToolExec`, `LlmFirstToken`, `LlmTotal` and `LatencyStage.FirstReply` all on the minted id. This test is the deliverable.
3. **Delete `ChannelToolApprovalHandler`.** `MultiAgentFactory` passes the delivery id to `ToolApprovalChatClient` directly; the DI lambda at `InjectorModule.cs:69-70` goes with it.
4. **Collapse the three names.** `agentKey`, `anchors.PersistenceKey` and `targets[0].ConversationId` should not all survive as separate concepts in `ChatMonitor`. Keep `AgentKey` as the grouping key it is; the delivery id is the single downstream identity.
5. **Update the comment at `ChatMonitor.cs:248-251`** — the reasoning it records now applies to every event, not one.

## Risks

- **Dashboard continuity.** Existing metric rows carry the old ids. Nothing migrates them; a scheduled conversation's history straddles the change. Worth confirming the dashboard tolerates that rather than erroring on a conversation whose events span two ids.
- **`HistoryStore` already keys on the delivery id** via `PersistenceKey`, per the comment at `ChatMonitor.cs:102-114` — so history is the one thing not changing. Do not "fix" it into the group key while unifying.
- Task 2's test needs a schedule fire with a null `ReplyTo` conversation id, which is the path that mints. `MonitorTests` has the fixtures; `Monitor_MultiTargetFanOut_*` shows the shape.
