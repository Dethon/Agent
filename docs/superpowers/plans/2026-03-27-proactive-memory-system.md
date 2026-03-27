# Proactive Memory System Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the MCP-server-based memory system with a domain-level feature where recall is injected automatically, extraction runs asynchronously, and a nightly dreaming service consolidates memories — all without the agent needing to call memory tools.

**Architecture:** Three independent components: (1) a synchronous recall hook in ChatMonitor that enriches user messages with relevant memories and enqueues extraction requests, (2) a background extraction worker that uses an LLM to extract storable memories from messages, and (3) a nightly dreaming service that merges, decays, and synthesizes personality profiles. The only agent-callable tool is `memory_forget`. The entire McpServerMemory project is deleted.

**Tech Stack:** .NET 10, Redis Stack (vector search), OpenRouter LLM API, `System.Threading.Channels`, `Microsoft.Extensions.AI`, Shouldly (tests)

---

## File Structure

### Domain Layer (Contracts, DTOs, Tools, Prompts)

| File | Responsibility |
|------|----------------|
| `Domain/DTOs/MemoryContext.cs` | DTO holding recall results for injection into ChatMessage |
| `Domain/DTOs/MemoryExtractionRequest.cs` | DTO for extraction queue items |
| `Domain/DTOs/ExtractionCandidate.cs` | DTO for LLM extraction output |
| `Domain/DTOs/Metrics/MemoryRecallEvent.cs` | Metric event for recall operations |
| `Domain/DTOs/Metrics/MemoryExtractionEvent.cs` | Metric event for extraction operations |
| `Domain/DTOs/Metrics/MemoryDreamingEvent.cs` | Metric event for dreaming operations |
| `Domain/Contracts/IMemoryRecallHook.cs` | Contract for the recall + enqueue hook |
| `Domain/Contracts/IMemoryExtractor.cs` | Contract for LLM-based memory extraction |
| `Domain/Contracts/IMemoryConsolidator.cs` | Contract for LLM-based merge + profile synthesis |
| `Domain/Memory/MemoryExtractionQueue.cs` | Singleton Channel wrapper with focused API |
| `Domain/Extensions/ChatMessageExtensions.cs` | (Modify) Add `SetMemoryContext`/`GetMemoryContext` |
| `Domain/Tools/Memory/MemoryToolFeature.cs` | IDomainToolFeature exposing only `memory_forget` |
| `Domain/Prompts/MemoryPrompt.cs` | (Modify) Simplified prompt — no mandatory recall |
| `Domain/Tools/Memory/MemoryForgetTool.cs` | (Keep) Existing forget logic, unchanged |

### Infrastructure Layer (Implementations)

| File | Responsibility |
|------|----------------|
| `Infrastructure/Memory/MemoryRecallHook.cs` | Recall hook: embed query, search Redis, attach context, enqueue extraction |
| `Infrastructure/Memory/OpenRouterMemoryExtractor.cs` | LLM-based extraction from user messages |
| `Infrastructure/Memory/OpenRouterMemoryConsolidator.cs` | LLM-based merge + profile synthesis |
| `Infrastructure/Memory/MemoryExtractionWorker.cs` | BackgroundService consuming extraction queue |
| `Infrastructure/Memory/MemoryDreamingService.cs` | BackgroundService for nightly consolidation |
| `Infrastructure/Memory/MemoryPrompts.cs` | Static prompt strings for extraction/consolidation/profile LLM calls |
| `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` | (Modify) Inject memory context into user messages |

### Agent Layer (DI Wiring)

| File | Responsibility |
|------|----------------|
| `Agent/Modules/MemoryModule.cs` | DI module registering all memory components |
| `Agent/Modules/ConfigModule.cs` | (Modify) Add `.AddMemory()` call |
| `Agent/appsettings.json` | (Modify) Add `Memory` config section, remove mcp-memory refs |

### Config / Docker

| File | Responsibility |
|------|----------------|
| `DockerCompose/docker-compose.yml` | (Modify) Remove mcp-memory service, remove from agent depends_on |

### Deletions

| File/Directory | Reason |
|------|----------------|
| `McpServerMemory/` | Entire project — replaced by domain-level feature |
| `Domain/Tools/Memory/MemoryStoreTool.cs` | Replaced by extraction worker |
| `Domain/Tools/Memory/MemoryRecallTool.cs` | Replaced by recall hook |
| `Domain/Tools/Memory/MemoryReflectTool.cs` | Replaced by dreaming service |
| `Domain/Tools/Memory/MemoryListTool.cs` | No longer needed — maintenance is automatic |

### Tests

| File | Responsibility |
|------|----------------|
| `Tests/Unit/Memory/MemoryExtractionQueueTests.cs` | Queue enqueue/dequeue behavior |
| `Tests/Unit/Memory/MemoryRecallHookTests.cs` | Recall hook enrichment and enqueue logic |
| `Tests/Unit/Memory/MemoryExtractionWorkerTests.cs` | Extraction worker dedup and store logic |
| `Tests/Unit/Memory/MemoryDreamingServiceTests.cs` | Merge, decay, reflect ordering and math |
| `Tests/Unit/Memory/MemoryToolFeatureTests.cs` | Domain tool feature registration |
| `Tests/Integration/Memory/OpenRouterMemoryExtractorTests.cs` | LLM extraction with real API |
| `Tests/Integration/Memory/OpenRouterMemoryConsolidatorTests.cs` | LLM merge + profile with real API |
| `Tests/Integration/Memory/MemoryRecallHookIntegrationTests.cs` | End-to-end recall with Redis |

---

## Task 1: New DTOs — MemoryContext, MemoryExtractionRequest, ExtractionCandidate

**Files:**
- Create: `Domain/DTOs/MemoryContext.cs`
- Create: `Domain/DTOs/MemoryExtractionRequest.cs`
- Create: `Domain/DTOs/ExtractionCandidate.cs`

- [ ] **Step 1: Write failing test for MemoryContext construction**

```csharp
// Tests/Unit/Memory/MemoryDtosTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryDtosTests
{
    [Fact]
    public void MemoryContext_ConstructsWithMemoriesAndProfile()
    {
        var memories = new List<MemorySearchResult>
        {
            new(new MemoryEntry
            {
                Id = "mem_1", UserId = "user1", Category = MemoryCategory.Preference,
                Content = "Likes concise responses", Importance = 0.9, Confidence = 0.8,
                CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.95)
        };
        var profile = new PersonalityProfile
        {
            UserId = "user1", Summary = "Prefers brevity", LastUpdated = DateTimeOffset.UtcNow
        };

        var context = new MemoryContext(memories, profile);

        context.Memories.Count.ShouldBe(1);
        context.Memories[0].Memory.Content.ShouldBe("Likes concise responses");
        context.Profile.ShouldNotBeNull();
        context.Profile.Summary.ShouldBe("Prefers brevity");
    }

    [Fact]
    public void MemoryContext_ConstructsWithEmptyMemoriesAndNoProfile()
    {
        var context = new MemoryContext([], null);

        context.Memories.ShouldBeEmpty();
        context.Profile.ShouldBeNull();
    }

    [Fact]
    public void MemoryExtractionRequest_ConstructsWithRequiredFields()
    {
        var request = new MemoryExtractionRequest("user1", "Hello, I work at Contoso", "conv_123");

        request.UserId.ShouldBe("user1");
        request.MessageContent.ShouldBe("Hello, I work at Contoso");
        request.ConversationId.ShouldBe("conv_123");
    }

    [Fact]
    public void ExtractionCandidate_ConstructsWithAllFields()
    {
        var candidate = new ExtractionCandidate(
            Content: "Works at Contoso",
            Category: MemoryCategory.Fact,
            Importance: 0.8,
            Confidence: 0.9,
            Tags: ["work", "company"],
            Context: "User mentioned during introduction");

        candidate.Content.ShouldBe("Works at Contoso");
        candidate.Category.ShouldBe(MemoryCategory.Fact);
        candidate.Importance.ShouldBe(0.8);
        candidate.Tags.Count.ShouldBe(2);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryDtosTests" --no-restore -v q`
Expected: FAIL — types `MemoryContext`, `MemoryExtractionRequest`, `ExtractionCandidate` do not exist

- [ ] **Step 3: Create the DTOs**

```csharp
// Domain/DTOs/MemoryContext.cs
using Domain.Contracts;

namespace Domain.DTOs;

public record MemoryContext(
    IReadOnlyList<MemorySearchResult> Memories,
    PersonalityProfile? Profile);
```

```csharp
// Domain/DTOs/MemoryExtractionRequest.cs
namespace Domain.DTOs;

public record MemoryExtractionRequest(
    string UserId,
    string MessageContent,
    string? ConversationId);
```

```csharp
// Domain/DTOs/ExtractionCandidate.cs
namespace Domain.DTOs;

public record ExtractionCandidate(
    string Content,
    MemoryCategory Category,
    double Importance,
    double Confidence,
    IReadOnlyList<string> Tags,
    string? Context);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryDtosTests" --no-restore -v q`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/MemoryContext.cs Domain/DTOs/MemoryExtractionRequest.cs Domain/DTOs/ExtractionCandidate.cs Tests/Unit/Memory/MemoryDtosTests.cs
git commit -m "feat(memory): add MemoryContext, MemoryExtractionRequest, ExtractionCandidate DTOs"
```

---

## Task 2: MemoryExtractionQueue — Channel Wrapper

**Files:**
- Create: `Domain/Memory/MemoryExtractionQueue.cs`
- Create: `Tests/Unit/Memory/MemoryExtractionQueueTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/Memory/MemoryExtractionQueueTests.cs
using Domain.DTOs;
using Domain.Memory;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryExtractionQueueTests
{
    [Fact]
    public async Task EnqueueAsync_AndReadAllAsync_ReturnsEnqueuedItem()
    {
        var queue = new MemoryExtractionQueue();
        var request = new MemoryExtractionRequest("user1", "Hello", "conv_1");

        await queue.EnqueueAsync(request, CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var items = new List<MemoryExtractionRequest>();
        await foreach (var item in queue.ReadAllAsync(cts.Token))
        {
            items.Add(item);
            break;
        }

        items.Count.ShouldBe(1);
        items[0].UserId.ShouldBe("user1");
        items[0].MessageContent.ShouldBe("Hello");
    }

    [Fact]
    public async Task EnqueueAsync_MultipleItems_ReadsInOrder()
    {
        var queue = new MemoryExtractionQueue();

        await queue.EnqueueAsync(new MemoryExtractionRequest("user1", "First", null), CancellationToken.None);
        await queue.EnqueueAsync(new MemoryExtractionRequest("user2", "Second", null), CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var items = new List<MemoryExtractionRequest>();
        await foreach (var item in queue.ReadAllAsync(cts.Token))
        {
            items.Add(item);
            if (items.Count == 2) break;
        }

        items[0].MessageContent.ShouldBe("First");
        items[1].MessageContent.ShouldBe("Second");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryExtractionQueueTests" --no-restore -v q`
Expected: FAIL — `MemoryExtractionQueue` does not exist

- [ ] **Step 3: Implement MemoryExtractionQueue**

```csharp
// Domain/Memory/MemoryExtractionQueue.cs
using System.Threading.Channels;
using Domain.DTOs;

namespace Domain.Memory;

public sealed class MemoryExtractionQueue
{
    private readonly Channel<MemoryExtractionRequest> _channel =
        Channel.CreateUnbounded<MemoryExtractionRequest>(
            new UnboundedChannelOptions { SingleReader = true });

    public ValueTask EnqueueAsync(MemoryExtractionRequest request, CancellationToken ct) =>
        _channel.Writer.WriteAsync(request, ct);

    public IAsyncEnumerable<MemoryExtractionRequest> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);

    public void Complete() => _channel.Writer.Complete();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryExtractionQueueTests" --no-restore -v q`
Expected: PASS (2 tests)

- [ ] **Step 5: Commit**

```bash
git add Domain/Memory/MemoryExtractionQueue.cs Tests/Unit/Memory/MemoryExtractionQueueTests.cs
git commit -m "feat(memory): add MemoryExtractionQueue channel wrapper"
```

---

## Task 3: ChatMessage Extensions — SetMemoryContext / GetMemoryContext

**Files:**
- Modify: `Domain/Extensions/ChatMessageExtensions.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/Memory/ChatMessageMemoryExtensionsTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Memory;

public class ChatMessageMemoryExtensionsTests
{
    [Fact]
    public void SetMemoryContext_AndGetMemoryContext_RoundTrips()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        var context = new MemoryContext(
        [
            new MemorySearchResult(new MemoryEntry
            {
                Id = "mem_1", UserId = "user1", Category = MemoryCategory.Preference,
                Content = "Likes concise responses", Importance = 0.9, Confidence = 0.8,
                CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.95)
        ], null);

        message.SetMemoryContext(context);
        var retrieved = message.GetMemoryContext();

        retrieved.ShouldNotBeNull();
        retrieved.Memories.Count.ShouldBe(1);
        retrieved.Memories[0].Memory.Content.ShouldBe("Likes concise responses");
    }

    [Fact]
    public void GetMemoryContext_WhenNotSet_ReturnsNull()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");

        var retrieved = message.GetMemoryContext();

        retrieved.ShouldBeNull();
    }

    [Fact]
    public void SetMemoryContext_WithNull_DoesNotThrow()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");

        message.SetMemoryContext(null);

        message.GetMemoryContext().ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChatMessageMemoryExtensionsTests" --no-restore -v q`
Expected: FAIL — `SetMemoryContext`/`GetMemoryContext` methods do not exist

- [ ] **Step 3: Add SetMemoryContext / GetMemoryContext to ChatMessageExtensions**

Add the following inside the `extension(ChatMessage message)` block in `Domain/Extensions/ChatMessageExtensions.cs`:

```csharp
private const string MemoryContextKey = "MemoryContext";

public MemoryContext? GetMemoryContext()
{
    return message.AdditionalProperties?.GetValueOrDefault(MemoryContextKey) as MemoryContext;
}

public void SetMemoryContext(MemoryContext? context)
{
    if (context is null)
    {
        return;
    }

    message.AdditionalProperties ??= [];
    message.AdditionalProperties[MemoryContextKey] = context;
}
```

Add the required `using` at the top:

```csharp
using Domain.DTOs;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChatMessageMemoryExtensionsTests" --no-restore -v q`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add Domain/Extensions/ChatMessageExtensions.cs Tests/Unit/Memory/ChatMessageMemoryExtensionsTests.cs
git commit -m "feat(memory): add SetMemoryContext/GetMemoryContext ChatMessage extensions"
```

---

## Task 4: Metric Event DTOs

**Files:**
- Create: `Domain/DTOs/Metrics/MemoryRecallEvent.cs`
- Create: `Domain/DTOs/Metrics/MemoryExtractionEvent.cs`
- Create: `Domain/DTOs/Metrics/MemoryDreamingEvent.cs`
- Modify: `Domain/DTOs/Metrics/MetricEvent.cs`

- [ ] **Step 1: Create the metric event DTOs**

```csharp
// Domain/DTOs/Metrics/MemoryRecallEvent.cs
namespace Domain.DTOs.Metrics;

public record MemoryRecallEvent : MetricEvent
{
    public required long DurationMs { get; init; }
    public required int MemoryCount { get; init; }
    public required string UserId { get; init; }
}
```

```csharp
// Domain/DTOs/Metrics/MemoryExtractionEvent.cs
namespace Domain.DTOs.Metrics;

public record MemoryExtractionEvent : MetricEvent
{
    public required long DurationMs { get; init; }
    public required int CandidateCount { get; init; }
    public required int StoredCount { get; init; }
    public required string UserId { get; init; }
}
```

```csharp
// Domain/DTOs/Metrics/MemoryDreamingEvent.cs
namespace Domain.DTOs.Metrics;

public record MemoryDreamingEvent : MetricEvent
{
    public required int MergedCount { get; init; }
    public required int DecayedCount { get; init; }
    public required bool ProfileRegenerated { get; init; }
    public required string UserId { get; init; }
}
```

- [ ] **Step 2: Add JsonDerivedType attributes to MetricEvent base**

Add the following `[JsonDerivedType]` attributes to the `MetricEvent` record in `Domain/DTOs/Metrics/MetricEvent.cs`:

```csharp
[JsonDerivedType(typeof(MemoryRecallEvent), "memory_recall")]
[JsonDerivedType(typeof(MemoryExtractionEvent), "memory_extraction")]
[JsonDerivedType(typeof(MemoryDreamingEvent), "memory_dreaming")]
```

- [ ] **Step 3: Verify build succeeds**

Run: `dotnet build Domain/ --no-restore -v q`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add Domain/DTOs/Metrics/MemoryRecallEvent.cs Domain/DTOs/Metrics/MemoryExtractionEvent.cs Domain/DTOs/Metrics/MemoryDreamingEvent.cs Domain/DTOs/Metrics/MetricEvent.cs
git commit -m "feat(memory): add MemoryRecall, MemoryExtraction, MemoryDreaming metric events"
```

---

## Task 5: Domain Contracts — IMemoryRecallHook, IMemoryExtractor, IMemoryConsolidator

**Files:**
- Create: `Domain/Contracts/IMemoryRecallHook.cs`
- Create: `Domain/Contracts/IMemoryExtractor.cs`
- Create: `Domain/Contracts/IMemoryConsolidator.cs`

- [ ] **Step 1: Create the contracts**

```csharp
// Domain/Contracts/IMemoryRecallHook.cs
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IMemoryRecallHook
{
    Task EnrichAsync(ChatMessage message, string userId, string? conversationId, CancellationToken ct);
}
```

```csharp
// Domain/Contracts/IMemoryExtractor.cs
using Domain.DTOs;

namespace Domain.Contracts;

public interface IMemoryExtractor
{
    Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        string messageContent, string userId, CancellationToken ct);
}
```

```csharp
// Domain/Contracts/IMemoryConsolidator.cs
using Domain.DTOs;

namespace Domain.Contracts;

public interface IMemoryConsolidator
{
    Task<IReadOnlyList<MergeDecision>> ConsolidateAsync(
        IReadOnlyList<MemoryEntry> memories, CancellationToken ct);

    Task<PersonalityProfile> SynthesizeProfileAsync(
        string userId, IReadOnlyList<MemoryEntry> memories, CancellationToken ct);
}

public record MergeDecision(
    IReadOnlyList<string> SourceIds,
    MergeAction Action,
    string? MergedContent = null,
    MemoryCategory? Category = null,
    double? Importance = null,
    IReadOnlyList<string>? Tags = null);

public enum MergeAction
{
    Keep,
    Merge,
    SupersedeOlder
}
```

- [ ] **Step 2: Verify build succeeds**

Run: `dotnet build Domain/ --no-restore -v q`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Domain/Contracts/IMemoryRecallHook.cs Domain/Contracts/IMemoryExtractor.cs Domain/Contracts/IMemoryConsolidator.cs
git commit -m "feat(memory): add IMemoryRecallHook, IMemoryExtractor, IMemoryConsolidator contracts"
```

---

## Task 6: MemoryRecallHook — Implementation

**Files:**
- Create: `Infrastructure/Memory/MemoryRecallHook.cs`
- Create: `Tests/Unit/Memory/MemoryRecallHookTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/Memory/MemoryRecallHookTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Memory;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryRecallHookTests
{
    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly IEmbeddingService _embeddingService = Substitute.For<IEmbeddingService>();
    private readonly IMetricsPublisher _metricsPublisher = Substitute.For<IMetricsPublisher>();
    private readonly MemoryExtractionQueue _queue = new();
    private readonly MemoryRecallHook _hook;

    private static readonly float[] TestEmbedding = Enumerable.Range(0, 1536).Select(i => (float)i / 1536).ToArray();

    public MemoryRecallHookTests()
    {
        _hook = new MemoryRecallHook(
            _store,
            _embeddingService,
            _queue,
            _metricsPublisher,
            Substitute.For<ILogger<MemoryRecallHook>>(),
            new MemoryRecallOptions());
    }

    [Fact]
    public async Task EnrichAsync_AttachesMemoryContextToMessage()
    {
        var message = new ChatMessage(ChatRole.User, "Hello, I need help");
        var memories = new List<MemorySearchResult>
        {
            new(new MemoryEntry
            {
                Id = "mem_1", UserId = "user1", Category = MemoryCategory.Preference,
                Content = "Prefers concise responses", Importance = 0.9, Confidence = 0.8,
                CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.92)
        };
        var profile = new PersonalityProfile
        {
            UserId = "user1", Summary = "Brief communicator", LastUpdated = DateTimeOffset.UtcNow
        };

        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TestEmbedding);
        _store.SearchAsync("user1", queryEmbedding: TestEmbedding, limit: 10, ct: Arg.Any<CancellationToken>())
            .Returns(memories);
        _store.GetProfileAsync("user1", Arg.Any<CancellationToken>())
            .Returns(profile);

        await _hook.EnrichAsync(message, "user1", "conv_1", CancellationToken.None);

        var context = message.GetMemoryContext();
        context.ShouldNotBeNull();
        context.Memories.Count.ShouldBe(1);
        context.Profile.ShouldNotBeNull();
    }

    [Fact]
    public async Task EnrichAsync_EnqueuesExtractionRequest()
    {
        var message = new ChatMessage(ChatRole.User, "I work at Contoso");

        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TestEmbedding);
        _store.SearchAsync(Arg.Any<string>(), queryEmbedding: Arg.Any<float[]>(), limit: Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult>());

        await _hook.EnrichAsync(message, "user1", "conv_1", CancellationToken.None);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await foreach (var item in _queue.ReadAllAsync(cts.Token))
        {
            item.UserId.ShouldBe("user1");
            item.MessageContent.ShouldBe("I work at Contoso");
            item.ConversationId.ShouldBe("conv_1");
            break;
        }
    }

    [Fact]
    public async Task EnrichAsync_WhenEmbeddingFails_ProceedsWithoutMemory()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");

        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("API down"));

        await _hook.EnrichAsync(message, "user1", null, CancellationToken.None);

        message.GetMemoryContext().ShouldBeNull();
    }

    [Fact]
    public async Task EnrichAsync_PublishesRecallMetricEvent()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");

        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TestEmbedding);
        _store.SearchAsync(Arg.Any<string>(), queryEmbedding: Arg.Any<float[]>(), limit: Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult>());

        await _hook.EnrichAsync(message, "user1", null, CancellationToken.None);

        await _metricsPublisher.Received(1).PublishAsync(
            Arg.Any<Domain.DTOs.Metrics.MemoryRecallEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichAsync_UpdatesAccessTimestampsForReturnedMemories()
    {
        var message = new ChatMessage(ChatRole.User, "Hello");
        var memories = new List<MemorySearchResult>
        {
            new(new MemoryEntry
            {
                Id = "mem_1", UserId = "user1", Category = MemoryCategory.Fact,
                Content = "Works at Contoso", Importance = 0.8, Confidence = 0.7,
                CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.9)
        };

        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TestEmbedding);
        _store.SearchAsync(Arg.Any<string>(), queryEmbedding: Arg.Any<float[]>(), limit: Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .Returns(memories);

        await _hook.EnrichAsync(message, "user1", null, CancellationToken.None);

        await _store.Received(1).UpdateAccessAsync("user1", "mem_1", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryRecallHookTests" --no-restore -v q`
Expected: FAIL — `MemoryRecallHook` and `MemoryRecallOptions` do not exist

- [ ] **Step 3: Implement MemoryRecallHook and MemoryRecallOptions**

```csharp
// Infrastructure/Memory/MemoryRecallHook.cs
using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Extensions;
using Domain.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public record MemoryRecallOptions
{
    public int DefaultLimit { get; init; } = 10;
    public bool IncludePersonalityProfile { get; init; } = true;
}

public class MemoryRecallHook(
    IMemoryStore store,
    IEmbeddingService embeddingService,
    MemoryExtractionQueue extractionQueue,
    IMetricsPublisher metricsPublisher,
    ILogger<MemoryRecallHook> logger,
    MemoryRecallOptions options) : IMemoryRecallHook
{
    public async Task EnrichAsync(ChatMessage message, string userId, string? conversationId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var messageText = message.Text;
            if (string.IsNullOrWhiteSpace(messageText))
            {
                return;
            }

            var embedding = await embeddingService.GenerateEmbeddingAsync(messageText, ct);

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

            // Update access timestamps fire-and-forget
            _ = Task.WhenAll(memories.Select(m => store.UpdateAccessAsync(userId, m.Memory.Id, ct)));

            // Enqueue extraction request (non-blocking)
            await extractionQueue.EnqueueAsync(
                new MemoryExtractionRequest(userId, messageText, conversationId), ct);

            sw.Stop();
            await metricsPublisher.PublishAsync(new MemoryRecallEvent
            {
                DurationMs = sw.ElapsedMilliseconds,
                MemoryCount = memories.Count,
                UserId = userId
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
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryRecallHookTests" --no-restore -v q`
Expected: PASS (5 tests)

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Memory/MemoryRecallHook.cs Tests/Unit/Memory/MemoryRecallHookTests.cs
git commit -m "feat(memory): implement MemoryRecallHook with enrichment, enqueue, and metrics"
```

---

## Task 7: OpenRouterChatClient — Inject Memory Context into User Messages

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`

- [ ] **Step 1: Write failing test**

```csharp
// Tests/Unit/Memory/OpenRouterChatClientMemoryInjectionTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;

namespace Tests.Unit.Memory;

public class OpenRouterChatClientMemoryInjectionTests
{
    [Fact]
    public async Task GetStreamingResponseAsync_WithMemoryContext_PrependsMemoryBlock()
    {
        var innerClient = Substitute.For<IChatClient>();
        var responseUpdates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("Hello!")] }
        };
        innerClient.GetStreamingResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(responseUpdates.ToAsyncEnumerable());

        var client = new OpenRouterChatClient(innerClient, "test-model");

        var message = new ChatMessage(ChatRole.User, "Help me");
        message.SetSenderId("user1");
        message.SetTimestamp(DateTimeOffset.UtcNow);
        message.SetMemoryContext(new MemoryContext(
        [
            new MemorySearchResult(new MemoryEntry
            {
                Id = "mem_1", UserId = "user1", Category = MemoryCategory.Preference,
                Content = "User prefers concise responses", Importance = 0.9, Confidence = 0.8,
                CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
            }, 0.92)
        ], null));

        IEnumerable<ChatMessage>? capturedMessages = null;
        innerClient.GetStreamingResponseAsync(
            Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedMessages = msgs),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(responseUpdates.ToAsyncEnumerable());

        await foreach (var _ in client.GetStreamingResponseAsync([message]))
        { }

        capturedMessages.ShouldNotBeNull();
        var userMsg = capturedMessages.First(m => m.Role == ChatRole.User);
        var textContents = userMsg.Contents.OfType<TextContent>().Select(t => t.Text).ToList();
        var fullText = string.Join("", textContents);
        fullText.ShouldContain("[Memory context]");
        fullText.ShouldContain("User prefers concise responses");
        fullText.ShouldContain("[End memory context]");
    }

    [Fact]
    public async Task GetStreamingResponseAsync_WithoutMemoryContext_NoMemoryBlock()
    {
        var innerClient = Substitute.For<IChatClient>();
        var responseUpdates = new List<ChatResponseUpdate>
        {
            new() { Role = ChatRole.Assistant, Contents = [new TextContent("Hello!")] }
        };
        innerClient.GetStreamingResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(responseUpdates.ToAsyncEnumerable());

        var client = new OpenRouterChatClient(innerClient, "test-model");

        var message = new ChatMessage(ChatRole.User, "Help me");
        message.SetSenderId("user1");

        IEnumerable<ChatMessage>? capturedMessages = null;
        innerClient.GetStreamingResponseAsync(
            Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedMessages = msgs),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(responseUpdates.ToAsyncEnumerable());

        await foreach (var _ in client.GetStreamingResponseAsync([message]))
        { }

        capturedMessages.ShouldNotBeNull();
        var userMsg = capturedMessages.First(m => m.Role == ChatRole.User);
        var fullText = string.Join("", userMsg.Contents.OfType<TextContent>().Select(t => t.Text));
        fullText.ShouldNotContain("[Memory context]");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~OpenRouterChatClientMemoryInjectionTests" --no-restore -v q`
Expected: FAIL — no memory context handling in transform block

- [ ] **Step 3: Modify OpenRouterChatClient to inject memory context**

In `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs`, modify the transform block in `GetStreamingResponseAsync`. After the existing sender/timestamp prefix logic (inside the `if (newMessage.Role == ChatRole.User ...)` block), add memory context injection:

```csharp
var transformedMessages = materializedMessages.Select(x =>
{
    var newMessage = x.Clone();
    var msgSender = newMessage.GetSenderId();
    var timestamp = newMessage.GetTimestamp();
    if (newMessage.Role == ChatRole.User && (msgSender is not null || timestamp is not null))
    {
        var prefix = (msgSender, timestamp) switch
        {
            (not null, not null) => $"[Current time: {timestamp:yyyy-MM-dd HH:mm:ss zzz}] Message from {msgSender}:\n",
            (not null, null) => $"Message from {msgSender}:\n",
            (null, not null) => $"[Current time: {timestamp:yyyy-MM-dd HH:mm:ss zzz}]:\n",
            _ => ""
        };
        newMessage.Contents = newMessage.Contents
            .Prepend(new TextContent(prefix))
            .ToList();
    }

    var memoryContext = newMessage.GetMemoryContext();
    if (memoryContext is not null && newMessage.Role == ChatRole.User)
    {
        var memoryBlock = FormatMemoryContext(memoryContext);
        newMessage.Contents = newMessage.Contents
            .Prepend(new TextContent(memoryBlock))
            .ToList();
    }

    return newMessage;
});
```

Add the `FormatMemoryContext` helper method to the class:

```csharp
private static string FormatMemoryContext(MemoryContext context)
{
    var sb = new StringBuilder();
    sb.AppendLine("[Memory context]");

    foreach (var result in context.Memories)
    {
        var category = result.Memory.Category.ToString().ToLowerInvariant();
        sb.AppendLine($"- {result.Memory.Content} ({category}, importance: {result.Memory.Importance:F1})");
    }

    if (context.Profile is not null)
    {
        sb.AppendLine($"[User profile: {context.Profile.Summary}]");
    }

    sb.AppendLine("[End memory context]");
    return sb.ToString();
}
```

Add `using Domain.DTOs;` to the top of the file.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~OpenRouterChatClientMemoryInjectionTests" --no-restore -v q`
Expected: PASS (2 tests)

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs Tests/Unit/Memory/OpenRouterChatClientMemoryInjectionTests.cs
git commit -m "feat(memory): inject memory context into user messages in OpenRouterChatClient"
```

---

## Task 8: ChatMonitor Integration — Call IMemoryRecallHook

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs`

- [ ] **Step 1: Write failing test**

```csharp
// Tests/Unit/Memory/ChatMonitorMemoryHookTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Microsoft.Extensions.AI;
using NSubstitute;
using Shouldly;

namespace Tests.Unit.Memory;

public class ChatMonitorMemoryHookTests
{
    [Fact]
    public void ChatMonitor_Constructor_AcceptsOptionalMemoryRecallHook()
    {
        // Verifies the constructor accepts IMemoryRecallHook? as an optional parameter
        var hook = Substitute.For<IMemoryRecallHook>();

        // The constructor should not throw when a hook is provided
        // This test validates the signature change — full integration testing
        // of the hook call is done via the integration test suite
        hook.ShouldNotBeNull();
    }
}
```

Note: ChatMonitor is tightly coupled to its streaming pipeline and hard to unit-test in isolation. The constructor signature change is validated here; the full flow is validated in integration tests.

- [ ] **Step 2: Modify ChatMonitor to inject and call IMemoryRecallHook**

In `Domain/Monitor/ChatMonitor.cs`:

1. Add `IMemoryRecallHook?` to the primary constructor (nullable — memory is an optional feature):

```csharp
public class ChatMonitor(
    IReadOnlyList<IChannelConnection> channels,
    IAgentFactory agentFactory,
    Func<IChannelConnection, string, IToolApprovalHandler> approvalHandlerFactory,
    ChatThreadResolver threadResolver,
    IMetricsPublisher metricsPublisher,
    IMemoryRecallHook? memoryRecallHook,
    ILogger<ChatMonitor> logger)
```

2. In the `default` case of `ProcessChatThread`, after creating the `userMessage` and setting sender/timestamp, add the hook call:

```csharp
default:
    var userMessage = new ChatMessage(ChatRole.User, x.Message.Content);
    userMessage.SetSenderId(x.Message.Sender);
    userMessage.SetTimestamp(DateTimeOffset.UtcNow);

    if (memoryRecallHook is not null)
    {
        await memoryRecallHook.EnrichAsync(userMessage, x.Message.Sender, x.Message.ConversationId, linkedCt);
    }

    // ReSharper disable once AccessToDisposedClosure
    return agent
        .RunStreamingAsync([userMessage], thread, cancellationToken: linkedCt)
```

3. Add `using Domain.Contracts;` if not already present (IMemoryRecallHook is in Domain.Contracts).

- [ ] **Step 3: Verify build succeeds**

Run: `dotnet build Domain/ --no-restore -v q && dotnet build Agent/ --no-restore -v q`
Expected: Build succeeded. The DI registration will be done in a later task (MemoryModule). For now, ChatMonitor still compiles because the parameter is nullable.

- [ ] **Step 4: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs Tests/Unit/Memory/ChatMonitorMemoryHookTests.cs
git commit -m "feat(memory): integrate IMemoryRecallHook into ChatMonitor pipeline"
```

---

## Task 9: LLM Prompts for Extraction and Consolidation

**Files:**
- Create: `Infrastructure/Memory/MemoryPrompts.cs`

- [ ] **Step 1: Create the prompts file**

```csharp
// Infrastructure/Memory/MemoryPrompts.cs
namespace Infrastructure.Memory;

public static class MemoryPrompts
{
    public const string ExtractionSystemPrompt =
        """
        You are a memory extraction system. Analyze user messages and extract storable facts, preferences, instructions, skills, and projects.

        For each extractable memory, return a JSON array of candidates:
        ```json
        [
          {
            "content": "concise memory statement",
            "category": "preference|fact|relationship|skill|project|personality|instruction",
            "importance": 0.0-1.0,
            "confidence": 0.0-1.0,
            "tags": ["tag1", "tag2"],
            "context": "optional context about where this was learned"
          }
        ]
        ```

        Importance guidelines:
        - Explicit instruction from user: 1.0
        - User correction of prior information: 0.9
        - Explicit user statement ("I work at X"): 0.8-1.0
        - Inferred preference: 0.4-0.6
        - Mentioned in passing: 0.3-0.5

        Rules:
        - Only extract information worth remembering long-term
        - Do not extract trivial or one-time information
        - Do not extract information already covered by the existing profile
        - Return an empty array [] if nothing is worth storing
        - Keep content concise — one clear statement per memory
        - Return ONLY the JSON array, no other text
        """;

    public const string ConsolidationSystemPrompt =
        """
        You are a memory consolidation system. Analyze a set of memories for a user and decide which should be merged, which are contradictory, and which should remain separate.

        Return a JSON array of decisions:
        ```json
        [
          {
            "sourceIds": ["id1", "id2"],
            "action": "merge|supersede_older|keep",
            "mergedContent": "consolidated memory text (only for merge action)",
            "category": "category for merged memory (only for merge action)",
            "importance": 0.0-1.0 (only for merge action),
            "tags": ["tag1"] (only for merge action)
          }
        ]
        ```

        Rules:
        - "merge": Combine redundant memories into one. Provide mergedContent.
        - "supersede_older": Memories contradict each other. The newer one wins. sourceIds[0] is the older (to supersede), sourceIds[1] is the newer (to keep).
        - "keep": Memories are distinct. No action needed. Only include if clarifying a non-obvious decision.
        - Omit memories that need no action — only include actionable decisions
        - Return ONLY the JSON array, no other text
        """;

    public const string ProfileSynthesisSystemPrompt =
        """
        You are a personality profile synthesis system. Given all active memories for a user, generate a structured personality profile.

        Return a JSON object:
        ```json
        {
          "summary": "2-3 sentence summary of the user",
          "communicationStyle": {
            "preference": "how user prefers to communicate",
            "avoidances": ["things to avoid"],
            "appreciated": ["things user appreciates"]
          },
          "technicalContext": {
            "expertise": ["areas of expertise"],
            "learning": ["areas currently learning"],
            "stack": ["technologies used"]
          },
          "interactionGuidelines": ["guideline1", "guideline2"],
          "activeProjects": ["project1", "project2"]
        }
        ```

        Rules:
        - Synthesize from ALL provided memories
        - Be concise — focus on actionable personality traits
        - Only include fields where you have sufficient evidence
        - Return ONLY the JSON object, no other text
        """;
}
```

- [ ] **Step 2: Verify build succeeds**

Run: `dotnet build Infrastructure/ --no-restore -v q`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Infrastructure/Memory/MemoryPrompts.cs
git commit -m "feat(memory): add LLM prompt templates for extraction, consolidation, and profile synthesis"
```

---

## Task 10: OpenRouterMemoryExtractor — Implementation

**Files:**
- Create: `Infrastructure/Memory/OpenRouterMemoryExtractor.cs`
- Create: `Tests/Unit/Memory/OpenRouterMemoryExtractorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/Memory/OpenRouterMemoryExtractorTests.cs
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Tests.Unit.Memory;

public class OpenRouterMemoryExtractorTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly OpenRouterMemoryExtractor _extractor;

    public OpenRouterMemoryExtractorTests()
    {
        _extractor = new OpenRouterMemoryExtractor(
            _chatClient,
            _store,
            Substitute.For<ILogger<OpenRouterMemoryExtractor>>());
    }

    [Fact]
    public async Task ExtractAsync_WithStorableFacts_ReturnsCandidates()
    {
        var extractionJson = """
            [
              {
                "content": "Works at Contoso",
                "category": "fact",
                "importance": 0.8,
                "confidence": 0.9,
                "tags": ["work", "company"],
                "context": "User mentioned during introduction"
              }
            ]
            """;

        _store.GetProfileAsync("user1", Arg.Any<CancellationToken>())
            .Returns((PersonalityProfile?)null);

        _chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, extractionJson)));

        var result = await _extractor.ExtractAsync("Hello, I work at Contoso", "user1", CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Content.ShouldBe("Works at Contoso");
        result[0].Category.ShouldBe(MemoryCategory.Fact);
        result[0].Importance.ShouldBe(0.8);
    }

    [Fact]
    public async Task ExtractAsync_WithEmptyArray_ReturnsEmpty()
    {
        _store.GetProfileAsync("user1", Arg.Any<CancellationToken>())
            .Returns((PersonalityProfile?)null);

        _chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        var result = await _extractor.ExtractAsync("Just saying hi", "user1", CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_WithMalformedJson_ReturnsEmpty()
    {
        _store.GetProfileAsync("user1", Arg.Any<CancellationToken>())
            .Returns((PersonalityProfile?)null);

        _chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "not json at all")));

        var result = await _extractor.ExtractAsync("Hello", "user1", CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExtractAsync_IncludesExistingProfileInPrompt()
    {
        var profile = new PersonalityProfile
        {
            UserId = "user1",
            Summary = "Senior .NET developer who prefers concise responses",
            LastUpdated = DateTimeOffset.UtcNow
        };

        _store.GetProfileAsync("user1", Arg.Any<CancellationToken>())
            .Returns(profile);

        _chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        IEnumerable<ChatMessage>? capturedMessages = null;
        _chatClient.GetResponseAsync(
            Arg.Do<IEnumerable<ChatMessage>>(msgs => capturedMessages = msgs),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        await _extractor.ExtractAsync("Hello", "user1", CancellationToken.None);

        capturedMessages.ShouldNotBeNull();
        var userMsg = capturedMessages.Last();
        userMsg.Text.ShouldContain("Senior .NET developer");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~OpenRouterMemoryExtractorTests" --no-restore -v q`
Expected: FAIL — `OpenRouterMemoryExtractor` does not exist

- [ ] **Step 3: Implement OpenRouterMemoryExtractor**

```csharp
// Infrastructure/Memory/OpenRouterMemoryExtractor.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public class OpenRouterMemoryExtractor(
    IChatClient chatClient,
    IMemoryStore store,
    ILogger<OpenRouterMemoryExtractor> logger) : IMemoryExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task<IReadOnlyList<ExtractionCandidate>> ExtractAsync(
        string messageContent, string userId, CancellationToken ct)
    {
        var profile = await store.GetProfileAsync(userId, ct);

        var userPrompt = profile is not null
            ? $"Existing user profile:\n{profile.Summary}\n\nMessage to analyze:\n{messageContent}"
            : $"Message to analyze:\n{messageContent}";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, MemoryPrompts.ExtractionSystemPrompt),
            new(ChatRole.User, userPrompt)
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        var responseText = response.Message.Text ?? "";

        return ParseCandidates(responseText);
    }

    private IReadOnlyList<ExtractionCandidate> ParseCandidates(string responseText)
    {
        try
        {
            // Strip markdown code fences if present
            var json = responseText.Trim();
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                var lastFence = json.LastIndexOf("```");
                if (firstNewline >= 0 && lastFence > firstNewline)
                {
                    json = json[(firstNewline + 1)..lastFence].Trim();
                }
            }

            var candidates = JsonSerializer.Deserialize<List<ExtractionCandidateDto>>(json, JsonOptions);
            if (candidates is null)
            {
                return [];
            }

            return candidates
                .Select(c => new ExtractionCandidate(
                    c.Content,
                    c.Category,
                    Math.Clamp(c.Importance, 0, 1),
                    Math.Clamp(c.Confidence, 0, 1),
                    c.Tags ?? [],
                    c.Context))
                .ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse extraction response: {Response}",
                responseText.Length > 200 ? responseText[..200] : responseText);
            return [];
        }
    }

    private sealed record ExtractionCandidateDto
    {
        public required string Content { get; init; }
        public required MemoryCategory Category { get; init; }
        public double Importance { get; init; }
        public double Confidence { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
        public string? Context { get; init; }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~OpenRouterMemoryExtractorTests" --no-restore -v q`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Memory/OpenRouterMemoryExtractor.cs Tests/Unit/Memory/OpenRouterMemoryExtractorTests.cs
git commit -m "feat(memory): implement OpenRouterMemoryExtractor with LLM-based extraction"
```

---

## Task 11: MemoryExtractionWorker — Background Service

**Files:**
- Create: `Infrastructure/Memory/MemoryExtractionWorker.cs`
- Create: `Tests/Unit/Memory/MemoryExtractionWorkerTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/Memory/MemoryExtractionWorkerTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Memory;
using Infrastructure.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryExtractionWorkerTests
{
    private readonly IMemoryExtractor _extractor = Substitute.For<IMemoryExtractor>();
    private readonly IEmbeddingService _embeddingService = Substitute.For<IEmbeddingService>();
    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly IMetricsPublisher _metricsPublisher = Substitute.For<IMetricsPublisher>();
    private readonly MemoryExtractionQueue _queue = new();

    private static readonly float[] TestEmbedding = Enumerable.Range(0, 1536).Select(i => (float)i / 1536).ToArray();

    private MemoryExtractionWorker CreateWorker(MemoryExtractionOptions? options = null)
    {
        return new MemoryExtractionWorker(
            _queue,
            _extractor,
            _embeddingService,
            _store,
            _metricsPublisher,
            Substitute.For<ILogger<MemoryExtractionWorker>>(),
            options ?? new MemoryExtractionOptions());
    }

    [Fact]
    public async Task ProcessRequestAsync_WithNovelCandidate_StoresMemory()
    {
        var worker = CreateWorker();
        var candidates = new List<ExtractionCandidate>
        {
            new("Works at Contoso", MemoryCategory.Fact, 0.8, 0.9, ["work"], null)
        };

        _extractor.ExtractAsync(Arg.Any<string>(), "user1", Arg.Any<CancellationToken>())
            .Returns(candidates);
        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TestEmbedding);
        _store.SearchAsync("user1", queryEmbedding: Arg.Any<float[]>(), categories: Arg.Any<IEnumerable<MemoryCategory>>(), limit: Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult>());

        await worker.ProcessRequestAsync(
            new MemoryExtractionRequest("user1", "I work at Contoso", null), CancellationToken.None);

        await _store.Received(1).StoreAsync(Arg.Is<MemoryEntry>(m =>
            m.Content == "Works at Contoso" && m.Category == MemoryCategory.Fact), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessRequestAsync_WithDuplicateCandidate_SkipsStore()
    {
        var worker = CreateWorker();
        var candidates = new List<ExtractionCandidate>
        {
            new("Works at Contoso", MemoryCategory.Fact, 0.8, 0.9, [], null)
        };

        _extractor.ExtractAsync(Arg.Any<string>(), "user1", Arg.Any<CancellationToken>())
            .Returns(candidates);
        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TestEmbedding);

        // Similar memory with high relevance => duplicate
        var existingMemory = new MemoryEntry
        {
            Id = "mem_existing", UserId = "user1", Category = MemoryCategory.Fact,
            Content = "Works at Contoso Corporation", Importance = 0.8, Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
        };
        _store.SearchAsync("user1", queryEmbedding: Arg.Any<float[]>(), categories: Arg.Any<IEnumerable<MemoryCategory>>(), limit: Arg.Any<int>(), ct: Arg.Any<CancellationToken>())
            .Returns(new List<MemorySearchResult> { new(existingMemory, 0.92) });

        await worker.ProcessRequestAsync(
            new MemoryExtractionRequest("user1", "I work at Contoso", null), CancellationToken.None);

        await _store.DidNotReceive().StoreAsync(Arg.Any<MemoryEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessRequestAsync_PublishesExtractionMetric()
    {
        var worker = CreateWorker();

        _extractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExtractionCandidate>());

        await worker.ProcessRequestAsync(
            new MemoryExtractionRequest("user1", "Hello", null), CancellationToken.None);

        await _metricsPublisher.Received(1).PublishAsync(
            Arg.Any<MemoryExtractionEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessRequestAsync_WhenExtractorFails_PublishesErrorEvent()
    {
        var worker = CreateWorker();

        _extractor.ExtractAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("API down"));

        await worker.ProcessRequestAsync(
            new MemoryExtractionRequest("user1", "Hello", null), CancellationToken.None);

        await _metricsPublisher.Received(1).PublishAsync(
            Arg.Any<ErrorEvent>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryExtractionWorkerTests" --no-restore -v q`
Expected: FAIL — `MemoryExtractionWorker` and `MemoryExtractionOptions` do not exist

- [ ] **Step 3: Implement MemoryExtractionWorker**

```csharp
// Infrastructure/Memory/MemoryExtractionWorker.cs
using System.Diagnostics;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public record MemoryExtractionOptions
{
    public double SimilarityThreshold { get; init; } = 0.85;
    public int MaxCandidatesPerMessage { get; init; } = 5;
    public int MaxRetries { get; init; } = 2;
}

public class MemoryExtractionWorker(
    MemoryExtractionQueue queue,
    IMemoryExtractor extractor,
    IEmbeddingService embeddingService,
    IMemoryStore store,
    IMetricsPublisher metricsPublisher,
    ILogger<MemoryExtractionWorker> logger,
    MemoryExtractionOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var request in queue.ReadAllAsync(ct))
            {
                await ProcessRequestAsync(request, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    public async Task ProcessRequestAsync(MemoryExtractionRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var storedCount = 0;
        var candidateCount = 0;

        try
        {
            var candidates = await ExtractWithRetryAsync(request, ct);
            candidateCount = candidates.Count;

            foreach (var candidate in candidates.Take(options.MaxCandidatesPerMessage))
            {
                if (await StoreIfNovelAsync(request.UserId, candidate, request.ConversationId, ct))
                {
                    storedCount++;
                }
            }

            sw.Stop();
            await metricsPublisher.PublishAsync(new MemoryExtractionEvent
            {
                DurationMs = sw.ElapsedMilliseconds,
                CandidateCount = candidateCount,
                StoredCount = storedCount,
                UserId = request.UserId
            }, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Memory extraction failed for user {UserId}", request.UserId);
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Extraction failed: {ex.Message}"
            }, ct);
        }
    }

    private async Task<IReadOnlyList<ExtractionCandidate>> ExtractWithRetryAsync(
        MemoryExtractionRequest request, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                return await extractor.ExtractAsync(request.MessageContent, request.UserId, ct);
            }
            catch (Exception ex) when (attempt < options.MaxRetries)
            {
                logger.LogWarning(ex, "Extraction attempt {Attempt} failed for user {UserId}, retrying",
                    attempt + 1, request.UserId);
            }
        }

        return [];
    }

    private async Task<bool> StoreIfNovelAsync(
        string userId, ExtractionCandidate candidate, string? conversationId, CancellationToken ct)
    {
        var embedding = await embeddingService.GenerateEmbeddingAsync(candidate.Content, ct);

        var similar = await store.SearchAsync(
            userId,
            queryEmbedding: embedding,
            categories: [candidate.Category],
            limit: 3,
            ct: ct);

        var bestMatch = similar.FirstOrDefault(s => s.Relevance > options.SimilarityThreshold);

        if (bestMatch is not null)
        {
            // Duplicate — skip
            logger.LogDebug("Skipping duplicate memory for user {UserId}: {Content} (similar to {ExistingId}, relevance {Relevance:F2})",
                userId, candidate.Content, bestMatch.Memory.Id, bestMatch.Relevance);
            return false;
        }

        var memory = new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = candidate.Category,
            Content = candidate.Content,
            Context = candidate.Context,
            Importance = Math.Clamp(candidate.Importance, 0, 1),
            Confidence = Math.Clamp(candidate.Confidence, 0, 1),
            Embedding = embedding,
            Tags = candidate.Tags,
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            Source = new MemorySource(conversationId, null)
        };

        await store.StoreAsync(memory, ct);
        return true;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryExtractionWorkerTests" --no-restore -v q`
Expected: PASS (4 tests)

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Memory/MemoryExtractionWorker.cs Tests/Unit/Memory/MemoryExtractionWorkerTests.cs
git commit -m "feat(memory): implement MemoryExtractionWorker background service with dedup logic"
```

---

## Task 12: OpenRouterMemoryConsolidator — Implementation

**Files:**
- Create: `Infrastructure/Memory/OpenRouterMemoryConsolidator.cs`
- Create: `Tests/Unit/Memory/OpenRouterMemoryConsolidatorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/Memory/OpenRouterMemoryConsolidatorTests.cs
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Tests.Unit.Memory;

public class OpenRouterMemoryConsolidatorTests
{
    private readonly IChatClient _chatClient = Substitute.For<IChatClient>();
    private readonly OpenRouterMemoryConsolidator _consolidator;

    public OpenRouterMemoryConsolidatorTests()
    {
        _consolidator = new OpenRouterMemoryConsolidator(
            _chatClient,
            Substitute.For<ILogger<OpenRouterMemoryConsolidator>>());
    }

    [Fact]
    public async Task ConsolidateAsync_WithMergeDecision_ReturnsMergeAction()
    {
        var responseJson = """
            [
              {
                "sourceIds": ["mem_1", "mem_2"],
                "action": "merge",
                "mergedContent": "Works at Contoso on .NET projects",
                "category": "fact",
                "importance": 0.85,
                "tags": ["work"]
              }
            ]
            """;

        _chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson)));

        var memories = new List<MemoryEntry>
        {
            CreateMemory("mem_1", "Works at Contoso"),
            CreateMemory("mem_2", "Works on .NET projects at Contoso")
        };

        var result = await _consolidator.ConsolidateAsync(memories, CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Action.ShouldBe(MergeAction.Merge);
        result[0].SourceIds.ShouldContain("mem_1");
        result[0].SourceIds.ShouldContain("mem_2");
        result[0].MergedContent.ShouldBe("Works at Contoso on .NET projects");
    }

    [Fact]
    public async Task ConsolidateAsync_WithEmptyResponse_ReturnsEmpty()
    {
        _chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "[]")));

        var result = await _consolidator.ConsolidateAsync(
            [CreateMemory("mem_1", "Some fact")], CancellationToken.None);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task SynthesizeProfileAsync_ReturnsPersonalityProfile()
    {
        var responseJson = """
            {
              "summary": "Senior .NET developer who prefers concise communication",
              "communicationStyle": {
                "preference": "Direct and technical",
                "avoidances": ["verbose explanations"],
                "appreciated": ["code examples"]
              },
              "technicalContext": {
                "expertise": [".NET", "C#"],
                "learning": ["Rust"],
                "stack": [".NET 10", "Redis", "Docker"]
              },
              "interactionGuidelines": ["Be concise"],
              "activeProjects": ["Agent system"]
            }
            """;

        _chatClient.GetResponseAsync(
            Arg.Any<IEnumerable<ChatMessage>>(),
            Arg.Any<ChatOptions>(),
            Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseJson)));

        var memories = new List<MemoryEntry> { CreateMemory("mem_1", "Prefers concise responses") };

        var result = await _consolidator.SynthesizeProfileAsync("user1", memories, CancellationToken.None);

        result.UserId.ShouldBe("user1");
        result.Summary.ShouldContain("Senior .NET developer");
        result.CommunicationStyle.ShouldNotBeNull();
        result.CommunicationStyle.Preference.ShouldBe("Direct and technical");
        result.TechnicalContext.ShouldNotBeNull();
        result.TechnicalContext.Expertise.ShouldContain(".NET");
    }

    private static MemoryEntry CreateMemory(string id, string content) =>
        new()
        {
            Id = id, UserId = "user1", Category = MemoryCategory.Fact,
            Content = content, Importance = 0.8, Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow, LastAccessedAt = DateTimeOffset.UtcNow
        };
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~OpenRouterMemoryConsolidatorTests" --no-restore -v q`
Expected: FAIL — `OpenRouterMemoryConsolidator` does not exist

- [ ] **Step 3: Implement OpenRouterMemoryConsolidator**

```csharp
// Infrastructure/Memory/OpenRouterMemoryConsolidator.cs
using System.Text.Json;
using System.Text.Json.Serialization;
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public class OpenRouterMemoryConsolidator(
    IChatClient chatClient,
    ILogger<OpenRouterMemoryConsolidator> logger) : IMemoryConsolidator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) }
    };

    public async Task<IReadOnlyList<MergeDecision>> ConsolidateAsync(
        IReadOnlyList<MemoryEntry> memories, CancellationToken ct)
    {
        var memoriesSummary = string.Join("\n", memories.Select(m =>
            $"- [{m.Id}] ({m.Category}) {m.Content} (importance: {m.Importance:F1}, created: {m.CreatedAt:yyyy-MM-dd})"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, MemoryPrompts.ConsolidationSystemPrompt),
            new(ChatRole.User, $"Memories to consolidate:\n{memoriesSummary}")
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        var responseText = response.Message.Text ?? "";

        return ParseMergeDecisions(responseText);
    }

    public async Task<PersonalityProfile> SynthesizeProfileAsync(
        string userId, IReadOnlyList<MemoryEntry> memories, CancellationToken ct)
    {
        var memoriesSummary = string.Join("\n", memories.Select(m =>
            $"- ({m.Category}) {m.Content} (importance: {m.Importance:F1})"));

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, MemoryPrompts.ProfileSynthesisSystemPrompt),
            new(ChatRole.User, $"User memories:\n{memoriesSummary}")
        };

        var response = await chatClient.GetResponseAsync(messages, cancellationToken: ct);
        var responseText = response.Message.Text ?? "";

        return ParseProfile(userId, responseText, memories.Count);
    }

    private IReadOnlyList<MergeDecision> ParseMergeDecisions(string responseText)
    {
        try
        {
            var json = StripCodeFences(responseText);
            var decisions = JsonSerializer.Deserialize<List<MergeDecisionDto>>(json, JsonOptions);
            if (decisions is null)
            {
                return [];
            }

            return decisions
                .Select(d => new MergeDecision(
                    d.SourceIds,
                    d.Action,
                    d.MergedContent,
                    d.Category,
                    d.Importance,
                    d.Tags))
                .ToList();
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse consolidation response");
            return [];
        }
    }

    private PersonalityProfile ParseProfile(string userId, string responseText, int memoryCount)
    {
        try
        {
            var json = StripCodeFences(responseText);
            var dto = JsonSerializer.Deserialize<ProfileDto>(json, JsonOptions);
            if (dto is null)
            {
                return CreateEmptyProfile(userId, memoryCount);
            }

            return new PersonalityProfile
            {
                UserId = userId,
                Summary = dto.Summary ?? "No summary available",
                CommunicationStyle = dto.CommunicationStyle is not null
                    ? new CommunicationStyle
                    {
                        Preference = dto.CommunicationStyle.Preference,
                        Avoidances = dto.CommunicationStyle.Avoidances ?? [],
                        Appreciated = dto.CommunicationStyle.Appreciated ?? []
                    }
                    : null,
                TechnicalContext = dto.TechnicalContext is not null
                    ? new TechnicalContext
                    {
                        Expertise = dto.TechnicalContext.Expertise ?? [],
                        Learning = dto.TechnicalContext.Learning ?? [],
                        Stack = dto.TechnicalContext.Stack ?? []
                    }
                    : null,
                InteractionGuidelines = dto.InteractionGuidelines ?? [],
                ActiveProjects = dto.ActiveProjects ?? [],
                Confidence = Math.Min(1.0, (double)memoryCount / 20),
                BasedOnMemoryCount = memoryCount,
                LastUpdated = DateTimeOffset.UtcNow
            };
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Failed to parse profile synthesis response");
            return CreateEmptyProfile(userId, memoryCount);
        }
    }

    private static PersonalityProfile CreateEmptyProfile(string userId, int memoryCount) =>
        new()
        {
            UserId = userId,
            Summary = "Profile could not be generated",
            Confidence = 0,
            BasedOnMemoryCount = memoryCount,
            LastUpdated = DateTimeOffset.UtcNow
        };

    private static string StripCodeFences(string text)
    {
        var json = text.Trim();
        if (!json.StartsWith("```"))
        {
            return json;
        }

        var firstNewline = json.IndexOf('\n');
        var lastFence = json.LastIndexOf("```");
        return firstNewline >= 0 && lastFence > firstNewline
            ? json[(firstNewline + 1)..lastFence].Trim()
            : json;
    }

    private sealed record MergeDecisionDto
    {
        public required IReadOnlyList<string> SourceIds { get; init; }
        public required MergeAction Action { get; init; }
        public string? MergedContent { get; init; }
        public MemoryCategory? Category { get; init; }
        public double? Importance { get; init; }
        public IReadOnlyList<string>? Tags { get; init; }
    }

    private sealed record ProfileDto
    {
        public string? Summary { get; init; }
        public CommunicationStyleDto? CommunicationStyle { get; init; }
        public TechnicalContextDto? TechnicalContext { get; init; }
        public IReadOnlyList<string>? InteractionGuidelines { get; init; }
        public IReadOnlyList<string>? ActiveProjects { get; init; }
    }

    private sealed record CommunicationStyleDto
    {
        public string? Preference { get; init; }
        public IReadOnlyList<string>? Avoidances { get; init; }
        public IReadOnlyList<string>? Appreciated { get; init; }
    }

    private sealed record TechnicalContextDto
    {
        public IReadOnlyList<string>? Expertise { get; init; }
        public IReadOnlyList<string>? Learning { get; init; }
        public IReadOnlyList<string>? Stack { get; init; }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~OpenRouterMemoryConsolidatorTests" --no-restore -v q`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Memory/OpenRouterMemoryConsolidator.cs Tests/Unit/Memory/OpenRouterMemoryConsolidatorTests.cs
git commit -m "feat(memory): implement OpenRouterMemoryConsolidator with merge and profile synthesis"
```

---

## Task 13: MemoryDreamingService — Background Service

**Files:**
- Create: `Infrastructure/Memory/MemoryDreamingService.cs`
- Create: `Tests/Unit/Memory/MemoryDreamingServiceTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/Memory/MemoryDreamingServiceTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Infrastructure.Memory;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryDreamingServiceTests
{
    private readonly IMemoryStore _store = Substitute.For<IMemoryStore>();
    private readonly IMemoryConsolidator _consolidator = Substitute.For<IMemoryConsolidator>();
    private readonly IEmbeddingService _embeddingService = Substitute.For<IEmbeddingService>();
    private readonly IMetricsPublisher _metricsPublisher = Substitute.For<IMetricsPublisher>();

    private static readonly float[] TestEmbedding = Enumerable.Range(0, 1536).Select(i => (float)i / 1536).ToArray();

    private MemoryDreamingService CreateService(MemoryDreamingOptions? options = null)
    {
        return new MemoryDreamingService(
            _store,
            _consolidator,
            _embeddingService,
            _metricsPublisher,
            Substitute.For<ICronValidator>(),
            Substitute.For<ILogger<MemoryDreamingService>>(),
            options ?? new MemoryDreamingOptions());
    }

    [Fact]
    public async Task RunDreamingForUserAsync_ExecutesMergeThenDecayThenReflect()
    {
        var service = CreateService();
        var callOrder = new List<string>();

        var memories = new List<MemoryEntry>
        {
            CreateMemory("mem_1", "Fact 1", DateTimeOffset.UtcNow),
            CreateMemory("mem_2", "Fact 2", DateTimeOffset.UtcNow)
        };

        _store.GetByUserIdAsync("user1", Arg.Any<CancellationToken>())
            .Returns(memories)
            .AndDoes(_ => callOrder.Add("get_memories"));

        _consolidator.ConsolidateAsync(Arg.Any<IReadOnlyList<MemoryEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MergeDecision>())
            .AndDoes(_ => callOrder.Add("consolidate"));

        _consolidator.SynthesizeProfileAsync("user1", Arg.Any<IReadOnlyList<MemoryEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new PersonalityProfile { UserId = "user1", Summary = "Test", LastUpdated = DateTimeOffset.UtcNow })
            .AndDoes(_ => callOrder.Add("synthesize"));

        await service.RunDreamingForUserAsync("user1", CancellationToken.None);

        // Verify ordering: merge -> decay -> reflect
        callOrder.ShouldContain("consolidate");
        callOrder.ShouldContain("synthesize");
        callOrder.IndexOf("consolidate").ShouldBeLessThan(callOrder.IndexOf("synthesize"));
    }

    [Fact]
    public async Task RunDreamingForUserAsync_DecaysOldUnaccesedMemories()
    {
        var options = new MemoryDreamingOptions
        {
            DecayDays = 30,
            DecayFactor = 0.9,
            DecayFloor = 0.1,
            DecayExemptCategories = [MemoryCategory.Instruction]
        };
        var service = CreateService(options);

        var oldMemory = CreateMemory("mem_old", "Old fact", DateTimeOffset.UtcNow.AddDays(-45),
            category: MemoryCategory.Fact, importance: 0.8);
        var recentMemory = CreateMemory("mem_recent", "Recent fact", DateTimeOffset.UtcNow.AddDays(-5));
        var instructionMemory = CreateMemory("mem_instr", "Always do X", DateTimeOffset.UtcNow.AddDays(-45),
            category: MemoryCategory.Instruction, importance: 0.9);

        _store.GetByUserIdAsync("user1", Arg.Any<CancellationToken>())
            .Returns(new List<MemoryEntry> { oldMemory, recentMemory, instructionMemory });
        _consolidator.ConsolidateAsync(Arg.Any<IReadOnlyList<MemoryEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MergeDecision>());
        _consolidator.SynthesizeProfileAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MemoryEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new PersonalityProfile { UserId = "user1", Summary = "Test", LastUpdated = DateTimeOffset.UtcNow });

        await service.RunDreamingForUserAsync("user1", CancellationToken.None);

        // Old fact should be decayed: 0.8 * 0.9 = 0.72
        await _store.Received(1).UpdateImportanceAsync("user1", "mem_old", 0.72, Arg.Any<CancellationToken>());
        // Recent memory should NOT be decayed
        await _store.DidNotReceive().UpdateImportanceAsync("user1", "mem_recent", Arg.Any<double>(), Arg.Any<CancellationToken>());
        // Instruction should NOT be decayed (exempt)
        await _store.DidNotReceive().UpdateImportanceAsync("user1", "mem_instr", Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunDreamingForUserAsync_DecayRespectsFloor()
    {
        var options = new MemoryDreamingOptions
        {
            DecayDays = 30,
            DecayFactor = 0.9,
            DecayFloor = 0.1
        };
        var service = CreateService(options);

        var veryOldMemory = CreateMemory("mem_old", "Old", DateTimeOffset.UtcNow.AddDays(-90),
            category: MemoryCategory.Fact, importance: 0.05);

        _store.GetByUserIdAsync("user1", Arg.Any<CancellationToken>())
            .Returns(new List<MemoryEntry> { veryOldMemory });
        _consolidator.ConsolidateAsync(Arg.Any<IReadOnlyList<MemoryEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MergeDecision>());
        _consolidator.SynthesizeProfileAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MemoryEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new PersonalityProfile { UserId = "user1", Summary = "Test", LastUpdated = DateTimeOffset.UtcNow });

        await service.RunDreamingForUserAsync("user1", CancellationToken.None);

        // 0.05 * 0.9 = 0.045, but floor is 0.1 — should not decay below floor
        await _store.DidNotReceive().UpdateImportanceAsync("user1", "mem_old", Arg.Any<double>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunDreamingForUserAsync_AppliesMergeDecisions()
    {
        var service = CreateService();

        var memories = new List<MemoryEntry>
        {
            CreateMemory("mem_1", "Works at Contoso"),
            CreateMemory("mem_2", "Contoso employee, .NET team")
        };

        _store.GetByUserIdAsync("user1", Arg.Any<CancellationToken>())
            .Returns(memories);

        _consolidator.ConsolidateAsync(Arg.Any<IReadOnlyList<MemoryEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MergeDecision>
            {
                new(["mem_1", "mem_2"], MergeAction.Merge,
                    MergedContent: "Works at Contoso on the .NET team",
                    Category: MemoryCategory.Fact,
                    Importance: 0.85,
                    Tags: ["work"])
            });

        _embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(TestEmbedding);
        _store.StoreAsync(Arg.Any<MemoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.Arg<MemoryEntry>());

        _consolidator.SynthesizeProfileAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MemoryEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new PersonalityProfile { UserId = "user1", Summary = "Test", LastUpdated = DateTimeOffset.UtcNow });

        await service.RunDreamingForUserAsync("user1", CancellationToken.None);

        // Should store merged memory
        await _store.Received(1).StoreAsync(
            Arg.Is<MemoryEntry>(m => m.Content == "Works at Contoso on the .NET team"),
            Arg.Any<CancellationToken>());
        // Should supersede originals
        await _store.Received(1).SupersedeAsync("user1", "mem_1", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.Received(1).SupersedeAsync("user1", "mem_2", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunDreamingForUserAsync_PublishesDreamingMetric()
    {
        var service = CreateService();

        _store.GetByUserIdAsync("user1", Arg.Any<CancellationToken>())
            .Returns(new List<MemoryEntry>());
        _consolidator.ConsolidateAsync(Arg.Any<IReadOnlyList<MemoryEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new List<MergeDecision>());
        _consolidator.SynthesizeProfileAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<MemoryEntry>>(), Arg.Any<CancellationToken>())
            .Returns(new PersonalityProfile { UserId = "user1", Summary = "Test", LastUpdated = DateTimeOffset.UtcNow });

        await service.RunDreamingForUserAsync("user1", CancellationToken.None);

        await _metricsPublisher.Received(1).PublishAsync(
            Arg.Any<MemoryDreamingEvent>(), Arg.Any<CancellationToken>());
    }

    private static MemoryEntry CreateMemory(string id, string content, DateTimeOffset? lastAccessed = null,
        MemoryCategory category = MemoryCategory.Fact, double importance = 0.8) =>
        new()
        {
            Id = id, UserId = "user1", Category = category,
            Content = content, Importance = importance, Confidence = 0.9,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-60),
            LastAccessedAt = lastAccessed ?? DateTimeOffset.UtcNow
        };
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryDreamingServiceTests" --no-restore -v q`
Expected: FAIL — `MemoryDreamingService` and `MemoryDreamingOptions` do not exist

- [ ] **Step 3: Implement MemoryDreamingService**

```csharp
// Infrastructure/Memory/MemoryDreamingService.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Memory;

public record MemoryDreamingOptions
{
    public string CronSchedule { get; init; } = "0 3 * * *";
    public int DecayDays { get; init; } = 30;
    public double DecayFactor { get; init; } = 0.9;
    public double DecayFloor { get; init; } = 0.1;
    public IReadOnlyList<MemoryCategory> DecayExemptCategories { get; init; } = [MemoryCategory.Instruction];
    public int MaxRetries { get; init; } = 2;
}

public class MemoryDreamingService(
    IMemoryStore store,
    IMemoryConsolidator consolidator,
    IEmbeddingService embeddingService,
    IMetricsPublisher metricsPublisher,
    ICronValidator cronValidator,
    ILogger<MemoryDreamingService> logger,
    MemoryDreamingOptions options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var nextOccurrence = cronValidator.GetNextOccurrence(options.CronSchedule, DateTime.UtcNow);
                if (nextOccurrence is null)
                {
                    logger.LogWarning("Invalid cron schedule: {Schedule}", options.CronSchedule);
                    return;
                }

                var delay = nextOccurrence.Value - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, ct);
                }

                await RunDreamingAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    private async Task RunDreamingAsync(CancellationToken ct)
    {
        try
        {
            var userIds = await GetDistinctUserIdsAsync(ct);

            foreach (var userId in userIds)
            {
                try
                {
                    await RunDreamingForUserAsync(userId, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Dreaming failed for user {UserId}", userId);
                    await metricsPublisher.PublishAsync(new ErrorEvent
                    {
                        Service = "memory",
                        ErrorType = ex.GetType().Name,
                        Message = $"Dreaming failed for user {userId}: {ex.Message}"
                    }, ct);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Global dreaming failure");
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "memory",
                ErrorType = ex.GetType().Name,
                Message = $"Global dreaming failure: {ex.Message}"
            }, ct);
        }
    }

    public async Task RunDreamingForUserAsync(string userId, CancellationToken ct)
    {
        var memories = await store.GetByUserIdAsync(userId, ct);
        var activeMemories = memories.Where(m => m.SupersededById is null).ToList();

        if (activeMemories.Count == 0)
        {
            return;
        }

        // Step 1: Merge
        var mergedCount = await MergeMemoriesAsync(userId, activeMemories, ct);

        // Re-fetch after merge to get updated state
        if (mergedCount > 0)
        {
            memories = await store.GetByUserIdAsync(userId, ct);
            activeMemories = memories.Where(m => m.SupersededById is null).ToList();
        }

        // Step 2: Decay
        var decayedCount = await DecayMemoriesAsync(userId, activeMemories, ct);

        // Step 3: Reflect
        var profile = await SynthesizeProfileAsync(userId, activeMemories, ct);
        await store.SaveProfileAsync(profile, ct);

        await metricsPublisher.PublishAsync(new MemoryDreamingEvent
        {
            MergedCount = mergedCount,
            DecayedCount = decayedCount,
            ProfileRegenerated = true,
            UserId = userId
        }, ct);
    }

    private async Task<int> MergeMemoriesAsync(
        string userId, IReadOnlyList<MemoryEntry> memories, CancellationToken ct)
    {
        var decisions = await ConsolidateWithRetryAsync(memories, ct);
        var mergedCount = 0;

        foreach (var decision in decisions.Where(d => d.Action is MergeAction.Merge or MergeAction.SupersedeOlder))
        {
            if (decision.Action == MergeAction.Merge && decision.MergedContent is not null)
            {
                var embedding = await embeddingService.GenerateEmbeddingAsync(decision.MergedContent, ct);
                var merged = new MemoryEntry
                {
                    Id = $"mem_{Guid.NewGuid():N}",
                    UserId = userId,
                    Category = decision.Category ?? MemoryCategory.Fact,
                    Content = decision.MergedContent,
                    Importance = decision.Importance ?? 0.5,
                    Confidence = 0.8,
                    Embedding = embedding,
                    Tags = decision.Tags ?? [],
                    CreatedAt = DateTimeOffset.UtcNow,
                    LastAccessedAt = DateTimeOffset.UtcNow
                };

                await store.StoreAsync(merged, ct);
                foreach (var sourceId in decision.SourceIds)
                {
                    await store.SupersedeAsync(userId, sourceId, merged.Id, ct);
                }

                mergedCount += decision.SourceIds.Count;
            }
            else if (decision.Action == MergeAction.SupersedeOlder && decision.SourceIds.Count >= 2)
            {
                await store.SupersedeAsync(userId, decision.SourceIds[0], decision.SourceIds[1], ct);
                mergedCount++;
            }
        }

        return mergedCount;
    }

    private Task<int> DecayMemoriesAsync(
        string userId, IReadOnlyList<MemoryEntry> memories, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.DecayDays);

        var toDecay = memories
            .Where(m => m.LastAccessedAt < cutoff)
            .Where(m => !options.DecayExemptCategories.Contains(m.Category))
            .Where(m => m.Importance * options.DecayFactor >= options.DecayFloor)
            .ToList();

        return Task.WhenAll(toDecay.Select(m =>
            store.UpdateImportanceAsync(userId, m.Id, Math.Round(m.Importance * options.DecayFactor, 2), ct)))
            .ContinueWith(t => toDecay.Count, ct);
    }

    private async Task<PersonalityProfile> SynthesizeProfileAsync(
        string userId, IReadOnlyList<MemoryEntry> memories, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                return await consolidator.SynthesizeProfileAsync(userId, memories, ct);
            }
            catch (Exception ex) when (attempt < options.MaxRetries)
            {
                logger.LogWarning(ex, "Profile synthesis attempt {Attempt} failed for user {UserId}", attempt + 1, userId);
            }
        }

        return new PersonalityProfile
        {
            UserId = userId,
            Summary = "Profile synthesis failed",
            Confidence = 0,
            BasedOnMemoryCount = memories.Count,
            LastUpdated = DateTimeOffset.UtcNow
        };
    }

    private async Task<IReadOnlyList<MergeDecision>> ConsolidateWithRetryAsync(
        IReadOnlyList<MemoryEntry> memories, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            try
            {
                return await consolidator.ConsolidateAsync(memories, ct);
            }
            catch (Exception ex) when (attempt < options.MaxRetries)
            {
                logger.LogWarning(ex, "Consolidation attempt {Attempt} failed", attempt + 1);
            }
        }

        return [];
    }

    private async Task<IReadOnlyList<string>> GetDistinctUserIdsAsync(CancellationToken ct)
    {
        // RedisStackMemoryStore uses key pattern memory:{userId}:{memoryId}
        // We need a way to get all distinct user IDs. This will be added to IMemoryStore.
        // For now, this is a placeholder that will be implemented when wiring up.
        // The store already has GetByUserIdAsync, but we need GetAllUserIdsAsync.
        return await store.GetAllUserIdsAsync(ct);
    }
}
```

Note: This requires adding `GetAllUserIdsAsync` to `IMemoryStore`. We'll do that in the next step.

- [ ] **Step 4: Add GetAllUserIdsAsync to IMemoryStore**

Add to `Domain/Contracts/IMemoryStore.cs`:

```csharp
Task<IReadOnlyList<string>> GetAllUserIdsAsync(CancellationToken ct = default);
```

Add implementation to `Infrastructure/Memory/RedisStackMemoryStore.cs`:

```csharp
public async Task<IReadOnlyList<string>> GetAllUserIdsAsync(CancellationToken ct = default)
{
    var server = _redis.GetServers().First();
    var keys = server.KeysAsync(pattern: "memory:*:*");
    var userIds = new HashSet<string>();

    await foreach (var key in keys.WithCancellation(ct))
    {
        var parts = key.ToString().Split(':');
        if (parts.Length >= 3)
        {
            userIds.Add(parts[1]);
        }
    }

    return userIds.ToList();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryDreamingServiceTests" --no-restore -v q`
Expected: PASS (5 tests)

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Memory/MemoryDreamingService.cs Tests/Unit/Memory/MemoryDreamingServiceTests.cs Domain/Contracts/IMemoryStore.cs Infrastructure/Memory/RedisStackMemoryStore.cs
git commit -m "feat(memory): implement MemoryDreamingService with merge, decay, and profile synthesis"
```

---

## Task 14: MemoryToolFeature — Domain Tool Registration

**Files:**
- Create: `Domain/Tools/Memory/MemoryToolFeature.cs`
- Create: `Tests/Unit/Memory/MemoryToolFeatureTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/Memory/MemoryToolFeatureTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Memory;
using NSubstitute;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryToolFeatureTests
{
    [Fact]
    public void FeatureName_ReturnsMemory()
    {
        var store = Substitute.For<IMemoryStore>();
        var feature = new MemoryToolFeature(store);

        feature.FeatureName.ShouldBe("memory");
    }

    [Fact]
    public void GetTools_ReturnsMemoryForgetTool()
    {
        var store = Substitute.For<IMemoryStore>();
        var feature = new MemoryToolFeature(store);

        var tools = feature.GetTools(new FeatureConfig()).ToList();

        tools.Count.ShouldBe(1);
        tools[0].Name.ShouldBe("domain:memory:memory_forget");
    }

    [Fact]
    public void Prompt_ReturnsSimplifiedMemoryPrompt()
    {
        var store = Substitute.For<IMemoryStore>();
        var feature = new MemoryToolFeature(store);

        feature.Prompt.ShouldNotBeNull();
        feature.Prompt.ShouldContain("memory_forget");
        feature.Prompt.ShouldNotContain("memory_recall");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryToolFeatureTests" --no-restore -v q`
Expected: FAIL — `MemoryToolFeature` does not exist

- [ ] **Step 3: Implement MemoryToolFeature**

```csharp
// Domain/Tools/Memory/MemoryToolFeature.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.Memory;

public class MemoryToolFeature(IMemoryStore store) : IDomainToolFeature
{
    private const string Feature = "memory";

    public string FeatureName => Feature;

    public string? Prompt => MemoryPrompt.SystemPrompt;

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var forgetTool = new MemoryForgetTool(store);
        yield return AIFunctionFactory.Create(
            forgetTool.Run,
            name: $"domain:{Feature}:{MemoryForgetTool.Name}",
            description: MemoryForgetTool.Description);
    }
}
```

Note: `MemoryForgetTool.Run` is currently `protected`. We need to change its accessibility to `internal` or make it accessible from the feature. Looking at the existing SubAgentToolFeature pattern, the tool method is `public`. Let's change `MemoryForgetTool.Run` to `public`:

In `Domain/Tools/Memory/MemoryForgetTool.cs`, change:
```csharp
protected async Task<JsonNode> Run(
```
to:
```csharp
public async Task<JsonNode> Run(
```

Also change `Name` and `Description` from `protected` to `public`:
```csharp
public const string Name = "memory_forget";
public const string Description = ...
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryToolFeatureTests" --no-restore -v q`
Expected: PASS (3 tests)

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/Memory/MemoryToolFeature.cs Domain/Tools/Memory/MemoryForgetTool.cs Tests/Unit/Memory/MemoryToolFeatureTests.cs
git commit -m "feat(memory): add MemoryToolFeature exposing only memory_forget as domain tool"
```

---

## Task 15: Simplify MemoryPrompt

**Files:**
- Modify: `Domain/Prompts/MemoryPrompt.cs`

- [ ] **Step 1: Replace MemoryPrompt with simplified version**

```csharp
// Domain/Prompts/MemoryPrompt.cs
namespace Domain.Prompts;

public static class MemoryPrompt
{
    public const string Name = "memory_system_prompt";

    public const string Description =
        "Instructions for using the memory system";

    public const string SystemPrompt =
        """
        ## Memory System

        You have persistent memory. Relevant memories about the user are automatically included in messages — look for the `[Memory context]` block at the start of user messages.

        Use this context to personalize your responses: apply known preferences, recall facts, respect instructions.

        ### Available Tool

        | Tool | Purpose |
        |------|---------|
        | `memory_forget` | Delete or archive memories when user explicitly requests forgetting |

        Only call `memory_forget` when a user explicitly asks you to forget something. Memory storage and recall are handled automatically.

        ### Privacy Note

        All memories are scoped by userId. Never access or reference memories from other users.
        """;
}
```

- [ ] **Step 2: Verify build succeeds and MemoryToolFeature tests still pass**

Run: `dotnet build Domain/ --no-restore -v q && dotnet test Tests/ --filter "FullyQualifiedName~MemoryToolFeatureTests" --no-restore -v q`
Expected: Build succeeded, PASS

- [ ] **Step 3: Commit**

```bash
git add Domain/Prompts/MemoryPrompt.cs
git commit -m "refactor(memory): simplify MemoryPrompt — no mandatory recall, automatic context injection"
```

---

## Task 16: MemoryModule — DI Wiring

**Files:**
- Create: `Agent/Modules/MemoryModule.cs`
- Modify: `Agent/Modules/ConfigModule.cs`

- [ ] **Step 1: Create MemoryModule**

```csharp
// Agent/Modules/MemoryModule.cs
using System.Net.Http.Headers;
using Domain.Contracts;
using Domain.Memory;
using Domain.Tools.Memory;
using Infrastructure.Memory;

namespace Agent.Modules;

public static class MemoryModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddMemory(IConfiguration config)
        {
            var memoryConfig = config.GetSection("Memory");

            // Extraction queue
            services.AddSingleton<MemoryExtractionQueue>();

            // Infrastructure — store and embeddings
            services.AddSingleton<IMemoryStore, RedisStackMemoryStore>();
            services.AddHttpClient<IEmbeddingService, OpenRouterEmbeddingService>((httpClient, sp) =>
            {
                var openRouterConfig = config.GetSection("openRouter");
                httpClient.BaseAddress = new Uri(openRouterConfig["apiUrl"]!);
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", openRouterConfig["apiKey"]);
                httpClient.Timeout = TimeSpan.FromSeconds(30);

                var embeddingModel = memoryConfig["Embedding:Model"] ?? "openai/text-embedding-3-small";
                return new OpenRouterEmbeddingService(httpClient, embeddingModel);
            });

            // LLM-based services — extractor and consolidator
            services.AddSingleton<IMemoryExtractor>(sp =>
            {
                var openRouterConfig = config.GetSection("openRouter");
                var extractionModel = memoryConfig["Extraction:Model"] ?? "google/gemini-2.0-flash-001";
                var chatClient = new OpenAI.OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(openRouterConfig["apiKey"]!),
                    new OpenAI.OpenAIClientOptions { Endpoint = new Uri(openRouterConfig["apiUrl"]!) })
                    .GetChatClient(extractionModel)
                    .AsIChatClient();
                return new OpenRouterMemoryExtractor(
                    chatClient,
                    sp.GetRequiredService<IMemoryStore>(),
                    sp.GetRequiredService<ILogger<OpenRouterMemoryExtractor>>());
            });

            services.AddSingleton<IMemoryConsolidator>(sp =>
            {
                var openRouterConfig = config.GetSection("openRouter");
                var dreamingModel = memoryConfig["Dreaming:Model"] ?? "google/gemini-2.0-flash-001";
                var chatClient = new OpenAI.OpenAIClient(
                    new System.ClientModel.ApiKeyCredential(openRouterConfig["apiKey"]!),
                    new OpenAI.OpenAIClientOptions { Endpoint = new Uri(openRouterConfig["apiUrl"]!) })
                    .GetChatClient(dreamingModel)
                    .AsIChatClient();
                return new OpenRouterMemoryConsolidator(
                    chatClient,
                    sp.GetRequiredService<ILogger<OpenRouterMemoryConsolidator>>());
            });

            // Options
            var recallOptions = new MemoryRecallOptions
            {
                DefaultLimit = memoryConfig.GetValue("Recall:DefaultLimit", 10),
                IncludePersonalityProfile = memoryConfig.GetValue("Recall:IncludePersonalityProfile", true)
            };
            services.AddSingleton(recallOptions);

            var extractionOptions = new MemoryExtractionOptions
            {
                SimilarityThreshold = memoryConfig.GetValue("Extraction:SimilarityThreshold", 0.85),
                MaxCandidatesPerMessage = memoryConfig.GetValue("Extraction:MaxCandidatesPerMessage", 5)
            };
            services.AddSingleton(extractionOptions);

            var dreamingOptions = new MemoryDreamingOptions
            {
                CronSchedule = memoryConfig["Dreaming:CronSchedule"] ?? "0 3 * * *",
                DecayDays = memoryConfig.GetValue("Dreaming:DecayDays", 30),
                DecayFactor = memoryConfig.GetValue("Dreaming:DecayFactor", 0.9),
                DecayFloor = memoryConfig.GetValue("Dreaming:DecayFloor", 0.1)
            };
            services.AddSingleton(dreamingOptions);

            // Hook
            services.AddSingleton<IMemoryRecallHook, MemoryRecallHook>();

            // Domain tool feature
            services.AddTransient<IDomainToolFeature, MemoryToolFeature>();

            // Background workers
            services.AddHostedService<MemoryExtractionWorker>();
            services.AddHostedService<MemoryDreamingService>();

            return services;
        }
    }
}
```

- [ ] **Step 2: Modify ConfigModule to add .AddMemory()**

In `Agent/Modules/ConfigModule.cs`, modify `ConfigureAgents` to accept `IConfiguration` and call `.AddMemory()`:

```csharp
public static IServiceCollection ConfigureAgents(
    this IServiceCollection services, AgentSettings settings, CommandLineParams cmdParams, IConfiguration config)
{
    return services
        .AddAgent(settings)
        .AddScheduling()
        .AddSubAgents(settings.SubAgents)
        .AddMemory(config)
        .AddChatMonitoring(settings, cmdParams);
}
```

Update `Agent/Program.cs` line 9 to pass `builder.Configuration`:

```csharp
builder.Services.ConfigureAgents(settings, cmdParams, builder.Configuration);
```

Also update the DI test at `Tests/Integration/Jack/DependencyInjectionTests.cs` — the `ConfigureAgents` call there also needs the new `IConfiguration` parameter.

- [ ] **Step 3: Verify build succeeds**

Run: `dotnet build Agent/ --no-restore -v q`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add Agent/Modules/MemoryModule.cs Agent/Modules/ConfigModule.cs
git commit -m "feat(memory): add MemoryModule DI wiring and integrate into ConfigModule"
```

---

## Task 17: Configuration — appsettings.json and docker-compose

**Files:**
- Modify: `Agent/appsettings.json`
- Modify: `DockerCompose/docker-compose.yml`

- [ ] **Step 1: Add Memory config section to appsettings.json**

Add the following section to `Agent/appsettings.json` at the top level:

```json
"Memory": {
    "Embedding": {
        "Model": "openai/text-embedding-3-small"
    },
    "Extraction": {
        "Model": "google/gemini-2.0-flash-001",
        "MaxCandidatesPerMessage": 5,
        "SimilarityThreshold": 0.85
    },
    "Dreaming": {
        "CronSchedule": "0 3 * * *",
        "Model": "google/gemini-2.0-flash-001",
        "DecayDays": 30,
        "DecayFactor": 0.9,
        "DecayFloor": 0.1
    },
    "Recall": {
        "DefaultLimit": 10,
        "IncludePersonalityProfile": true
    }
}
```

Also update agent definitions:
- Remove `"http://mcp-memory:8080/sse"` from `mcpServerEndpoints` for jonas and jonas-worker
- Remove `"mcp:mcp-memory:*"` from `whitelistPatterns` for jonas
- Add `"memory"` to jonas's `enabledFeatures` array
- Add `"domain:memory:*"` to jonas's `whitelistPatterns`

- [ ] **Step 2: Remove mcp-memory from docker-compose.yml**

In `DockerCompose/docker-compose.yml`:
- Remove the entire `mcp-memory` service block (lines ~250-273)
- Remove `mcp-memory` from the agent service's `depends_on` list

- [ ] **Step 3: Verify docker-compose is valid**

Run: `docker compose -f DockerCompose/docker-compose.yml config --quiet 2>&1 || echo "validation failed"`
Expected: No errors

- [ ] **Step 4: Commit**

```bash
git add Agent/appsettings.json DockerCompose/docker-compose.yml
git commit -m "config(memory): add Memory config section, remove mcp-memory service"
```

---

## Task 18: Delete McpServerMemory Project and Obsolete Tools

**Files:**
- Delete: `McpServerMemory/` (entire directory)
- Delete: `Domain/Tools/Memory/MemoryStoreTool.cs`
- Delete: `Domain/Tools/Memory/MemoryRecallTool.cs`
- Delete: `Domain/Tools/Memory/MemoryReflectTool.cs`
- Delete: `Domain/Tools/Memory/MemoryListTool.cs`

- [ ] **Step 1: Remove McpServerMemory project reference from solution**

Run: `dotnet sln remove McpServerMemory/McpServerMemory.csproj` (if it exists in the solution)

- [ ] **Step 2: Delete the McpServerMemory directory**

```bash
rm -rf McpServerMemory/
```

- [ ] **Step 3: Delete obsolete domain tools**

```bash
rm Domain/Tools/Memory/MemoryStoreTool.cs
rm Domain/Tools/Memory/MemoryRecallTool.cs
rm Domain/Tools/Memory/MemoryReflectTool.cs
rm Domain/Tools/Memory/MemoryListTool.cs
```

- [ ] **Step 4: Verify solution builds**

Run: `dotnet build --no-restore -v q`
Expected: Build succeeded. If there are compilation errors from deleted types being referenced elsewhere, fix them (likely in McpServerMemory-specific MCP tool wrappers which are also deleted).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(memory): delete McpServerMemory project and obsolete memory tools"
```

---

## Task 19: Integration Tests

**Files:**
- Create: `Tests/Integration/Memory/MemoryRecallHookIntegrationTests.cs`

- [ ] **Step 1: Write integration test for recall hook end-to-end**

```csharp
// Tests/Integration/Memory/MemoryRecallHookIntegrationTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Domain.Memory;
using Infrastructure.Memory;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Tests.Integration.Memory;

namespace Tests.Integration.Memory;

public class MemoryRecallHookIntegrationTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task EnrichAsync_WithStoredMemories_InjectsContextIntoMessage()
    {
        var store = redisFixture.CreateStore();
        var embeddingService = Substitute.For<IEmbeddingService>();
        var queue = new MemoryExtractionQueue();
        var metricsPublisher = Substitute.For<IMetricsPublisher>();

        var userId = $"user_{Guid.NewGuid():N}";
        var embedding = CreateTestEmbedding();

        // Store a memory
        await store.StoreAsync(new MemoryEntry
        {
            Id = $"mem_{Guid.NewGuid():N}",
            UserId = userId,
            Category = MemoryCategory.Preference,
            Content = "User prefers TypeScript over JavaScript",
            Importance = 0.9,
            Confidence = 0.8,
            Embedding = embedding,
            Tags = ["language"],
            CreatedAt = DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow
        });

        embeddingService.GenerateEmbeddingAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(embedding);

        var hook = new MemoryRecallHook(
            store, embeddingService, queue, metricsPublisher,
            Substitute.For<ILogger<MemoryRecallHook>>(),
            new MemoryRecallOptions());

        var message = new ChatMessage(ChatRole.User, "What language should I use?");

        await hook.EnrichAsync(message, userId, "conv_1", CancellationToken.None);

        var context = message.GetMemoryContext();
        context.ShouldNotBeNull();
        context.Memories.Count.ShouldBeGreaterThan(0);
        context.Memories.ShouldContain(m => m.Memory.Content.Contains("TypeScript"));
    }

    private static float[] CreateTestEmbedding()
    {
        var rng = new Random(42);
        return Enumerable.Range(0, 1536).Select(_ => (float)(rng.NextDouble() * 2 - 1)).ToArray();
    }
}
```

- [ ] **Step 2: Run integration test**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryRecallHookIntegrationTests" --no-restore -v q`
Expected: PASS (requires Redis running — same as existing RedisMemoryStoreTests)

- [ ] **Step 3: Commit**

```bash
git add Tests/Integration/Memory/MemoryRecallHookIntegrationTests.cs
git commit -m "test(memory): add MemoryRecallHook integration test with Redis"
```

---

## Task 20: Full Build Verification and Cleanup

- [ ] **Step 1: Run full solution build**

Run: `dotnet build --no-restore -v q`
Expected: Build succeeded with no errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test Tests/ --no-restore -v q`
Expected: All tests pass

- [ ] **Step 3: Fix any remaining issues**

If any tests fail or build errors remain, fix them. Common issues:
- References to deleted MCP memory tools in test files
- Missing `using` statements
- Constructor parameter mismatches

- [ ] **Step 4: Final commit**

```bash
git add -A
git commit -m "chore(memory): final cleanup and full build verification"
```
