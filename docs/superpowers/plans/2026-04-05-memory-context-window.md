# Memory Context Window Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Feed a small rolling window of recent conversation history into the memory recall and extraction paths, sourced from the already-persisted thread in Redis, so follow-up clarifications and short replies produce accurate memories and recall results.

**Architecture:** Recall embeds the last 3 user messages (current + 2 prior). Extraction runs async on the last 6 mixed turns sliced up to an `AnchorIndex` captured at enqueue time (freezes context against subsequent turns). The extractor prompt is updated to treat earlier turns as context and extract only from the `[CURRENT]` user message. No new state stores — we read from `IThreadStateStore`, which already persists thread history via `RedisChatMessageStore`.

**Tech Stack:** .NET 10, C# (file-scoped namespaces, primary constructors, records), xUnit + Shouldly + Moq, `Microsoft.Extensions.AI` (`ChatMessage`, `ChatRole`, `AgentSession`), Redis via `StackExchange.Redis`.

**Reference spec:** `docs/superpowers/specs/2026-04-05-memory-context-window-design.md`

**Project rules to follow:**
- `.claude/rules/tdd.md` — RED → GREEN → REVIEW → COMMIT per triplet
- `.claude/rules/dotnet-style.md` — file-scoped namespaces, primary constructors, LINQ over loops, no XML doc comments
- `.claude/rules/domain-layer.md` — Domain never imports Infrastructure
- `.claude/rules/infrastructure-layer.md` — Infrastructure never imports Agent
- `.claude/rules/testing.md` — test naming `{Method}_{Scenario}_{ExpectedResult}`, Shouldly assertions

**Commit after every task.** Use conventional commits (`feat:`, `refactor:`, `test:`).

---

## File Structure

**New files:**
- `Domain/Memory/ConversationWindowRenderer.cs` — pure static helper that renders a `IReadOnlyList<ChatMessage>` into a turn-marked string for LLM prompts.
- `Tests/Unit/Memory/ConversationWindowRendererTests.cs` — unit tests for the renderer.

**Modified files:**
- `Domain/Contracts/IMemoryExtractor.cs` — signature change: `string messageContent` → `IReadOnlyList<ChatMessage> contextWindow`.
- `Domain/Contracts/IMemoryRecallHook.cs` — signature change: add `AgentSession thread` parameter.
- `Domain/DTOs/MemoryExtractionRequest.cs` — remove `MessageContent`, add `ThreadStateKey`, `AnchorIndex`.
- `Domain/Prompts/MemoryPrompts.cs` — amend `ExtractionSystemPrompt` with window-aware rules.
- `Domain/Monitor/ChatMonitor.cs` — pass `thread` into `EnrichAsync`.
- `Infrastructure/Memory/MemoryRecallHook.cs` — fetch thread, build user-only window, compute anchor, enqueue new DTO, fallback on fetch failure. Add `WindowUserTurns` to `MemoryRecallOptions`.
- `Infrastructure/Memory/MemoryExtractionWorker.cs` — fetch thread via `IThreadStateStore`, slice to `AnchorIndex`, take last M turns, call extractor with window, drop missing thread gracefully. Add `WindowMixedTurns` to `MemoryExtractionOptions`.
- `Infrastructure/Memory/OpenRouterMemoryExtractor.cs` — accept window, render via `ConversationWindowRenderer`, use in prompt.
- `Infrastructure/Agents/ChatClients/RedisChatMessageStore.cs` — add public static `TryGetStateKey(AgentSession, out string?)` helper.
- `Agent/Modules/MemoryModule.cs` — wire new config keys.

**Modified test files:**
- `Tests/Unit/Infrastructure/RedisChatMessageStoreTests.cs` — tests for `TryGetStateKey`.
- `Tests/Unit/Memory/MemoryRecallHookTests.cs` — updates for new signature + new tests for windowing/anchor/fallback.
- `Tests/Unit/Memory/MemoryExtractionWorkerTests.cs` — updates for new DTO shape + tests for slicing/missing-thread.
- `Tests/Integration/Memory/MemoryRecallHookIntegrationTests.cs` — update for new signature + new async drift test.

---

## Pre-flight

### Task 0: Branch setup and baseline build

**Files:** none

- [ ] **Step 1: Verify you are on a feature branch**

```bash
git status
git rev-parse --abbrev-ref HEAD
```

Expected: clean working tree, branch is NOT `master`. If on `master`, create a feature branch:
```bash
git checkout -b feature/memory-context-window
```

- [ ] **Step 2: Baseline build and unit tests**

```bash
dotnet build Agent.sln
dotnet test Tests/Tests.csproj --filter "Category!=Integration&Category!=E2E"
```

Expected: build succeeds, all unit tests pass. If they don't, stop and investigate before making any changes.

---

## Task 1: `ConversationWindowRenderer` pure helper

**Files:**
- Create: `Domain/Memory/ConversationWindowRenderer.cs`
- Create: `Tests/Unit/Memory/ConversationWindowRendererTests.cs`

This is a Domain-layer pure static helper. It takes a list of `ChatMessage` and produces a prompt-friendly string with per-turn markers. The final user message is marked `[CURRENT]`; everything before it is marked `[context -N]` with a negative offset relative to the current turn. Assistant turns are labeled `assistant:` and user turns `user:`.

### Step 1: Write the failing tests

- [ ] **Step 1a: Create the test file**

Create `Tests/Unit/Memory/ConversationWindowRendererTests.cs`:

```csharp
using Domain.Memory;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Memory;

public class ConversationWindowRendererTests
{
    [Fact]
    public void Render_WithSingleUserMessage_MarksItAsCurrent()
    {
        var window = new List<ChatMessage>
        {
            new(ChatRole.User, "cold")
        };

        var rendered = ConversationWindowRenderer.Render(window);

        rendered.ShouldBe("[CURRENT]    user: cold");
    }

    [Fact]
    public void Render_WithMixedTurns_UsesRelativeContextOffsets()
    {
        var window = new List<ChatMessage>
        {
            new(ChatRole.User, "I've been thinking about moving"),
            new(ChatRole.Assistant, "Any particular destination?"),
            new(ChatRole.User, "Portugal, probably"),
            new(ChatRole.Assistant, "Lisbon or somewhere quieter?"),
            new(ChatRole.User, "Lisbon, next spring")
        };

        var rendered = ConversationWindowRenderer.Render(window);

        rendered.ShouldBe(
            "[context -2] user: I've been thinking about moving\n" +
            "[context -2] assistant: Any particular destination?\n" +
            "[context -1] user: Portugal, probably\n" +
            "[context -1] assistant: Lisbon or somewhere quieter?\n" +
            "[CURRENT]    user: Lisbon, next spring");
    }

    [Fact]
    public void Render_WithEmptyWindow_ReturnsEmptyString()
    {
        var rendered = ConversationWindowRenderer.Render([]);
        rendered.ShouldBe(string.Empty);
    }

    [Fact]
    public void Render_WithAssistantAsFinalMessage_StillMarksFinalAsCurrent()
    {
        // Defensive: the renderer doesn't enforce that the last message is a user turn.
        // The caller (extraction worker) guarantees it, but the renderer stays general.
        var window = new List<ChatMessage>
        {
            new(ChatRole.User, "hi"),
            new(ChatRole.Assistant, "hello")
        };

        var rendered = ConversationWindowRenderer.Render(window);

        rendered.ShouldBe(
            "[context -1] user: hi\n" +
            "[CURRENT]    assistant: hello");
    }

    [Fact]
    public void Render_GroupsTurnsByUserTurnBoundary()
    {
        // A "turn" pair is (user, assistant). The offset decrements each time we cross
        // a user message going backwards from CURRENT.
        var window = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "leading assistant msg"),
            new(ChatRole.User, "first user"),
            new(ChatRole.Assistant, "first reply"),
            new(ChatRole.User, "second user")
        };

        var rendered = ConversationWindowRenderer.Render(window);

        rendered.ShouldBe(
            "[context -1] assistant: leading assistant msg\n" +
            "[context -1] user: first user\n" +
            "[context -1] assistant: first reply\n" +
            "[CURRENT]    user: second user");
    }
}
```

- [ ] **Step 1b: Run tests to verify they fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConversationWindowRendererTests"
```

Expected: compilation FAIL with "The name 'ConversationWindowRenderer' does not exist in the namespace 'Domain.Memory'".

### Step 2: Implement the renderer

- [ ] **Step 2a: Create the renderer file**

Create `Domain/Memory/ConversationWindowRenderer.cs`:

```csharp
using Microsoft.Extensions.AI;

namespace Domain.Memory;

public static class ConversationWindowRenderer
{
    public static string Render(IReadOnlyList<ChatMessage> window)
    {
        if (window.Count == 0)
        {
            return string.Empty;
        }

        var lastIndex = window.Count - 1;

        // Compute per-message offset: 0 for the last message (CURRENT),
        // otherwise the count of user messages strictly between this message
        // and the end (exclusive of the end itself).
        var lines = window.Select((msg, i) =>
        {
            if (i == lastIndex)
            {
                return $"[CURRENT]    {RoleLabel(msg.Role)}: {msg.Text}";
            }

            var userTurnsAfter = window
                .Skip(i + 1)
                .Take(lastIndex - i)
                .Count(m => m.Role == ChatRole.User);

            var offset = userTurnsAfter == 0 ? 1 : userTurnsAfter;
            return $"[context -{offset}] {RoleLabel(msg.Role)}: {msg.Text}";
        });

        return string.Join("\n", lines);
    }

    private static string RoleLabel(ChatRole role) =>
        role == ChatRole.Assistant ? "assistant" : "user";
}
```

- [ ] **Step 2b: Run tests to verify they pass**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConversationWindowRendererTests"
```

Expected: all 5 tests PASS.

### Step 3: Commit

- [ ] **Step 3: Commit the renderer**

```bash
git add Domain/Memory/ConversationWindowRenderer.cs Tests/Unit/Memory/ConversationWindowRendererTests.cs
git commit -m "feat(memory): add ConversationWindowRenderer for turn-marked prompt text"
```

---

## Task 2: `RedisChatMessageStore.TryGetStateKey` helper

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/RedisChatMessageStore.cs`
- Modify: `Tests/Unit/Infrastructure/RedisChatMessageStoreTests.cs`

`MemoryRecallHook` needs to read the thread state key from an `AgentSession` without creating a new key if it's absent (the current `ResolveRedisKey` creates one). Add a static `TryGetStateKey` that reads only.

### Step 1: Write failing test

- [ ] **Step 1a: Add two test cases to `RedisChatMessageStoreTests.cs`**

Append these methods to the existing class in `Tests/Unit/Infrastructure/RedisChatMessageStoreTests.cs` (before the closing brace):

```csharp
    [Fact]
    public void TryGetStateKey_WhenKeyPresentInStateBag_ReturnsTrueAndKey()
    {
        var session = CreateSessionWithKey("my-conversation-id");

        var result = RedisChatMessageStore.TryGetStateKey(session, out var stateKey);

        result.ShouldBeTrue();
        stateKey.ShouldBe("my-conversation-id");
    }

    [Fact]
    public void TryGetStateKey_WhenKeyAbsent_ReturnsFalseAndNull()
    {
        var session = new Mock<AgentSession>().Object;

        var result = RedisChatMessageStore.TryGetStateKey(session, out var stateKey);

        result.ShouldBeFalse();
        stateKey.ShouldBeNull();
    }
```

- [ ] **Step 1b: Run tests to verify they fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RedisChatMessageStoreTests.TryGetStateKey"
```

Expected: compilation FAIL with "'RedisChatMessageStore' does not contain a definition for 'TryGetStateKey'".

### Step 2: Add the helper

- [ ] **Step 2a: Add the static method to `RedisChatMessageStore.cs`**

Open `Infrastructure/Agents/ChatClients/RedisChatMessageStore.cs` and add this method inside the class, just below the `StateKey` constant (around line 11):

```csharp
    public static bool TryGetStateKey(AgentSession session, out string? stateKey)
    {
        if (session.StateBag.TryGetValue<string>(StateKey, out var key) && !string.IsNullOrEmpty(key))
        {
            stateKey = key;
            return true;
        }
        stateKey = null;
        return false;
    }
```

- [ ] **Step 2b: Run tests to verify they pass**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RedisChatMessageStoreTests"
```

Expected: all `RedisChatMessageStoreTests` PASS (both the existing tests and the 2 new ones).

### Step 3: Commit

- [ ] **Step 3: Commit the helper**

```bash
git add Infrastructure/Agents/ChatClients/RedisChatMessageStore.cs Tests/Unit/Infrastructure/RedisChatMessageStoreTests.cs
git commit -m "feat(infra): add RedisChatMessageStore.TryGetStateKey read-only helper"
```

---

## Task 3: Extractor prompt update + option fields + renderer usage

**Files:**
- Modify: `Domain/Prompts/MemoryPrompts.cs`
- Modify: `Infrastructure/Memory/MemoryRecallHook.cs`
- Modify: `Infrastructure/Memory/MemoryExtractionWorker.cs`

This task is purely text/config. It does NOT wire the new behavior yet; it prepares the building blocks so later tasks can plug them in. Because no behavior changes, build + full test suite must stay green after this task.

### Step 1: Update the extractor prompt

- [ ] **Step 1: Replace `ExtractionSystemPrompt` in `Domain/Prompts/MemoryPrompts.cs`**

Find `ExtractionSystemPrompt` (around line 43) and replace its body with:

```csharp
    public const string ExtractionSystemPrompt =
        """
        You are a memory extraction system. You will be given a short window of recent conversation turns rendered with turn markers like `[context -1]` and `[CURRENT]`. Your job is to extract storable facts, preferences, instructions, skills, events, and projects from the CURRENT user message only.

        Importance guidelines:
        - Explicit instruction from user: 1.0
        - User correction of prior information: 0.9
        - Explicit user statement ("I work at X"): 0.8-1.0
        - Inferred preference: 0.4-0.6
        - Mentioned in passing: 0.3-0.5

        Rules:
        - Extract memories ONLY from the `[CURRENT]` user message. The `[context -N]` turns exist solely to disambiguate pronouns, short replies, and references — never extract facts from them directly.
        - Do not extract facts that were already fully established in earlier turns of the window; they have already been processed on previous invocations.
        - Treat `assistant:` turns as context for interpreting the user's statements. NEVER treat assistant content as a source of fact about the user.
        - Only extract information the user reveals about themselves — preferences, facts, instructions, skills, relationships, or context that will remain relevant across multiple future conversations.
        - Do not extract information about the bot, system, or assistant itself — its capabilities, features, architecture, or behavior are not user memories.
        - Do not extract observations derived from generic or exploratory questions (e.g. "what can you do?", "how does this work?") — these reveal nothing about the user.
        - Do not extract short-lived or ephemeral information: current tasks, transient moods, one-off requests, in-progress actions, or anything that will lose relevance once the current conversation ends.
        - Do not extract trivial details, small talk, or conversational filler that carries no actionable insight.
        - Do not extract information already covered by the existing profile.
        - If the `[CURRENT]` user message adds nothing new about the user, return an empty candidates array.
        - Keep content concise — one clear statement per memory.
        """;
```

### Step 2: Add `WindowUserTurns` to `MemoryRecallOptions`

- [ ] **Step 2: Modify `Infrastructure/Memory/MemoryRecallHook.cs`**

Find the `MemoryRecallOptions` record (around line 12) and add the new field:

```csharp
public record MemoryRecallOptions
{
    public int DefaultLimit { get; init; } = 10;
    public bool IncludePersonalityProfile { get; init; } = true;
    public int WindowUserTurns { get; init; } = 3;
}
```

### Step 3: Add `WindowMixedTurns` to `MemoryExtractionOptions`

- [ ] **Step 3: Modify `Infrastructure/Memory/MemoryExtractionWorker.cs`**

Find the `MemoryExtractionOptions` record (around line 11) and add the new field:

```csharp
public record MemoryExtractionOptions
{
    public double SimilarityThreshold { get; init; } = 0.85;
    public int MaxCandidatesPerMessage { get; init; } = 5;
    public int MaxRetries { get; init; } = 2;
    public int WindowMixedTurns { get; init; } = 6;
}
```

### Step 4: Verify build and tests

- [ ] **Step 4: Build and run full unit test suite**

```bash
dotnet build Agent.sln
dotnet test Tests/Tests.csproj --filter "Category!=Integration&Category!=E2E"
```

Expected: build succeeds, all existing unit tests still PASS. Behavior is unchanged at this point.

### Step 5: Commit

- [ ] **Step 5: Commit the preparatory changes**

```bash
git add Domain/Prompts/MemoryPrompts.cs Infrastructure/Memory/MemoryRecallHook.cs Infrastructure/Memory/MemoryExtractionWorker.cs
git commit -m "refactor(memory): add window options and update extractor prompt for windowed input"
```

---

## Task 4: Refactor `IMemoryExtractor` to accept a windowed message list

**Files:**
- Modify: `Domain/Contracts/IMemoryExtractor.cs`
- Modify: `Infrastructure/Memory/OpenRouterMemoryExtractor.cs`
- Modify: `Infrastructure/Memory/MemoryExtractionWorker.cs`
- Modify: `Tests/Unit/Memory/MemoryExtractionWorkerTests.cs`

Pure structural refactor: change `ExtractAsync(string, ...)` to `ExtractAsync(IReadOnlyList<ChatMessage>, ...)`. The worker still passes a single-message list (wrapping the DTO's `MessageContent`) — real windowing comes in Task 5. Existing behavior must stay identical.

### Step 1: Change the interface

- [ ] **Step 1: Update `Domain/Contracts/IMemoryExtractor.cs`**

Replace the file contents:

```csharp
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IMemoryExtractor
{
    Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        IReadOnlyList<ChatMessage> contextWindow, string userId, CancellationToken ct);
}
```

### Step 2: Update `OpenRouterMemoryExtractor` to render the window

- [ ] **Step 2: Modify `Infrastructure/Memory/OpenRouterMemoryExtractor.cs`**

Replace the `ExtractAsync` method (lines 30-46) with:

```csharp
    public async Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        IReadOnlyList<ChatMessage> contextWindow, string userId, CancellationToken ct)
    {
        if (contextWindow.Count == 0)
        {
            return [];
        }

        var profile = await store.GetProfileAsync(userId, ct);
        var renderedWindow = ConversationWindowRenderer.Render(contextWindow);

        var userPrompt = profile is not null
            ? $"Existing user profile:\n{profile.Summary}\n\nConversation window:\n{renderedWindow}"
            : $"Conversation window:\n{renderedWindow}";

        var userMessage = new ChatMessage(ChatRole.User, userPrompt);
        userMessage.SetSenderId(userId);

        var messages = new List<ChatMessage> { userMessage };

        var response = await chatClient.GetResponseAsync(messages, _extractionChatOptions, ct);
        return ParseCandidates(response.Text);
    }
```

Also add the missing using at the top of the file:

```csharp
using Domain.Memory;
```

### Step 3: Update `MemoryExtractionWorker` to wrap the DTO's `MessageContent` in a single-message list

- [ ] **Step 3: Modify `Infrastructure/Memory/MemoryExtractionWorker.cs` — `ExtractWithRetryAsync`**

Find `ExtractWithRetryAsync` (around line 90) and change the extractor call. Replace:

```csharp
                return await extractor.ExtractAsync(request.MessageContent, request.UserId, ct);
```

with:

```csharp
                var tempWindow = new List<ChatMessage>
                {
                    new(ChatRole.User, request.MessageContent)
                };
                return await extractor.ExtractAsync(tempWindow, request.UserId, ct);
```

Also add the missing using at the top of the file:

```csharp
using Microsoft.Extensions.AI;
```

### Step 4: Update existing `MemoryExtractionWorkerTests`

- [ ] **Step 4: Modify `Tests/Unit/Memory/MemoryExtractionWorkerTests.cs`**

Every existing test uses `.Setup(e => e.ExtractAsync(...))` with a `string` first argument. Update them to match the new signature.

Add the using at the top of the file:

```csharp
using Microsoft.Extensions.AI;
```

Update the 5 setups/verifies:

1. `ProcessRequestAsync_WithNovelCandidate_StoresMemory` — line 47-49: change to
```csharp
        _extractor
            .Setup(e => e.ExtractAsync(
                It.Is<IReadOnlyList<ChatMessage>>(w =>
                    w.Count == 1 && w[0].Text == "Hello, I work at Contoso"),
                "user1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
```

2. `ProcessRequestAsync_WithDuplicateCandidate_SkipsStore` — line 95-97: change to
```csharp
        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
```

3. `ProcessRequestAsync_PublishesExtractionMetric` — line 132-134: same treatment
```csharp
        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
```

4. `ProcessRequestAsync_SkipsExtraction_WhenAgentDoesNotHaveMemoryFeature` — line 169-171: update verify
```csharp
        _extractor.Verify(
            e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
```

5. `ProcessRequestAsync_WhenExtractorFails_PublishesErrorEvent` — line 180-182:
```csharp
        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Connection refused"));
```

### Step 5: Verify build and tests

- [ ] **Step 5: Build and run full unit test suite**

```bash
dotnet build Agent.sln
dotnet test Tests/Tests.csproj --filter "Category!=Integration&Category!=E2E"
```

Expected: build succeeds, all unit tests PASS. Behavior is identical — extraction still receives a single message, just wrapped in a list.

### Step 6: Commit

- [ ] **Step 6: Commit the refactor**

```bash
git add Domain/Contracts/IMemoryExtractor.cs Infrastructure/Memory/OpenRouterMemoryExtractor.cs Infrastructure/Memory/MemoryExtractionWorker.cs Tests/Unit/Memory/MemoryExtractionWorkerTests.cs
git commit -m "refactor(memory): change IMemoryExtractor to accept windowed ChatMessage list"
```

---

## Task 5: `MemoryExtractionRequest` DTO shape + worker windowing behavior

**Files:**
- Modify: `Domain/DTOs/MemoryExtractionRequest.cs`
- Modify: `Infrastructure/Memory/MemoryExtractionWorker.cs`
- Modify: `Infrastructure/Memory/MemoryRecallHook.cs`
- Modify: `Tests/Unit/Memory/MemoryExtractionWorkerTests.cs`
- Modify: `Tests/Unit/Memory/MemoryRecallHookTests.cs` (call sites only — recall behavior still unchanged)
- Modify: `Tests/Unit/Memory/MemoryExtractionQueueTests.cs` (if the queue tests construct `MemoryExtractionRequest`)

This task changes the DTO shape and implements the real extraction-side windowing: worker fetches the thread via `IThreadStateStore`, slices `messages[0..=AnchorIndex]`, takes the last `WindowMixedTurns` turns, and calls the extractor with the window. This is a TDD triplet — a new behavior test drives the fetch-and-slice logic.

### Step 1: Write failing test for windowed extraction

- [ ] **Step 1a: Add the new test to `MemoryExtractionWorkerTests.cs`**

First update the class header to add an `IThreadStateStore` mock. Modify the test class fields and constructor:

```csharp
public class MemoryExtractionWorkerTests
{
    private readonly Mock<IMemoryExtractor> _extractor = new();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IThreadStateStore> _threadStateStore = new();
    private readonly Mock<IMetricsPublisher> _metricsPublisher = new();
    private readonly Mock<IAgentDefinitionProvider> _agentDefinitionProvider = new();
    private readonly MemoryExtractionQueue _queue = new();
    private readonly MemoryExtractionOptions _options = new();
    private readonly MemoryExtractionWorker _worker;

    public MemoryExtractionWorkerTests()
    {
        _worker = new MemoryExtractionWorker(
            _queue,
            _extractor.Object,
            _embeddingService.Object,
            _store.Object,
            _threadStateStore.Object,
            _metricsPublisher.Object,
            _agentDefinitionProvider.Object,
            NullLogger<MemoryExtractionWorker>.Instance,
            _options);
    }
```

Now update every existing `new MemoryExtractionRequest(...)` call in the file. The old signature was `(userId, messageContent, conversationId, agentId)`. The new signature is `(userId, threadStateKey, anchorIndex, conversationId, agentId)`. You need to also set up `_threadStateStore.GetMessagesAsync` to return the expected single user message for each existing test so they keep passing.

For each existing test, replace the request construction and add a thread-store setup. Example for `ProcessRequestAsync_WithNovelCandidate_StoresMemory`:

```csharp
        var threadMessages = new ChatMessage[]
        {
            new(ChatRole.User, "Hello, I work at Contoso")
        };
        _threadStateStore.Setup(s => s.GetMessagesAsync("thread-key-1"))
            .ReturnsAsync(threadMessages);

        var request = new MemoryExtractionRequest("user1", "thread-key-1", 0, "conv_1", null);
```

And update the extractor setup to match the windowed input (the worker will slice `[0..=0]`, take last 6, which yields the single message):

```csharp
        _extractor
            .Setup(e => e.ExtractAsync(
                It.Is<IReadOnlyList<ChatMessage>>(w =>
                    w.Count == 1 && w[0].Text == "Hello, I work at Contoso"),
                "user1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([candidate]);
```

Apply the same pattern to the other 4 existing tests: each constructs its own `threadMessages` (single-message is fine for tests that don't care about content, e.g. `new(ChatRole.User, "Some message")`), sets up `_threadStateStore.GetMessagesAsync`, and uses the new DTO constructor with `anchorIndex = threadMessages.Length - 1`.

Now add the NEW behavior test at the bottom of the class:

```csharp
    [Fact]
    public async Task ProcessRequestAsync_WithWindow_PassesLastMTurnsToExtractor()
    {
        var stateKey = "state-key-window";
        var allMessages = new ChatMessage[]
        {
            new(ChatRole.User, "turn1 user"),            // index 0
            new(ChatRole.Assistant, "turn1 assistant"),  // index 1
            new(ChatRole.User, "turn2 user"),            // index 2
            new(ChatRole.Assistant, "turn2 assistant"),  // index 3
            new(ChatRole.User, "turn3 user"),            // index 4
            new(ChatRole.Assistant, "turn3 assistant"),  // index 5
            new(ChatRole.User, "turn4 user"),            // index 6 <- anchor
            new(ChatRole.Assistant, "turn4 assistant"),  // index 7 (must be excluded)
            new(ChatRole.User, "turn5 user (drift)")     // index 8 (must be excluded)
        };

        _threadStateStore.Setup(s => s.GetMessagesAsync(stateKey))
            .ReturnsAsync(allMessages);

        IReadOnlyList<ChatMessage>? capturedWindow = null;
        _extractor
            .Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChatMessage>, string, CancellationToken>((w, _, _) => capturedWindow = w)
            .ReturnsAsync([]);

        // AnchorIndex = 6 means the slice is messages[0..6] inclusive = 7 messages (indexes 0-6).
        // Then take last WindowMixedTurns=6 => skip index 0, take indexes 1..6.
        var request = new MemoryExtractionRequest("user1", stateKey, 6, "conv_1", null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        capturedWindow.ShouldNotBeNull();
        capturedWindow.Count.ShouldBe(6);
        capturedWindow[0].Text.ShouldBe("turn1 assistant");
        capturedWindow[^1].Text.ShouldBe("turn4 user");
        capturedWindow.ShouldNotContain(m => m.Text == "turn4 assistant");
        capturedWindow.ShouldNotContain(m => m.Text == "turn5 user (drift)");
    }

    [Fact]
    public async Task ProcessRequestAsync_WithMissingThread_DropsRequestAndPublishesZeroMetric()
    {
        _threadStateStore.Setup(s => s.GetMessagesAsync("gone"))
            .ReturnsAsync((ChatMessage[]?)null);

        MetricEvent? published = null;
        _metricsPublisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((evt, _) => published = evt)
            .Returns(Task.CompletedTask);

        var request = new MemoryExtractionRequest("user1", "gone", 0, "conv_1", null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _extractor.Verify(
            e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        published.ShouldNotBeNull();
        published.ShouldBeOfType<MemoryExtractionEvent>();
        var evt = (MemoryExtractionEvent)published;
        evt.CandidateCount.ShouldBe(0);
        evt.StoredCount.ShouldBe(0);
    }

    [Fact]
    public async Task ProcessRequestAsync_WithAnchorBeyondThreadLength_DropsRequest()
    {
        var allMessages = new ChatMessage[]
        {
            new(ChatRole.User, "only message")
        };
        _threadStateStore.Setup(s => s.GetMessagesAsync("short"))
            .ReturnsAsync(allMessages);

        var request = new MemoryExtractionRequest("user1", "short", 99, "conv_1", null);

        await _worker.ProcessRequestAsync(request, CancellationToken.None);

        _extractor.Verify(
            e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
```

- [ ] **Step 1b: Run tests to verify they fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MemoryExtractionWorkerTests"
```

Expected: compilation FAIL. `MemoryExtractionRequest` doesn't have the new positional args; `MemoryExtractionWorker` constructor doesn't accept `IThreadStateStore`.

### Step 2: Update the DTO

- [ ] **Step 2: Replace contents of `Domain/DTOs/MemoryExtractionRequest.cs`**

```csharp
namespace Domain.DTOs;

public record MemoryExtractionRequest(
    string UserId,
    string ThreadStateKey,
    int AnchorIndex,
    string? ConversationId,
    string? AgentId);
```

### Step 3: Update the worker to fetch, slice, and window

- [ ] **Step 3a: Modify `Infrastructure/Memory/MemoryExtractionWorker.cs`**

Add `IThreadStateStore threadStateStore` to the primary constructor parameter list. The full class header becomes:

```csharp
public class MemoryExtractionWorker(
    MemoryExtractionQueue queue,
    IMemoryExtractor extractor,
    IEmbeddingService embeddingService,
    IMemoryStore store,
    IThreadStateStore threadStateStore,
    IMetricsPublisher metricsPublisher,
    IAgentDefinitionProvider agentDefinitionProvider,
    ILogger<MemoryExtractionWorker> logger,
    MemoryExtractionOptions options) : BackgroundService
```

Replace the body of `ExtractWithRetryAsync` (currently around lines 90-106) with a fetch-slice-window pipeline:

```csharp
    private async Task<IReadOnlyList<ExtractionCandidate>> ExtractWithRetryAsync(
        MemoryExtractionRequest request, CancellationToken ct)
    {
        var thread = await threadStateStore.GetMessagesAsync(request.ThreadStateKey);
        if (thread is null || request.AnchorIndex < 0 || request.AnchorIndex >= thread.Length)
        {
            logger.LogDebug(
                "Extraction dropped: thread missing or anchor out of range (user {UserId}, key {Key}, anchor {Anchor})",
                request.UserId, request.ThreadStateKey, request.AnchorIndex);
            return [];
        }

        var window = thread
            .Take(request.AnchorIndex + 1)
            .TakeLast(options.WindowMixedTurns)
            .ToList();

        if (window.Count == 0)
        {
            return [];
        }

        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                return await extractor.ExtractAsync(window, request.UserId, ct);
            }
            catch (Exception ex) when (attempt < options.MaxRetries)
            {
                logger.LogWarning(ex, "Extraction attempt {Attempt} failed for user {UserId}, retrying",
                    attempt + 1, request.UserId);
            }
        }
        return [];
    }
```

- [ ] **Step 3b: Remove the temporary single-message wrap from earlier**

Make sure the old code block added in Task 4 Step 3 (`var tempWindow = ... new(ChatRole.User, request.MessageContent)`) is gone — it has been replaced by the real windowing above.

### Step 4: Fix the `MemoryRecallHook` call site that enqueues extraction

- [ ] **Step 4: Modify `Infrastructure/Memory/MemoryRecallHook.cs`**

Find the existing enqueue call (around line 84-85):

```csharp
            // Enqueue extraction request (non-blocking)
            await extractionQueue.EnqueueAsync(
                new MemoryExtractionRequest(userId, messageText, conversationId, agentId), ct);
```

This will no longer compile. Temporarily replace it with stub values that keep the build green. Real values come in Task 7:

```csharp
            // Enqueue extraction request (non-blocking)
            // NOTE: ThreadStateKey and AnchorIndex populated in Task 7 once thread plumbing lands.
            await extractionQueue.EnqueueAsync(
                new MemoryExtractionRequest(userId, string.Empty, -1, conversationId, agentId), ct);
```

### Step 5: Update `Tests/Unit/Memory/MemoryExtractionQueueTests.cs` if it constructs the DTO

- [ ] **Step 5: Modify `Tests/Unit/Memory/MemoryExtractionQueueTests.cs`**

```bash
grep -n "new MemoryExtractionRequest" Tests/Unit/Memory/MemoryExtractionQueueTests.cs
```

For each occurrence, change from `(userId, messageContent, conversationId, agentId)` to `(userId, threadStateKey, anchorIndex, conversationId, agentId)`. A typical update:

```csharp
// before:
var request = new MemoryExtractionRequest("user1", "hello", "conv1", null);
// after:
var request = new MemoryExtractionRequest("user1", "state-key", 0, "conv1", null);
```

Only the shape matters for queue tests — pick any plausible values.

### Step 6: Update `MemoryRecallHookTests` for DTO shape (call-site only)

- [ ] **Step 6: Modify `Tests/Unit/Memory/MemoryRecallHookTests.cs`**

Find the `EnrichAsync_EnqueuesExtractionRequest` test (around line 71-90). It currently asserts `item.MessageContent.ShouldBe("I work at Contoso")`. That field no longer exists. Replace the assertion with a shape check (real field values are driven in Task 7):

```csharp
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var item in _queue.ReadAllAsync(cts.Token))
        {
            item.UserId.ShouldBe("user1");
            item.ConversationId.ShouldBe("conv_1");
            break;
        }
```

### Step 7: Wire `IThreadStateStore` into the DI registration

- [ ] **Step 7: Modify `Agent/Modules/MemoryModule.cs`**

`MemoryExtractionWorker` now depends on `IThreadStateStore`. The DI container already has `IThreadStateStore` registered (used by `RedisChatMessageStore`), so no code change is required in `MemoryModule.cs` beyond a quick verification:

```bash
grep -n "IThreadStateStore" Agent/Modules/*.cs
```

Expected: at least one existing registration of `IThreadStateStore` (likely in `InjectorModule` or a state module). If it doesn't exist for some reason, register it before `services.AddHostedService<MemoryExtractionWorker>()` in `MemoryModule.cs`.

### Step 8: Also wire the new options values

- [ ] **Step 8: Modify `Agent/Modules/MemoryModule.cs`**

Find the `MemoryRecallOptions` creation (around line 67) and add the new config key:

```csharp
            var recallOptions = new MemoryRecallOptions
            {
                DefaultLimit = memoryConfig.GetValue("Recall:DefaultLimit", 10),
                IncludePersonalityProfile = memoryConfig.GetValue("Recall:IncludePersonalityProfile", true),
                WindowUserTurns = memoryConfig.GetValue("Recall:WindowUserTurns", 3)
            };
```

And `MemoryExtractionOptions` (around line 74):

```csharp
            var extractionOptions = new MemoryExtractionOptions
            {
                SimilarityThreshold = memoryConfig.GetValue("Extraction:SimilarityThreshold", 0.85),
                MaxCandidatesPerMessage = memoryConfig.GetValue("Extraction:MaxCandidatesPerMessage", 5),
                WindowMixedTurns = memoryConfig.GetValue("Extraction:WindowMixedTurns", 6)
            };
```

### Step 9: Update `appsettings.json` / `appsettings.Development.json`

- [ ] **Step 9: Add config placeholders**

Per the project's CLAUDE.md: new config keys must be added to both `appsettings.json` files. Add under the `Memory` section:

```json
"Memory": {
  "Recall": {
    "WindowUserTurns": 3
  },
  "Extraction": {
    "WindowMixedTurns": 6
  }
}
```

Merge with existing `Memory` block — do not overwrite. Inspect the files first:

```bash
grep -n "Memory" Agent/appsettings.json Agent/appsettings.Development.json
```

### Step 10: Build and run unit tests

- [ ] **Step 10: Full build and unit tests**

```bash
dotnet build Agent.sln
dotnet test Tests/Tests.csproj --filter "Category!=Integration&Category!=E2E"
```

Expected: build succeeds, ALL unit tests PASS including the 3 new worker tests (`WithWindow_PassesLastMTurnsToExtractor`, `WithMissingThread_DropsRequestAndPublishesZeroMetric`, `WithAnchorBeyondThreadLength_DropsRequest`).

### Step 11: Commit

- [ ] **Step 11: Commit the extraction windowing**

```bash
git add Domain/DTOs/MemoryExtractionRequest.cs Infrastructure/Memory/MemoryExtractionWorker.cs Infrastructure/Memory/MemoryRecallHook.cs Agent/Modules/MemoryModule.cs Agent/appsettings.json Agent/appsettings.Development.json Tests/Unit/Memory/MemoryExtractionWorkerTests.cs Tests/Unit/Memory/MemoryRecallHookTests.cs Tests/Unit/Memory/MemoryExtractionQueueTests.cs
git commit -m "feat(memory): extraction fetches thread and slices windowed context at anchor"
```

---

## Task 6: `MemoryRecallHook` builds user-only window and sets anchor

**Files:**
- Modify: `Domain/Contracts/IMemoryRecallHook.cs`
- Modify: `Infrastructure/Memory/MemoryRecallHook.cs`
- Modify: `Domain/Monitor/ChatMonitor.cs`
- Modify: `Tests/Unit/Memory/MemoryRecallHookTests.cs`
- Modify: `Tests/Integration/Memory/MemoryRecallHookIntegrationTests.cs`
- Modify: `Tests/Unit/Domain/MonitorTests.cs` (if it sets up `IMemoryRecallHook`)

Add the `AgentSession thread` parameter, fetch from `IThreadStateStore`, build the last-N user-message window + current, compute `anchorIndex = thread.Count`, and enqueue the extraction request with real values.

### Step 1: Write failing tests

- [ ] **Step 1a: Add `IThreadStateStore` mock to the test class**

Modify `Tests/Unit/Memory/MemoryRecallHookTests.cs` class header:

```csharp
public class MemoryRecallHookTests
{
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IEmbeddingService> _embeddingService = new();
    private readonly Mock<IThreadStateStore> _threadStateStore = new();
    private readonly Mock<IMetricsPublisher> _metricsPublisher = new();
    private readonly Mock<IAgentDefinitionProvider> _agentDefinitionProvider = new();
    private readonly MemoryExtractionQueue _queue = new();
    private readonly MemoryRecallHook _hook;

    private static readonly float[] _testEmbedding = Enumerable.Range(0, 1536).Select(i => (float)i / 1536).ToArray();

    public MemoryRecallHookTests()
    {
        _hook = new MemoryRecallHook(
            _store.Object,
            _embeddingService.Object,
            _threadStateStore.Object,
            _queue,
            _metricsPublisher.Object,
            _agentDefinitionProvider.Object,
            Mock.Of<ILogger<MemoryRecallHook>>(),
            new MemoryRecallOptions());
    }

    private static AgentSession CreateSessionWithStateKey(string stateKey)
    {
        var session = new Mock<AgentSession>().Object;
        session.StateBag.SetValue(RedisChatMessageStore.StateKey, stateKey);
        return session;
    }
```

Add usings at the top:

```csharp
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;
```

Every existing test calls `_hook.EnrichAsync(message, "user1", "conv_1", null, CancellationToken.None)`. The new signature adds `AgentSession thread` just before the cancellation token. Update EVERY call site in the file. Example:

```csharp
// before
await _hook.EnrichAsync(message, "user1", "conv_1", null, CancellationToken.None);
// after
var session = CreateSessionWithStateKey("state-1");
_threadStateStore.Setup(s => s.GetMessagesAsync("state-1"))
    .ReturnsAsync((ChatMessage[]?)null);
await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);
```

For each existing test, decide whether that test needs a real thread history or can use `null`. Tests that are purely about the non-window parts (profile attach, access-timestamp update, skip-on-disabled-feature) can use `null` from the store. The enqueue test needs real messages so it can assert the anchor.

Now add the new behavior tests at the end of the class:

```csharp
    [Fact]
    public async Task EnrichAsync_BuildsRecallWindowFromLastUserMessages()
    {
        var currentMessage = new ChatMessage(ChatRole.User, "and surf?");
        var session = CreateSessionWithStateKey("state-window");

        // 5 persisted messages: 3 user + 2 assistant. WindowUserTurns=3 means
        // take the last 2 user messages from history + the current one = 3 user messages.
        var persisted = new ChatMessage[]
        {
            new(ChatRole.User, "beaches near Lisbon?"),       // index 0 - user (older)
            new(ChatRole.Assistant, "Cascais, Guincho..."),   // index 1 - assistant (ignored)
            new(ChatRole.User, "which has the best surf?"),   // index 2 - user
            new(ChatRole.Assistant, "Guincho is famous..."),  // index 3 - assistant (ignored)
            new(ChatRole.User, "and for beginners?")          // index 4 - user
        };
        _threadStateStore.Setup(s => s.GetMessagesAsync("state-window"))
            .ReturnsAsync(persisted);

        string? capturedEmbeddingInput = null;
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((text, _) => capturedEmbeddingInput = text)
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
                It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _hook.EnrichAsync(currentMessage, "user1", "conv_1", null, session, CancellationToken.None);

        capturedEmbeddingInput.ShouldNotBeNull();
        // Default WindowUserTurns=3: last 2 user messages from persisted + current.
        capturedEmbeddingInput.ShouldContain("which has the best surf?");
        capturedEmbeddingInput.ShouldContain("and for beginners?");
        capturedEmbeddingInput.ShouldContain("and surf?");
        // Oldest user message (index 0) is outside the window of 3.
        capturedEmbeddingInput.ShouldNotContain("beaches near Lisbon?");
        // Assistant turns are never part of the recall window.
        capturedEmbeddingInput.ShouldNotContain("Cascais");
        capturedEmbeddingInput.ShouldNotContain("Guincho is famous");
    }

    [Fact]
    public async Task EnrichAsync_EnqueuesExtractionWithAnchorIndexEqualToPersistedCount()
    {
        var message = new ChatMessage(ChatRole.User, "current");
        var session = CreateSessionWithStateKey("state-anchor");

        // 4 persisted messages means the incoming user message will land at index 4.
        var persisted = new ChatMessage[]
        {
            new(ChatRole.User, "m0"),
            new(ChatRole.Assistant, "m1"),
            new(ChatRole.User, "m2"),
            new(ChatRole.Assistant, "m3")
        };
        _threadStateStore.Setup(s => s.GetMessagesAsync("state-anchor"))
            .ReturnsAsync(persisted);

        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
                It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var item in _queue.ReadAllAsync(cts.Token))
        {
            item.UserId.ShouldBe("user1");
            item.ThreadStateKey.ShouldBe("state-anchor");
            item.AnchorIndex.ShouldBe(4);
            item.ConversationId.ShouldBe("conv_1");
            break;
        }
    }

    [Fact]
    public async Task EnrichAsync_WhenThreadStoreThrows_FallsBackToCurrentMessageOnly()
    {
        var message = new ChatMessage(ChatRole.User, "hello");
        var session = CreateSessionWithStateKey("state-broken");

        _threadStateStore.Setup(s => s.GetMessagesAsync("state-broken"))
            .ThrowsAsync(new InvalidOperationException("redis down"));

        string? capturedEmbeddingInput = null;
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((text, _) => capturedEmbeddingInput = text)
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
                It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);

        capturedEmbeddingInput.ShouldBe("hello");
    }

    [Fact]
    public async Task EnrichAsync_WhenSessionHasNoStateKey_FallsBackToCurrentMessageOnly()
    {
        var message = new ChatMessage(ChatRole.User, "hello");
        var session = new Mock<AgentSession>().Object;  // no StateKey set

        string? capturedEmbeddingInput = null;
        _embeddingService
            .Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<string, CancellationToken>((text, _) => capturedEmbeddingInput = text)
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]>(),
                It.IsAny<IEnumerable<MemoryCategory>>(), It.IsAny<IEnumerable<string>>(), It.IsAny<double?>(),
                It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        await _hook.EnrichAsync(message, "user1", "conv_1", null, session, CancellationToken.None);

        capturedEmbeddingInput.ShouldBe("hello");
        _threadStateStore.Verify(s => s.GetMessagesAsync(It.IsAny<string>()), Times.Never);
    }
```

- [ ] **Step 1b: Run tests to verify they fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MemoryRecallHookTests"
```

Expected: compilation FAIL — `MemoryRecallHook` constructor doesn't take `IThreadStateStore`; `EnrichAsync` doesn't accept `AgentSession`.

### Step 2: Update `IMemoryRecallHook` interface

- [ ] **Step 2: Replace contents of `Domain/Contracts/IMemoryRecallHook.cs`**

```csharp
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IMemoryRecallHook
{
    Task EnrichAsync(
        ChatMessage message,
        string userId,
        string? conversationId,
        string? agentId,
        AgentSession thread,
        CancellationToken ct);
}
```

### Step 3: Update `MemoryRecallHook` implementation

- [ ] **Step 3: Modify `Infrastructure/Memory/MemoryRecallHook.cs`**

Add `IThreadStateStore` to the primary constructor. Add a using:

```csharp
using Microsoft.Agents.AI;
using Infrastructure.Agents.ChatClients;
```

The class header becomes:

```csharp
public class MemoryRecallHook(
    IMemoryStore store,
    IEmbeddingService embeddingService,
    IThreadStateStore threadStateStore,
    MemoryExtractionQueue extractionQueue,
    IMetricsPublisher metricsPublisher,
    IAgentDefinitionProvider agentDefinitionProvider,
    ILogger<MemoryRecallHook> logger,
    MemoryRecallOptions options) : IMemoryRecallHook
```

Replace the `EnrichAsync` method entirely:

```csharp
    public async Task EnrichAsync(
        ChatMessage message,
        string userId,
        string? conversationId,
        string? agentId,
        AgentSession thread,
        CancellationToken ct)
    {
        if (agentId is not null)
        {
            var agentDef = agentDefinitionProvider.GetById(agentId);
            if (agentDef is not null && !agentDef.EnabledFeatures.Contains("memory", StringComparer.OrdinalIgnoreCase))
            {
                return;
            }
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var messageText = message.Text;
            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            var (persisted, stateKey) = await TryFetchThreadAsync(thread);
            var anchorIndex = persisted?.Length ?? 0;

            var embeddingInput = BuildRecallWindowText(messageText, persisted, options.WindowUserTurns);

            var embedding = await embeddingService.GenerateEmbeddingAsync(embeddingInput, ct);

            var memoriesTask = store.SearchAsync(userId, queryEmbedding: embedding, limit: options.DefaultLimit, ct: ct);
            var profileTask = options.IncludePersonalityProfile
                ? store.GetProfileAsync(userId, ct)
                : Task.FromResult<PersonalityProfile?>(null);

            await Task.WhenAll(memoriesTask, profileTask);

            var memories = await memoriesTask;
            var profile = await profileTask;

            if (memories.Count > 0 || profile is not null)
            {
                message.SetMemoryContext(new MemoryContext(memories, profile));
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.WhenAll(memories.Select(m => store.UpdateAccessAsync(userId, m.Memory.Id, CancellationToken.None)));
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to update access timestamps for user {UserId}", userId);
                    await metricsPublisher.PublishAsync(new ErrorEvent
                    {
                        Service = "memory",
                        ErrorType = ex.GetType().Name,
                        Message = $"Access timestamp update failed: {ex.Message}"
                    });
                }
            });

            if (stateKey is not null)
            {
                await extractionQueue.EnqueueAsync(
                    new MemoryExtractionRequest(userId, stateKey, anchorIndex, conversationId, agentId), ct);
            }

            sw.Stop();
            await metricsPublisher.PublishAsync(new MemoryRecallEvent
            {
                DurationMs = sw.ElapsedMilliseconds,
                MemoryCount = memories.Count,
                UserId = userId,
                ConversationId = conversationId,
                AgentId = agentId is not null ? agentDefinitionProvider.GetById(agentId)?.Name ?? agentId : null
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory recall failed for user {UserId}", userId);
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Recall failed: {ex.Message}"
            }, ct);
        }
    }

    private async Task<(ChatMessage[]? Messages, string? StateKey)> TryFetchThreadAsync(AgentSession thread)
    {
        if (!RedisChatMessageStore.TryGetStateKey(thread, out var stateKey) || stateKey is null)
        {
            return (null, null);
        }

        try
        {
            var messages = await threadStateStore.GetMessagesAsync(stateKey);
            return (messages, stateKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch thread history for recall window (key {Key})", stateKey);
            return (null, stateKey);
        }
    }

    private static string BuildRecallWindowText(string currentText, ChatMessage[]? persisted, int windowUserTurns)
    {
        if (persisted is null || persisted.Length == 0 || windowUserTurns <= 1)
        {
            return currentText;
        }

        var priorUserMessages = persisted
            .Where(m => m.Role == ChatRole.User)
            .TakeLast(windowUserTurns - 1)
            .Select(m => m.Text)
            .ToList();

        if (priorUserMessages.Count == 0)
        {
            return currentText;
        }

        priorUserMessages.Add(currentText);
        return string.Join("\n", priorUserMessages);
    }
```

### Step 4: Update `ChatMonitor` to pass the thread

- [ ] **Step 4: Modify `Domain/Monitor/ChatMonitor.cs` line 95**

Change:

```csharp
                        if (memoryRecallHook is not null)
                        {
                            await memoryRecallHook.EnrichAsync(userMessage, x.Message.Sender, x.Message.ConversationId, x.Message.AgentId, linkedCt);
                        }
```

to:

```csharp
                        if (memoryRecallHook is not null)
                        {
                            await memoryRecallHook.EnrichAsync(userMessage, x.Message.Sender, x.Message.ConversationId, x.Message.AgentId, thread, linkedCt);
                        }
```

### Step 5: Update any other callers or mocks

- [ ] **Step 5: Find and fix remaining callers**

```bash
grep -rn "EnrichAsync" --include="*.cs" .
```

For every call with the old 5-arg signature, add the `AgentSession` parameter. Likely locations:

- `Tests/Integration/Memory/MemoryRecallHookIntegrationTests.cs` — update the single call to construct a session with a state key and pass it.
- `Tests/Unit/Domain/MonitorTests.cs` — if it mocks `IMemoryRecallHook.EnrichAsync`, update Moq setups.

For `MemoryRecallHookIntegrationTests.cs`:

```csharp
using Infrastructure.Agents.ChatClients;
using Microsoft.Agents.AI;

// ... inside the test:
var threadStateStore = new Mock<IThreadStateStore>();
threadStateStore.Setup(s => s.GetMessagesAsync(It.IsAny<string>()))
    .ReturnsAsync((ChatMessage[]?)null);

var hook = new MemoryRecallHook(
    store, embeddingService.Object, threadStateStore.Object, queue, metricsPublisher.Object,
    agentDefinitionProvider.Object,
    Mock.Of<ILogger<MemoryRecallHook>>(),
    new MemoryRecallOptions());

var message = new ChatMessage(ChatRole.User, "What language should I use?");
var session = new Mock<AgentSession>().Object;
session.StateBag.SetValue(RedisChatMessageStore.StateKey, "test-state-key");

await hook.EnrichAsync(message, userId, "conv_1", null, session, CancellationToken.None);
```

### Step 6: Build and test

- [ ] **Step 6: Full build + unit tests**

```bash
dotnet build Agent.sln
dotnet test Tests/Tests.csproj --filter "Category!=Integration&Category!=E2E"
```

Expected: build succeeds, all unit tests PASS, including the 4 new recall tests.

### Step 7: Commit

- [ ] **Step 7: Commit the recall windowing**

```bash
git add Domain/Contracts/IMemoryRecallHook.cs Infrastructure/Memory/MemoryRecallHook.cs Domain/Monitor/ChatMonitor.cs Tests/Unit/Memory/MemoryRecallHookTests.cs Tests/Integration/Memory/MemoryRecallHookIntegrationTests.cs Tests/Unit/Domain/MonitorTests.cs
git commit -m "feat(memory): recall builds user-only window from persisted thread and sets anchor"
```

---

## Task 7: Integration test — async drift

**Files:**
- Create: `Tests/Integration/Memory/MemoryExtractionWorkerDriftTests.cs`

End-to-end integration test proving the anchor index prevents subsequent turns from bleeding into earlier extraction requests.

### Step 1: Write the test

- [ ] **Step 1: Create the new test file**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Memory;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Memory;

[Trait("Category", "Integration")]
public class MemoryExtractionWorkerDriftTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task ProcessRequestAsync_WhenAdditionalTurnsArrive_DoesNotLeakThemIntoExtraction()
    {
        var stateKey = $"drift-test-{Guid.NewGuid():N}";

        var store = new RedisStackMemoryStore(redisFixture.Connection);
        var threadStore = new Infrastructure.StateManagers.RedisThreadStateStore(
            redisFixture.Connection, TimeSpan.FromMinutes(5));

        // Seed the thread with 3 messages: user, assistant, user.
        // The triggering user message is at index 2.
        await threadStore.SetMessagesAsync(stateKey,
        [
            new ChatMessage(ChatRole.User, "I'm planning a trip"),
            new ChatMessage(ChatRole.Assistant, "Where to?"),
            new ChatMessage(ChatRole.User, "Japan in April")
        ]);

        var extractor = new Mock<IMemoryExtractor>();
        IReadOnlyList<ChatMessage>? capturedWindow = null;
        extractor.Setup(e => e.ExtractAsync(
                It.IsAny<IReadOnlyList<ChatMessage>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<ChatMessage>, string, CancellationToken>((w, _, _) => capturedWindow = w)
            .ReturnsAsync([]);

        var embedding = new Mock<IEmbeddingService>();
        var metrics = new Mock<IMetricsPublisher>();
        var agentDefs = new Mock<IAgentDefinitionProvider>();

        var worker = new MemoryExtractionWorker(
            new MemoryExtractionQueue(),
            extractor.Object,
            embedding.Object,
            store,
            threadStore,
            metrics.Object,
            agentDefs.Object,
            NullLogger<MemoryExtractionWorker>.Instance,
            new MemoryExtractionOptions());

        // Simulate the queued request (anchor=2 was captured when "Japan in April" was the latest turn).
        var request = new MemoryExtractionRequest(
            UserId: $"user-{Guid.NewGuid():N}",
            ThreadStateKey: stateKey,
            AnchorIndex: 2,
            ConversationId: "conv-drift",
            AgentId: null);

        // After enqueue, two more turns arrive and get persisted.
        await threadStore.SetMessagesAsync(stateKey,
        [
            new ChatMessage(ChatRole.User, "I'm planning a trip"),
            new ChatMessage(ChatRole.Assistant, "Where to?"),
            new ChatMessage(ChatRole.User, "Japan in April"),
            new ChatMessage(ChatRole.Assistant, "Great choice!"),
            new ChatMessage(ChatRole.User, "Actually, make it Thailand"),  // <- drift, must be excluded
            new ChatMessage(ChatRole.Assistant, "Thailand is wonderful too")
        ]);

        // Now the worker processes the ORIGINAL request.
        await worker.ProcessRequestAsync(request, CancellationToken.None);

        capturedWindow.ShouldNotBeNull();
        capturedWindow.ShouldNotContain(m => m.Text.Contains("Thailand"));
        capturedWindow.ShouldNotContain(m => m.Text == "Great choice!");
        capturedWindow[^1].Text.ShouldBe("Japan in April");
    }
}
```

- [ ] **Step 2: Run the integration test**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MemoryExtractionWorkerDriftTests"
```

Expected: PASS. If Redis is not available, the fixture will skip it — not a failure.

### Step 3: Commit

- [ ] **Step 3: Commit the drift integration test**

```bash
git add Tests/Integration/Memory/MemoryExtractionWorkerDriftTests.cs
git commit -m "test(memory): integration test asserts anchor freezes extraction against async drift"
```

---

## Task 8: Prompt rendering smoke test

**Files:**
- Modify: `Tests/Unit/Memory/MemoryExtractionWorkerTests.cs` (or add a new `OpenRouterMemoryExtractorTests.cs` if one does not exist)

Verify that when the worker passes a multi-turn window, the renderer produces a string containing the `[CURRENT]` marker and the turns are in order. This guards the extractor prompt integration contract without depending on an LLM.

### Step 1: Check for an existing extractor test file

- [ ] **Step 1: Look for existing extractor tests**

```bash
find Tests -name "OpenRouterMemoryExtractor*.cs"
```

If a file exists, add the test there. Otherwise create `Tests/Unit/Memory/OpenRouterMemoryExtractorTests.cs`.

### Step 2: Write the test

- [ ] **Step 2: Add test that asserts the renderer output is passed to the chat client**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class OpenRouterMemoryExtractorTests
{
    [Fact]
    public async Task ExtractAsync_WithMultiTurnWindow_BuildsPromptContainingCurrentMarkerAndTurns()
    {
        var chatClient = new Mock<IChatClient>();
        var memoryStore = new Mock<IMemoryStore>();
        memoryStore.Setup(s => s.GetProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        ChatMessage? capturedUserPrompt = null;
        chatClient.Setup(c => c.GetResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions, CancellationToken>((msgs, _, _) =>
            {
                capturedUserPrompt = msgs.Single();
            })
            .ReturnsAsync(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "{\"candidates\":[]}")));

        var extractor = new OpenRouterMemoryExtractor(
            chatClient.Object,
            memoryStore.Object,
            NullLogger<OpenRouterMemoryExtractor>.Instance);

        var window = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "hot or cold?"),
            new(ChatRole.User, "cold")
        };

        await extractor.ExtractAsync(window, "user1", CancellationToken.None);

        capturedUserPrompt.ShouldNotBeNull();
        var promptText = capturedUserPrompt.Text;
        promptText.ShouldContain("[CURRENT]");
        promptText.ShouldContain("cold");
        promptText.ShouldContain("hot or cold?");
    }
}
```

- [ ] **Step 3: Run the test**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterMemoryExtractorTests"
```

Expected: PASS.

### Step 4: Commit

- [ ] **Step 4: Commit the prompt smoke test**

```bash
git add Tests/Unit/Memory/OpenRouterMemoryExtractorTests.cs
git commit -m "test(memory): assert extractor prompt includes CURRENT marker and window turns"
```

---

## Task 9: Final verification

### Step 1: Full build

- [ ] **Step 1: Build the full solution**

```bash
dotnet build Agent.sln
```

Expected: 0 errors, 0 warnings (or only warnings that existed before this work — compare against `git stash && dotnet build` if unsure).

### Step 2: Run full test suite

- [ ] **Step 2: Unit tests**

```bash
dotnet test Tests/Tests.csproj --filter "Category!=Integration&Category!=E2E"
```

Expected: ALL unit tests PASS.

- [ ] **Step 3: Integration tests (requires local Redis)**

```bash
dotnet test Tests/Tests.csproj --filter "Category=Integration"
```

Expected: PASS or SKIP if Redis unavailable.

### Step 4: Smoke-test the stack locally

- [ ] **Step 4: Bring up the docker stack and send a message**

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-websearch mcp-idealista mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus redis caddy
docker compose -f DockerCompose/docker-compose.yml -p jackbot logs -f agent | head -200
```

In the webchat, send a 3-turn conversation ("what beaches are near Lisbon?" → agent reply → "which has better surf?"). Watch the agent logs for the memory recall event.

Check the dashboard at `https://assistants.herfluffness.com/dashboard/` (or `http://localhost:5003/dashboard/`):
- Memory analytics panel should show recall events with non-zero memory counts (after a few turns build up stored memories).
- Extraction candidate counts should be sane (not exploding).

### Step 5: Commit any final touch-ups

- [ ] **Step 5: Commit if needed**

```bash
git status
# If anything was changed during verification, commit it.
```

---

## Self-Review Checklist (for the author, before handing off)

**Spec coverage:**
- [x] Asymmetric windows (recall=user-only 3, extraction=mixed 6) → Tasks 5 & 6
- [x] `IThreadStateStore` as context source → Tasks 5 & 6
- [x] `AnchorIndex` freezes extraction against drift → Task 5 + Task 7 integration test
- [x] Extractor prompt amended with `[CURRENT]` rules → Task 3
- [x] `ConversationWindowRenderer` + `[CURRENT]` marker → Task 1 + Task 8 test
- [x] Error handling: missing thread, out-of-range anchor, fetch failure fallback → Task 5 Step 3 + Task 6 fallback tests
- [x] Options: `WindowUserTurns`, `WindowMixedTurns` → Task 3 + Task 5 Step 8 DI wiring
- [x] `TryGetStateKey` read-only helper → Task 2
- [x] ChatMonitor passes thread into hook → Task 6 Step 4

**Placeholder scan:**
- No TBDs, TODOs, or vague directives in any task.
- Every code step shows the exact code to write.
- Every test step shows the full test body.

**Type consistency:**
- `MemoryExtractionRequest(string UserId, string ThreadStateKey, int AnchorIndex, string? ConversationId, string? AgentId)` — used consistently across Task 5 & Task 6.
- `IMemoryExtractor.ExtractAsync(IReadOnlyList<ChatMessage>, string, CancellationToken)` — matches Task 4 and Task 8.
- `IMemoryRecallHook.EnrichAsync(ChatMessage, string, string?, string?, AgentSession, CancellationToken)` — Task 6.
- `ConversationWindowRenderer.Render(IReadOnlyList<ChatMessage>) -> string` — Task 1 and Task 8.
- `RedisChatMessageStore.TryGetStateKey(AgentSession, out string?) -> bool` — Task 2 and Task 6.
