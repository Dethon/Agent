# Memory Observability Integration — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate the three memory metric events (MemoryRecallEvent, MemoryExtractionEvent, MemoryDreamingEvent) through the full observability stack — collector, query service, REST API, SignalR, and a new Dashboard Memory page.

**Architecture:** Follow the Unified Metrics Pattern — the exact conventions used by Token/Tool/Error/Schedule events. Each memory event gets its own Redis sorted set + hash totals, query methods, API endpoints, SignalR broadcast, and Dashboard state. A single Memory page with tabbed event tables provides the UI.

**Tech Stack:** .NET 10, Redis (StackExchange.Redis), SignalR, Blazor WebAssembly, ApexCharts, xUnit + Moq + Shouldly

**Spec:** `docs/superpowers/specs/2026-03-29-memory-observability-integration-design.md`

---

### Task 1: Memory Metric Enums

**Files:**
- Create: `Domain/DTOs/Metrics/Enums/MemoryDimension.cs`
- Create: `Domain/DTOs/Metrics/Enums/MemoryMetric.cs`

- [ ] **Step 1: Create MemoryDimension enum**

```csharp
namespace Domain.DTOs.Metrics.Enums;

public enum MemoryDimension { User, EventType, Agent }
```

- [ ] **Step 2: Create MemoryMetric enum**

```csharp
namespace Domain.DTOs.Metrics.Enums;

public enum MemoryMetric { Count, AvgDuration, StoredCount, MergedCount, DecayedCount }
```

- [ ] **Step 3: Verify build**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/MemoryDimension.cs Domain/DTOs/Metrics/Enums/MemoryMetric.cs
git commit -m "feat(memory): add MemoryDimension and MemoryMetric enums"
```

---

### Task 2: MetricsCollectorService — Memory Event Handlers

**Files:**
- Modify: `Observability/Services/MetricsCollectorService.cs`
- Test: `Tests/Unit/Observability/Services/MetricsCollectorServiceTests.cs`

- [ ] **Step 1: Write failing tests for MemoryRecallEvent**

Add to `MetricsCollectorServiceTests.cs`:

```csharp
[Fact]
public async Task ProcessEventAsync_MemoryRecall_IncrementsDailyTotals()
{
    var evt = new MemoryRecallEvent
    {
        DurationMs = 250,
        MemoryCount = 5,
        UserId = "alice",
        Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
    };

    await _sut.ProcessEventAsync(evt, _db.Object);

    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:recalls", 1, It.IsAny<CommandFlags>()), Times.Once);
    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:recallDuration", 250, It.IsAny<CommandFlags>()), Times.Once);
    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:recallMemories", 5, It.IsAny<CommandFlags>()), Times.Once);
    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:byUser:alice", 1, It.IsAny<CommandFlags>()), Times.Once);
}

[Fact]
public async Task ProcessEventAsync_MemoryRecall_AddsSortedSetEntry()
{
    var evt = new MemoryRecallEvent
    {
        DurationMs = 250,
        MemoryCount = 5,
        UserId = "alice",
        Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
    };

    await _sut.ProcessEventAsync(evt, _db.Object);

    _db.Verify(d => d.SortedSetAddAsync(
        "metrics:memory-recall:2026-03-15",
        It.Is<RedisValue>(v => v.ToString().Contains("\"userId\":\"alice\"")),
        evt.Timestamp.ToUnixTimeMilliseconds(),
        It.IsAny<SortedSetWhen>(),
        It.IsAny<CommandFlags>()), Times.Once);
}

[Fact]
public async Task ProcessEventAsync_MemoryRecall_ForwardsToSignalR()
{
    var evt = new MemoryRecallEvent
    {
        DurationMs = 250,
        MemoryCount = 5,
        UserId = "alice"
    };

    await _sut.ProcessEventAsync(evt, _db.Object);

    _clientProxy.Verify(c => c.SendCoreAsync(
        "OnMemoryRecall",
        It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], evt)),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsCollectorServiceTests.ProcessEventAsync_MemoryRecall" --no-restore`
Expected: 3 failures — no case handler for MemoryRecallEvent

- [ ] **Step 3: Write failing tests for MemoryExtractionEvent**

Add to `MetricsCollectorServiceTests.cs`:

```csharp
[Fact]
public async Task ProcessEventAsync_MemoryExtraction_IncrementsDailyTotals()
{
    var evt = new MemoryExtractionEvent
    {
        DurationMs = 1500,
        CandidateCount = 8,
        StoredCount = 3,
        UserId = "bob",
        Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
    };

    await _sut.ProcessEventAsync(evt, _db.Object);

    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:extractions", 1, It.IsAny<CommandFlags>()), Times.Once);
    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:extractionDuration", 1500, It.IsAny<CommandFlags>()), Times.Once);
    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:candidates", 8, It.IsAny<CommandFlags>()), Times.Once);
    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:stored", 3, It.IsAny<CommandFlags>()), Times.Once);
    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:byUser:bob", 1, It.IsAny<CommandFlags>()), Times.Once);
}

[Fact]
public async Task ProcessEventAsync_MemoryExtraction_AddsSortedSetEntry()
{
    var evt = new MemoryExtractionEvent
    {
        DurationMs = 1500,
        CandidateCount = 8,
        StoredCount = 3,
        UserId = "bob",
        Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
    };

    await _sut.ProcessEventAsync(evt, _db.Object);

    _db.Verify(d => d.SortedSetAddAsync(
        "metrics:memory-extraction:2026-03-15",
        It.Is<RedisValue>(v => v.ToString().Contains("\"userId\":\"bob\"")),
        evt.Timestamp.ToUnixTimeMilliseconds(),
        It.IsAny<SortedSetWhen>(),
        It.IsAny<CommandFlags>()), Times.Once);
}

[Fact]
public async Task ProcessEventAsync_MemoryExtraction_ForwardsToSignalR()
{
    var evt = new MemoryExtractionEvent
    {
        DurationMs = 1500,
        CandidateCount = 8,
        StoredCount = 3,
        UserId = "bob"
    };

    await _sut.ProcessEventAsync(evt, _db.Object);

    _clientProxy.Verify(c => c.SendCoreAsync(
        "OnMemoryExtraction",
        It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], evt)),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

- [ ] **Step 4: Write failing tests for MemoryDreamingEvent**

Add to `MetricsCollectorServiceTests.cs`:

```csharp
[Fact]
public async Task ProcessEventAsync_MemoryDreaming_IncrementsDailyTotals()
{
    var evt = new MemoryDreamingEvent
    {
        MergedCount = 7,
        DecayedCount = 3,
        ProfileRegenerated = true,
        UserId = "alice",
        Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
    };

    await _sut.ProcessEventAsync(evt, _db.Object);

    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:dreamings", 1, It.IsAny<CommandFlags>()), Times.Once);
    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:merged", 7, It.IsAny<CommandFlags>()), Times.Once);
    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:decayed", 3, It.IsAny<CommandFlags>()), Times.Once);
    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:profileRegens", 1, It.IsAny<CommandFlags>()), Times.Once);
}

[Fact]
public async Task ProcessEventAsync_MemoryDreaming_NoProfileRegen_DoesNotIncrementRegens()
{
    var evt = new MemoryDreamingEvent
    {
        MergedCount = 4,
        DecayedCount = 1,
        ProfileRegenerated = false,
        UserId = "bob",
        Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
    };

    await _sut.ProcessEventAsync(evt, _db.Object);

    _db.Verify(d => d.HashIncrementAsync(
        "metrics:totals:2026-03-15", "memory:profileRegens", It.IsAny<long>(), It.IsAny<CommandFlags>()), Times.Never);
}

[Fact]
public async Task ProcessEventAsync_MemoryDreaming_AddsSortedSetEntry()
{
    var evt = new MemoryDreamingEvent
    {
        MergedCount = 7,
        DecayedCount = 3,
        ProfileRegenerated = true,
        UserId = "alice",
        Timestamp = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero)
    };

    await _sut.ProcessEventAsync(evt, _db.Object);

    _db.Verify(d => d.SortedSetAddAsync(
        "metrics:memory-dreaming:2026-03-15",
        It.Is<RedisValue>(v => v.ToString().Contains("\"userId\":\"alice\"")),
        evt.Timestamp.ToUnixTimeMilliseconds(),
        It.IsAny<SortedSetWhen>(),
        It.IsAny<CommandFlags>()), Times.Once);
}

[Fact]
public async Task ProcessEventAsync_MemoryDreaming_ForwardsToSignalR()
{
    var evt = new MemoryDreamingEvent
    {
        MergedCount = 7,
        DecayedCount = 3,
        ProfileRegenerated = true,
        UserId = "alice"
    };

    await _sut.ProcessEventAsync(evt, _db.Object);

    _clientProxy.Verify(c => c.SendCoreAsync(
        "OnMemoryDreaming",
        It.Is<object[]>(args => args.Length == 1 && ReferenceEquals(args[0], evt)),
        It.IsAny<CancellationToken>()), Times.Once);
}
```

- [ ] **Step 5: Run all new tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsCollectorServiceTests.ProcessEventAsync_Memory" --no-restore`
Expected: 11 failures

- [ ] **Step 6: Implement ProcessMemoryRecallAsync**

Add to `MetricsCollectorService.cs` switch block, after the `case HeartbeatEvent` handler:

```csharp
case MemoryRecallEvent recall:
    await ProcessMemoryRecallAsync(recall, db);
    break;
```

Add method:

```csharp
private async Task ProcessMemoryRecallAsync(MemoryRecallEvent evt, IDatabase db)
{
    var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
    var sortedSetKey = $"metrics:memory-recall:{dateKey}";
    var totalsKey = $"metrics:totals:{dateKey}";
    var score = evt.Timestamp.ToUnixTimeMilliseconds();
    var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

    await Task.WhenAll(
        db.SortedSetAddAsync(sortedSetKey, json, score),
        db.HashIncrementAsync(totalsKey, "memory:recalls"),
        db.HashIncrementAsync(totalsKey, "memory:recallDuration", evt.DurationMs),
        db.HashIncrementAsync(totalsKey, "memory:recallMemories", evt.MemoryCount),
        db.HashIncrementAsync(totalsKey, $"memory:byUser:{evt.UserId}"),
        db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
        db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

    await hubContext.Clients.All.SendAsync("OnMemoryRecall", evt);
}
```

- [ ] **Step 7: Implement ProcessMemoryExtractionAsync**

Add to switch block:

```csharp
case MemoryExtractionEvent extraction:
    await ProcessMemoryExtractionAsync(extraction, db);
    break;
```

Add method:

```csharp
private async Task ProcessMemoryExtractionAsync(MemoryExtractionEvent evt, IDatabase db)
{
    var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
    var sortedSetKey = $"metrics:memory-extraction:{dateKey}";
    var totalsKey = $"metrics:totals:{dateKey}";
    var score = evt.Timestamp.ToUnixTimeMilliseconds();
    var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

    await Task.WhenAll(
        db.SortedSetAddAsync(sortedSetKey, json, score),
        db.HashIncrementAsync(totalsKey, "memory:extractions"),
        db.HashIncrementAsync(totalsKey, "memory:extractionDuration", evt.DurationMs),
        db.HashIncrementAsync(totalsKey, "memory:candidates", evt.CandidateCount),
        db.HashIncrementAsync(totalsKey, "memory:stored", evt.StoredCount),
        db.HashIncrementAsync(totalsKey, $"memory:byUser:{evt.UserId}"),
        db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
        db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry));

    await hubContext.Clients.All.SendAsync("OnMemoryExtraction", evt);
}
```

- [ ] **Step 8: Implement ProcessMemoryDreamingAsync**

Add to switch block:

```csharp
case MemoryDreamingEvent dreaming:
    await ProcessMemoryDreamingAsync(dreaming, db);
    break;
```

Add method:

```csharp
private async Task ProcessMemoryDreamingAsync(MemoryDreamingEvent evt, IDatabase db)
{
    var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
    var sortedSetKey = $"metrics:memory-dreaming:{dateKey}";
    var totalsKey = $"metrics:totals:{dateKey}";
    var score = evt.Timestamp.ToUnixTimeMilliseconds();
    var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

    var tasks = new List<Task>
    {
        db.SortedSetAddAsync(sortedSetKey, json, score),
        db.HashIncrementAsync(totalsKey, "memory:dreamings"),
        db.HashIncrementAsync(totalsKey, "memory:merged", evt.MergedCount),
        db.HashIncrementAsync(totalsKey, "memory:decayed", evt.DecayedCount),
        db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
        db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
    };

    if (evt.ProfileRegenerated)
    {
        tasks.Add(db.HashIncrementAsync(totalsKey, "memory:profileRegens"));
    }

    await Task.WhenAll(tasks);

    await hubContext.Clients.All.SendAsync("OnMemoryDreaming", evt);
}
```

- [ ] **Step 9: Run all tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsCollectorServiceTests" --no-restore`
Expected: All 20 tests pass (9 existing + 11 new)

- [ ] **Step 10: Commit**

```bash
git add Observability/Services/MetricsCollectorService.cs Tests/Unit/Observability/Services/MetricsCollectorServiceTests.cs
git commit -m "feat(memory): add memory event handlers to MetricsCollectorService"
```

---

### Task 3: MetricsQueryService — Summary & Event Retrieval

**Files:**
- Modify: `Observability/Services/MetricsQueryService.cs`
- Modify: `Dashboard.Client/Services/MetricsApiService.cs` (MetricsSummary record)
- Test: `Tests/Unit/Observability/Services/MetricsQueryServiceTests.cs`

- [ ] **Step 1: Write failing test for summary with memory fields**

Add to `MetricsQueryServiceTests.cs`:

```csharp
[Fact]
public async Task GetSummaryAsync_WithMemoryFields_ReturnsMemoryTotals()
{
    var date = new DateOnly(2026, 3, 15);
    _db.Setup(d => d.HashGetAllAsync("metrics:totals:2026-03-15", It.IsAny<CommandFlags>()))
        .ReturnsAsync(
        [
            new HashEntry("tokens:input", 100),
            new HashEntry("tokens:output", 50),
            new HashEntry("tokens:cost", 10000),
            new HashEntry("tools:count", 10),
            new HashEntry("tools:errors", 2),
            new HashEntry("memory:recalls", 25),
            new HashEntry("memory:extractions", 15),
            new HashEntry("memory:dreamings", 3),
            new HashEntry("memory:stored", 42),
            new HashEntry("memory:merged", 7),
            new HashEntry("memory:decayed", 4),
        ]);

    var result = await _sut.GetSummaryAsync(date, date);

    result.TotalRecalls.ShouldBe(25);
    result.TotalExtractions.ShouldBe(15);
    result.TotalDreamings.ShouldBe(3);
    result.MemoriesStored.ShouldBe(42);
    result.MemoriesMerged.ShouldBe(7);
    result.MemoriesDecayed.ShouldBe(4);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceTests.GetSummaryAsync_WithMemoryFields" --no-restore`
Expected: Compilation error — MetricsSummary does not have memory fields

- [ ] **Step 3: Update MetricsSummary record**

In `Dashboard.Client/Services/MetricsApiService.cs`, update the MetricsSummary record:

```csharp
public record MetricsSummary(
    long InputTokens,
    long OutputTokens,
    long TotalTokens,
    decimal Cost,
    long ToolCalls,
    long ToolErrors,
    long TotalRecalls = 0,
    long TotalExtractions = 0,
    long TotalDreamings = 0,
    long MemoriesStored = 0,
    long MemoriesMerged = 0,
    long MemoriesDecayed = 0);
```

Also update the server-side MetricsSummary record in `Observability/Services/MetricsQueryService.cs` to match (same signature with defaults).

- [ ] **Step 4: Update GetSummaryAsync to read memory hash fields**

In `MetricsQueryService.cs`, add variables after the existing ones:

```csharp
long recalls = 0, extractions = 0, dreamings = 0, memoriesStored = 0, memoriesMerged = 0, memoriesDecayed = 0;
```

Add cases to the switch block inside the foreach:

```csharp
case "memory:recalls":
    recalls += value;
    break;
case "memory:extractions":
    extractions += value;
    break;
case "memory:dreamings":
    dreamings += value;
    break;
case "memory:stored":
    memoriesStored += value;
    break;
case "memory:merged":
    memoriesMerged += value;
    break;
case "memory:decayed":
    memoriesDecayed += value;
    break;
```

Update the return statement to include memory fields:

```csharp
return new MetricsSummary(
    inputTokens,
    outputTokens,
    inputTokens + outputTokens,
    costFixed / 10000m,
    toolCalls,
    toolErrors,
    recalls,
    extractions,
    dreamings,
    memoriesStored,
    memoriesMerged,
    memoriesDecayed);
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceTests" --no-restore`
Expected: All tests pass (existing + new)

- [ ] **Step 6: Commit**

```bash
git add Observability/Services/MetricsQueryService.cs Dashboard.Client/Services/MetricsApiService.cs Tests/Unit/Observability/Services/MetricsQueryServiceTests.cs
git commit -m "feat(memory): add memory fields to MetricsSummary and GetSummaryAsync"
```

---

### Task 4: MetricsQueryService — Grouped Aggregation

**Files:**
- Modify: `Observability/Services/MetricsQueryService.cs`
- Test: `Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs`

- [ ] **Step 1: Write failing tests for GetMemoryGroupedAsync**

Add to `MetricsQueryServiceGroupingTests.cs`:

```csharp
// =====================================================================
// Memory Grouped Aggregation
// =====================================================================

[Fact]
public async Task GetMemoryGroupedAsync_ByUser_Count_CountsPerUser()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:memory-recall:2026-03-15",
    [
        new MemoryRecallEvent { DurationMs = 100, MemoryCount = 5, UserId = "alice" },
        new MemoryRecallEvent { DurationMs = 200, MemoryCount = 3, UserId = "alice" },
    ]);
    SetupSortedSet("metrics:memory-extraction:2026-03-15",
    [
        new MemoryExtractionEvent { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "bob" },
    ]);
    SetupSortedSet("metrics:memory-dreaming:2026-03-15",
    [
        new MemoryDreamingEvent { MergedCount = 5, DecayedCount = 2, ProfileRegenerated = true, UserId = "alice" },
    ]);

    var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.User, MemoryMetric.Count, date, date);

    result["alice"].ShouldBe(3m); // 2 recalls + 1 dreaming
    result["bob"].ShouldBe(1m);   // 1 extraction
}

[Fact]
public async Task GetMemoryGroupedAsync_ByEventType_Count_CountsPerType()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:memory-recall:2026-03-15",
    [
        new MemoryRecallEvent { DurationMs = 100, MemoryCount = 5, UserId = "alice" },
        new MemoryRecallEvent { DurationMs = 200, MemoryCount = 3, UserId = "bob" },
    ]);
    SetupSortedSet("metrics:memory-extraction:2026-03-15",
    [
        new MemoryExtractionEvent { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "alice" },
    ]);
    SetupSortedSet("metrics:memory-dreaming:2026-03-15", []);

    var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.EventType, MemoryMetric.Count, date, date);

    result["Recall"].ShouldBe(2m);
    result["Extraction"].ShouldBe(1m);
}

[Fact]
public async Task GetMemoryGroupedAsync_ByUser_AvgDuration_AveragesRecallAndExtractionOnly()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:memory-recall:2026-03-15",
    [
        new MemoryRecallEvent { DurationMs = 100, MemoryCount = 5, UserId = "alice" },
        new MemoryRecallEvent { DurationMs = 300, MemoryCount = 3, UserId = "alice" },
    ]);
    SetupSortedSet("metrics:memory-extraction:2026-03-15",
    [
        new MemoryExtractionEvent { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "alice" },
    ]);
    SetupSortedSet("metrics:memory-dreaming:2026-03-15",
    [
        new MemoryDreamingEvent { MergedCount = 5, DecayedCount = 2, ProfileRegenerated = true, UserId = "alice" },
    ]);

    var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.User, MemoryMetric.AvgDuration, date, date);

    // Only recall + extraction have duration: (100 + 300 + 1000) / 3 = 466.666...
    result["alice"].ShouldBeInRange(466.66m, 466.67m);
}

[Fact]
public async Task GetMemoryGroupedAsync_ByEventType_StoredCount_SumsFromExtractions()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:memory-recall:2026-03-15",
    [
        new MemoryRecallEvent { DurationMs = 100, MemoryCount = 5, UserId = "alice" },
    ]);
    SetupSortedSet("metrics:memory-extraction:2026-03-15",
    [
        new MemoryExtractionEvent { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "alice" },
        new MemoryExtractionEvent { DurationMs = 2000, CandidateCount = 12, StoredCount = 5, UserId = "bob" },
    ]);
    SetupSortedSet("metrics:memory-dreaming:2026-03-15", []);

    var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.EventType, MemoryMetric.StoredCount, date, date);

    result["Extraction"].ShouldBe(8m); // 3 + 5
    result["Recall"].ShouldBe(0m);     // recalls have no StoredCount
}

[Fact]
public async Task GetMemoryGroupedAsync_ByAgent_MergedCount_SumsFromDreaming()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:memory-recall:2026-03-15", []);
    SetupSortedSet("metrics:memory-extraction:2026-03-15", []);
    SetupSortedSet("metrics:memory-dreaming:2026-03-15",
    [
        new MemoryDreamingEvent { MergedCount = 5, DecayedCount = 2, ProfileRegenerated = true, UserId = "alice", AgentId = "agent-1" },
        new MemoryDreamingEvent { MergedCount = 3, DecayedCount = 1, ProfileRegenerated = false, UserId = "bob", AgentId = "agent-1" },
        new MemoryDreamingEvent { MergedCount = 7, DecayedCount = 4, ProfileRegenerated = true, UserId = "alice", AgentId = null },
    ]);

    var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.Agent, MemoryMetric.MergedCount, date, date);

    result["agent-1"].ShouldBe(8m); // 5 + 3
    result["unknown"].ShouldBe(7m);
}

[Fact]
public async Task GetMemoryGroupedAsync_EmptyData_ReturnsEmptyDictionary()
{
    var date = new DateOnly(2026, 3, 15);
    SetupSortedSet("metrics:memory-recall:2026-03-15", []);
    SetupSortedSet("metrics:memory-extraction:2026-03-15", []);
    SetupSortedSet("metrics:memory-dreaming:2026-03-15", []);

    var result = await _sut.GetMemoryGroupedAsync(MemoryDimension.User, MemoryMetric.Count, date, date);

    result.ShouldBeEmpty();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests.GetMemoryGroupedAsync" --no-restore`
Expected: Compilation error — GetMemoryGroupedAsync does not exist

- [ ] **Step 3: Implement GetMemoryGroupedAsync**

Add to `MetricsQueryService.cs`, using the `MemoryDimension` and `MemoryMetric` enums:

```csharp
public async Task<Dictionary<string, decimal>> GetMemoryGroupedAsync(
    MemoryDimension dimension, MemoryMetric metric, DateOnly from, DateOnly to)
{
    var recalls = await GetEventsAsync<MemoryRecallEvent>("metrics:memory-recall:", from, to);
    var extractions = await GetEventsAsync<MemoryExtractionEvent>("metrics:memory-extraction:", from, to);
    var dreamings = await GetEventsAsync<MemoryDreamingEvent>("metrics:memory-dreaming:", from, to);

    var allEvents = recalls.Cast<MetricEvent>()
        .Concat(extractions)
        .Concat(dreamings)
        .ToList();

    return allEvents
        .GroupBy(e => dimension switch
        {
            MemoryDimension.User => e switch
            {
                MemoryRecallEvent r => r.UserId,
                MemoryExtractionEvent x => x.UserId,
                MemoryDreamingEvent d => d.UserId,
                _ => "unknown"
            },
            MemoryDimension.EventType => e switch
            {
                MemoryRecallEvent => "Recall",
                MemoryExtractionEvent => "Extraction",
                MemoryDreamingEvent => "Dreaming",
                _ => "unknown"
            },
            MemoryDimension.Agent => e.AgentId ?? "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(dimension))
        })
        .ToDictionary(
            g => g.Key,
            g => metric switch
            {
                MemoryMetric.Count => (decimal)g.Count(),
                MemoryMetric.AvgDuration => g.Any(e => e is MemoryRecallEvent or MemoryExtractionEvent)
                    ? (decimal)g.Where(e => e is MemoryRecallEvent or MemoryExtractionEvent)
                        .Average(e => e switch
                        {
                            MemoryRecallEvent r => r.DurationMs,
                            MemoryExtractionEvent x => x.DurationMs,
                            _ => 0
                        })
                    : 0m,
                MemoryMetric.StoredCount => g.OfType<MemoryExtractionEvent>().Sum(e => (decimal)e.StoredCount),
                MemoryMetric.MergedCount => g.OfType<MemoryDreamingEvent>().Sum(e => (decimal)e.MergedCount),
                MemoryMetric.DecayedCount => g.OfType<MemoryDreamingEvent>().Sum(e => (decimal)e.DecayedCount),
                _ => throw new ArgumentOutOfRangeException(nameof(metric))
            });
}
```

Add the required using at the top:

```csharp
using Domain.DTOs.Metrics.Enums;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsQueryServiceGroupingTests" --no-restore`
Expected: All 16 tests pass (10 existing + 6 new)

- [ ] **Step 5: Commit**

```bash
git add Observability/Services/MetricsQueryService.cs Tests/Unit/Observability/Services/MetricsQueryServiceGroupingTests.cs
git commit -m "feat(memory): add GetMemoryGroupedAsync to MetricsQueryService"
```

---

### Task 5: REST API Endpoints

**Files:**
- Modify: `Observability/MetricsApiEndpoints.cs`

- [ ] **Step 1: Add memory API endpoints**

Add to `MetricsApiEndpoints.cs`, after the existing schedule endpoints:

```csharp
api.MapGet("/memory/recall", async (
    MetricsQueryService query,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return await query.GetEventsAsync<MemoryRecallEvent>("metrics:memory-recall:", fromDate, toDate);
});

api.MapGet("/memory/extraction", async (
    MetricsQueryService query,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return await query.GetEventsAsync<MemoryExtractionEvent>("metrics:memory-extraction:", fromDate, toDate);
});

api.MapGet("/memory/dreaming", async (
    MetricsQueryService query,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return await query.GetEventsAsync<MemoryDreamingEvent>("metrics:memory-dreaming:", fromDate, toDate);
});

api.MapGet("/memory/by/{dimension}", async (
    MetricsQueryService query,
    MemoryDimension dimension,
    MemoryMetric metric,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return await query.GetMemoryGroupedAsync(dimension, metric, fromDate, toDate);
});
```

Add the required usings:

```csharp
using Domain.DTOs.Metrics.Enums;
```

- [ ] **Step 2: Verify build**

Run: `dotnet build Observability/Observability.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Observability/MetricsApiEndpoints.cs
git commit -m "feat(memory): add memory metric REST API endpoints"
```

---

### Task 6: MetricsApiService — Client-Side API Methods

**Files:**
- Modify: `Dashboard.Client/Services/MetricsApiService.cs`

- [ ] **Step 1: Add memory API methods**

Add to `MetricsApiService` class:

```csharp
public Task<List<MemoryRecallEvent>?> GetMemoryRecallAsync(DateOnly from, DateOnly to) =>
    http.GetFromJsonAsync<List<MemoryRecallEvent>>($"api/metrics/memory/recall?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

public Task<List<MemoryExtractionEvent>?> GetMemoryExtractionAsync(DateOnly from, DateOnly to) =>
    http.GetFromJsonAsync<List<MemoryExtractionEvent>>($"api/metrics/memory/extraction?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

public Task<List<MemoryDreamingEvent>?> GetMemoryDreamingAsync(DateOnly from, DateOnly to) =>
    http.GetFromJsonAsync<List<MemoryDreamingEvent>>($"api/metrics/memory/dreaming?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");

public Task<Dictionary<string, decimal>?> GetMemoryGroupedAsync(
    MemoryDimension dimension, MemoryMetric metric, DateOnly from, DateOnly to,
    CancellationToken ct = default) =>
    http.GetFromJsonAsync<Dictionary<string, decimal>>(
        $"api/metrics/memory/by/{dimension}?metric={metric}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);
```

- [ ] **Step 2: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Services/MetricsApiService.cs
git commit -m "feat(memory): add memory API methods to MetricsApiService"
```

---

### Task 7: SignalR Hub — Client Handlers

**Files:**
- Modify: `Dashboard.Client/Services/MetricsHubService.cs`

- [ ] **Step 1: Add memory event handler registrations**

Add to `MetricsHubService`, after the existing OnScheduleExecution method:

```csharp
public virtual IDisposable OnMemoryRecall(Func<MemoryRecallEvent, Task> handler) =>
    _connection!.On("OnMemoryRecall", handler);

public virtual IDisposable OnMemoryExtraction(Func<MemoryExtractionEvent, Task> handler) =>
    _connection!.On("OnMemoryExtraction", handler);

public virtual IDisposable OnMemoryDreaming(Func<MemoryDreamingEvent, Task> handler) =>
    _connection!.On("OnMemoryDreaming", handler);
```

- [ ] **Step 2: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Services/MetricsHubService.cs
git commit -m "feat(memory): add memory event handlers to MetricsHubService"
```

---

### Task 8: Dashboard State — MemoryStore

**Files:**
- Create: `Dashboard.Client/State/Memory/MemoryState.cs`
- Create: `Dashboard.Client/State/Memory/MemoryStore.cs`

- [ ] **Step 1: Create MemoryState**

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Memory;

public record MemoryState
{
    public IReadOnlyList<MemoryRecallEvent> RecallEvents { get; init; } = [];
    public IReadOnlyList<MemoryExtractionEvent> ExtractionEvents { get; init; } = [];
    public IReadOnlyList<MemoryDreamingEvent> DreamingEvents { get; init; } = [];
    public MemoryDimension GroupBy { get; init; } = MemoryDimension.User;
    public MemoryMetric Metric { get; init; } = MemoryMetric.Count;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}
```

- [ ] **Step 2: Create MemoryStore**

```csharp
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Memory;

public record SetMemoryRecallEvents(IReadOnlyList<MemoryRecallEvent> Events) : IAction;
public record SetMemoryExtractionEvents(IReadOnlyList<MemoryExtractionEvent> Events) : IAction;
public record SetMemoryDreamingEvents(IReadOnlyList<MemoryDreamingEvent> Events) : IAction;
public record SetMemoryBreakdown(Dictionary<string, decimal> Breakdown) : IAction;
public record SetMemoryGroupBy(MemoryDimension GroupBy) : IAction;
public record SetMemoryMetric(MemoryMetric Metric) : IAction;
public record AppendMemoryRecallEvent(MemoryRecallEvent Event) : IAction;
public record AppendMemoryExtractionEvent(MemoryExtractionEvent Event) : IAction;
public record AppendMemoryDreamingEvent(MemoryDreamingEvent Event) : IAction;
public record SetMemoryDateRange(DateOnly From, DateOnly To) : IAction;

public sealed class MemoryStore : Store<MemoryState>
{
    public MemoryStore() : base(new MemoryState()) { }

    public void SetRecallEvents(IReadOnlyList<MemoryRecallEvent> events) =>
        Dispatch(new SetMemoryRecallEvents(events), static (s, a) => s with { RecallEvents = a.Events });

    public void SetExtractionEvents(IReadOnlyList<MemoryExtractionEvent> events) =>
        Dispatch(new SetMemoryExtractionEvents(events), static (s, a) => s with { ExtractionEvents = a.Events });

    public void SetDreamingEvents(IReadOnlyList<MemoryDreamingEvent> events) =>
        Dispatch(new SetMemoryDreamingEvents(events), static (s, a) => s with { DreamingEvents = a.Events });

    public void SetBreakdown(Dictionary<string, decimal> breakdown) =>
        Dispatch(new SetMemoryBreakdown(breakdown), static (s, a) => s with { Breakdown = a.Breakdown });

    public void SetGroupBy(MemoryDimension groupBy) =>
        Dispatch(new SetMemoryGroupBy(groupBy), static (s, a) => s with { GroupBy = a.GroupBy });

    public void SetMetric(MemoryMetric metric) =>
        Dispatch(new SetMemoryMetric(metric), static (s, a) => s with { Metric = a.Metric });

    public void AppendRecallEvent(MemoryRecallEvent evt) =>
        Dispatch(new AppendMemoryRecallEvent(evt), static (s, a) => s with
        {
            RecallEvents = [.. s.RecallEvents, a.Event],
        });

    public void AppendExtractionEvent(MemoryExtractionEvent evt) =>
        Dispatch(new AppendMemoryExtractionEvent(evt), static (s, a) => s with
        {
            ExtractionEvents = [.. s.ExtractionEvents, a.Event],
        });

    public void AppendDreamingEvent(MemoryDreamingEvent evt) =>
        Dispatch(new AppendMemoryDreamingEvent(evt), static (s, a) => s with
        {
            DreamingEvents = [.. s.DreamingEvents, a.Event],
        });

    public void SetDateRange(DateOnly from, DateOnly to) =>
        Dispatch(new SetMemoryDateRange(from, to), static (s, a) => s with { From = a.From, To = a.To });
}
```

- [ ] **Step 3: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add Dashboard.Client/State/Memory/MemoryState.cs Dashboard.Client/State/Memory/MemoryStore.cs
git commit -m "feat(memory): add MemoryState and MemoryStore for dashboard"
```

---

### Task 9: MetricsState & MetricsStore — Memory Counter Fields

**Files:**
- Modify: `Dashboard.Client/State/Metrics/MetricsState.cs`
- Modify: `Dashboard.Client/State/Metrics/MetricsStore.cs`

- [ ] **Step 1: Add memory fields to MetricsState**

Add after the existing properties in `MetricsState.cs`:

```csharp
public long TotalRecalls { get; init; }
public long TotalExtractions { get; init; }
public long TotalDreamings { get; init; }
public long MemoriesStored { get; init; }
public long MemoriesMerged { get; init; }
public long MemoriesDecayed { get; init; }
```

- [ ] **Step 2: Add memory increment actions to MetricsStore**

Add action records:

```csharp
public record IncrementMemoryRecall(int MemoryCount) : IAction;
public record IncrementMemoryExtraction(int StoredCount) : IAction;
public record IncrementMemoryDreaming(int MergedCount, int DecayedCount) : IAction;
```

Add methods:

```csharp
public void IncrementMemoryRecall(int memoryCount) =>
    Dispatch(new IncrementMemoryRecall(memoryCount), static (s, a) => s with
    {
        TotalRecalls = s.TotalRecalls + 1,
    });

public void IncrementMemoryExtraction(int storedCount) =>
    Dispatch(new IncrementMemoryExtraction(storedCount), static (s, a) => s with
    {
        TotalExtractions = s.TotalExtractions + 1,
        MemoriesStored = s.MemoriesStored + a.StoredCount,
    });

public void IncrementMemoryDreaming(int mergedCount, int decayedCount) =>
    Dispatch(new IncrementMemoryDreaming(mergedCount, decayedCount), static (s, a) => s with
    {
        TotalDreamings = s.TotalDreamings + 1,
        MemoriesMerged = s.MemoriesMerged + a.MergedCount,
        MemoriesDecayed = s.MemoriesDecayed + a.DecayedCount,
    });
```

- [ ] **Step 3: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: Build succeeded

- [ ] **Step 4: Commit**

```bash
git add Dashboard.Client/State/Metrics/MetricsState.cs Dashboard.Client/State/Metrics/MetricsStore.cs
git commit -m "feat(memory): add memory counter fields to MetricsState and MetricsStore"
```

---

### Task 10: DataLoadEffect & MetricsHubEffect — Memory Integration

**Files:**
- Modify: `Dashboard.Client/Effects/DataLoadEffect.cs`
- Modify: `Dashboard.Client/Effects/MetricsHubEffect.cs`
- Modify: `Tests/Unit/Dashboard.Client/Effects/MetricsHubEffectTests.cs`

- [ ] **Step 1: Write failing test for memory SignalR events**

Add to the `_rapidEventCases` dictionary in `MetricsHubEffectTests.cs`:

```csharp
["MemoryRecall"] = (
    new Dictionary<string, decimal> { ["stale-memory"] = 50m },
    new Dictionary<string, decimal> { ["fresh-memory"] = 100m },
    hub => hub.FireMemoryRecall(new MemoryRecallEvent
    { DurationMs = 100, MemoryCount = 5, UserId = "test" }),
    self => self._memoryStore.State.Breakdown),
["MemoryExtraction"] = (
    new Dictionary<string, decimal> { ["stale-extract"] = 30m },
    new Dictionary<string, decimal> { ["fresh-extract"] = 60m },
    hub => hub.FireMemoryExtraction(new MemoryExtractionEvent
    { DurationMs = 1000, CandidateCount = 8, StoredCount = 3, UserId = "test" }),
    self => self._memoryStore.State.Breakdown),
["MemoryDreaming"] = (
    new Dictionary<string, decimal> { ["stale-dream"] = 10m },
    new Dictionary<string, decimal> { ["fresh-dream"] = 20m },
    hub => hub.FireMemoryDreaming(new MemoryDreamingEvent
    { MergedCount = 5, DecayedCount = 2, ProfileRegenerated = true, UserId = "test" }),
    self => self._memoryStore.State.Breakdown),
```

Add `MemoryStore` field to the test class:

```csharp
private readonly MemoryStore _memoryStore = new();
```

Update the constructor to pass `_memoryStore` to `MetricsHubEffect`. Update `DisposeAsync` to dispose `_memoryStore`.

Add fire methods to `FakeMetricsHub`:

```csharp
private readonly List<Func<MemoryRecallEvent, Task>> _recallHandlers = [];
private readonly List<Func<MemoryExtractionEvent, Task>> _extractionHandlers = [];
private readonly List<Func<MemoryDreamingEvent, Task>> _dreamingHandlers = [];

public override IDisposable OnMemoryRecall(Func<MemoryRecallEvent, Task> handler)
{
    _recallHandlers.Add(handler);
    return new ActionDisposable(() => _recallHandlers.Remove(handler));
}

public override IDisposable OnMemoryExtraction(Func<MemoryExtractionEvent, Task> handler)
{
    _extractionHandlers.Add(handler);
    return new ActionDisposable(() => _extractionHandlers.Remove(handler));
}

public override IDisposable OnMemoryDreaming(Func<MemoryDreamingEvent, Task> handler)
{
    _dreamingHandlers.Add(handler);
    return new ActionDisposable(() => _dreamingHandlers.Remove(handler));
}

public Task FireMemoryRecall(MemoryRecallEvent evt) =>
    Task.WhenAll(_recallHandlers.Select(h => h(evt)));

public Task FireMemoryExtraction(MemoryExtractionEvent evt) =>
    Task.WhenAll(_extractionHandlers.Select(h => h(evt)));

public Task FireMemoryDreaming(MemoryDreamingEvent evt) =>
    Task.WhenAll(_dreamingHandlers.Select(h => h(evt)));
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsHubEffectTests" --no-restore`
Expected: Compilation error — MetricsHubEffect constructor doesn't accept MemoryStore

- [ ] **Step 3: Update MetricsHubEffect to handle memory events**

Add `MemoryStore memoryStore` to the primary constructor parameter list.

Add CTS field:

```csharp
private CancellationTokenSource _memoryBreakdownCts = new();
```

Add refresh method:

```csharp
private async Task RefreshMemoryBreakdownAsync(CancellationToken ct)
{
    try
    {
        var s = memoryStore.State;
        var result = await api.GetMemoryGroupedAsync(s.GroupBy, s.Metric, s.From, s.To, ct);
        ct.ThrowIfCancellationRequested();
        memoryStore.SetBreakdown(result ?? []);
    }
    catch (OperationCanceledException) { }
    catch { /* Breakdown stays at last known value */ }
}
```

Add subscriptions in `StartAsync()`, before the connection event handlers:

```csharp
_subscriptions.Add(hub.OnMemoryRecall(async evt =>
{
    metricsStore.IncrementMemoryRecall(evt.MemoryCount);
    memoryStore.AppendRecallEvent(evt);
    var ct = ResetCts(ref _memoryBreakdownCts);
    await RefreshMemoryBreakdownAsync(ct);
}));

_subscriptions.Add(hub.OnMemoryExtraction(async evt =>
{
    metricsStore.IncrementMemoryExtraction(evt.StoredCount);
    memoryStore.AppendExtractionEvent(evt);
    var ct = ResetCts(ref _memoryBreakdownCts);
    await RefreshMemoryBreakdownAsync(ct);
}));

_subscriptions.Add(hub.OnMemoryDreaming(async evt =>
{
    metricsStore.IncrementMemoryDreaming(evt.MergedCount, evt.DecayedCount);
    memoryStore.AppendDreamingEvent(evt);
    var ct = ResetCts(ref _memoryBreakdownCts);
    await RefreshMemoryBreakdownAsync(ct);
}));
```

Add `_memoryBreakdownCts.Dispose();` to `DisposeAsync()`.

- [ ] **Step 4: Update DataLoadEffect to load memory data**

Add `MemoryStore memoryStore` to the primary constructor parameter list.

In `LoadAsync`, add after `schedulesStore.SetDateRange(from, to);`:

```csharp
memoryStore.SetDateRange(from, to);
```

Add API calls after the existing breakdown tasks:

```csharp
var memoryRecallTask = api.GetMemoryRecallAsync(from, to);
var memoryExtractionTask = api.GetMemoryExtractionAsync(from, to);
var memoryDreamingTask = api.GetMemoryDreamingAsync(from, to);
var memoryBreakdownTask = api.GetMemoryGroupedAsync(
    memoryStore.State.GroupBy, memoryStore.State.Metric, from, to);
```

Add to the `Task.WhenAll` call:

```csharp
memoryRecallTask, memoryExtractionTask, memoryDreamingTask, memoryBreakdownTask
```

Add after `schedulesStore.SetBreakdown(...)`:

```csharp
memoryStore.SetRecallEvents(await memoryRecallTask ?? []);
memoryStore.SetExtractionEvents(await memoryExtractionTask ?? []);
memoryStore.SetDreamingEvents(await memoryDreamingTask ?? []);
memoryStore.SetBreakdown(await memoryBreakdownTask ?? []);
```

Update the summary mapping to include memory fields:

```csharp
if (summary is not null)
{
    metricsStore.UpdateSummary(new MetricsState
    {
        InputTokens = summary.InputTokens,
        OutputTokens = summary.OutputTokens,
        Cost = summary.Cost,
        ToolCalls = summary.ToolCalls,
        ToolErrors = summary.ToolErrors,
        TotalRecalls = summary.TotalRecalls,
        TotalExtractions = summary.TotalExtractions,
        TotalDreamings = summary.TotalDreamings,
        MemoriesStored = summary.MemoriesStored,
        MemoriesMerged = summary.MemoriesMerged,
        MemoriesDecayed = summary.MemoriesDecayed,
    });
}
```

- [ ] **Step 5: Register MemoryStore in Dashboard.Client/Program.cs**

Add using:

```csharp
using Dashboard.Client.State.Memory;
```

Add after the existing store registrations:

```csharp
builder.Services.AddSingleton<MemoryStore>();
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~MetricsHubEffectTests" --no-restore`
Expected: All 7 theory cases pass (4 existing + 3 new)

- [ ] **Step 7: Commit**

```bash
git add Dashboard.Client/Effects/DataLoadEffect.cs Dashboard.Client/Effects/MetricsHubEffect.cs Dashboard.Client/Program.cs Tests/Unit/Dashboard.Client/Effects/MetricsHubEffectTests.cs
git commit -m "feat(memory): integrate memory events into DataLoadEffect and MetricsHubEffect"
```

---

### Task 11: Dashboard Memory Page

**Files:**
- Create: `Dashboard.Client/Pages/Memory.razor`
- Modify: `Dashboard.Client/Layout/MainLayout.razor`

- [ ] **Step 1: Create Memory.razor page**

```razor
@page "/memory"
@using Dashboard.Client.State.Memory
@using Dashboard.Client.State.Metrics
@using Domain.DTOs.Metrics
@using Domain.DTOs.Metrics.Enums
@using static Dashboard.Client.Components.PillSelector
@implements IDisposable
@inject MemoryStore Store
@inject MetricsStore Metrics
@inject DataLoadEffect DataLoad
@inject MetricsApiService Api
@inject LocalStorageService Storage

<div class="memory-page">
    <header class="page-header">
        <h2>Memory</h2>
        <div class="controls">
            <PillSelector Label="Group by" Options="DimensionOptions" Value="@_state.GroupBy.ToString()"
                          OnChanged="OnDimensionChanged" />
            <PillSelector Label="Metric" Options="MetricOptions" Value="@_state.Metric.ToString()"
                          OnChanged="OnMetricChanged" />
            <PillSelector Label="Time" Options="TimeOptions" Value="@_selectedDays.ToString()"
                          OnChanged="OnTimeChanged" />
        </div>
    </header>

    <section class="kpi-row">
        <KpiCard Label="Total Recalls" Value="@_metrics.TotalRecalls.ToString("N0")" Color="var(--accent-blue)" />
        <KpiCard Label="Total Extractions" Value="@_metrics.TotalExtractions.ToString("N0")" Color="var(--accent-green)" />
        <KpiCard Label="Avg Latency" Value="@($"{_avgLatency:F0}ms")" Color="var(--accent-yellow)" />
        <KpiCard Label="Memories Stored" Value="@_metrics.MemoriesStored.ToString("N0")" Color="var(--accent-purple)" />
        <KpiCard Label="Merged" Value="@_metrics.MemoriesMerged.ToString("N0")" Color="var(--accent-pink)" />
        <KpiCard Label="Decayed" Value="@_metrics.MemoriesDecayed.ToString("N0")" Color="var(--accent-red)" />
    </section>

    <section class="section">
        <DynamicChart Data="_state.Breakdown" ChartType="DynamicChart.ChartMode.HorizontalBar"
                      MetricLabel="@(MetricOptions.FirstOrDefault(o => o.Value == _state.Metric.ToString())?.Label ?? "")"
                      Unit="@GetMetricUnit()" />
    </section>

    <section class="section">
        <div class="tab-card">
            <div class="tab-bar">
                <div class="tab @(_activeTab == "Recall" ? "tab-active" : "")" @onclick='() => SetTab("Recall")'>
                    Recall
                </div>
                <div class="tab @(_activeTab == "Extraction" ? "tab-active" : "")" @onclick='() => SetTab("Extraction")'>
                    Extraction
                </div>
                <div class="tab @(_activeTab == "Dreaming" ? "tab-active" : "")" @onclick='() => SetTab("Dreaming")'>
                    Dreaming
                </div>
            </div>

            @if (_activeTab == "Recall")
            {
                <div class="events-table">
                    <div class="table-header recall-grid">
                        <span class='sortable @SortClass("Time")' @onclick='() => SortBy("Time")'>Time @SortIndicator("Time")</span>
                        <span class='sortable @SortClass("User")' @onclick='() => SortBy("User")'>User @SortIndicator("User")</span>
                        <span class='sortable @SortClass("Duration")' @onclick='() => SortBy("Duration")'>Duration @SortIndicator("Duration")</span>
                        <span class='sortable @SortClass("Memories")' @onclick='() => SortBy("Memories")'>Memories Found @SortIndicator("Memories")</span>
                    </div>
                    @foreach (var evt in GetSortedRecalls())
                    {
                        <div class="table-row recall-grid">
                            <span>@evt.Timestamp.ToString("dd/MM HH:mm:ss")</span>
                            <span>@evt.UserId</span>
                            <span>@evt.DurationMs ms</span>
                            <span>@evt.MemoryCount</span>
                        </div>
                    }
                </div>
            }
            else if (_activeTab == "Extraction")
            {
                <div class="events-table">
                    <div class="table-header extraction-grid">
                        <span class='sortable @SortClass("Time")' @onclick='() => SortBy("Time")'>Time @SortIndicator("Time")</span>
                        <span class='sortable @SortClass("User")' @onclick='() => SortBy("User")'>User @SortIndicator("User")</span>
                        <span class='sortable @SortClass("Duration")' @onclick='() => SortBy("Duration")'>Duration @SortIndicator("Duration")</span>
                        <span class='sortable @SortClass("Candidates")' @onclick='() => SortBy("Candidates")'>Candidates @SortIndicator("Candidates")</span>
                        <span class='sortable @SortClass("Stored")' @onclick='() => SortBy("Stored")'>Stored @SortIndicator("Stored")</span>
                    </div>
                    @foreach (var evt in GetSortedExtractions())
                    {
                        <div class="table-row extraction-grid">
                            <span>@evt.Timestamp.ToString("dd/MM HH:mm:ss")</span>
                            <span>@evt.UserId</span>
                            <span>@evt.DurationMs ms</span>
                            <span>@evt.CandidateCount</span>
                            <span class="stored-count">@evt.StoredCount</span>
                        </div>
                    }
                </div>
            }
            else
            {
                <div class="events-table">
                    <div class="table-header dreaming-grid">
                        <span class='sortable @SortClass("Time")' @onclick='() => SortBy("Time")'>Time @SortIndicator("Time")</span>
                        <span class='sortable @SortClass("User")' @onclick='() => SortBy("User")'>User @SortIndicator("User")</span>
                        <span class='sortable @SortClass("Merged")' @onclick='() => SortBy("Merged")'>Merged @SortIndicator("Merged")</span>
                        <span class='sortable @SortClass("Decayed")' @onclick='() => SortBy("Decayed")'>Decayed @SortIndicator("Decayed")</span>
                        <span>Profile Regen</span>
                    </div>
                    @foreach (var evt in GetSortedDreamings())
                    {
                        <div class="table-row dreaming-grid">
                            <span>@evt.Timestamp.ToString("dd/MM HH:mm:ss")</span>
                            <span>@evt.UserId</span>
                            <span class="merged-count">@evt.MergedCount</span>
                            <span class="decayed-count">@evt.DecayedCount</span>
                            <span>@(evt.ProfileRegenerated ? "\u2713" : "\u2014")</span>
                        </div>
                    }
                </div>
            }
        </div>
    </section>
</div>

@code {
    private MemoryState _state = new();
    private MetricsState _metrics = new();
    private int _selectedDays = 1;
    private DateOnly _from = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateOnly _to = DateOnly.FromDateTime(DateTime.UtcNow);
    private double _avgLatency;
    private string _activeTab = "Recall";
    private IDisposable? _sub;
    private IDisposable? _metricsSub;

    private static readonly IReadOnlyList<PillOption> DimensionOptions =
    [
        new("User", nameof(MemoryDimension.User)),
        new("Event Type", nameof(MemoryDimension.EventType)),
        new("Agent", nameof(MemoryDimension.Agent)),
    ];

    private static readonly IReadOnlyList<PillOption> MetricOptions =
    [
        new("Count", nameof(MemoryMetric.Count)),
        new("Avg Duration", nameof(MemoryMetric.AvgDuration)),
        new("Stored", nameof(MemoryMetric.StoredCount)),
        new("Merged", nameof(MemoryMetric.MergedCount)),
        new("Decayed", nameof(MemoryMetric.DecayedCount)),
    ];

    private static readonly IReadOnlyList<PillOption> TimeOptions =
    [
        new("Today", "1"),
        new("7d", "7"),
        new("30d", "30"),
    ];

    protected override async Task OnInitializedAsync()
    {
        var savedGroupBy = await Storage.GetAsync<MemoryDimension>("memory.groupBy");
        var savedMetric = await Storage.GetAsync<MemoryMetric>("memory.metric");
        var savedDays = await Storage.GetIntAsync("memory.days");
        var savedTab = await Storage.GetStringAsync("memory.activeTab");

        if (savedGroupBy.HasValue) Store.SetGroupBy(savedGroupBy.Value);
        if (savedMetric.HasValue) Store.SetMetric(savedMetric.Value);
        if (savedDays.HasValue) _selectedDays = savedDays.Value;
        if (!string.IsNullOrEmpty(savedTab)) _activeTab = savedTab;

        _sub = Store.StateObservable.Subscribe(s =>
        {
            _state = s;
            ComputeAvgLatency(s);
            InvokeAsync(StateHasChanged);
        });

        _metricsSub = Metrics.StateObservable.Subscribe(m =>
        {
            _metrics = m;
            InvokeAsync(StateHasChanged);
        });

        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to.AddDays(-(_selectedDays - 1));
        Store.SetDateRange(_from, _to);
        await DataLoad.LoadAsync(_from, _to);
    }

    private void ComputeAvgLatency(MemoryState s)
    {
        var totalDuration = s.RecallEvents.Sum(e => e.DurationMs) + s.ExtractionEvents.Sum(e => e.DurationMs);
        var totalOps = s.RecallEvents.Count + s.ExtractionEvents.Count;
        _avgLatency = totalOps > 0 ? (double)totalDuration / totalOps : 0;
    }

    private async Task OnDimensionChanged(string value)
    {
        Store.SetGroupBy(Enum.Parse<MemoryDimension>(value));
        await Storage.SetAsync("memory.groupBy", value);
        await ReloadBreakdown();
    }

    private async Task OnMetricChanged(string value)
    {
        Store.SetMetric(Enum.Parse<MemoryMetric>(value));
        await Storage.SetAsync("memory.metric", value);
        await ReloadBreakdown();
    }

    private async Task OnTimeChanged(string value)
    {
        _selectedDays = int.Parse(value);
        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to.AddDays(-(_selectedDays - 1));
        Store.SetDateRange(_from, _to);
        await Storage.SetAsync("memory.days", value);
        await DataLoad.LoadAsync(_from, _to);
    }

    private async Task SetTab(string tab)
    {
        _activeTab = tab;
        await Storage.SetAsync("memory.activeTab", tab);
    }

    private async Task ReloadBreakdown()
    {
        var breakdown = await Api.GetMemoryGroupedAsync(
            Store.State.GroupBy, Store.State.Metric, _from, _to);
        Store.SetBreakdown(breakdown ?? []);
    }

    private string GetMetricUnit() => _state.Metric switch
    {
        MemoryMetric.AvgDuration => "ms",
        _ => ""
    };

    private string _sortColumn = "Time";
    private bool _sortAsc;

    private void SortBy(string column)
    {
        if (_sortColumn == column) _sortAsc = !_sortAsc;
        else { _sortColumn = column; _sortAsc = false; }
    }

    private IEnumerable<MemoryRecallEvent> GetSortedRecalls()
    {
        var events = _state.RecallEvents.TakeLast(50).AsEnumerable();
        Func<MemoryRecallEvent, object> keySelector = _sortColumn switch
        {
            "User" => e => e.UserId,
            "Duration" => e => e.DurationMs,
            "Memories" => e => e.MemoryCount,
            _ => e => e.Timestamp
        };
        return _sortAsc ? events.OrderBy(keySelector) : events.OrderByDescending(keySelector);
    }

    private IEnumerable<MemoryExtractionEvent> GetSortedExtractions()
    {
        var events = _state.ExtractionEvents.TakeLast(50).AsEnumerable();
        Func<MemoryExtractionEvent, object> keySelector = _sortColumn switch
        {
            "User" => e => e.UserId,
            "Duration" => e => e.DurationMs,
            "Candidates" => e => e.CandidateCount,
            "Stored" => e => e.StoredCount,
            _ => e => e.Timestamp
        };
        return _sortAsc ? events.OrderBy(keySelector) : events.OrderByDescending(keySelector);
    }

    private IEnumerable<MemoryDreamingEvent> GetSortedDreamings()
    {
        var events = _state.DreamingEvents.TakeLast(50).AsEnumerable();
        Func<MemoryDreamingEvent, object> keySelector = _sortColumn switch
        {
            "User" => e => e.UserId,
            "Merged" => e => e.MergedCount,
            "Decayed" => e => e.DecayedCount,
            _ => e => e.Timestamp
        };
        return _sortAsc ? events.OrderBy(keySelector) : events.OrderByDescending(keySelector);
    }

    private string SortClass(string column) => _sortColumn == column ? "sorted" : "";
    private string SortIndicator(string column) => _sortColumn != column ? "" : _sortAsc ? "\u25b2" : "\u25bc";

    public void Dispose()
    {
        _sub?.Dispose();
        _metricsSub?.Dispose();
    }
}

<style>
    .memory-page { display: flex; flex-direction: column; gap: 1.5rem; }
    .page-header { display: flex; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; gap: 1rem; }
    .page-header h2 { font-size: 1.4rem; font-weight: 600; }
    .controls { display: flex; gap: 1.5rem; flex-wrap: wrap; align-items: flex-end; }
    .kpi-row { display: flex; gap: 1rem; flex-wrap: wrap; }
    .section h3 { font-size: 1rem; margin-bottom: 0.8rem; color: var(--text-secondary); }

    .tab-card { background: var(--bg-card); border-radius: 8px; overflow: hidden; }
    .tab-bar { display: flex; border-bottom: 1px solid rgba(255,255,255,0.06); }
    .tab { padding: 0.75rem 1.25rem; font-size: 0.85rem; font-weight: 500; color: var(--text-secondary); cursor: pointer; border-bottom: 2px solid transparent; transition: color 0.15s, border-color 0.15s; }
    .tab:hover { color: var(--text-primary); }
    .tab-active { color: var(--accent-blue); border-bottom-color: var(--accent-blue); }

    .events-table { display: flex; flex-direction: column; gap: 2px; padding: 0.5rem; }
    .table-header, .table-row { display: grid; gap: 0.5rem; padding: 0.4rem 0.8rem; font-size: 0.82rem; }
    .table-header { background: var(--bg-secondary); color: var(--text-secondary); font-weight: 600; border-radius: 4px; text-transform: uppercase; font-size: 0.7rem; }
    .table-row { background: var(--bg-card); border-radius: 4px; }
    .table-row:hover { background: rgba(255,255,255,0.03); }
    .sortable { cursor: pointer; user-select: none; }
    .sortable:hover { color: var(--text-primary); }
    .sortable.sorted { color: var(--text-primary); }

    .recall-grid { grid-template-columns: 135px minmax(0, 1fr) 100px 120px; }
    .extraction-grid { grid-template-columns: 135px minmax(0, 1fr) 100px 100px 80px; }
    .dreaming-grid { grid-template-columns: 135px minmax(0, 1fr) 80px 80px 100px; }

    .stored-count { color: var(--accent-green); }
    .merged-count { color: var(--accent-pink); }
    .decayed-count { color: var(--accent-red); }

    @@media (max-width: 768px) {
        .page-header { flex-direction: column; }
        .page-header h2 { font-size: 1.1rem; }
        .events-table { overflow-x: auto; -webkit-overflow-scrolling: touch; }
        .table-header, .table-row { font-size: 0.72rem; padding: 0.4rem 0.5rem; min-width: 420px; }
    }
</style>
```

- [ ] **Step 2: Add Memory nav entry to MainLayout.razor**

Add after the Schedules NavLink:

```razor
<NavLink href="/memory" class="sidebar-icon" title="Memory">
    <span>&#128065;&#xFE0E;</span>
</NavLink>
```

- [ ] **Step 3: Check if LocalStorageService has GetStringAsync**

Verify `Dashboard.Client/Services/LocalStorageService.cs` has a `GetStringAsync` method. If it only has `GetAsync<T>` and `GetIntAsync`, add:

```csharp
public async Task<string?> GetStringAsync(string key) =>
    await _js.InvokeAsync<string?>("localStorage.getItem", key);
```

- [ ] **Step 4: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git add Dashboard.Client/Pages/Memory.razor Dashboard.Client/Layout/MainLayout.razor
git commit -m "feat(memory): add Memory dashboard page with tabbed event tables"
```

---

### Task 12: Full Build & Test Verification

**Files:** None (verification only)

- [ ] **Step 1: Run full solution build**

Run: `dotnet build`
Expected: Build succeeded with 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test Tests/Tests.csproj --no-restore`
Expected: All tests pass

- [ ] **Step 3: Run only memory-related tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Memory" --no-restore -v normal`
Expected: All memory-related tests pass

- [ ] **Step 4: Commit any fixes if needed**

Only commit if step 1-3 revealed issues that required fixes.
