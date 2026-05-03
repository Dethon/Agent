# Context Truncation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Drop oldest non-essential conversation messages on every LLM hop when an estimated token count (chars/4, 95% safety margin) would overflow a configurable per-agent or global token budget.

**Architecture:** A pure `MessageTruncator` static helper computes per-message token estimates and produces a truncated message list while preserving system messages, the last user message, and tool-call/result pair atomicity. The truncator runs inside `OpenRouterChatClient.GetStreamingResponseAsync` immediately before forwarding to the inner OpenAI client — this placement automatically covers every iteration of `FunctionInvokingChatClient`'s tool-call loop. When truncation drops at least one message, a new `ContextTruncationEvent` is published through the existing `IMetricsPublisher` pipeline. The collector aggregates by sender/model into Redis, and three new `TokenMetric` enum values plus a KPI card on `Tokens.razor` surface the data on the dashboard.

**Tech Stack:** .NET 10, C# 13, `Microsoft.Extensions.AI`, OpenAI .NET SDK, Redis, Blazor WebAssembly, xUnit + Moq + Shouldly. Follow `.claude/rules/dotnet-style.md` (file-scoped namespaces, primary constructors, LINQ over loops, no XML docs) and `.claude/rules/tdd.md` (Red-Green-Refactor) throughout.

**Spec:** `docs/superpowers/specs/2026-05-03-context-truncation-design.md`

---

## File Map

**New files:**
- `Infrastructure/Agents/ChatClients/MessageTruncator.cs` — pure static truncation helper.
- `Domain/DTOs/Metrics/ContextTruncationEvent.cs` — metric DTO.
- `Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs`
- `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientTruncationTests.cs`
- `Tests/Unit/Observability/Services/MetricsCollectorTruncationTests.cs`
- `Tests/Unit/Observability/Services/MetricsQueryServiceTruncationTests.cs`

**Modified:**
- `Domain/DTOs/AgentDefinition.cs`
- `Domain/DTOs/SubAgentDefinition.cs`
- `Domain/DTOs/Metrics/MetricEvent.cs`
- `Domain/DTOs/Metrics/Enums/TokenMetric.cs`
- `Agent/Settings/AgentSettings.cs`
- `Agent/Modules/InjectorModule.cs`
- `Infrastructure/Agents/MultiAgentFactory.cs`
- `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`
- `Observability/Services/MetricsCollectorService.cs`
- `Observability/Services/MetricsQueryService.cs`
- `Dashboard.Client/Pages/Tokens.razor`
- `Dashboard.Client/State/Tokens/TokensStore.cs` (only if metric-switching state needs adjustment)
- `Agent/appsettings.json`, `Agent/appsettings.Local.json`

---

## Task 1: Add `MaxContextTokens` to `AgentDefinition`

**Files:**
- Modify: `Domain/DTOs/AgentDefinition.cs`

- [ ] **Step 1: Add the optional property**

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record AgentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Model { get; init; }
    public required string[] McpServerEndpoints { get; init; }
    public string[] WhitelistPatterns { get; init; } = [];
    public string? CustomInstructions { get; init; }
    public string? TelegramBotToken { get; init; }
    public string[] EnabledFeatures { get; init; } = [];
    public int? MaxContextTokens { get; init; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Domain/Domain.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Domain/DTOs/AgentDefinition.cs
git commit -m "feat(domain): add MaxContextTokens to AgentDefinition"
```

---

## Task 2: Add `MaxContextTokens` to `SubAgentDefinition`

**Files:**
- Modify: `Domain/DTOs/SubAgentDefinition.cs`

- [ ] **Step 1: Add the optional property**

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record SubAgentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string Model { get; init; }
    public required string[] McpServerEndpoints { get; init; }
    public string? CustomInstructions { get; init; }
    public string[] EnabledFeatures { get; init; } = [];
    public int MaxExecutionSeconds { get; init; } = 120;
    public int? MaxContextTokens { get; init; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Domain/Domain.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Domain/DTOs/SubAgentDefinition.cs
git commit -m "feat(domain): add MaxContextTokens to SubAgentDefinition"
```

---

## Task 3: Add `MaxContextTokens` to `OpenRouterConfiguration` settings record

**Files:**
- Modify: `Agent/Settings/AgentSettings.cs`

- [ ] **Step 1: Add the optional property**

Replace the `OpenRouterConfiguration` record:

```csharp
public record OpenRouterConfiguration
{
    public required string ApiUrl { get; [UsedImplicitly] init; }
    public required string ApiKey { get; [UsedImplicitly] init; }
    public int? MaxContextTokens { get; [UsedImplicitly] init; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Agent/Agent.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

```bash
git add Agent/Settings/AgentSettings.cs
git commit -m "feat(settings): add MaxContextTokens to OpenRouterConfiguration"
```

---

## Task 4: Create `MessageTruncator` skeleton + `EstimateTokens` (TDD)

**Files:**
- Create: `Infrastructure/Agents/ChatClients/MessageTruncator.cs`
- Create: `Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs`:

```csharp
using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class MessageTruncatorTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("a", 1)]
    [InlineData("abcd", 1)]
    [InlineData("abcde", 2)]
    [InlineData("abcdefgh", 2)]
    [InlineData("abcdefghi", 3)]
    public void EstimateTokens_ReturnsCeilingOfCharsDividedByFour(string input, int expected)
    {
        MessageTruncator.EstimateTokens(input).ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Run test to verify it fails (RED)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageTruncatorTests"`
Expected: FAIL — `MessageTruncator` type does not exist.

- [ ] **Step 3: Create the minimal implementation**

Create `Infrastructure/Agents/ChatClients/MessageTruncator.cs`:

```csharp
namespace Infrastructure.Agents.ChatClients;

internal static class MessageTruncator
{
    public static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;
}
```

- [ ] **Step 4: Run test to verify it passes (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageTruncatorTests"`
Expected: PASS — 6 cases pass.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/MessageTruncator.cs Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs
git commit -m "feat(infra): add MessageTruncator.EstimateTokens"
```

---

## Task 5: Add `EstimateMessageTokens` (TDD)

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/MessageTruncator.cs`
- Modify: `Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `MessageTruncatorTests.cs`:

```csharp
[Fact]
public void EstimateMessageTokens_TextContent_CountsTextPlusOverhead()
{
    var msg = new ChatMessage(ChatRole.User, "abcdefgh"); // 8 chars => 2 tokens
    MessageTruncator.EstimateMessageTokens(msg).ShouldBe(2 + 4); // + per-message overhead
}

[Fact]
public void EstimateMessageTokens_FunctionCall_CountsSerializedJsonPlusOverhead()
{
    var call = new FunctionCallContent("call-1", "doStuff",
        new Dictionary<string, object?> { ["x"] = 1 });
    var msg = new ChatMessage(ChatRole.Assistant, [call]);

    var expectedJson = JsonSerializer.Serialize(new { name = "doStuff", arguments = call.Arguments });
    var expectedTokens = MessageTruncator.EstimateTokens(expectedJson) + 4;

    MessageTruncator.EstimateMessageTokens(msg).ShouldBe(expectedTokens);
}

[Fact]
public void EstimateMessageTokens_FunctionResult_CountsSerializedResultPlusOverhead()
{
    var result = new FunctionResultContent("call-1", "ok-result");
    var msg = new ChatMessage(ChatRole.Tool, [result]);

    var expectedJson = JsonSerializer.Serialize(result.Result);
    var expectedTokens = MessageTruncator.EstimateTokens(expectedJson) + 4;

    MessageTruncator.EstimateMessageTokens(msg).ShouldBe(expectedTokens);
}

[Fact]
public void EstimateMessageTokens_MultipleContents_SumsAllPlusSingleOverhead()
{
    var msg = new ChatMessage(ChatRole.User,
        [new TextContent("abcd"), new TextContent("efgh")]); // 1 + 1 = 2 tokens
    MessageTruncator.EstimateMessageTokens(msg).ShouldBe(2 + 4);
}
```

Add the required usings at the top of the test file:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;
```

- [ ] **Step 2: Run tests to verify they fail (RED)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageTruncatorTests"`
Expected: 4 new tests FAIL — `EstimateMessageTokens` does not exist.

- [ ] **Step 3: Implement the helper**

Replace `MessageTruncator.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.ChatClients;

internal static class MessageTruncator
{
    private const int PerMessageOverhead = 4;
    private const int OtherContentOverhead = 4;

    public static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    public static int EstimateMessageTokens(ChatMessage message)
    {
        var contentTokens = message.Contents.Sum(EstimateContentTokens);
        return contentTokens + PerMessageOverhead;
    }

    private static int EstimateContentTokens(AIContent content) => content switch
    {
        TextContent t => EstimateTokens(t.Text),
        TextReasoningContent r => EstimateTokens(r.Text),
        FunctionCallContent fc => EstimateTokens(JsonSerializer.Serialize(
            new { name = fc.Name, arguments = fc.Arguments })),
        FunctionResultContent fr => EstimateTokens(JsonSerializer.Serialize(fr.Result)),
        _ => OtherContentOverhead
    };
}
```

- [ ] **Step 4: Run tests to verify they pass (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageTruncatorTests"`
Expected: PASS — all tests green.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/MessageTruncator.cs Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs
git commit -m "feat(infra): add MessageTruncator.EstimateMessageTokens"
```

---

## Task 6: Add `Truncate` — no-op cases (TDD)

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/MessageTruncator.cs`
- Modify: `Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Append:

```csharp
[Fact]
public void Truncate_NullMaxTokens_ReturnsOriginalUnchanged()
{
    var msgs = new List<ChatMessage>
    {
        new(ChatRole.System, "sys"),
        new(ChatRole.User, "hi")
    };

    var result = MessageTruncator.Truncate(
        msgs, null, out var dropped, out var before, out var after);

    result.ShouldBe(msgs);
    dropped.ShouldBe(0);
    before.ShouldBe(after);
}

[Fact]
public void Truncate_UnderThreshold_ReturnsOriginalUnchanged()
{
    var msgs = new List<ChatMessage>
    {
        new(ChatRole.User, "hi") // tiny
    };

    var result = MessageTruncator.Truncate(
        msgs, 10000, out var dropped, out var before, out var after);

    result.ShouldBe(msgs);
    dropped.ShouldBe(0);
    before.ShouldBe(after);
}

[Fact]
public void Truncate_EmptyList_ReturnsOriginalUnchanged()
{
    var msgs = new List<ChatMessage>();

    var result = MessageTruncator.Truncate(
        msgs, 100, out var dropped, out var before, out var after);

    result.ShouldBe(msgs);
    dropped.ShouldBe(0);
    before.ShouldBe(0);
    after.ShouldBe(0);
}
```

- [ ] **Step 2: Run tests to verify they fail (RED)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageTruncatorTests"`
Expected: FAIL — `Truncate` does not exist.

- [ ] **Step 3: Add the no-op `Truncate`**

Append to `MessageTruncator.cs`:

```csharp
private const double SafetyRatio = 0.95;

public static IReadOnlyList<ChatMessage> Truncate(
    IReadOnlyList<ChatMessage> messages,
    int? maxContextTokens,
    out int droppedCount,
    out int tokensBefore,
    out int tokensAfter)
{
    droppedCount = 0;
    tokensBefore = messages.Sum(EstimateMessageTokens);
    tokensAfter = tokensBefore;

    if (maxContextTokens is null or <= 0 || messages.Count == 0)
    {
        return messages;
    }

    var threshold = (int)Math.Floor(maxContextTokens.Value * SafetyRatio);
    if (tokensBefore <= threshold)
    {
        return messages;
    }

    return messages; // truncation logic added in next task
}
```

- [ ] **Step 4: Run tests to verify they pass (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageTruncatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/MessageTruncator.cs Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs
git commit -m "feat(infra): add MessageTruncator.Truncate no-op cases"
```

---

## Task 7: `Truncate` — drop oldest, preserve system + last user (TDD)

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/MessageTruncator.cs`
- Modify: `Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Append:

```csharp
[Fact]
public void Truncate_OverThreshold_DropsOldestNonPinnedMessage()
{
    // Build 4 messages so per-message text dominates.
    // Each "x" * 80 -> 20 tokens + 4 overhead = 24 tokens per message.
    var sys  = new ChatMessage(ChatRole.System,    new string('s', 80));
    var u1   = new ChatMessage(ChatRole.User,      new string('a', 80));
    var a1   = new ChatMessage(ChatRole.Assistant, new string('b', 80));
    var u2   = new ChatMessage(ChatRole.User,      new string('c', 80)); // last user (pinned)
    var msgs = new List<ChatMessage> { sys, u1, a1, u2 };

    // total = 96. Threshold at 95% of 80 = 76. Need to drop until <= 76.
    var result = MessageTruncator.Truncate(
        msgs, maxContextTokens: 80,
        out var dropped, out var before, out var after);

    dropped.ShouldBeGreaterThanOrEqualTo(1);
    result.ShouldContain(sys);                // system pinned
    result.ShouldContain(u2);                 // last user pinned
    result.ShouldNotContain(u1);              // oldest non-pinned dropped first
    after.ShouldBeLessThanOrEqualTo(76);
    before.ShouldBe(96);
}

[Fact]
public void Truncate_AlwaysPreservesAllSystemMessages()
{
    var sys1 = new ChatMessage(ChatRole.System, new string('a', 400));
    var sys2 = new ChatMessage(ChatRole.System, new string('b', 400));
    var u1   = new ChatMessage(ChatRole.User,   new string('c', 80));
    var msgs = new List<ChatMessage> { sys1, sys2, u1 };

    var result = MessageTruncator.Truncate(
        msgs, maxContextTokens: 50,
        out _, out _, out _);

    result.ShouldContain(sys1);
    result.ShouldContain(sys2);
    result.ShouldContain(u1); // last user always preserved
}

[Fact]
public void Truncate_StopsDroppingOnceUnderThreshold()
{
    var sys = new ChatMessage(ChatRole.System,    new string('s', 4));
    var u1  = new ChatMessage(ChatRole.User,      new string('a', 80));
    var a1  = new ChatMessage(ChatRole.Assistant, new string('b', 80));
    var u2  = new ChatMessage(ChatRole.User,      new string('c', 4));
    var msgs = new List<ChatMessage> { sys, u1, a1, u2 };

    // Limit so dropping just u1 brings us under threshold.
    var result = MessageTruncator.Truncate(
        msgs, maxContextTokens: 70,
        out var dropped, out _, out _);

    dropped.ShouldBe(1);
    result.ShouldContain(a1); // not dropped — already under threshold
    result.ShouldNotContain(u1);
}
```

- [ ] **Step 2: Run tests to verify they fail (RED)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageTruncatorTests"`
Expected: 3 new tests FAIL — current `Truncate` is no-op.

- [ ] **Step 3: Implement drop-oldest logic**

Replace the body of `Truncate` in `MessageTruncator.cs`:

```csharp
public static IReadOnlyList<ChatMessage> Truncate(
    IReadOnlyList<ChatMessage> messages,
    int? maxContextTokens,
    out int droppedCount,
    out int tokensBefore,
    out int tokensAfter)
{
    droppedCount = 0;
    tokensBefore = messages.Sum(EstimateMessageTokens);
    tokensAfter = tokensBefore;

    if (maxContextTokens is null or <= 0 || messages.Count == 0)
    {
        return messages;
    }

    var threshold = (int)Math.Floor(maxContextTokens.Value * SafetyRatio);
    if (tokensBefore <= threshold)
    {
        return messages;
    }

    var lastUserIndex = LastIndexOfRole(messages, ChatRole.User);
    var pinned = new HashSet<int>(
        Enumerable.Range(0, messages.Count)
            .Where(i => messages[i].Role == ChatRole.System || i == lastUserIndex));

    var kept = messages.Select((m, i) => (Message: m, Index: i, Tokens: EstimateMessageTokens(m)))
        .ToList();
    var currentTokens = tokensBefore;

    for (var i = 0; i < kept.Count && currentTokens > threshold; )
    {
        if (pinned.Contains(kept[i].Index))
        {
            i++;
            continue;
        }

        currentTokens -= kept[i].Tokens;
        kept.RemoveAt(i);
        droppedCount++;
        // do not increment i — list shifted left
    }

    tokensAfter = currentTokens;
    return kept.Select(k => k.Message).ToList();
}

private static int LastIndexOfRole(IReadOnlyList<ChatMessage> messages, ChatRole role)
{
    for (var i = messages.Count - 1; i >= 0; i--)
    {
        if (messages[i].Role == role) return i;
    }
    return -1;
}
```

(Manual loops here are intentional: the index tracking with conditional removal is the kind of complex control flow the dotnet-style rule explicitly allows for. Keep the rest of the file LINQ-based.)

- [ ] **Step 4: Run tests to verify they pass (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageTruncatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/MessageTruncator.cs Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs
git commit -m "feat(infra): MessageTruncator drops oldest non-pinned messages"
```

---

## Task 8: `Truncate` — preserve tool-call/result pairs atomically (TDD)

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/MessageTruncator.cs`
- Modify: `Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Append:

```csharp
[Fact]
public void Truncate_DropsToolCallAssistantTogetherWithMatchingToolResult()
{
    var sys = new ChatMessage(ChatRole.System,    new string('s', 4));
    var assistantWithCall = new ChatMessage(
        ChatRole.Assistant,
        [new FunctionCallContent("call-1", "doStuff",
            new Dictionary<string, object?> { ["padding"] = new string('p', 200) })]);
    var toolResult = new ChatMessage(
        ChatRole.Tool,
        [new FunctionResultContent("call-1", new string('r', 200))]);
    var lastUser = new ChatMessage(ChatRole.User, new string('u', 4));

    var msgs = new List<ChatMessage> { sys, assistantWithCall, toolResult, lastUser };

    var result = MessageTruncator.Truncate(
        msgs, maxContextTokens: 30,
        out var dropped, out _, out _);

    dropped.ShouldBe(2); // pair dropped together
    result.ShouldNotContain(assistantWithCall);
    result.ShouldNotContain(toolResult);
    result.ShouldContain(sys);
    result.ShouldContain(lastUser);
}

[Fact]
public void Truncate_NeverSplitsToolCallResultPair()
{
    // Even when only the assistant call (without its result) would be small enough to drop
    // and bring us under the threshold, we must NOT drop just the call — we drop both or neither.
    var sys = new ChatMessage(ChatRole.System,    new string('s', 4));
    var smallAssistant = new ChatMessage(
        ChatRole.Assistant,
        [new FunctionCallContent("call-1", "f", new Dictionary<string, object?>())]);
    var bigToolResult = new ChatMessage(
        ChatRole.Tool,
        [new FunctionResultContent("call-1", new string('r', 800))]);
    var lastUser = new ChatMessage(ChatRole.User, new string('u', 4));

    var msgs = new List<ChatMessage> { sys, smallAssistant, bigToolResult, lastUser };

    var result = MessageTruncator.Truncate(
        msgs, maxContextTokens: 80,
        out var dropped, out _, out _);

    // If smallAssistant is dropped, bigToolResult MUST also be dropped.
    var hasAssistant = result.Contains(smallAssistant);
    var hasResult = result.Contains(bigToolResult);
    hasAssistant.ShouldBe(hasResult); // both present or both absent
    dropped.ShouldBeGreaterThanOrEqualTo(2); // they go together
}
```

- [ ] **Step 2: Run tests to verify they fail (RED)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageTruncatorTests"`
Expected: FAIL — pair atomicity not yet implemented.

- [ ] **Step 3: Update `Truncate` to drop pairs atomically**

Replace `Truncate` in `MessageTruncator.cs` with the pair-aware version:

```csharp
public static IReadOnlyList<ChatMessage> Truncate(
    IReadOnlyList<ChatMessage> messages,
    int? maxContextTokens,
    out int droppedCount,
    out int tokensBefore,
    out int tokensAfter)
{
    droppedCount = 0;
    tokensBefore = messages.Sum(EstimateMessageTokens);
    tokensAfter = tokensBefore;

    if (maxContextTokens is null or <= 0 || messages.Count == 0)
    {
        return messages;
    }

    var threshold = (int)Math.Floor(maxContextTokens.Value * SafetyRatio);
    if (tokensBefore <= threshold)
    {
        return messages;
    }

    var lastUserIndex = LastIndexOfRole(messages, ChatRole.User);
    var pinned = new HashSet<int>(
        Enumerable.Range(0, messages.Count)
            .Where(i => messages[i].Role == ChatRole.System || i == lastUserIndex));

    var groups = BuildDropGroups(messages, pinned);

    var kept = new HashSet<int>(Enumerable.Range(0, messages.Count));
    var currentTokens = tokensBefore;

    foreach (var group in groups)
    {
        if (currentTokens <= threshold) break;

        var groupTokens = group.Sum(idx => EstimateMessageTokens(messages[idx]));
        foreach (var idx in group) kept.Remove(idx);
        currentTokens -= groupTokens;
        droppedCount += group.Count;
    }

    tokensAfter = currentTokens;
    return Enumerable.Range(0, messages.Count)
        .Where(kept.Contains)
        .Select(i => messages[i])
        .ToList();
}

// Returns drop candidates as ordered groups (oldest first).
// An assistant message containing FunctionCallContent forms a group with
// every subsequent non-pinned tool message whose FunctionResultContent.CallId
// matches one of its call ids.
private static IReadOnlyList<IReadOnlyList<int>> BuildDropGroups(
    IReadOnlyList<ChatMessage> messages, HashSet<int> pinned)
{
    var groups = new List<IReadOnlyList<int>>();
    var consumed = new HashSet<int>();

    for (var i = 0; i < messages.Count; i++)
    {
        if (pinned.Contains(i) || consumed.Contains(i)) continue;

        var msg = messages[i];
        var callIds = msg.Contents.OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet();

        if (msg.Role == ChatRole.Assistant && callIds.Count > 0)
        {
            var group = new List<int> { i };
            consumed.Add(i);
            for (var j = i + 1; j < messages.Count; j++)
            {
                if (pinned.Contains(j) || consumed.Contains(j)) continue;
                var hasMatchingResult = messages[j].Contents
                    .OfType<FunctionResultContent>()
                    .Any(r => callIds.Contains(r.CallId));
                if (hasMatchingResult)
                {
                    group.Add(j);
                    consumed.Add(j);
                }
            }
            groups.Add(group);
        }
        else
        {
            groups.Add(new[] { i });
            consumed.Add(i);
        }
    }

    return groups;
}
```

- [ ] **Step 4: Run all truncator tests to verify everything passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MessageTruncatorTests"`
Expected: PASS — all tests including earlier ones still green.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/MessageTruncator.cs Tests/Unit/Infrastructure/Agents/ChatClients/MessageTruncatorTests.cs
git commit -m "feat(infra): MessageTruncator drops tool-call/result pairs atomically"
```

---

## Task 9: Create `ContextTruncationEvent` DTO

**Files:**
- Create: `Domain/DTOs/Metrics/ContextTruncationEvent.cs`
- Modify: `Domain/DTOs/Metrics/MetricEvent.cs`

- [ ] **Step 1: Create the DTO**

Create `Domain/DTOs/Metrics/ContextTruncationEvent.cs`:

```csharp
namespace Domain.DTOs.Metrics;

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

- [ ] **Step 2: Register the polymorphic discriminator**

Replace `Domain/DTOs/Metrics/MetricEvent.cs`:

```csharp
using System.Text.Json.Serialization;

namespace Domain.DTOs.Metrics;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(TokenUsageEvent), "token_usage")]
[JsonDerivedType(typeof(ToolCallEvent), "tool_call")]
[JsonDerivedType(typeof(ErrorEvent), "error")]
[JsonDerivedType(typeof(ScheduleExecutionEvent), "schedule_execution")]
[JsonDerivedType(typeof(HeartbeatEvent), "heartbeat")]
[JsonDerivedType(typeof(MemoryRecallEvent), "memory_recall")]
[JsonDerivedType(typeof(MemoryExtractionEvent), "memory_extraction")]
[JsonDerivedType(typeof(MemoryDreamingEvent), "memory_dreaming")]
[JsonDerivedType(typeof(ContextTruncationEvent), "context_truncation")]
public abstract record MetricEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? AgentId { get; init; }
    public string? ConversationId { get; init; }
}
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build Domain/Domain.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Domain/DTOs/Metrics/ContextTruncationEvent.cs Domain/DTOs/Metrics/MetricEvent.cs
git commit -m "feat(domain): add ContextTruncationEvent metric DTO"
```

---

## Task 10: Extend `TokenMetric` enum with truncation values

**Files:**
- Modify: `Domain/DTOs/Metrics/Enums/TokenMetric.cs`

- [ ] **Step 1: Append the three values**

Replace `Domain/DTOs/Metrics/Enums/TokenMetric.cs`:

```csharp
namespace Domain.DTOs.Metrics.Enums;

public enum TokenMetric
{
    Tokens,
    Cost,
    TruncationCount,
    MessagesDropped,
    TokensTrimmed
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Domain/Domain.csproj`
Expected: build succeeds. Existing query service `switch` will still compile because the default branch throws.

- [ ] **Step 3: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/TokenMetric.cs
git commit -m "feat(domain): add truncation values to TokenMetric enum"
```

---

## Task 11: Wire truncation into `OpenRouterChatClient` (TDD)

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`
- Create: `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientTruncationTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientTruncationTests.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class OpenRouterChatClientTruncationTests
{
    private readonly Mock<IChatClient> _innerClient = new();
    private readonly Mock<IMetricsPublisher> _publisher = new();

    [Fact]
    public async Task GetStreamingResponseAsync_NullMaxContext_ForwardsAllMessages()
    {
        var sut = new OpenRouterChatClient(
            _innerClient.Object, "test-model", maxContextTokens: null,
            metricsPublisher: _publisher.Object);

        var sys = new ChatMessage(ChatRole.System, "sys");
        var u1  = new ChatMessage(ChatRole.User,   new string('a', 4000));
        var u2  = new ChatMessage(ChatRole.User,   "hi");
        u2.SetSenderId("alice");

        IEnumerable<ChatMessage>? captured = null;
        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (msgs, _, _) => captured = msgs.ToList())
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        await foreach (var _ in sut.GetStreamingResponseAsync([sys, u1, u2])) { }

        captured!.Count().ShouldBe(3);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_OverThreshold_DropsAndPublishesEvent()
    {
        var sut = new OpenRouterChatClient(
            _innerClient.Object, "test-model", maxContextTokens: 80,
            metricsPublisher: _publisher.Object);

        var sys = new ChatMessage(ChatRole.System, new string('s', 4));
        var u1  = new ChatMessage(ChatRole.User,   new string('a', 400));
        var u2  = new ChatMessage(ChatRole.User,   "hi");
        u2.SetSenderId("alice");

        IEnumerable<ChatMessage>? captured = null;
        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken>(
                (msgs, _, _) => captured = msgs.ToList())
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        ContextTruncationEvent? publishedEvent = null;
        _publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) =>
            {
                if (e is ContextTruncationEvent t) publishedEvent = t;
            })
            .Returns(Task.CompletedTask);

        await foreach (var _ in sut.GetStreamingResponseAsync([sys, u1, u2])) { }

        captured!.Count().ShouldBeLessThan(3); // u1 dropped
        publishedEvent.ShouldNotBeNull();
        publishedEvent!.Sender.ShouldBe("alice");
        publishedEvent.Model.ShouldBe("test-model");
        publishedEvent.DroppedMessages.ShouldBeGreaterThanOrEqualTo(1);
        publishedEvent.MaxContextTokens.ShouldBe(80);
        publishedEvent.EstimatedTokensAfter.ShouldBeLessThan(publishedEvent.EstimatedTokensBefore);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_UnderThreshold_DoesNotPublishTruncationEvent()
    {
        var sut = new OpenRouterChatClient(
            _innerClient.Object, "test-model", maxContextTokens: 100000,
            metricsPublisher: _publisher.Object);

        var u = new ChatMessage(ChatRole.User, "hi");
        u.SetSenderId("alice");

        _innerClient
            .Setup(c => c.GetStreamingResponseAsync(
                It.IsAny<IEnumerable<ChatMessage>>(),
                It.IsAny<ChatOptions?>(),
                It.IsAny<CancellationToken>()))
            .Returns(AsyncEnumerable.Empty<ChatResponseUpdate>());

        await foreach (var _ in sut.GetStreamingResponseAsync([u])) { }

        _publisher.Verify(
            p => p.PublishAsync(It.IsAny<ContextTruncationEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (RED)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterChatClientTruncationTests"`
Expected: FAIL — `OpenRouterChatClient` ctor does not accept `maxContextTokens`.

- [ ] **Step 3: Add `maxContextTokens` to `OpenRouterChatClient`**

Modify `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`:

3a. Add a private field and accept it in the public constructor:

Replace the constructor block (the public + internal pair) with:

```csharp
private readonly int? _maxContextTokens;

public OpenRouterChatClient(
    string endpoint,
    string apiKey,
    string model,
    int? maxContextTokens = null,
    IMetricsPublisher? metricsPublisher = null)
{
    _model = model;
    _maxContextTokens = maxContextTokens;
    _metricsPublisher = metricsPublisher;
    _httpClient = CreateHttpClient(_reasoningQueue, _costQueue);
    _transport = new HttpClientPipelineTransport(_httpClient);
    _client = CreateClient(endpoint, apiKey, model, _transport);
}

internal OpenRouterChatClient(
    IChatClient innerClient,
    string model,
    int? maxContextTokens = null,
    IMetricsPublisher? metricsPublisher = null)
{
    _model = model;
    _maxContextTokens = maxContextTokens;
    _metricsPublisher = metricsPublisher;
    _client = innerClient;
}
```

(The existing `internal OpenRouterChatClient(IChatClient innerClient, string model, IMetricsPublisher? metricsPublisher = null)` constructor is replaced by the new internal one — `maxContextTokens` defaults to `null` so existing tests pass.)

3b. In `GetStreamingResponseAsync`, after the existing `transformedMessages` `Select` line, materialize and run truncation. Find this section:

```csharp
var transformedMessages = materializedMessages.Select(x =>
{
    ...
});

UsageContent? usage = null;

await foreach (var update in _client.GetStreamingResponseAsync(transformedMessages, options, ct))
```

Replace with:

```csharp
var transformedMessages = materializedMessages.Select(x =>
{
    ...
}).ToList(); // unchanged body, now materialized

var truncated = MessageTruncator.Truncate(
    transformedMessages, _maxContextTokens,
    out var droppedCount, out var tokensBefore, out var tokensAfter);

if (droppedCount > 0 && _metricsPublisher is not null)
{
    await _metricsPublisher.PublishAsync(new ContextTruncationEvent
    {
        Sender = sender ?? "unknown",
        Model = _model,
        DroppedMessages = droppedCount,
        EstimatedTokensBefore = tokensBefore,
        EstimatedTokensAfter = tokensAfter,
        MaxContextTokens = _maxContextTokens ?? 0
    }, ct);
}

UsageContent? usage = null;

await foreach (var update in _client.GetStreamingResponseAsync(truncated, options, ct))
```

(Keep the rest of the method unchanged. Do not duplicate the `Select` body — only add `.ToList()` to its end and insert the truncator call beneath it.)

- [ ] **Step 4: Run tests to verify they pass (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenRouterChatClient"`
Expected: PASS — both new truncation tests AND existing `OpenRouterChatClientMetricsTests` still pass.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientTruncationTests.cs
git commit -m "feat(infra): truncate context inside OpenRouterChatClient"
```

---

## Task 12: Update existing positional callers of `OpenRouterChatClient` (compilation fix)

**Files:**
- Modify: `Agent/Modules/MemoryModule.cs`
- Modify: `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientMetricsTests.cs`

Both constructors gained a new optional `int? maxContextTokens` parameter inserted **before** `metricsPublisher`. Two existing callsites pass `metricsPublisher` positionally and will now fail to compile because `IMetricsPublisher` is not assignable to `int?`.

- [ ] **Step 1: Build first to confirm the failure**

Run: `dotnet build`
Expected: 3 errors:
- `MemoryModule.cs` line ~46 — argument type mismatch.
- `MemoryModule.cs` line ~60 — same.
- `OpenRouterChatClientMetricsTests.cs` line 19 — same.

- [ ] **Step 2: Use named argument in both `MemoryModule.cs` calls**

In `Agent/Modules/MemoryModule.cs`, find both `new OpenRouterChatClient(...)` calls. Replace the first one:

```csharp
var chatClient = new OpenRouterChatClient(
    openRouterConfig["apiUrl"]!, openRouterConfig["apiKey"]!,
    extractionModel, metricsPublisher);
```

with:

```csharp
var chatClient = new OpenRouterChatClient(
    openRouterConfig["apiUrl"]!, openRouterConfig["apiKey"]!,
    extractionModel, metricsPublisher: metricsPublisher);
```

Do the same for the `dreamingModel` call.

- [ ] **Step 3: Use named argument in `OpenRouterChatClientMetricsTests.cs`**

Replace line 19 in `Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientMetricsTests.cs`:

```csharp
_sut = new OpenRouterChatClient(_innerClient.Object, "test-model", _publisher.Object);
```

with:

```csharp
_sut = new OpenRouterChatClient(_innerClient.Object, "test-model", metricsPublisher: _publisher.Object);
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build`
Expected: build succeeds across all projects.

- [ ] **Step 5: Run all tests to confirm nothing else regressed**

Run: `dotnet test Tests/Tests.csproj`
Expected: PASS — including the previously broken `OpenRouterChatClientMetricsTests`.

- [ ] **Step 6: Commit**

```bash
git add Agent/Modules/MemoryModule.cs Tests/Unit/Infrastructure/Agents/ChatClients/OpenRouterChatClientMetricsTests.cs
git commit -m "fix: use named arg for metricsPublisher after OpenRouterChatClient ctor change"
```

---

## Task 13: Add `MaxContextTokens` to `OpenRouterConfig` and pass through `MultiAgentFactory`

**Files:**
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs`
- Modify: `Agent/Modules/InjectorModule.cs`

- [ ] **Step 1: Extend the `OpenRouterConfig` record**

In `Infrastructure/Agents/MultiAgentFactory.cs`, replace the `OpenRouterConfig` record at the bottom of the file:

```csharp
public record OpenRouterConfig
{
    public required string ApiUrl { get; init; }
    public required string ApiKey { get; init; }
    public int? MaxContextTokens { get; init; }
}
```

- [ ] **Step 2: Update `CreateChatClient` to resolve effective limit**

In the same file, replace `CreateChatClient` and update both call sites to pass the per-agent override. Replace the bottom `CreateChatClient`:

```csharp
private OpenRouterChatClient CreateChatClient(string model, IMetricsPublisher? publisher = null, int? maxContextTokens = null)
{
    return new OpenRouterChatClient(
        openRouterConfig.ApiUrl,
        openRouterConfig.ApiKey,
        model,
        maxContextTokens ?? openRouterConfig.MaxContextTokens,
        publisher ?? metricsPublisher);
}
```

In `CreateSubAgent`, change the line:

```csharp
var chatClient = CreateChatClient(definition.Model, agentPublisher);
```

to:

```csharp
var chatClient = CreateChatClient(definition.Model, agentPublisher, definition.MaxContextTokens);
```

In `CreateFromDefinition`, change the line:

```csharp
var chatClient = CreateChatClient(definition.Model, agentPublisher);
```

to:

```csharp
var chatClient = CreateChatClient(definition.Model, agentPublisher, definition.MaxContextTokens);
```

- [ ] **Step 3: Map `MaxContextTokens` from settings to `OpenRouterConfig`**

In `Agent/Modules/InjectorModule.cs`, replace the `llmConfig` initialization inside `AddAgent`:

```csharp
var llmConfig = new OpenRouterConfig
{
    ApiUrl = settings.OpenRouter.ApiUrl,
    ApiKey = settings.OpenRouter.ApiKey,
    MaxContextTokens = settings.OpenRouter.MaxContextTokens
};
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build`
Expected: build succeeds across all projects.

- [ ] **Step 5: Run all tests to confirm nothing regressed**

Run: `dotnet test Tests/Tests.csproj`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Agents/MultiAgentFactory.cs Agent/Modules/InjectorModule.cs
git commit -m "feat(agent): wire effective MaxContextTokens through MultiAgentFactory"
```

---

## Task 14: Add `ProcessContextTruncationAsync` to `MetricsCollectorService` (TDD)

**Files:**
- Modify: `Observability/Services/MetricsCollectorService.cs`
- Create: `Tests/Unit/Observability/Services/MetricsCollectorTruncationTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Observability/Services/MetricsCollectorTruncationTests.cs`:

```csharp
using Domain.DTOs.Metrics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Observability.Hubs;
using Observability.Services;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsCollectorTruncationTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly Mock<IHubContext<MetricsHub>> _hubContext = new();
    private readonly Mock<IHubClients> _hubClients = new();
    private readonly Mock<IClientProxy> _clientProxy = new();
    private readonly MetricsCollectorService _sut;

    public MetricsCollectorTruncationTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _hubClients.Setup(c => c.All).Returns(_clientProxy.Object);
        _hubContext.Setup(h => h.Clients).Returns(_hubClients.Object);

        _sut = new MetricsCollectorService(
            _redis.Object, _hubContext.Object,
            NullLogger<MetricsCollectorService>.Instance);
    }

    [Fact]
    public async Task ProcessEventAsync_ContextTruncation_IncrementsTotalsAndAddsToTimeline()
    {
        var evt = new ContextTruncationEvent
        {
            Sender = "alice",
            Model = "z-ai/glm-5.1",
            DroppedMessages = 3,
            EstimatedTokensBefore = 500,
            EstimatedTokensAfter = 350,
            MaxContextTokens = 400,
            Timestamp = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero)
        };

        await _sut.ProcessEventAsync(evt, _db.Object);

        _db.Verify(d => d.SortedSetAddAsync(
            "metrics:truncations:2026-05-03",
            It.IsAny<RedisValue>(),
            It.IsAny<double>(),
            It.IsAny<SortedSetWhen>(),
            It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-05-03", "truncations:count", 1, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-05-03", "truncations:dropped", 3, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-05-03", "truncations:tokensTrimmed", 150, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-05-03", "truncations:bySender:alice", 1, It.IsAny<CommandFlags>()), Times.Once);
        _db.Verify(d => d.HashIncrementAsync(
            "metrics:totals:2026-05-03", "truncations:byModel:z-ai/glm-5.1", 1, It.IsAny<CommandFlags>()), Times.Once);
        _clientProxy.Verify(p => p.SendCoreAsync(
            "OnContextTruncation",
            It.Is<object?[]>(args => args.Length == 1 && args[0] == evt),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run test to verify it fails (RED)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsCollectorTruncationTests"`
Expected: FAIL — no handler for `ContextTruncationEvent`.

- [ ] **Step 3: Add the handler**

In `Observability/Services/MetricsCollectorService.cs`, add a new case to the `ProcessEventAsync` switch:

```csharp
case ContextTruncationEvent truncation:
    await ProcessContextTruncationAsync(truncation, db);
    break;
```

Add this method to the class:

```csharp
private async Task ProcessContextTruncationAsync(ContextTruncationEvent evt, IDatabase db)
{
    var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
    var sortedSetKey = $"metrics:truncations:{dateKey}";
    var totalsKey = $"metrics:totals:{dateKey}";
    var score = evt.Timestamp.ToUnixTimeMilliseconds();
    var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);
    var trimmed = evt.EstimatedTokensBefore - evt.EstimatedTokensAfter;

    await Task.WhenAll(
        db.SortedSetAddAsync(sortedSetKey, json, score),
        db.HashIncrementAsync(totalsKey, "truncations:count", 1),
        db.HashIncrementAsync(totalsKey, "truncations:dropped", evt.DroppedMessages),
        db.HashIncrementAsync(totalsKey, "truncations:tokensTrimmed", trimmed),
        db.HashIncrementAsync(totalsKey, $"truncations:bySender:{evt.Sender}", 1),
        db.HashIncrementAsync(totalsKey, $"truncations:byModel:{evt.Model}", 1),
        db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
        db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

    await hubContext.Clients.All.SendAsync("OnContextTruncation", evt);
}
```

- [ ] **Step 4: Run test to verify it passes (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsCollectorTruncationTests"`
Expected: PASS.

- [ ] **Step 5: Run all observability tests to confirm no regression**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Observability"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Observability/Services/MetricsCollectorService.cs Tests/Unit/Observability/Services/MetricsCollectorTruncationTests.cs
git commit -m "feat(observability): collect ContextTruncationEvent to Redis"
```

---

## Task 15: Extend `MetricsQueryService.GetTokenGroupedAsync` for truncation metrics (TDD)

**Files:**
- Modify: `Observability/Services/MetricsQueryService.cs`
- Create: `Tests/Unit/Observability/Services/MetricsQueryServiceTruncationTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Observability/Services/MetricsQueryServiceTruncationTests.cs`:

```csharp
using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Moq;
using Observability.Services;
using Shouldly;
using StackExchange.Redis;

namespace Tests.Unit.Observability.Services;

public class MetricsQueryServiceTruncationTests
{
    private readonly Mock<IDatabase> _db = new();
    private readonly Mock<IConnectionMultiplexer> _redis = new();
    private readonly MetricsQueryService _sut;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public MetricsQueryServiceTruncationTests()
    {
        _redis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
            .Returns(_db.Object);
        _sut = new MetricsQueryService(_redis.Object);
    }

    private void SetupTruncationEvents(string key, IEnumerable<ContextTruncationEvent> events)
    {
        var entries = events
            .Select(e => new RedisValue(JsonSerializer.Serialize<MetricEvent>(e, _jsonOptions)))
            .ToArray();
        _db.Setup(d => d.SortedSetRangeByScoreAsync(key, It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(), It.IsAny<long>(),
                It.IsAny<CommandFlags>()))
            .ReturnsAsync(entries);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_TruncationCountByModel_GroupsByModel()
    {
        var date = new DateOnly(2026, 5, 3);
        var ts = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero);
        SetupTruncationEvents("metrics:truncations:2026-05-03",
        [
            new() { Sender = "alice", Model = "m-A", DroppedMessages = 1, EstimatedTokensBefore = 100, EstimatedTokensAfter = 80, MaxContextTokens = 100, Timestamp = ts },
            new() { Sender = "bob",   Model = "m-A", DroppedMessages = 2, EstimatedTokensBefore = 200, EstimatedTokensAfter = 150, MaxContextTokens = 200, Timestamp = ts },
            new() { Sender = "alice", Model = "m-B", DroppedMessages = 4, EstimatedTokensBefore = 400, EstimatedTokensAfter = 300, MaxContextTokens = 400, Timestamp = ts }
        ]);

        var result = await _sut.GetTokenGroupedAsync(
            TokenDimension.Model, TokenMetric.TruncationCount, date, date);

        result["m-A"].ShouldBe(2m);
        result["m-B"].ShouldBe(1m);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_MessagesDroppedByUser_SumsDroppedCounts()
    {
        var date = new DateOnly(2026, 5, 3);
        var ts = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero);
        SetupTruncationEvents("metrics:truncations:2026-05-03",
        [
            new() { Sender = "alice", Model = "m", DroppedMessages = 1, EstimatedTokensBefore = 100, EstimatedTokensAfter = 80, MaxContextTokens = 100, Timestamp = ts },
            new() { Sender = "alice", Model = "m", DroppedMessages = 3, EstimatedTokensBefore = 100, EstimatedTokensAfter = 80, MaxContextTokens = 100, Timestamp = ts },
            new() { Sender = "bob",   Model = "m", DroppedMessages = 2, EstimatedTokensBefore = 100, EstimatedTokensAfter = 80, MaxContextTokens = 100, Timestamp = ts }
        ]);

        var result = await _sut.GetTokenGroupedAsync(
            TokenDimension.User, TokenMetric.MessagesDropped, date, date);

        result["alice"].ShouldBe(4m);
        result["bob"].ShouldBe(2m);
    }

    [Fact]
    public async Task GetTokenGroupedAsync_TokensTrimmedByModel_SumsBeforeMinusAfter()
    {
        var date = new DateOnly(2026, 5, 3);
        var ts = new DateTimeOffset(2026, 5, 3, 10, 0, 0, TimeSpan.Zero);
        SetupTruncationEvents("metrics:truncations:2026-05-03",
        [
            new() { Sender = "alice", Model = "m-A", DroppedMessages = 1, EstimatedTokensBefore = 500, EstimatedTokensAfter = 400, MaxContextTokens = 500, Timestamp = ts },
            new() { Sender = "bob",   Model = "m-A", DroppedMessages = 1, EstimatedTokensBefore = 200, EstimatedTokensAfter = 100, MaxContextTokens = 200, Timestamp = ts }
        ]);

        var result = await _sut.GetTokenGroupedAsync(
            TokenDimension.Model, TokenMetric.TokensTrimmed, date, date);

        result["m-A"].ShouldBe(200m);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (RED)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceTruncationTests"`
Expected: FAIL — `GetTokenGroupedAsync` throws on `TruncationCount`.

- [ ] **Step 3: Branch the query on metric type**

In `Observability/Services/MetricsQueryService.cs`, replace `GetTokenGroupedAsync`:

```csharp
public async Task<Dictionary<string, decimal>> GetTokenGroupedAsync(
    TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to)
{
    return metric switch
    {
        TokenMetric.Tokens or TokenMetric.Cost
            => await GroupTokenUsageAsync(dimension, metric, from, to),
        TokenMetric.TruncationCount or TokenMetric.MessagesDropped or TokenMetric.TokensTrimmed
            => await GroupTruncationsAsync(dimension, metric, from, to),
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };
}

private async Task<Dictionary<string, decimal>> GroupTokenUsageAsync(
    TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to)
{
    var events = await GetEventsAsync<TokenUsageEvent>("metrics:tokens:", from, to);
    return events
        .GroupBy(e => dimension switch
        {
            TokenDimension.User => e.Sender,
            TokenDimension.Model => e.Model,
            TokenDimension.Agent => e.AgentId ?? "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        })
        .ToDictionary(
            g => g.Key,
            g => metric switch
            {
                TokenMetric.Tokens => g.Sum(e => (decimal)(e.InputTokens + e.OutputTokens)),
                TokenMetric.Cost => g.Sum(e => e.Cost),
                _ => throw new ArgumentOutOfRangeException(nameof(metric))
            });
}

private async Task<Dictionary<string, decimal>> GroupTruncationsAsync(
    TokenDimension dimension, TokenMetric metric, DateOnly from, DateOnly to)
{
    var events = await GetEventsAsync<ContextTruncationEvent>("metrics:truncations:", from, to);
    return events
        .GroupBy(e => dimension switch
        {
            TokenDimension.User => e.Sender,
            TokenDimension.Model => e.Model,
            TokenDimension.Agent => e.AgentId ?? "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        })
        .ToDictionary(
            g => g.Key,
            g => metric switch
            {
                TokenMetric.TruncationCount => (decimal)g.Count(),
                TokenMetric.MessagesDropped => g.Sum(e => (decimal)e.DroppedMessages),
                TokenMetric.TokensTrimmed   => g.Sum(e => (decimal)(e.EstimatedTokensBefore - e.EstimatedTokensAfter)),
                _ => throw new ArgumentOutOfRangeException(nameof(metric))
            });
}
```

- [ ] **Step 4: Run tests to verify they pass (GREEN)**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceTruncationTests"`
Expected: PASS.

- [ ] **Step 5: Run all query service tests to confirm no regression**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryService"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add Observability/Services/MetricsQueryService.cs Tests/Unit/Observability/Services/MetricsQueryServiceTruncationTests.cs
git commit -m "feat(observability): branch GetTokenGroupedAsync on truncation metrics"
```

---

## Task 16: Add Truncations KPI card and metric pills on `Tokens.razor`

**Files:**
- Modify: `Dashboard.Client/Pages/Tokens.razor`

`MetricOptions` is hard-coded in `Tokens.razor` (verified: lines 81–85), so the three new pills must be added explicitly. `MetricsApiService.GetTokenGroupedAsync` already exists, so no changes are needed in `Dashboard.Client/Services/MetricsApiService.cs`.

- [ ] **Step 1: Add the three new metric pill options**

In `Dashboard.Client/Pages/Tokens.razor`, replace the `MetricOptions` block (currently lines 81–85):

```csharp
private static readonly IReadOnlyList<PillOption> MetricOptions =
[
    new("Tokens", nameof(TokenMetric.Tokens)),
    new("Cost ($)", nameof(TokenMetric.Cost)),
    new("Truncations", nameof(TokenMetric.TruncationCount)),
    new("Msgs dropped", nameof(TokenMetric.MessagesDropped)),
    new("Tokens trimmed", nameof(TokenMetric.TokensTrimmed)),
];
```

- [ ] **Step 2: Add a `_truncations` backing field**

In the `@code` block (around line 71, next to `_cost`):

```csharp
private long _truncations;
```

- [ ] **Step 3: Add the Truncations KPI card to the KPI row**

In the `<section class="kpi-row">` block (around lines 25–29), append after the `Cost` card:

```razor
<KpiCard Label="Truncations" Value="@_truncations.ToString("N0")" Color="var(--accent-yellow)" />
```

- [ ] **Step 4: Load `_truncations` from the API on init and on time change**

Add a private helper in the `@code` block:

```csharp
private async Task ReloadTruncationsAsync()
{
    var breakdown = await Api.GetTokenGroupedAsync(
        TokenDimension.Model, TokenMetric.TruncationCount, _from, _to);
    _truncations = breakdown is null ? 0 : (long)breakdown.Values.Sum();
    await InvokeAsync(StateHasChanged);
}
```

In `OnInitializedAsync`, after `await DataLoad.LoadAsync(_from, _to);` (around line 116), append:

```csharp
await ReloadTruncationsAsync();
```

In `OnTimeChanged`, after `await DataLoad.LoadAsync(_from, _to);` (around line 140), append:

```csharp
await ReloadTruncationsAsync();
```

- [ ] **Step 5: Build the dashboard project**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: build succeeds.

- [ ] **Step 6: Commit**

```bash
git add Dashboard.Client/Pages/Tokens.razor
git commit -m "feat(dashboard): show context truncation KPIs on Tokens page

Manual UI test required: load /dashboard/tokens with the docker stack
running and confirm the Truncations KPI card and the three new metric
pills (TruncationCount, MessagesDropped, TokensTrimmed) render and that
selecting a truncation metric repopulates the chart."
```

---

## Task 17: Add `maxContextTokens` to appsettings skeleton

**Files:**
- Modify: `Agent/appsettings.json`
- Modify: `Agent/appsettings.Local.json`

- [ ] **Step 1: Add the field to `Agent/appsettings.json`**

Inside the `"openRouter"` block, add `"maxContextTokens": null`:

```jsonc
"openRouter": {
    "apiUrl": "https://openrouter.ai/api/v1/",
    "apiKey": "",
    "maxContextTokens": null
},
```

- [ ] **Step 2: Same for `Agent/appsettings.Local.json`**

Add `"maxContextTokens": null` (or a developer-preferred number) inside the `"openRouter"` block. If the file does not contain `"openRouter"`, leave it untouched.

- [ ] **Step 3: Build and run all tests to confirm config loads**

Run: `dotnet build`
Run: `dotnet test Tests/Tests.csproj`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add Agent/appsettings.json Agent/appsettings.Local.json
git commit -m "chore(config): add maxContextTokens skeleton to appsettings"
```

---

## Task 18: Final verification — run the whole suite

- [ ] **Step 1: Full solution build**

Run: `dotnet build`
Expected: build succeeds across all projects.

- [ ] **Step 2: Full test suite**

Run: `dotnet test Tests/Tests.csproj`
Expected: all tests PASS — both new tests and pre-existing suite.

- [ ] **Step 3: Confirm no leftover stubs**

Run: `grep -rn "TODO\|TBD" Infrastructure/Agents/ChatClients/MessageTruncator.cs Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs Domain/DTOs/Metrics/ContextTruncationEvent.cs Observability/Services/MetricsCollectorService.cs Observability/Services/MetricsQueryService.cs`
Expected: no matches.

- [ ] **Step 4: Optional manual smoke test**

If the docker stack is available locally, follow the launch instructions in `CLAUDE.md` and:
- Open `https://assistants.herfluffness.com/dashboard/tokens` and confirm the new KPI card and metric pills are rendered.
- Send a long enough conversation through WebChat to trigger truncation and confirm the KPI value increments.

(Truncation only fires when `openRouter.maxContextTokens` or a per-agent `maxContextTokens` is set to a value smaller than the conversation. For a smoke test, set it to a small number like `5000` in `appsettings.Local.json`.)
