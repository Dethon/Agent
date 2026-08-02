# Resolve the Config Patch Once Implementation Plan

**Goal:** One two-field DTO is read back out of the message property bag by two unrelated modules, with two different validation policies, and one of them parks the result in client-level mutable state. Resolve it once and let it ride the request.

**Why now:** `_modelOverrideBox` is written per streaming call at `OpenRouterChatClient.cs:151` and read later on the HTTP pipeline thread at `:380`. `ChatMonitor.cs:86-88` runs a conversation's turns through `.Merge()`, which spawns a consumer task per inner stream — so a second turn can null the box between the first turn's write and its read, and the first request goes out on the configured model while `EffectiveModel` reports whichever value won.

**Source:** architecture review 2026-08-02, candidate 4.

## Global Constraints

- TDD per task. `dotnet test Tests/Unit --nologo -v q`.
- `.cs` files have no trailing newline; pre-commit runs `dotnet format` and re-stages whole files.
- File-scoped namespaces, primary constructors, no XML doc comments.
- **`.claude/rules/openrouter-routing.md` governs.** Provider routing is enforced on every turn including model-override turns; a conflict is a config error, never a silent drop. Nothing in this plan touches the provider node.
- Commit after each task.

## Locked decisions

**Both fields resolve in `McpAgent.CreateRunOptions`.** It already resolves the reasoning half; the model half moves there from `OpenRouterChatClient`.

**The model rides `ChatOptions.ModelId`**, not a box. Verify during task 2 that `ModelId` reaches `OpenRouterHttpHelpers.PrepareRequestBodyAsync` through the inner client; if it does not, carry it on `ChatOptions.AdditionalProperties` and read it per-request in the handler — but never on client-level state.

**One validation policy: fall back and warn.** Today a non-whitelisted model is rejected and falls back, while an unparseable reasoning effort is swallowed by a caught `ArgumentException` at `McpAgent.cs:390-400` with no log at all. Both keep fallback semantics; both become visible.

```csharp
logger.LogWarning("Rejected config patch {Field}={Value}; using {Fallback}", field, value, fallback);
```

**Turns serialize within a conversation.** Replace the inner `Merge` at `ChatMonitor.cs:86-88` with sequential consumption. Multi-target fan-out is untouched — `Monitor_MultiTargetFanOut_DeliversToTargetsConcurrently` must still pass.

Serializing is what makes the other two hazards moot, so they need no separate fix: `ToolApprovalChatClient._dynamicallyApproved` is an unlocked `HashSet` mutated at `:67`, and `OpenRouterChatClient`'s `_reasoningQueue`/`_costQueue`/`_cachedTokenQueue` are drained per-update and per-response, cross-attributing between interleaved SSE streams.

## Tasks

1. **Serialize turns within a group.** Failing test first: two messages queued for one conversation produce non-overlapping turn windows. Assert fan-out concurrency is unaffected.
2. **Move model resolution into `CreateRunOptions`.** `ResolveModelOverride` and the whitelist check move with it; delete `ModelOverrideBox` and the `volatile string?`. `EffectiveModel` reads from options rather than ambient state, so `McpAgent.SafePublishLatencyAsync` stamps the model the request actually used.
3. **Unify validation.** Both fields warn on rejection. Keeps `OpenRouterModelOverrideTests`' five cases green; adds the effort cases that never existed.
4. **End-to-end test.** Today the chain has coverage at four disjoint points — wire→`ChannelMessage`, serialization, the pure whitelist function, latency attribution — and nothing pins that `ChatMonitor.cs:198` stamps the patch onto the user message or that `McpAgent.cs:281` applies the reasoning half. Deleting either line passes the entire unit suite. One test should span notification to request body.
5. **Guard the options bypass.** `options ??= CreateRunOptions(...)` at `McpAgent.cs:270` means an externally supplied `AgentRunOptions` silently skips instructions, tools, reasoning and now the patch. Nothing tests that path. Log or assert when options arrive pre-built.

## Risks

- **Serializing turns is an observable behaviour change** for two rapid messages in one conversation: they are answered in order rather than concurrently. This is what the stack already assumes; the change makes the assumption true rather than accidental.
- **`ChatOptions.ModelId` may not survive the inner OpenAI client.** Task 2 must verify against a real request body, not just a unit assertion on options.
- The reasoning/cost queues stay FIFO-per-client. Serializing makes that correct; if concurrent turns are ever reintroduced, they break again — worth a comment at the `Merge` site saying so.
