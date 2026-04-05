# Memory Context Window — Design

## Problem

`MemoryRecallHook.EnrichAsync` currently embeds only the current user message, and `MemoryExtractionWorker` extracts from it in isolation. This causes visible misses in production:

- **Recall**: follow-up clarifications across turns ("I'm thinking about the beach one... actually make it closer to the city") produce muddy single-sentence embeddings with no context to resolve references.
- **Extraction**: short user replies to agent questions ("cold", "yes", "next month") carry no standalone information, and facts stated piecewise across turns ("I'm moving" → two turns later → "to Lisbon") never get assembled into a single memory.

## Approach

Feed a small rolling window of recent conversation history into both recall and extraction, sourced from the already-persisted thread in Redis via `IThreadStateStore`. The windows are **asymmetric** because the two consumers have different needs:

- **Recall** runs synchronously and blocks the agent response. It needs tight, focused context to resolve references without muddying the embedding vector. **Last 3 user messages total**, user-only — that is, the current incoming message plus the 2 most recent user messages from persisted history.
- **Extraction** runs asynchronously. It benefits from wider context — specifically, the agent's prior turn — to disambiguate short user replies. **Last 6 mixed turns** (user + assistant), with prompt-level guidance to extract only from the final user message.

No new state stores are introduced. The existing `RedisChatMessageStore` already persists `ChatMessage[]` per conversation at the end of every agent turn; we read from it.

## Components & Changes

### 1. `IMemoryRecallHook.EnrichAsync` signature

Add an `AgentSession thread` parameter. The hook:

1. Resolves the state key from `thread.StateBag["ChatHistoryProviderState"]`.
2. Fetches the full message list via `IThreadStateStore.GetMessagesAsync(stateKey)`.
3. Builds the recall window as `[last (WindowUserTurns - 1) user messages from persisted history] + [current incoming message]` (default total = 3), concatenates with newline separators, embeds once, searches as today. At recall time the current message is not yet in the persisted thread — it will be appended by `RedisChatMessageStore` only after the agent turn completes — so the persisted fetch gives us strictly "prior" history.
4. Computes `anchorIndex = fetchedMessages.Count` — the index at which the triggering user message will land once the turn is persisted.
5. Enqueues extraction with `ThreadStateKey` + `AnchorIndex`.

The fetch is piggybacked — the hook already reads from Redis now (embedding + search). One additional `GetMessagesAsync` call per recall.

### 2. `MemoryExtractionRequest` DTO

```csharp
public record MemoryExtractionRequest(
    string UserId,
    string ThreadStateKey,
    int AnchorIndex,
    string? ConversationId,
    string? AgentId);
```

The `MessageContent` field is removed — the worker now derives the full context window from the persisted thread at process time.

### 3. `MemoryExtractionWorker.ExtractWithRetryAsync`

1. Fetch the thread via `IThreadStateStore.GetMessagesAsync(request.ThreadStateKey)`.
2. If the thread is missing (e.g. cleared between enqueue and extraction), drop the request with a debug log and publish zero-count metrics.
3. Take `messages[0..=AnchorIndex]` — this is the slice as it existed at the triggering turn, stable regardless of any T+1/T+2 turns that may have arrived while the request was queued.
4. From that slice, take the last **M mixed turns** (`WindowMixedTurns`, default 6).
5. Pass the window to `IMemoryExtractor.ExtractAsync` along with `userId`.

**Why the anchor matters**: extraction is async. If the user fires three messages in quick succession, the worker processing the first request's extraction must not see turns T+1/T+2 — that would bleed forward-in-time context into past memories and scramble the "recent/novel" signal the prompt relies on. The anchor index freezes the slice at the moment the request was enqueued.

### 4. `IMemoryExtractor.ExtractAsync` signature

Change from:
```csharp
Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(string messageContent, string userId, CancellationToken ct);
```
to:
```csharp
Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(IReadOnlyList<ChatMessage> contextWindow, string userId, CancellationToken ct);
```

The implementation (`OpenRouterMemoryExtractor`) renders the window as a structured prompt with explicit turn markers (see prompt section below).

### 5. `Domain/Prompts/MemoryPrompts.cs`

The extractor prompt is amended with the following rules:

- You will be given a short window of recent conversation turns rendered with turn markers.
- Extract memories **only from the `[CURRENT]` user message**. Earlier turns exist solely to disambiguate pronouns, short replies, and references.
- Do not extract facts that were already fully established in earlier turns of the window — those turns have been processed already on previous invocations.
- Treat assistant turns as context for interpreting the user's statements. Never treat assistant content as a source of fact about the user.
- If `[CURRENT]` adds nothing new about the user, return an empty list.

Window rendering format (example):
```
[context -2] user: I've been thinking about moving
[context -1] assistant: Any particular destination in mind?
[context -1] user: Portugal, probably
[context  0] assistant: Lisbon or somewhere quieter?
[CURRENT]    user: Lisbon, next spring
```

The `[CURRENT]` marker is visually unambiguous so the "extract from CURRENT only" instruction has a clear referent.

### 6. Configuration

```csharp
public record MemoryRecallOptions
{
    public int DefaultLimit { get; init; } = 10;
    public bool IncludePersonalityProfile { get; init; } = true;
    public int WindowUserTurns { get; init; } = 3;
}

public record MemoryExtractionOptions
{
    public double SimilarityThreshold { get; init; } = 0.85;
    public int MaxCandidatesPerMessage { get; init; } = 5;
    public int MaxRetries { get; init; } = 2;
    public int WindowMixedTurns { get; init; } = 6;
}
```

Wired through the existing DI options pattern in `Agent/Modules/MemoryModule.cs`.

### 7. `ChatMonitor.ProcessChatThread`

Pass the `thread` into `memoryRecallHook.EnrichAsync`. No other behavioral changes.

### 8. `RedisChatMessageStore.StateKey`

Currently `internal const string StateKey = "ChatHistoryProviderState"`. Promote to `public` so `MemoryRecallHook` can resolve the key from `AgentSession.StateBag`. Alternative: expose a static helper `RedisChatMessageStore.TryGetStateKey(AgentSession, out string?)`. The helper is preferred — it keeps the constant encapsulated and gives a single place to change the key scheme later.

## Data Flow

```
Turn T — user sends "cold"
    │
    ▼
ChatMonitor.ProcessChatThread
    │ thread (AgentSession, already resolved)
    ▼
MemoryRecallHook.EnrichAsync(message, userId, convId, agentId, thread, ct)
    │
    ├─► IThreadStateStore.GetMessagesAsync(stateKey)   ← persisted history
    │       returns [msg0, msg1, ..., msgN-1]
    │
    ├─► window = last 2 user messages from persisted + current "cold" = 3 total
    ├─► embed(window) → search → attach MemoryContext
    │
    └─► extractionQueue.Enqueue(
            ThreadStateKey=stateKey,
            AnchorIndex=N                              ← where "cold" will land
        )

...agent turn runs, response streams back, RedisChatMessageStore persists
   user message at index N and assistant reply at N+1..N+k...

Later — possibly after turns T+1, T+2 have been processed and persisted

MemoryExtractionWorker.ProcessRequestAsync(request)
    │
    ├─► IThreadStateStore.GetMessagesAsync(request.ThreadStateKey)
    │       returns [msg0, ..., msgN, msgN+1..., msgN+k, msgN+k+1 (T+1 user), ...]
    │
    ├─► slice = messages[0..=AnchorIndex]              ← frozen at turn T
    ├─► window = last 6 turns of slice (mixed)
    │
    ├─► extractor.ExtractAsync(window, userId, ct)
    │       prompt: "extract from [CURRENT] only, skip established facts"
    │
    └─► dedup via similarity search → store novel candidates
```

## Error Handling

- **Thread missing at extraction time** (cleared via `ChatCommand.Clear` between enqueue and extraction): drop the request with a debug log. Publish a zero-count `MemoryExtractionEvent` so the dashboard reflects the drop.
- **Thread shorter than AnchorIndex**: can happen only if history was truncated externally. Treat as missing; drop the request.
- **Empty window after slicing**: no user messages in the slice. Drop the request — nothing to extract from.
- **Recall-path thread fetch failure**: log and fall back to the current behavior (embed the current message alone). Recall must remain on the happy path; memory context is an enhancement, not a hard dependency.

## Testing

### Unit

- `Tests/Unit/Memory/MemoryRecallHookTests.cs`
  - Add fake `IThreadStateStore`. Verify the hook passes the last 3 user messages (ignoring assistant turns) to the embedder.
  - Verify `anchorIndex` equals the fetched message count.
  - Verify fallback when thread fetch throws: single-message embedding still runs.
  - Verify the new `thread` parameter is honored (state key pulled from `StateBag`).

- `Tests/Unit/Memory/MemoryExtractionWorkerTests.cs`
  - Fake thread store returning controlled message lists. Verify slice-to-anchor behavior.
  - Verify last-M windowing on the sliced result.
  - Verify missing-thread path drops the request and publishes zero-count metrics.
  - Verify extractor is invoked with the windowed list, not a single string.

### Integration

- `Tests/Integration/Memory/MemoryRecallHookIntegrationTests.cs`
  - End-to-end with real Redis + real thread store. Seed a conversation, run recall, assert the window was built correctly and extraction was enqueued with the right anchor.

- **New: async drift test**
  - Enqueue an extraction request for turn T (anchor=N).
  - Append turns T+1 and T+2 to the thread directly via the store.
  - Run the worker against the original request.
  - Assert the extractor received only messages up to index N — none of T+1/T+2 leaked in.

- **New: overlap dedup test**
  - Simulate three consecutive turns where only the final one reveals new information.
  - Assert the extractor prompt includes the "skip already-established facts" instruction.
  - Assert `CandidateCount` and `StoredCount` remain sane (no explosion from re-extracting the same facts).

### Prompt regression

Add a small fixture-based test for `OpenRouterMemoryExtractor` (or a prompt-render unit test if the LLM call is mocked) asserting the rendered prompt contains the `[CURRENT]` marker, the context turns in order, and the guard instructions.

## Non-Goals

- **LLM query rewrite for recall** — rejected for latency cost. The user explicitly prioritized keeping recall on the synchronous hot path cheap.
- **Durable in-memory window buffer** — unnecessary; the thread store already provides durable, cross-instance history.
- **Changes to `MemoryDreamingService` / consolidation** — these operate on already-stored memories and are unaffected by how extraction sourced them.
- **Cross-conversation context** — windows are scoped to a single `ThreadStateKey`. Cross-conversation memory unification is handled by the existing vector search + dreaming loop.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| ~5ms added recall latency from the extra Redis GET | Same Redis instance as memory store; single key read; measured via existing `MemoryRecallEvent.DurationMs` metric post-deploy |
| Extractor LLM behavior changes when fed a window vs. a single message (could over- or under-extract) | Fixture tests for prompt rendering; dashboard metrics (`CandidateCount`, `StoredCount`) surface drift; dreaming loop prunes noise |
| `RedisChatMessageStore.StateKey` coupling leaks into memory layer | Expose via `TryGetStateKey(AgentSession)` helper rather than promoting the constant; single place to change later |
| Hook signature change ripples through tests and fakes | Mechanical update; enforced by the compiler |
| Assistant turns in extraction window cause hallucinated facts to be stored as memories | Prompt explicitly forbids treating assistant content as source-of-fact; extractor already filters via confidence scoring |
