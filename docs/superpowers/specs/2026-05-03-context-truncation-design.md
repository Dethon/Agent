# Context Truncation Design

**Date:** 2026-05-03
**Status:** Approved (pending spec review)
**Owner:** dethon

## Goal

Automatically truncate conversation context when a request would otherwise overflow the LLM's supported context window. The maximum token budget is configurable per agent in `appsettings.json`, with a global default fallback.

## Motivation

Long-running threads, large tool results, and accumulated memory context can grow the message list past the context window of the underlying model. Today the request would either be rejected by the provider or silently degrade. We want a deterministic, model-agnostic guard that drops the oldest non-essential messages before the request is sent.

## Non-goals

- Exact token counts per model. We use a chars/4 approximation with a 95% safety margin.
- LLM-based summarization of dropped messages.
- Per-message importance scoring or semantic retention beyond "preserve system + last user".
- Truncation of the system prompt.

## Configuration

Three new fields (global, per-agent, per-subagent), all `int?` (null = no truncation):

```jsonc
"openRouter": {
    "apiUrl": "...",
    "apiKey": "...",
    "maxContextTokens": 200000      // global default (NEW, optional)
},
"agents": [
    {
        "id": "jonas",
        "model": "z-ai/glm-5.1",
        "maxContextTokens": 1000000,  // per-agent override (NEW, optional)
        ...
    }
],
"subAgents": [
    {
        "id": "jonas-worker",
        "model": "z-ai/glm-5.1",
        "maxContextTokens": 200000,   // per-subagent override (NEW, optional)
        ...
    }
]
```

**Resolution rule:** `effective = agentDef.MaxContextTokens ?? settings.OpenRouter.MaxContextTokens`. When both are `null`, no truncation is performed.

**Safety margin:** A hard-coded constant `0.95` is applied internally. The truncator targets `floor(effective * 0.95)` tokens to absorb chars/4 imprecision and leave room for the response.

## Token Estimation

A pure helper computes an approximate token count without any external tokenizer.

```csharp
internal static int EstimateTokens(string text) => (text.Length + 3) / 4;
```

Per `ChatMessage`, sum estimates across all `Contents`:

| Content type | Estimated as |
|---|---|
| `TextContent` | `EstimateTokens(text)` |
| `TextReasoningContent` | `EstimateTokens(text)` |
| `FunctionCallContent` | `EstimateTokens(JsonSerializer.Serialize({Name, Arguments}))` |
| `FunctionResultContent` | `EstimateTokens(JsonSerializer.Serialize(Result))` |
| Any other content | Fixed overhead of `4` tokens |

A per-message structural overhead of `4` tokens is added to approximate role/structural framing the provider applies.

## Truncation Algorithm

**Pinned messages (never dropped):**

- All messages with role `System`
- The last message with role `User` in the list

**Algorithm:**

1. Compute total estimated tokens across all messages.
2. If `total <= floor(maxContextTokens * 0.95)` → no-op, forward as-is.
3. Otherwise, build a chronological "drop candidate" list of indices (non-pinned messages).
4. Group tool-call pairs: any `Assistant` message containing `FunctionCallContent` is grouped with all subsequent messages whose `FunctionResultContent.CallId` matches one of the assistant's call ids. The group is dropped atomically — never split.
5. Drop the oldest group (or single message), recompute total, repeat until under threshold OR no more candidates remain.
6. If still over after exhausting candidates: log a warning and forward anyway. Truncation is a best-effort guard, not a hard wall — let the provider reject if it must.

## Pipeline Placement

Existing chain:

```
ChatClientAgent  →  ToolApprovalChatClient : FunctionInvokingChatClient  →  OpenRouterChatClient
```

Truncation runs **inside `OpenRouterChatClient.GetStreamingResponseAsync`**, after the existing message transformation (sender/timestamp prefix and memory-context block are included in the token estimate) and before the call to `_client.GetStreamingResponseAsync`.

This placement guarantees the truncator runs on **every** call into the inner client, including the iterations the `FunctionInvokingChatClient` middleware inserts between tool calls. Each hop sees the freshly-grown message list and gets a fresh truncation pass.

### `OpenRouterChatClient` constructor change

```csharp
public OpenRouterChatClient(
    string endpoint,
    string apiKey,
    string model,
    int? maxContextTokens = null,           // NEW
    IMetricsPublisher? metricsPublisher = null)
```

### Internal helper

A new pure static class `MessageTruncator` in `Infrastructure.Agents.ChatClients`:

```csharp
internal static class MessageTruncator
{
    private const double SafetyRatio = 0.95;
    private const int PerMessageOverhead = 4;
    private const int OtherContentOverhead = 4;

    public static int EstimateTokens(string text);
    public static int EstimateMessageTokens(ChatMessage message);
    public static IReadOnlyList<ChatMessage> Truncate(
        IReadOnlyList<ChatMessage> messages,
        int? maxContextTokens,
        out int droppedCount,
        out int tokensBefore,
        out int tokensAfter);
}
```

## DI Wiring

Both `MultiAgentFactory` (`Infrastructure/Agents/`) and `SubAgentModule` (`Agent/Modules/`) construct `OpenRouterChatClient` per agent. They each:

1. Read `AgentSettings.OpenRouter.MaxContextTokens` (global default).
2. Read the per-agent / per-subagent `MaxContextTokens`.
3. Compute `effective = agentDef.MaxContextTokens ?? settings.OpenRouter.MaxContextTokens`.
4. Pass `effective` into the `OpenRouterChatClient` constructor.

## Metrics

### New DTO — `Domain/DTOs/Metrics/ContextTruncationEvent.cs`

```csharp
public record ContextTruncationEvent : MetricEvent
{
    public required string Sender { get; init; }
    public required string Model { get; init; }
    public required int DroppedMessages { get; init; }
    public required int EstimatedTokensBefore { get; init; }
    public required int EstimatedTokensAfter { get; init; }
    public required int MaxContextTokens { get; init; }
}
```

### Publishing

Inside `OpenRouterChatClient`, when `Truncate` reports `droppedCount >= 1`, publish via the already-injected `_metricsPublisher.PublishAsync(...)` — same fire-and-forget pattern as the existing `TokenUsageEvent`.

### Enums — extend existing `TokenMetric`

Append three values to `Domain/DTOs/Metrics/Enums/TokenMetric.cs`:

- `TruncationCount` — number of truncation events
- `MessagesDropped` — total messages dropped (sum)
- `TokensTrimmed` — total tokens trimmed (sum of `Before − After`)

`TokenDimension` (`Sender`, `Model`) is unchanged.

### Collector (`MetricsCollectorService`)

A new handler reacts to `ContextTruncationEvent`:

- Increment per-dimension hashes in Redis: `metrics:truncations:by-sender`, `metrics:truncations:by-model`.
- Push to a sorted set timeline: `metrics:truncations:timeline` (score = unix ms).
- Forward the event to the SignalR hub for live dashboard updates.

### Query (`MetricsQueryService`)

Extend the existing token breakdown query so it branches on the selected `TokenMetric`. When the value is one of the new truncation metrics, query the truncation Redis keys instead of the token-usage keys, returning the same `Breakdown` shape.

### REST API

`MetricsApiEndpoints.cs` — no new endpoint; the existing token endpoint accepts the new `TokenMetric` values transparently.

## Dashboard

Reuse `Dashboard.Client/Pages/Tokens.razor`. No new page, no new nav entry.

- **Metric pill:** Picks up the three new `TokenMetric` values automatically.
- **Dimension pill:** Unchanged (`Sender`, `Model`).
- **KPI row:** Add one new `KpiCard` for "Truncations (last N days)" alongside Input/Output/Cost. Always visible regardless of the selected metric.
- **Chart:** No structural change — `DynamicChart` re-renders against `_state.Breakdown` for whichever metric is selected.
- **Recent events table:** Unchanged. Truncation events are not listed individually; the KPI card surfaces the volume.

## Edge Cases

| Case | Behavior |
|---|---|
| Both global and per-agent `MaxContextTokens` are `null` | Truncation disabled; messages forwarded unchanged. |
| Total tokens within budget | No-op; no metric event. |
| Pinned messages alone exceed the budget | Log a warning; forward anyway. Provider may reject. |
| `maxContextTokens` is set to `0` or negative | Treat as `null` (disabled). Logged once at startup. |
| Empty message list | No-op. |

## Testing (TDD)

All work follows Red-Green-Refactor per `.claude/rules/tdd.md`.

### Pure unit tests — `Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs`

- `EstimateTokens` returns ceiling of chars/4
- `EstimateMessageTokens` sums across `TextContent`, `FunctionCallContent`, `FunctionResultContent`, plus per-message overhead
- `Truncate` no-ops when total tokens ≤ 95% of max
- `Truncate` returns the original list when `maxTokens` is null
- `Truncate` drops oldest non-pinned messages first
- `Truncate` always preserves `System` messages
- `Truncate` always preserves the last `User` message
- `Truncate` drops a tool-call assistant message together with its matching tool-result messages (matched by `CallId`)
- `Truncate` never splits a tool-call/result pair
- `Truncate` reports accurate `droppedCount`, `tokensBefore`, `tokensAfter`
- `Truncate` stops dropping once under threshold (no over-trim)
- `Truncate` returns the original list when no candidates remain but it's still over (caller handles)

### `OpenRouterChatClient` integration tests — `OpenRouterChatClientTruncationTests.cs`

- Forwards messages unchanged when `maxContextTokens` is null
- Forwards truncated messages when total exceeds 95% of the limit
- Publishes `ContextTruncationEvent` with correct sender/model/counts when truncation occurs
- Does **not** publish `ContextTruncationEvent` when no messages are dropped
- Truncation runs **after** the sender/timestamp/memory-context transformation (asserts the prefix tokens are counted)

### DI / wiring tests — `Tests/Unit/Agent/`

- `MultiAgentFactory` resolves effective limit = per-agent override when set
- `MultiAgentFactory` falls back to global default when override is null
- Effective limit is `null` when both are absent
- Same two cases for `SubAgentModule`

### Metrics pipeline tests — `Tests/Unit/Observability/`

- `MetricsCollectorService` increments truncation counters in Redis on `ContextTruncationEvent`
- `MetricsCollectorService` adds an entry to the truncations timeline sorted set
- `MetricsCollectorService` forwards the event to the SignalR hub
- `MetricsQueryService` returns correct grouped values for the new `TokenMetric` enum values

## Files Touched

| File | Change |
|---|---|
| `Agent/appsettings.json` | Add `openRouter.maxContextTokens` (placeholder/null) |
| `Agent/appsettings.Local.json` | Same |
| `Agent/Settings/AgentSettings.cs` | `OpenRouterConfiguration.MaxContextTokens` field |
| `Domain/DTOs/AgentDefinition.cs` | `MaxContextTokens` field |
| `Domain/DTOs/SubAgentDefinition.cs` | `MaxContextTokens` field |
| `Infrastructure/Agents/ChatClients/MessageTruncator.cs` | New file (pure helper) |
| `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` | Constructor param, truncation call, metric publish |
| `Infrastructure/Agents/MultiAgentFactory.cs` | Resolve effective limit, pass to chat client |
| `Agent/Modules/SubAgentModule.cs` | Same |
| `Domain/DTOs/Metrics/ContextTruncationEvent.cs` | New DTO |
| `Domain/DTOs/Metrics/Enums/TokenMetric.cs` | Add `TruncationCount`, `MessagesDropped`, `TokensTrimmed` |
| `Observability/Services/MetricsCollectorService.cs` | New handler for `ContextTruncationEvent` |
| `Observability/Services/MetricsQueryService.cs` | Branch query on truncation metrics |
| `Dashboard.Client/Pages/Tokens.razor` | Add Truncations KPI card |
| `Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs` | New |
| `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientTruncationTests.cs` | New |
| `Tests/Unit/Agent/MultiAgentFactoryTruncationTests.cs` | New (or extend existing) |
| `Tests/Unit/Agent/SubAgentModuleTruncationTests.cs` | New (or extend existing) |
| `Tests/Unit/Observability/MetricsCollectorTruncationTests.cs` | New |
| `Tests/Unit/Observability/MetricsQueryServiceTruncationTests.cs` | New |

## Out of Scope

- Per-model context-length lookup tables.
- Real tokenizer integration (SharpToken, Tiktoken).
- LLM-based summarization of dropped context.
- Truncating system prompts or memory context independently.
- Dashboard widget for individual truncation events (only aggregates surfaced).
- Docker `.env` / `docker-compose.yml` updates (not a secret).
