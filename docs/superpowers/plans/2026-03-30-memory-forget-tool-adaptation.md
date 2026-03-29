# MemoryForgetTool Adaptation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Adapt `MemoryForgetTool` to leverage the new proactive memory system's vector search, tags, and importance filtering — replacing the naive in-memory filtering approach.

**Architecture:** Inject `IEmbeddingService` into `MemoryForgetTool` alongside `IMemoryStore`. Replace `GetByUserIdAsync` + LINQ filtering with `SearchAsync` (which supports vector search, categories, tags, minImportance). Add post-filtering for `maxImportance` and `olderThan` since `SearchAsync` doesn't support those natively. Update the system prompt to guide LLM toward proactive correction handling.

**Tech Stack:** .NET 10, Redis Stack (vector search), Moq, Shouldly, xUnit

---

## File Map

| Action | File | Responsibility |
|--------|------|----------------|
| Modify | `Domain/Tools/Memory/MemoryForgetTool.cs` | Add `IEmbeddingService`, tags, maxImportance params; use `SearchAsync` |
| Modify | `Domain/Tools/Memory/MemoryToolFeature.cs` | Inject `IEmbeddingService` into `MemoryForgetTool` |
| Modify | `Domain/Prompts/MemoryPrompts.cs` | Update `FeatureSystemPrompt` for new params and proactive correction |
| Modify | `Agent/Modules/MemoryModule.cs` | Pass `IEmbeddingService` to `MemoryToolFeature` |
| Modify | `Tests/Unit/Memory/MemoryToolFeatureTests.cs` | Update mocks for new `IEmbeddingService` dependency |
| Create | `Tests/Unit/Memory/MemoryForgetToolTests.cs` | Unit tests for all forget tool behaviors |

---

### Task 1: Add `IEmbeddingService` dependency and new parameters to `MemoryForgetTool`

**Files:**
- Create: `Tests/Unit/Memory/MemoryForgetToolTests.cs`
- Modify: `Domain/Tools/Memory/MemoryForgetTool.cs`

- [ ] **Step 1: Write failing test — semantic search generates embedding and delegates to SearchAsync**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Memory;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryForgetToolTests
{
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IEmbeddingService> _embedding = new();

    private MemoryForgetTool CreateTool() => new(_store.Object, _embedding.Object);

    [Fact]
    public async Task Run_WithQuery_GeneratesEmbeddingAndUsesSearchAsync()
    {
        var userId = "user1";
        var query = "my job";
        var fakeEmbedding = new float[] { 0.1f, 0.2f, 0.3f };
        var memory = CreateMemory("mem1", "I work at Acme Corp", MemoryCategory.Fact);

        _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
            .ReturnsAsync(fakeEmbedding);
        _store.Setup(s => s.SearchAsync(
                userId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new MemorySearchResult(memory, 0.9)]);
        _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var tool = CreateTool();
        var result = await tool.Run(userId, query: query);

        _embedding.Verify(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()), Times.Once);
        _store.Verify(s => s.SearchAsync(
            userId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()), Times.Once);
        result["affectedCount"]!.GetValue<int>().ShouldBe(1);
    }

    private static MemoryEntry CreateMemory(
        string id, string content, MemoryCategory category,
        double importance = 0.5, DateTimeOffset? createdAt = null) =>
        new()
        {
            Id = id,
            UserId = "user1",
            Category = category,
            Content = content,
            Importance = importance,
            Confidence = 0.8,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            LastAccessedAt = DateTimeOffset.UtcNow,
            Tags = []
        };
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryForgetToolTests.Run_WithQuery_GeneratesEmbeddingAndUsesSearchAsync" --no-restore`
Expected: FAIL — `MemoryForgetTool` constructor doesn't accept `IEmbeddingService`

- [ ] **Step 3: Update `MemoryForgetTool` — add `IEmbeddingService`, new parameters, use `SearchAsync`**

Replace the entire `MemoryForgetTool` implementation:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;

namespace Domain.Tools.Memory;

public class MemoryForgetTool(IMemoryStore store, IEmbeddingService embeddingService)
{
    private const int ContentPreviewLength = 100;
    private const int SearchLimit = 100;

    public const string Name = "memory_forget";

    public const string Description = """
                                         Removes or archives memories. Use when information is outdated, wrong, or user
                                         explicitly asks you to forget something.

                                         Modes:
                                         - delete: Permanent removal
                                         - archive: Keep for history but exclude from normal recall (marks as superseded)

                                         When to use:
                                         - User corrects previous information → archive the outdated memory
                                         - User explicitly requests forgetting
                                         - Information is clearly outdated
                                         - Bulk cleanup of low-importance memories

                                         TIP: When user provides corrected info, prefer using archive mode instead of
                                         delete—this preserves history while excluding the outdated memory from recall.
                                         Use semantic query (not exact text) to find memories — e.g. "my job" will match
                                         memories about employment.
                                         """;

    public async Task<JsonNode> Run(
        string userId,
        string? memoryId = null,
        string? query = null,
        string? categories = null,
        string? tags = null,
        string? olderThan = null,
        double? maxImportance = null,
        ForgetMode mode = ForgetMode.Delete,
        string? reason = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(memoryId) && string.IsNullOrWhiteSpace(query))
        {
            return CreateErrorResponse("Either memoryId or query must be provided");
        }

        var affectedMemories = !string.IsNullOrWhiteSpace(memoryId)
            ? await ForgetById(userId, memoryId, mode, ct)
            : await ForgetBySearch(userId, query!, ParseCategories(categories), ParseTags(tags),
                ParseDate(olderThan), maxImportance, mode, ct);

        return CreateSuccessResponse(mode, affectedMemories, reason);
    }

    private async Task<List<AffectedMemory>> ForgetById(
        string userId, string memoryId, ForgetMode mode, CancellationToken ct)
    {
        var memory = await store.GetByIdAsync(userId, memoryId, ct);
        if (memory is null)
        {
            return [];
        }

        var success = await ApplyForgetMode(userId, memory, mode, ct);
        return success ? [new AffectedMemory(memory.Id, TruncateContent(memory.Content))] : [];
    }

    private async Task<List<AffectedMemory>> ForgetBySearch(
        string userId, string query, List<MemoryCategory>? parsedCategories, List<string>? parsedTags,
        DateTimeOffset? olderThan, double? maxImportance, ForgetMode mode, CancellationToken ct)
    {
        var queryEmbedding = await embeddingService.GenerateEmbeddingAsync(query, ct);

        var results = await store.SearchAsync(
            userId, query, queryEmbedding, parsedCategories, parsedTags,
            minImportance: null, limit: SearchLimit, ct);

        var affected = new List<AffectedMemory>();

        foreach (var result in results)
        {
            var memory = result.Memory;

            if (olderThan.HasValue && memory.CreatedAt >= olderThan.Value)
                continue;

            if (maxImportance.HasValue && memory.Importance > maxImportance.Value)
                continue;

            if (await ApplyForgetMode(userId, memory, mode, ct))
            {
                affected.Add(new AffectedMemory(memory.Id, TruncateContent(memory.Content)));
            }
        }

        return affected;
    }

    private async Task<bool> ApplyForgetMode(string userId, MemoryEntry memory, ForgetMode mode, CancellationToken ct)
    {
        return mode switch
        {
            ForgetMode.Delete => await store.DeleteAsync(userId, memory.Id, ct),
            ForgetMode.Archive => await store.SupersedeAsync(userId, memory.Id, "archived", ct),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
    }

    private static List<MemoryCategory>? ParseCategories(string? categories)
    {
        if (string.IsNullOrWhiteSpace(categories))
            return null;

        return categories
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(c => Enum.TryParse<MemoryCategory>(c, ignoreCase: true, out var cat) ? cat : (MemoryCategory?)null)
            .Where(c => c.HasValue)
            .Select(c => c!.Value)
            .ToList();
    }

    private static List<string>? ParseTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
            return null;

        return tags
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
    }

    private static DateTimeOffset? ParseDate(string? date)
    {
        return string.IsNullOrWhiteSpace(date) ? null : DateTimeOffset.Parse(date);
    }

    private static string TruncateContent(string content)
    {
        return content.Length > ContentPreviewLength
            ? content[..ContentPreviewLength] + "..."
            : content;
    }

    private static JsonObject CreateErrorResponse(string message)
    {
        return new JsonObject { ["error"] = message };
    }

    private static JsonObject CreateSuccessResponse(ForgetMode mode, List<AffectedMemory> affected, string? reason)
    {
        var response = new JsonObject
        {
            ["status"] = "success",
            ["action"] = mode.ToString().ToLowerInvariant(),
            ["affectedCount"] = affected.Count,
            ["affectedMemories"] = new JsonArray(affected.Select(m => m.ToJson()).ToArray())
        };

        if (!string.IsNullOrWhiteSpace(reason))
        {
            response["reason"] = reason;
        }

        return response;
    }

    private sealed record AffectedMemory(string Id, string Content)
    {
        public JsonNode ToJson()
        {
            return new JsonObject
            {
                ["id"] = Id,
                ["content"] = Content
            };
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryForgetToolTests.Run_WithQuery_GeneratesEmbeddingAndUsesSearchAsync" --no-restore`
Expected: PASS

---

### Task 2: Add remaining unit tests for new filtering capabilities

**Files:**
- Modify: `Tests/Unit/Memory/MemoryForgetToolTests.cs`

- [ ] **Step 1: Write failing tests for tag filtering, importance filtering, olderThan post-filter, and combined filters**

Add these tests to `MemoryForgetToolTests`:

```csharp
[Fact]
public async Task Run_WithTags_PassesTagsToSearchAsync()
{
    var userId = "user1";
    var query = "some query";
    var parsedTags = new List<string> { "work", "project" };
    var fakeEmbedding = new float[] { 0.1f };
    var memory = CreateMemory("mem1", "Work project info", MemoryCategory.Project);

    _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
        .ReturnsAsync(fakeEmbedding);
    _store.Setup(s => s.SearchAsync(
            userId, query, fakeEmbedding, null,
            It.Is<IEnumerable<string>>(t => t.SequenceEqual(parsedTags)),
            null, 100, It.IsAny<CancellationToken>()))
        .ReturnsAsync([new MemorySearchResult(memory, 0.9)]);
    _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(true);

    var tool = CreateTool();
    var result = await tool.Run(userId, query: query, tags: "work,project");

    _store.Verify(s => s.SearchAsync(
        userId, query, fakeEmbedding, null,
        It.Is<IEnumerable<string>>(t => t.SequenceEqual(parsedTags)),
        null, 100, It.IsAny<CancellationToken>()), Times.Once);
    result["affectedCount"]!.GetValue<int>().ShouldBe(1);
}

[Fact]
public async Task Run_WithMaxImportance_FiltersOutHighImportanceMemories()
{
    var userId = "user1";
    var query = "stuff";
    var fakeEmbedding = new float[] { 0.1f };
    var lowImportance = CreateMemory("mem1", "Low importance", MemoryCategory.Event, importance: 0.3);
    var highImportance = CreateMemory("mem2", "High importance", MemoryCategory.Instruction, importance: 0.9);

    _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
        .ReturnsAsync(fakeEmbedding);
    _store.Setup(s => s.SearchAsync(
            userId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()))
        .ReturnsAsync([
            new MemorySearchResult(lowImportance, 0.8),
            new MemorySearchResult(highImportance, 0.7)
        ]);
    _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(true);

    var tool = CreateTool();
    var result = await tool.Run(userId, query: query, maxImportance: 0.5);

    result["affectedCount"]!.GetValue<int>().ShouldBe(1);
    _store.Verify(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()), Times.Once);
    _store.Verify(s => s.DeleteAsync(userId, "mem2", It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task Run_WithOlderThan_FiltersOutRecentMemories()
{
    var userId = "user1";
    var query = "stuff";
    var fakeEmbedding = new float[] { 0.1f };
    var cutoff = DateTimeOffset.UtcNow.AddDays(-7);
    var oldMemory = CreateMemory("mem1", "Old memory", MemoryCategory.Fact, createdAt: cutoff.AddDays(-1));
    var newMemory = CreateMemory("mem2", "New memory", MemoryCategory.Fact, createdAt: cutoff.AddDays(1));

    _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
        .ReturnsAsync(fakeEmbedding);
    _store.Setup(s => s.SearchAsync(
            userId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()))
        .ReturnsAsync([
            new MemorySearchResult(oldMemory, 0.8),
            new MemorySearchResult(newMemory, 0.7)
        ]);
    _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(true);

    var tool = CreateTool();
    var result = await tool.Run(userId, query: query, olderThan: cutoff.ToString("O"));

    result["affectedCount"]!.GetValue<int>().ShouldBe(1);
    _store.Verify(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()), Times.Once);
    _store.Verify(s => s.DeleteAsync(userId, "mem2", It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task Run_WithCategories_PassesCategoriesToSearchAsync()
{
    var userId = "user1";
    var query = "stuff";
    var fakeEmbedding = new float[] { 0.1f };
    var memory = CreateMemory("mem1", "A preference", MemoryCategory.Preference);

    _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
        .ReturnsAsync(fakeEmbedding);
    _store.Setup(s => s.SearchAsync(
            userId, query, fakeEmbedding,
            It.Is<IEnumerable<MemoryCategory>>(c => c.SequenceEqual(new[] { MemoryCategory.Preference })),
            null, null, 100, It.IsAny<CancellationToken>()))
        .ReturnsAsync([new MemorySearchResult(memory, 0.9)]);
    _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(true);

    var tool = CreateTool();
    var result = await tool.Run(userId, query: query, categories: "Preference");

    result["affectedCount"]!.GetValue<int>().ShouldBe(1);
}

[Fact]
public async Task Run_WithMemoryId_StillUsesDirectLookup()
{
    var userId = "user1";
    var memory = CreateMemory("mem1", "Direct lookup", MemoryCategory.Fact);

    _store.Setup(s => s.GetByIdAsync(userId, "mem1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(memory);
    _store.Setup(s => s.DeleteAsync(userId, "mem1", It.IsAny<CancellationToken>()))
        .ReturnsAsync(true);

    var tool = CreateTool();
    var result = await tool.Run(userId, memoryId: "mem1");

    result["affectedCount"]!.GetValue<int>().ShouldBe(1);
    _embedding.Verify(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    _store.Verify(s => s.SearchAsync(
        It.IsAny<string>(), It.IsAny<string>(), It.IsAny<float[]?>(),
        It.IsAny<IEnumerable<MemoryCategory>?>(), It.IsAny<IEnumerable<string>?>(),
        It.IsAny<double?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
}

[Fact]
public async Task Run_ArchiveMode_CallsSupersedeAsync()
{
    var userId = "user1";
    var query = "my job";
    var fakeEmbedding = new float[] { 0.1f };
    var memory = CreateMemory("mem1", "I work at Acme", MemoryCategory.Fact);

    _embedding.Setup(e => e.GenerateEmbeddingAsync(query, It.IsAny<CancellationToken>()))
        .ReturnsAsync(fakeEmbedding);
    _store.Setup(s => s.SearchAsync(
            userId, query, fakeEmbedding, null, null, null, 100, It.IsAny<CancellationToken>()))
        .ReturnsAsync([new MemorySearchResult(memory, 0.9)]);
    _store.Setup(s => s.SupersedeAsync(userId, "mem1", "archived", It.IsAny<CancellationToken>()))
        .ReturnsAsync(true);

    var tool = CreateTool();
    var result = await tool.Run(userId, query: query, mode: ForgetMode.Archive);

    _store.Verify(s => s.SupersedeAsync(userId, "mem1", "archived", It.IsAny<CancellationToken>()), Times.Once);
    result["action"]!.GetValue<string>().ShouldBe("archive");
}

[Fact]
public async Task Run_NoMemoryIdOrQuery_ReturnsError()
{
    var tool = CreateTool();
    var result = await tool.Run("user1");

    result["error"]!.GetValue<string>().ShouldBe("Either memoryId or query must be provided");
}
```

- [ ] **Step 2: Run all tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryForgetToolTests" --no-restore`
Expected: All 8 tests PASS (1 from Task 1 + 7 new)

- [ ] **Step 3: Commit**

```bash
git add Tests/Unit/Memory/MemoryForgetToolTests.cs Domain/Tools/Memory/MemoryForgetTool.cs
git commit -m "feat(memory): add semantic search, tag and importance filtering to MemoryForgetTool"
```

---

### Task 3: Update `MemoryToolFeature` and DI wiring for new dependency

**Files:**
- Modify: `Domain/Tools/Memory/MemoryToolFeature.cs`
- Modify: `Agent/Modules/MemoryModule.cs`
- Modify: `Tests/Unit/Memory/MemoryToolFeatureTests.cs`

- [ ] **Step 1: Write failing test — MemoryToolFeature requires IEmbeddingService**

Update `MemoryToolFeatureTests.cs` to pass both dependencies:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Memory;
using Moq;
using Shouldly;

namespace Tests.Unit.Memory;

public class MemoryToolFeatureTests
{
    private readonly Mock<IMemoryStore> _store = new();
    private readonly Mock<IEmbeddingService> _embedding = new();

    private MemoryToolFeature CreateFeature() => new(_store.Object, _embedding.Object);

    [Fact]
    public void FeatureName_ReturnsMemory()
    {
        var feature = CreateFeature();
        feature.FeatureName.ShouldBe("memory");
    }

    [Fact]
    public void GetTools_ReturnsMemoryForgetTool()
    {
        var feature = CreateFeature();
        var tools = feature.GetTools(new FeatureConfig()).ToList();
        tools.Count.ShouldBe(1);
        tools[0].Name.ShouldBe("domain:memory:memory_forget");
    }

    [Fact]
    public void Prompt_ReturnsSimplifiedMemoryPrompt()
    {
        var feature = CreateFeature();
        feature.Prompt.ShouldNotBeNull();
        feature.Prompt.ShouldContain("memory_forget");
        feature.Prompt.ShouldNotContain("memory_recall");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryToolFeatureTests" --no-restore`
Expected: FAIL — `MemoryToolFeature` constructor doesn't accept `IEmbeddingService`

- [ ] **Step 3: Update `MemoryToolFeature` to accept and pass `IEmbeddingService`**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.Memory;

public class MemoryToolFeature(IMemoryStore store, IEmbeddingService embeddingService) : IDomainToolFeature
{
    private const string Feature = "memory";

    public string FeatureName => Feature;

    public string? Prompt => MemoryPrompts.FeatureSystemPrompt;

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var forgetTool = new MemoryForgetTool(store, embeddingService);
        yield return AIFunctionFactory.Create(
            forgetTool.Run,
            name: $"domain:{Feature}:{MemoryForgetTool.Name}",
            description: MemoryForgetTool.Description);
    }
}
```

- [ ] **Step 4: Update `MemoryModule.cs` DI registration**

In `Agent/Modules/MemoryModule.cs`, the `MemoryToolFeature` is registered as `IDomainToolFeature` via transient. Since it now needs `IEmbeddingService`, and `IEmbeddingService` is already registered, DI resolves it automatically. No changes needed — the DI container injects both `IMemoryStore` and `IEmbeddingService` into the primary constructor.

Verify by checking that `IEmbeddingService` is registered (line 25) and `MemoryToolFeature` is registered as transient (line 95) — both already exist.

- [ ] **Step 5: Run all tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryToolFeatureTests" --no-restore`
Expected: All 3 tests PASS

- [ ] **Step 6: Commit**

```bash
git add Domain/Tools/Memory/MemoryToolFeature.cs Tests/Unit/Memory/MemoryToolFeatureTests.cs
git commit -m "feat(memory): wire IEmbeddingService into MemoryToolFeature"
```

---

### Task 4: Update `FeatureSystemPrompt` for new parameters and proactive correction

**Files:**
- Modify: `Domain/Prompts/MemoryPrompts.cs`
- Modify: `Tests/Unit/Memory/MemoryToolFeatureTests.cs`

- [ ] **Step 1: Write failing test — prompt mentions new capabilities**

Add to `MemoryToolFeatureTests.cs`:

```csharp
[Fact]
public void Prompt_DocumentsNewFilteringCapabilities()
{
    var feature = CreateFeature();
    feature.Prompt.ShouldNotBeNull();
    feature.Prompt.ShouldContain("tags");
    feature.Prompt.ShouldContain("maxImportance");
    feature.Prompt.ShouldContain("corrects");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryToolFeatureTests.Prompt_DocumentsNewFilteringCapabilities" --no-restore`
Expected: FAIL — prompt doesn't contain "tags", "maxImportance", or "corrects"

- [ ] **Step 3: Update `MemoryPrompts.FeatureSystemPrompt`**

Replace `FeatureSystemPrompt` in `Domain/Prompts/MemoryPrompts.cs`:

```csharp
public const string FeatureSystemPrompt =
    """
    ## Memory System

    You have persistent memory. Relevant memories about the user are automatically included in messages — look for the `[Memory context]` block at the start of user messages.

    Use this context to personalize your responses: apply known preferences, recall facts, respect instructions.

    ### Available Tool

    | Tool | Purpose |
    |------|---------|
    | `memory_forget` | Delete or archive memories — by ID, semantic query, categories, tags, importance, or age |

    **Parameters:**
    - `memoryId` — target a specific memory by ID
    - `query` — semantic search (e.g. "my job" matches employment memories even without exact text match)
    - `categories` — comma-separated: Preference, Fact, Relationship, Skill, Project, Personality, Instruction, Event
    - `tags` — comma-separated tag filter
    - `maxImportance` — only affect memories with importance ≤ this value (useful for bulk cleanup)
    - `olderThan` — only affect memories created before this date (ISO 8601)
    - `mode` — `delete` (permanent) or `archive` (exclude from recall but preserve history)
    - `reason` — optional explanation

    ### When to Use

    - **User corrects information:** Proactively archive the outdated memory (archive mode), even without an explicit "forget" request. If a user says "actually I work at NewCo now", archive the old employer memory.
    - **User explicitly asks to forget:** Delete or archive as requested.
    - **Information is clearly outdated:** Archive stale memories.
    - **Bulk cleanup:** Use `maxImportance` to clear low-value automatically-extracted memories.

    Memory storage and recall are handled automatically — only use `memory_forget` for removal/archival.

    ### Privacy Note

    All memories are scoped by userId. Never access or reference memories from other users.
    """;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MemoryToolFeatureTests" --no-restore`
Expected: All 4 tests PASS

- [ ] **Step 5: Commit**

```bash
git add Domain/Prompts/MemoryPrompts.cs Tests/Unit/Memory/MemoryToolFeatureTests.cs
git commit -m "feat(memory): update FeatureSystemPrompt with new forget tool capabilities and proactive correction guidance"
```

---

### Task 5: Final verification — run all memory tests

**Files:** None (verification only)

- [ ] **Step 1: Run all memory-related unit tests**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~Memory" --no-restore -v normal`
Expected: All tests PASS (MemoryForgetToolTests: 8, MemoryToolFeatureTests: 4, plus any existing memory tests)

- [ ] **Step 2: Run full test suite to check for regressions**

Run: `dotnet test Tests/ --no-restore`
Expected: All tests PASS

- [ ] **Step 3: Verify build compiles cleanly**

Run: `dotnet build --no-restore`
Expected: Build succeeded with 0 errors, 0 warnings
