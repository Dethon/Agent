# Message Pipeline Latency Optimization Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cut cold-start and per-message latency in the agent's message processing pipeline without changing any feature behavior — per-conversation MCP sessions, memory recall quality, approval flows, resource-subscription lifecycle, and metrics coverage are all preserved.

**Architecture:** Seven independent optimizations on the hot paths identified by analysis: share the OpenRouter HTTP handler across conversations (kills per-conversation TLS), overlap the auto-approve notification with tool execution, bound the memory-recall history read (tail read + LLEN instead of full LRANGE), buffer metric publishes off the hot path, parallelize the MCP session build internals, cache MCP prompts with stale-while-revalidate, parallelize multi-target reply delivery, and add an end-to-end `FirstReply` latency stage so the wins are measurable.

**Tech Stack:** .NET 10, xUnit + Shouldly + Moq, StackExchange.Redis, MCP C# SDK, Microsoft.Agents.AI.

---

## Repo conventions (read first)

- **No trailing newline in any `.cs` file** (including tests). The pre-commit hook runs `dotnet format` and re-stages whole files.
- Commits go **directly to `master`** (user's standing preference). The repo may be checked out on `rust-satellites`; switch first: `git checkout master && git pull`.
- Test naming: `{Method}_{Scenario}_{ExpectedResult}`. Assertions with Shouldly.
- Build/test from repo root: `dotnet build` / `dotnet test Tests --filter "Category!=E2E&Category!=Integration&Category!=Llm"` for the unit suite. Integration tests need Docker (in this WSL env ~148 non-E2E tests fail with `DockerUnavailableException` as a **pre-existing baseline** — compare failure sets, not counts).
- LINQ over loops per `.claude/rules/dotnet-style.md`.

## Hard constraints — DO NOT change these behaviors

1. **Per-conversation MCP sessions stay.** Each conversation keeps its own `ThreadSession` (own MCP clients, sampling handler, resource subscriptions). No connection pooling/sharing of `McpClient` across conversations.
2. **Resource-sync completion semantics stay.** `SyncResourcesAsync` must still run to completion before `ResourcesSynced` fires, and it still runs synchronously at the end of each agent run — the subscription channel's completion (which ends the turn's merged stream) depends on it. Parallelize *inside* it only. The broader resource-subscription rework is explicitly deferred.
3. **No memory degradation.** Recall must still inject the same memories; do not skip/timeout recall. The extraction anchor must remain exactly the persisted thread length.
4. **No metric loss under normal operation.** Buffering is fine; events keep their creation-time `Timestamp` (set in `MetricEvent` initializer, so buffering does not skew them).
5. **Approval semantics stay**: non-whitelisted tools still block on `RequestApprovalAsync`; rejection still terminates. Only the *auto-approved notify* overlaps execution.
6. **Don't touch** the FIFO global channel merge, per-conversation turn serialization, or fail-soft MCP connect (a down server still fails the turn — changing that is a product decision, out of scope).

---

### Task 1: Share the OpenRouter HTTP handler across chat client instances

Every conversation creates a fresh `OpenRouterChatClient` → fresh `SocketsHttpHandler` → fresh TCP+TLS handshake to OpenRouter on its first LLM call (~100–300ms). Share one static handler; keep the per-instance `ReasoningHandler` (it carries per-instance queues) wrapping it.

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs:250-262`
- Test: `Tests/Unit/Infrastructure/OpenRouterChatClientSharedHandlerTests.cs` (create)

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Infrastructure/OpenRouterChatClientSharedHandlerTests.cs`:

```csharp
using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class OpenRouterChatClientSharedHandlerTests
{
    [Fact]
    public async Task Dispose_DoesNotDisposeSharedHandler()
    {
        var client = new OpenRouterChatClient("https://example.invalid/v1/", "key", "model");
        client.Dispose();

        using var invoker = new HttpMessageInvoker(OpenRouterChatClient.SharedHandler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:9/");
        // Port 9 (discard) is closed: a live handler fails with a connection error;
        // a disposed handler would throw ObjectDisposedException instead.
        await Should.ThrowAsync<HttpRequestException>(
            () => invoker.SendAsync(request, CancellationToken.None));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~OpenRouterChatClientSharedHandlerTests"`
Expected: FAIL — compile error, `OpenRouterChatClient` has no `SharedHandler` member. Capture the failure output (RED evidence required).

- [ ] **Step 3: Implement the shared handler**

In `OpenRouterChatClient.cs`, replace `CreateHttpClient` (lines 250–262) with:

```csharp
    // One handler (= one connection pool) for the whole process: a per-conversation
    // handler would pay a fresh TCP+TLS handshake to OpenRouter on every new
    // conversation's first LLM call.
    private static readonly SocketsHttpHandler _sharedHandler = new()
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    };

    internal static SocketsHttpHandler SharedHandler => _sharedHandler;

    private static HttpClient CreateHttpClient(
        ConcurrentQueue<string> reasoningQueue, ConcurrentQueue<decimal> costQueue)
    {
        var handler = new ReasoningHandler(reasoningQueue, costQueue) { InnerHandler = _sharedHandler };
        return new HttpClient(handler, disposeHandler: false);
    }
```

Leave `Dispose()` as is — with `disposeHandler: false`, `_httpClient.Dispose()` no longer touches the handler chain. (`Tests` has `InternalsVisibleTo` on Infrastructure, so `internal` is visible.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~OpenRouterChatClientSharedHandlerTests"`
Expected: PASS

- [ ] **Step 5: Run the OpenRouter-related unit tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~OpenRouter&Category!=Llm&Category!=Integration"`
Expected: PASS (no regressions in `OpenRouterHttpHelpersTests` etc.)

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs Tests/Unit/Infrastructure/OpenRouterChatClientSharedHandlerTests.cs
git commit -m "perf(agent): share OpenRouter HTTP handler across conversations"
```

---

### Task 2: Overlap auto-approve notification with tool execution

`ToolApprovalChatClient.InvokeFunctionAsync` awaits `NotifyAutoApprovedAsync` (a full MCP round trip to the channel server) **before** invoking an auto-approved tool. The notification is display-only; run it concurrently. Note the deliberate semantic nuance: a notify failure still fails the turn (exception propagates from `Task.WhenAll`), but can no longer prevent the tool from executing.

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/ToolApprovalChatClient.cs:51-55`
- Test: `Tests/Unit/Infrastructure/ToolApprovalChatClientTests.cs` (extend)

- [ ] **Step 1: Write the failing test**

Append to `Tests/Unit/Infrastructure/ToolApprovalChatClientTests.cs` (inside the class):

```csharp
    [Fact]
    public async Task InvokeFunctionAsync_AutoApproved_InvokesToolWithoutWaitingForNotify()
    {
        // Arrange
        var notifyGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new GatedNotifyApprovalHandler(notifyGate.Task);
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var function = AIFunctionFactory.Create(() =>
        {
            invoked.TrySetResult();
            return "result";
        }, "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));
        var client = new ToolApprovalChatClient(fakeClient, handler, whitelistPatterns: ["mcp__server__TestTool"]);
        var options = new ChatOptions { Tools = [function] };

        // Act
        var responseTask = client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert: the tool runs while the auto-approval notification is still in flight
        var completed = await Task.WhenAny(invoked.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        completed.ShouldBe(invoked.Task, "tool invocation must not wait for the notify round trip");

        notifyGate.TrySetResult();
        await responseTask;
        handler.NotifyCalls.ShouldBe(1);
    }

    private sealed class GatedNotifyApprovalHandler(Task gate) : Domain.Contracts.IToolApprovalHandler
    {
        public int NotifyCalls;

        public Task<ToolApprovalResult> RequestApprovalAsync(
            IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
            => Task.FromResult(ToolApprovalResult.Rejected);

        public async Task NotifyAutoApprovedAsync(
            IReadOnlyList<ToolApprovalRequest> requests, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref NotifyCalls);
            await gate;
        }
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~InvokeFunctionAsync_AutoApproved_InvokesToolWithoutWaitingForNotify"`
Expected: FAIL — `completed` is the `Task.Delay`, because the current code awaits notify before invoking. Capture output.

- [ ] **Step 3: Implement concurrent notify**

In `ToolApprovalChatClient.InvokeFunctionAsync`, replace:

```csharp
        if (_patternMatcher.IsMatch(toolName) || _dynamicallyApproved.Contains(toolName))
        {
            await _approvalHandler.NotifyAutoApprovedAsync([request], cancellationToken);
            return await InvokeWithMetricsAsync(context, toolName, cancellationToken);
        }
```

with:

```csharp
        if (_patternMatcher.IsMatch(toolName) || _dynamicallyApproved.Contains(toolName))
        {
            // The notification is display-only; overlapping it with the invocation keeps a
            // channel round trip off the tool's critical path. A notify failure still
            // surfaces, but no longer prevents the tool from executing.
            var notifyTask = _approvalHandler.NotifyAutoApprovedAsync([request], cancellationToken);
            var invokeTask = InvokeWithMetricsAsync(context, toolName, cancellationToken).AsTask();
            await Task.WhenAll(notifyTask, invokeTask);
            return await invokeTask;
        }
```

- [ ] **Step 4: Run the full ToolApprovalChatClient test class**

Run: `dotnet test Tests --filter "FullyQualifiedName~Tests.Unit.Infrastructure.ToolApprovalChatClientTests"`
Expected: PASS (existing auto-approve tests still record notifications)

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/ToolApprovalChatClient.cs Tests/Unit/Infrastructure/ToolApprovalChatClientTests.cs
git commit -m "perf(agent): overlap auto-approve notification with tool execution"
```

---

### Task 3: Bound the memory-recall history read (tail + LLEN)

`MemoryRecallHook` fetches the **entire** thread (unbounded `LRANGE` + per-message JSON deserialize) on every message, only to build a 3-user-turn window and compute the extraction anchor (= thread length). Replace with `LLEN` (anchor) + bounded tail read. The 200-message tail bound is generous — it only changes behavior if the last 2 user turns span >200 persisted messages, far above observed tool-round counts. The chat-history wire format is untouched (still the same Redis list of the same JSON), so the second reader (`RedisStateService`/WebChat) is unaffected. `MemoryExtractionWorker` keeps using full `GetMessagesAsync` (off the hot path, needs the full window).

**Files:**
- Modify: `Domain/Contracts/IThreadStateStore.cs`
- Modify: `Infrastructure/StateManagers/RedisThreadStateStore.cs`
- Modify: `Infrastructure/StateManagers/NullThreadStateStore.cs`
- Modify: `Infrastructure/Memory/MemoryRecallHook.cs:58-61,140-157` and `MemoryRecallOptions`
- Test: `Tests/Unit/Memory/MemoryRecallHookTests.cs` (update mocks + new test)
- Test: `Tests/Integration/StateManagers/RedisThreadStateStoreTests.cs` (extend)

- [ ] **Step 1: Write the failing unit test**

Append to `Tests/Unit/Memory/MemoryRecallHookTests.cs` (reuse the file's existing setup helpers — `CreateSessionWithStateKey`, `_store`/`_embeddingService` mock setups copied from the nearest passing test in the same file for embedding + search):

```csharp
    [Fact]
    public async Task EnrichAsync_LongThread_AnchorsExtractionAtFullThreadLengthUsingTailRead()
    {
        // 500 messages persisted but only a bounded tail fetched: the extraction anchor
        // must still point at the real end of the thread, and the full-list read must not run.
        var session = CreateSessionWithStateKey("state-tail");
        var tail = Enumerable.Range(0, 200)
            .Select(i => new ChatMessage(i % 2 == 0 ? ChatRole.User : ChatRole.Assistant, $"m{i}"))
            .ToArray();
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-tail")).ReturnsAsync(500L);
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-tail", It.IsAny<int>())).ReturnsAsync(tail);
        _embeddingService.Setup(e => e.GenerateEmbeddingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(_testEmbedding);
        _store.Setup(s => s.SearchAsync(It.IsAny<string>(), It.IsAny<float[]>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        _store.Setup(s => s.GetProfileAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PersonalityProfile?)null);

        var message = new ChatMessage(ChatRole.User, "current question");
        await _hook.EnrichAsync(message, "user-1", "conv-1", null, session, CancellationToken.None);

        _queue.Complete();
        var request = await _queue.ReadAllAsync(CancellationToken.None).FirstAsync();
        request.AnchorIndex.ShouldBe(500);
        _threadStateStore.Verify(s => s.GetMessagesAsync(It.IsAny<string>()), Times.Never);
    }
```

Note: `SearchAsync`/`GetProfileAsync`/`MemoryExtractionRequest` member names — mirror the exact signatures already mocked at the top of this test file; if a parameter list differs, copy the `Setup` lines from the nearest existing test verbatim and only change the return values. `AnchorIndex` is the third positional parameter of `MemoryExtractionRequest` — check its declared name in `Domain/Memory` (or `Domain/DTOs`) and use that property name.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~EnrichAsync_LongThread_AnchorsExtractionAtFullThreadLengthUsingTailRead"`
Expected: FAIL — compile error: `IThreadStateStore` has no `GetMessageCountAsync`/`GetTailMessagesAsync`. Capture output.

- [ ] **Step 3: Add the contract methods and implementations**

`Domain/Contracts/IThreadStateStore.cs` — add after `GetMessagesAsync`:

```csharp
    Task<long> GetMessageCountAsync(string key);
    Task<ChatMessage[]?> GetTailMessagesAsync(string key, int maxCount);
```

`Infrastructure/StateManagers/RedisThreadStateStore.cs` — add after `GetMessagesAsync`:

```csharp
    public async Task<long> GetMessageCountAsync(string key)
    {
        return await _db.ListLengthAsync(key);
    }

    public async Task<ChatMessage[]?> GetTailMessagesAsync(string key, int maxCount)
    {
        var values = await _db.ListRangeAsync(key, -maxCount, -1);
        return values.Length == 0
            ? null
            : values.Select(v => JsonSerializer.Deserialize<ChatMessage>(v.ToString())!).ToArray();
    }
```

`Infrastructure/StateManagers/NullThreadStateStore.cs` — add:

```csharp
    public Task<long> GetMessageCountAsync(string key) => Task.FromResult(0L);

    public Task<ChatMessage[]?> GetTailMessagesAsync(string key, int maxCount) =>
        Task.FromResult<ChatMessage[]?>(null);
```

Also implement on any other `IThreadStateStore` implementor the compiler flags (search: `grep -rl "IThreadStateStore" --include="*.cs" | grep -v obj` — test fakes in `Tests/` may need the two members too).

- [ ] **Step 4: Use the bounded read in MemoryRecallHook**

In `Infrastructure/Memory/MemoryRecallHook.cs`:

Add to `MemoryRecallOptions`:

```csharp
    public int RecallTailMessages { get; init; } = 200;
```

Replace `TryFetchThreadAsync` with:

```csharp
    private async Task<(ChatMessage[]? Messages, long Count, string? StateKey)> TryFetchThreadAsync(AgentSession thread)
    {
        if (!RedisChatMessageStore.TryGetStateKey(thread, out var stateKey) || stateKey is null)
        {
            return (null, 0, null);
        }

        try
        {
            var countTask = threadStateStore.GetMessageCountAsync(stateKey);
            var tailTask = threadStateStore.GetTailMessagesAsync(stateKey, options.RecallTailMessages);
            await Task.WhenAll(countTask, tailTask);
            return (await tailTask, await countTask, stateKey);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to fetch thread history for recall window (key {Key})", stateKey);
            return (null, 0, stateKey);
        }
    }
```

And in `EnrichAsync`, replace:

```csharp
            var (persisted, stateKey) = await TryFetchThreadAsync(thread);
            var anchorIndex = persisted?.Length ?? 0;
```

with:

```csharp
            var (persisted, persistedCount, stateKey) = await TryFetchThreadAsync(thread);
            var anchorIndex = (int)persistedCount;
```

(`BuildRecallWindowText` is unchanged — `Where(User).TakeLast(n-1)` over the tail equals the same over the full list whenever the tail covers the last user turns.)

- [ ] **Step 5: Update existing mocks in MemoryRecallHookTests**

Every existing `_threadStateStore.Setup(s => s.GetMessagesAsync(...)).ReturnsAsync(msgs)` whose test exercises the recall window/anchor must additionally (or instead) set up the new methods, e.g.:

```csharp
        _threadStateStore.Setup(s => s.GetTailMessagesAsync("state-test", It.IsAny<int>())).ReturnsAsync(msgs);
        _threadStateStore.Setup(s => s.GetMessageCountAsync("state-test")).ReturnsAsync((long)msgs.Length);
```

The "store throws → fallback" test must throw from `GetTailMessagesAsync`/`GetMessageCountAsync` instead of `GetMessagesAsync`.

- [ ] **Step 6: Run the Memory unit tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~Tests.Unit.Memory"`
Expected: PASS, including the new anchor test.

- [ ] **Step 7: Add integration tests for the new store methods**

Append to `Tests/Integration/StateManagers/RedisThreadStateStoreTests.cs`:

```csharp
    [Fact]
    public async Task GetTailMessagesAsync_ListLongerThanMax_ReturnsOnlyTailInOrder()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();
        await store.AppendMessagesAsync(key,
            [.. Enumerable.Range(0, 10).Select(i => new ChatMessage(ChatRole.User, $"m{i}"))]);

        var tail = await store.GetTailMessagesAsync(key, 3);

        tail.ShouldNotBeNull();
        tail.Select(m => m.Text).ShouldBe(["m7", "m8", "m9"]);
    }

    [Fact]
    public async Task GetTailMessagesAsync_MaxLargerThanList_ReturnsAllMessages()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();
        await store.AppendMessagesAsync(key, [new ChatMessage(ChatRole.User, "only")]);

        var tail = await store.GetTailMessagesAsync(key, 50);

        tail.ShouldNotBeNull();
        tail.ShouldHaveSingleItem().Text.ShouldBe("only");
    }

    [Fact]
    public async Task GetMessageCountAsync_ReturnsListLength_AndZeroForMissingKey()
    {
        var key = $"thread-{Guid.NewGuid():N}";
        var store = NewStore();
        (await store.GetMessageCountAsync(key)).ShouldBe(0);

        await store.AppendMessagesAsync(key,
            [new ChatMessage(ChatRole.User, "a"), new ChatMessage(ChatRole.Assistant, "b")]);

        (await store.GetMessageCountAsync(key)).ShouldBe(2);
    }
```

Run: `dotnet test Tests --filter "FullyQualifiedName~RedisThreadStateStoreTests"`
Expected: PASS with Docker available; in this WSL env they may fail with `DockerUnavailableException` (pre-existing baseline) — in that case verify they compile and rely on CI.

- [ ] **Step 8: Commit**

```bash
git add Domain/Contracts/IThreadStateStore.cs Infrastructure/StateManagers/RedisThreadStateStore.cs Infrastructure/StateManagers/NullThreadStateStore.cs Infrastructure/Memory/MemoryRecallHook.cs Tests/Unit/Memory/MemoryRecallHookTests.cs Tests/Integration/StateManagers/RedisThreadStateStoreTests.cs
git commit -m "perf(memory): bounded tail read + LLEN anchor for recall window"
```

(If other files needed the new interface members, add them too.)

---

### Task 4: Buffered metrics publisher

Hot paths await a Redis `PUBLISH` round trip per metric event (2 per tool call, 2+ per recall, several per turn). Wrap the publisher in a bounded channel drained by a background task. Events are stamped at creation (`MetricEvent.Timestamp` initializer), so buffering does not skew data. Capacity 10 000 with `DropWrite` + warning log — unreachable under normal load, and a Redis outage no longer ripples into turns.

**Files:**
- Create: `Infrastructure/Metrics/BufferedMetricsPublisher.cs`
- Modify: `Agent/Modules/InjectorModule.cs:34` (registration)
- Test: `Tests/Unit/Infrastructure/Metrics/BufferedMetricsPublisherTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/Infrastructure/Metrics/BufferedMetricsPublisherTests.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Infrastructure.Metrics;
using Shouldly;

namespace Tests.Unit.Infrastructure.Metrics;

public class BufferedMetricsPublisherTests
{
    private sealed class RecordingPublisher : IMetricsPublisher
    {
        private readonly List<MetricEvent> _events = [];
        public TaskCompletionSource? Gate { get; set; }
        public Exception? ToThrow { get; set; }

        public IReadOnlyList<MetricEvent> Events
        {
            get { lock (_events) return [.. _events]; }
        }

        public async Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
        {
            if (Gate is not null)
            {
                await Gate.Task;
            }

            if (ToThrow is not null)
            {
                throw ToThrow;
            }

            lock (_events)
            {
                _events.Add(metricEvent);
            }
        }
    }

    private static ErrorEvent Event(string msg = "m") =>
        new() { Service = "test", ErrorType = "t", Message = msg };

    [Fact]
    public async Task PublishAsync_InnerBlocked_ReturnsImmediately()
    {
        var inner = new RecordingPublisher
        {
            Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        await using var publisher = new BufferedMetricsPublisher(inner);

        var publish = publisher.PublishAsync(Event());

        publish.IsCompleted.ShouldBeTrue("hot-path publish must not wait on the inner publisher");
        inner.Gate.TrySetResult();
    }

    [Fact]
    public async Task PublishAsync_InnerThrows_DoesNotPropagate()
    {
        var inner = new RecordingPublisher { ToThrow = new InvalidOperationException("redis down") };
        await using var publisher = new BufferedMetricsPublisher(inner);

        await Should.NotThrowAsync(() => publisher.PublishAsync(Event()));
        await publisher.DisposeAsync();
    }

    [Fact]
    public async Task DisposeAsync_FlushesPendingEvents()
    {
        var inner = new RecordingPublisher();
        var publisher = new BufferedMetricsPublisher(inner);
        foreach (var i in Enumerable.Range(0, 100))
        {
            await publisher.PublishAsync(Event($"e{i}"));
        }

        await publisher.DisposeAsync();

        inner.Events.Count.ShouldBe(100);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~BufferedMetricsPublisherTests"`
Expected: FAIL — `BufferedMetricsPublisher` does not exist. Capture output.

- [ ] **Step 3: Implement**

Create `Infrastructure/Metrics/BufferedMetricsPublisher.cs`:

```csharp
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Metrics;

public sealed class BufferedMetricsPublisher : IMetricsPublisher, IAsyncDisposable
{
    private readonly IMetricsPublisher _inner;
    private readonly ILogger<BufferedMetricsPublisher>? _logger;
    private readonly Channel<MetricEvent> _events;
    private readonly Task _drainTask;

    public BufferedMetricsPublisher(
        IMetricsPublisher inner,
        ILogger<BufferedMetricsPublisher>? logger = null,
        int capacity = 10_000)
    {
        _inner = inner;
        _logger = logger;
        _events = Channel.CreateBounded<MetricEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });
        _drainTask = Task.Run(DrainAsync);
    }

    public Task PublishAsync(MetricEvent metricEvent, CancellationToken ct = default)
    {
        if (!_events.Writer.TryWrite(metricEvent))
        {
            _logger?.LogWarning("Metrics buffer full; dropping {EventType}", metricEvent.GetType().Name);
        }

        return Task.CompletedTask;
    }

    private async Task DrainAsync()
    {
        await foreach (var metricEvent in _events.Reader.ReadAllAsync())
        {
            try
            {
                await _inner.PublishAsync(metricEvent);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Failed to publish {EventType}", metricEvent.GetType().Name);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        await Task.WhenAny(_drainTask, Task.Delay(TimeSpan.FromSeconds(5)));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~BufferedMetricsPublisherTests"`
Expected: PASS

- [ ] **Step 5: Register in the agent's DI**

In `Agent/Modules/InjectorModule.cs`, replace:

```csharp
                .AddSingleton<IMetricsPublisher, RedisMetricsPublisher>()
```

with:

```csharp
                .AddSingleton<IMetricsPublisher>(sp => new BufferedMetricsPublisher(
                    new RedisMetricsPublisher(sp.GetRequiredService<IConnectionMultiplexer>()),
                    sp.GetService<ILogger<BufferedMetricsPublisher>>()))
```

Add `using StackExchange.Redis;` and `using Microsoft.Extensions.Logging;` if missing. The DI container disposes `IAsyncDisposable` singletons at shutdown, flushing the buffer. Scope: agent process only — channel servers keep their direct publishers.

- [ ] **Step 6: Build and run the unit suite**

Run: `dotnet build && dotnet test Tests --filter "Category!=E2E&Category!=Integration&Category!=Llm"`
Expected: build OK; unit failures match the pre-existing baseline only.

- [ ] **Step 7: Commit**

```bash
git add Infrastructure/Metrics/BufferedMetricsPublisher.cs Agent/Modules/InjectorModule.cs Tests/Unit/Infrastructure/Metrics/BufferedMetricsPublisherTests.cs
git commit -m "perf(metrics): buffer metric publishes off the hot path"
```

---

### Task 5: Parallelize the MCP session build internals

This is a behavior-preserving refactor (Red-Green-**Refactor** — existing tests are the safety net; no new unit seam is practical because `McpClient` is a concrete SDK type). Three sequential chains become concurrent, preserving output order everywhere (`Task.WhenAll` preserves input order):

1. `McpClientManager.CreateAsync`: tools and prompts load **concurrently** (currently tools → then prompts).
2. `LoadPrompts`: clients fetched **in parallel** (currently strictly sequential per server — this is where the HA prompt's live HTTP stall serializes with every other server).
3. `McpFileSystemDiscovery` + `McpSubscriptionManager.SyncResourcesAsync`: per-client work in parallel; registry mounts and the `ResourcesSynced` event stay sequential/single-fire. **Do not** change when `ResourcesSynced` fires relative to sync completion (constraint #2).

**Files:**
- Modify: `Infrastructure/Agents/Mcp/McpClientManager.cs:35-39,108-133`
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemDiscovery.cs:14-63`
- Modify: `Infrastructure/Agents/Mcp/McpSubscriptionManager.cs:64-101`

- [ ] **Step 1: Confirm green baseline**

Run: `dotnet test Tests --filter "Category!=E2E&Category!=Integration&Category!=Llm"`
Record the failure list (pre-existing baseline).

- [ ] **Step 2: Parallelize McpClientManager**

Replace `CreateAsync` body:

```csharp
        var clientsWithEndpoints = await CreateClientsWithRetry(name, description, endpoints, handlers, ct);
        var toolsTask = LoadTools(clientsWithEndpoints, ct);
        var promptsTask = LoadPrompts(clientsWithEndpoints, userId, ct);
        await Task.WhenAll(toolsTask, promptsTask);
        var clients = clientsWithEndpoints.Select(c => c.Client).ToArray();
        return new McpClientManager(clients, await toolsTask, await promptsTask);
```

Replace `LoadPrompts` with (note the signature now takes the tuples, and per-client fetching is extracted for Task 6 to wrap):

```csharp
    private static async Task<string[]> LoadPrompts(
        IEnumerable<(McpClient Client, string ServerName)> clients, string userId, CancellationToken ct)
    {
        var userContextPrompt = $"## User Context\n" +
                                $"Conversation created by user: '{userId}'\n" +
                                $"Use this userId/username for all user-scoped operations. unless you get more " +
                                $"updated information in the user's message";
        var perClient = await Task.WhenAll(clients
            .Where(c => c.Client.ServerCapabilities.Prompts is not null)
            .Select(c => FetchPromptsAsync(c.Client, ct)));

        return perClient
            .SelectMany(p => p)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Prepend(userContextPrompt)
            .ToArray();
    }

    private static async Task<string[]> FetchPromptsAsync(McpClient client, CancellationToken ct)
    {
        var list = await client.ListPromptsAsync(cancellationToken: ct);
        return await Task.WhenAll(list.Select(async p =>
        {
            var result = await client.GetPromptAsync(p.Name, cancellationToken: ct);
            return string.Join("\n", result.Messages
                .Select(m => m.Content)
                .OfType<TextContentBlock>()
                .Select(t => t.Text));
        }));
    }
```

(Prompt order is unchanged: user context first, then per-client prompts in client order — `Task.WhenAll` preserves it.)

- [ ] **Step 3: Parallelize filesystem discovery**

Replace the body of `McpFileSystemDiscovery.DiscoverAndMountAsync` with a parallel gather + sequential mount (the registry is not assumed thread-safe and mount order stays deterministic):

```csharp
    public static async Task DiscoverAndMountAsync(
        IReadOnlyList<McpClient> clients,
        VirtualFileSystemRegistry registry,
        ILogger logger,
        CancellationToken ct)
    {
        var perClient = await Task.WhenAll(clients
            .Where(c => c.ServerCapabilities.Resources is not null)
            .Select(client => GatherMountsAsync(client, logger, ct)));

        foreach (var (mount, backend) in perClient.SelectMany(m => m))
        {
            registry.Mount(mount, backend);
            logger.LogInformation("Discovered filesystem '{Name}' at mount point '{MountPoint}'",
                mount.Name, mount.MountPoint);
        }
    }

    private static async Task<IReadOnlyList<(FileSystemMount Mount, McpFileSystemBackend Backend)>> GatherMountsAsync(
        McpClient client, ILogger logger, CancellationToken ct)
    {
        var resources = await client.ListResourcesAsync(cancellationToken: ct);
        var filesystemResources = resources
            .Where(r => r.Uri.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (filesystemResources.Count == 0)
        {
            return [];
        }

        var mounts = await Task.WhenAll(filesystemResources.Select(async resource =>
        {
            try
            {
                var content = await client.ReadResourceAsync(resource.Uri, cancellationToken: ct);
                var text = string.Join("", content.Contents
                    .OfType<TextResourceContents>()
                    .Select(c => c.Text));

                var metadata = JsonSerializer.Deserialize<FileSystemResourceMetadata>(text,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (metadata is null || string.IsNullOrEmpty(metadata.Name) || string.IsNullOrEmpty(metadata.MountPoint))
                {
                    logger.LogWarning("Invalid filesystem resource metadata at {Uri}", resource.Uri);
                    return ((FileSystemMount Mount, McpFileSystemBackend Backend)?)null;
                }

                var mount = new FileSystemMount(metadata.Name, metadata.MountPoint, metadata.Description ?? "");
                var backend = new McpFileSystemBackend(client, metadata.Name, logger);
                return (mount, backend);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read filesystem resource at {Uri}", resource.Uri);
                return null;
            }
        }));

        return mounts.Where(m => m is not null).Select(m => m!.Value).ToList();
    }
```

- [ ] **Step 4: Parallelize resource sync internals**

In `McpSubscriptionManager`, replace `SyncResourcesAsync` with:

```csharp
    public async Task SyncResourcesAsync(IEnumerable<McpClient> clients, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_isDisposed == 1, this);

        var clientHasResources = await Task.WhenAll(clients
            .Where(c => c.ServerCapabilities.Resources is { Subscribe: true })
            .Select(c => SyncClientAsync(c, ct)));

        if (ResourcesSynced != null)
        {
            await ResourcesSynced(clientHasResources.Any(r => r), ct);
        }
    }

    private async Task<bool> SyncClientAsync(McpClient client, CancellationToken ct)
    {
        var current = (await client.ListResourcesAsync(cancellationToken: ct))
            .Where(r => !r.Uri.StartsWith("filesystem://", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.Uri)
            .ToHashSet();
        var previous = _subscribedResources.GetValueOrDefault(client) ?? [];

        await Task.WhenAll(current.Except(previous)
            .Select(uri => client.SubscribeToResourceAsync(uri, cancellationToken: ct)));
        await Task.WhenAll(previous.Except(current)
            .Select(uri => client.UnsubscribeFromResourceAsync(uri, cancellationToken: ct)));

        _subscribedResources[client] = current;
        return current.Count > 0;
    }
```

(`ResourcesSynced` still fires exactly once, after all clients complete — same as today. `_subscribedResources` is already a `ConcurrentDictionary`.)

- [ ] **Step 5: Build and verify against baseline**

Run: `dotnet build && dotnet test Tests --filter "Category!=E2E&Category!=Integration&Category!=Llm"`
Expected: build OK; failures identical to the Step 1 baseline. If Docker is available, also run: `dotnet test Tests --filter "FullyQualifiedName~McpSubscriptionManagerTests|FullyQualifiedName~ThreadSessionTests"` (these are `Category=Llm`/Integration and may be skipped/fail without Docker + user secrets — pre-existing).

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Agents/Mcp/McpClientManager.cs Infrastructure/Agents/Mcp/McpFileSystemDiscovery.cs Infrastructure/Agents/Mcp/McpSubscriptionManager.cs
git commit -m "perf(agent): parallelize MCP session build (tools+prompts, fs discovery, resource sync)"
```

---

### Task 6: MCP prompt cache (stale-while-revalidate, per server)

Prompts are re-fetched from every server on every new conversation. They are static per server (verified: no server prompt takes user/session parameters; the user-context prompt is built locally) except the HA and Scheduling setup summaries, which are computed server-side. A stale-while-revalidate cache keyed by `ServerName` keeps content fresh to within ~TTL (60s) with **zero** added staleness inside a conversation, removes an entire warmup stage on warm starts, and confines the known HA-unreachable 30s prompt stall to background refreshes after the first build. MCP *connections* stay per conversation — only the prompt round trips are cached (sessions preserved).

**Files:**
- Create: `Infrastructure/Agents/Mcp/McpPromptCache.cs`
- Modify: `Infrastructure/Agents/Mcp/McpClientManager.cs` (`CreateAsync`/`LoadPrompts` take an optional cache)
- Modify: `Infrastructure/Agents/ThreadSession.cs` (thread the cache through `CreateAsync`/builder)
- Modify: `Infrastructure/Agents/McpAgent.cs` (optional ctor param, pass to `ThreadSession.CreateAsync`)
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs` (one cache instance for the process, passed to every `McpAgent`)
- Test: `Tests/Unit/Infrastructure/Mcp/McpPromptCacheTests.cs` (create)

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/Infrastructure/Mcp/McpPromptCacheTests.cs`:

```csharp
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Infrastructure.Mcp;

public class McpPromptCacheTests
{
    private static readonly TimeSpan _ttl = TimeSpan.FromSeconds(60);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        condition().ShouldBeTrue();
    }

    [Fact]
    public async Task GetOrFetchAsync_FirstCall_FetchesInline()
    {
        var cache = new McpPromptCache(new FakeTimeProvider(), _ttl);

        var prompts = await cache.GetOrFetchAsync("server-a", () => Task.FromResult(new[] { "p1" }));

        prompts.ShouldBe(["p1"]);
    }

    [Fact]
    public async Task GetOrFetchAsync_FreshHit_DoesNotRefetch()
    {
        var cache = new McpPromptCache(new FakeTimeProvider(), _ttl);
        var fetches = 0;
        Task<string[]> Fetch()
        {
            Interlocked.Increment(ref fetches);
            return Task.FromResult(new[] { "p1" });
        }

        await cache.GetOrFetchAsync("server-a", Fetch);
        var second = await cache.GetOrFetchAsync("server-a", Fetch);

        second.ShouldBe(["p1"]);
        fetches.ShouldBe(1);
    }

    [Fact]
    public async Task GetOrFetchAsync_StaleHit_ServesStaleAndRefreshesInBackground()
    {
        var time = new FakeTimeProvider();
        var cache = new McpPromptCache(time, _ttl);
        var fetches = 0;
        Task<string[]> Fetch()
        {
            var n = Interlocked.Increment(ref fetches);
            return Task.FromResult(new[] { $"v{n}" });
        }

        (await cache.GetOrFetchAsync("server-a", Fetch)).ShouldBe(["v1"]);
        time.Advance(_ttl + TimeSpan.FromSeconds(1));

        var staleServed = await cache.GetOrFetchAsync("server-a", Fetch);

        staleServed.ShouldBe(["v1"], "a stale hit must serve the cached value without blocking");
        await WaitUntilAsync(() => Volatile.Read(ref fetches) == 2);
        await WaitUntilAsync(() =>
            cache.GetOrFetchAsync("server-a", Fetch).GetAwaiter().GetResult().SequenceEqual(["v2"]));
    }

    [Fact]
    public async Task GetOrFetchAsync_RefreshFails_KeepsServingStaleValue()
    {
        var time = new FakeTimeProvider();
        var cache = new McpPromptCache(time, _ttl);
        var fetches = 0;
        Task<string[]> Fetch()
        {
            var n = Interlocked.Increment(ref fetches);
            return n == 1
                ? Task.FromResult(new[] { "v1" })
                : Task.FromException<string[]>(new HttpRequestException("server down"));
        }

        await cache.GetOrFetchAsync("server-a", Fetch);
        time.Advance(_ttl + TimeSpan.FromSeconds(1));

        (await cache.GetOrFetchAsync("server-a", Fetch)).ShouldBe(["v1"]);
        await WaitUntilAsync(() => Volatile.Read(ref fetches) >= 2);
        (await cache.GetOrFetchAsync("server-a", Fetch)).ShouldBe(["v1"]);
    }
}
```

(If `Microsoft.Extensions.Time.Testing` is not already referenced by `Tests.csproj`, mirror the `FakeTimeProvider` usage in `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` — it is already used there.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~McpPromptCacheTests"`
Expected: FAIL — `McpPromptCache` does not exist. Capture output.

- [ ] **Step 3: Implement the cache**

Create `Infrastructure/Agents/Mcp/McpPromptCache.cs` (public — it appears in `McpAgent`'s public ctor):

```csharp
using System.Collections.Concurrent;

namespace Infrastructure.Agents.Mcp;

public sealed class McpPromptCache(TimeProvider timeProvider, TimeSpan ttl)
{
    private sealed record CacheEntry(string[] Prompts, DateTimeOffset FetchedAt);

    private readonly ConcurrentDictionary<string, CacheEntry> _entries = new();
    private readonly ConcurrentDictionary<string, Task> _refreshes = new();

    public async Task<string[]> GetOrFetchAsync(string serverKey, Func<Task<string[]>> fetch)
    {
        if (!_entries.TryGetValue(serverKey, out var entry))
        {
            var prompts = await fetch();
            _entries[serverKey] = new CacheEntry(prompts, timeProvider.GetUtcNow());
            return prompts;
        }

        if (timeProvider.GetUtcNow() - entry.FetchedAt >= ttl)
        {
            // Stale: serve the cached value now, refresh in the background (single-flight per
            // server). A failed refresh keeps the stale value; the next stale hit retries.
            _refreshes.GetOrAdd(serverKey, key => Task.Run(async () =>
            {
                try
                {
                    var prompts = await fetch();
                    _entries[key] = new CacheEntry(prompts, timeProvider.GetUtcNow());
                }
                catch
                {
                    // Stale prompts beat a blocked or failed session build.
                }
                finally
                {
                    _refreshes.TryRemove(key, out _);
                }
            }));
        }

        return entry.Prompts;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~McpPromptCacheTests"`
Expected: PASS

- [ ] **Step 5: Thread the cache through the session build**

All call-site changes are additive optional parameters; passing `null` preserves today's always-fetch behavior (subagents and tests are unaffected).

`McpClientManager.CreateAsync` — add parameter `McpPromptCache? promptCache = null` (before `ct`) and change the prompts line from Task 5 to:

```csharp
        var promptsTask = LoadPrompts(clientsWithEndpoints, userId, promptCache, ct);
```

`LoadPrompts` — add the parameter and wrap the per-client fetch:

```csharp
    private static async Task<string[]> LoadPrompts(
        IEnumerable<(McpClient Client, string ServerName)> clients,
        string userId,
        McpPromptCache? promptCache,
        CancellationToken ct)
    {
        var userContextPrompt = $"## User Context\n" +
                                $"Conversation created by user: '{userId}'\n" +
                                $"Use this userId/username for all user-scoped operations. unless you get more " +
                                $"updated information in the user's message";
        var perClient = await Task.WhenAll(clients
            .Where(c => c.Client.ServerCapabilities.Prompts is not null)
            .Select(c => promptCache is null
                ? FetchPromptsAsync(c.Client, ct)
                : promptCache.GetOrFetchAsync(c.ServerName, () => FetchPromptsAsync(c.Client, ct))));

        return perClient
            .SelectMany(p => p)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Prepend(userContextPrompt)
            .ToArray();
    }
```

(A background refresh may run against a client whose session has since been disposed — the fetch fails, the catch keeps the stale entry, and the next conversation's stale hit retries with its own live client. Acceptable by design.)

`ThreadSession.CreateAsync` and `ThreadSessionBuilder` — add `McpPromptCache? promptCache = null` parameters and pass through to `McpClientManager.CreateAsync`.

`McpAgent` — add ctor parameter `McpPromptCache? promptCache = null`, store in a field, pass to `ThreadSession.CreateAsync` in `GetOrCreateSessionAsync`.

`MultiAgentFactory` — add a field and pass it in both `CreateFromDefinition` and `CreateSubAgent`:

```csharp
    private readonly McpPromptCache _promptCache = new(TimeProvider.System, TimeSpan.FromSeconds(60));
```

(pass `promptCache: _promptCache` to each `new McpAgent(...)`).

- [ ] **Step 6: Build and verify against baseline**

Run: `dotnet build && dotnet test Tests --filter "Category!=E2E&Category!=Integration&Category!=Llm"`
Expected: build OK; failures match baseline.

- [ ] **Step 7: Commit**

```bash
git add Infrastructure/Agents/Mcp/McpPromptCache.cs Infrastructure/Agents/Mcp/McpClientManager.cs Infrastructure/Agents/ThreadSession.cs Infrastructure/Agents/McpAgent.cs Infrastructure/Agents/MultiAgentFactory.cs Tests/Unit/Infrastructure/Mcp/McpPromptCacheTests.cs
git commit -m "perf(agent): stale-while-revalidate MCP prompt cache per server"
```

---

### Task 7: Parallel multi-target reply delivery

`ChatMonitor.DeliverUpdateAsync` delivers each chunk to fan-out targets sequentially. Parallelize across targets while keeping per-target error isolation and per-target update ordering (updates themselves stay sequential).

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs:229-256`
- Test: `Tests/Unit/Domain/MonitorTests.cs` (extend `FakeChannelConnection` + new test)

- [ ] **Step 1: Add a gate hook to the fake channel**

In `Tests/Unit/Domain/MonitorTests.cs`, extend `FakeChannelConnection`:

```csharp
    public Func<Task>? ReplyGate { get; init; }
```

and change `SendReplyAsync` to:

```csharp
    public async Task SendReplyAsync(string conversationId, string content, ReplyContentType contentType, bool isComplete, string? messageId, CancellationToken ct)
    {
        if (ReplyGate is not null)
        {
            await ReplyGate();
        }

        var reply = (conversationId, content, contentType, isComplete);
        SentReplies.Add(reply);
        OnReply?.Invoke(reply);
    }
```

- [ ] **Step 2: Write the failing test**

Append to `ChatMonitorTests` in the same file:

```csharp
    [Fact]
    public async Task Monitor_MultiTargetFanOut_DeliversToTargetsConcurrently()
    {
        // Both fan-out targets block until BOTH have been called. Concurrent delivery
        // passes; sequential delivery times out target A, which then misses the reply.
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        Func<Task> gate = async () =>
        {
            if (Interlocked.Increment(ref started) >= 2)
            {
                bothStarted.TrySetResult();
            }

            await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        };
        var targetA = new FakeChannelConnection { ChannelId = "signalr", ConversationIdToReturn = "conv-a", ReplyGate = gate };
        var targetB = new FakeChannelConnection { ChannelId = "telegram", ConversationIdToReturn = "conv-b", ReplyGate = gate };
        var origin = new FakeChannelConnection { ChannelId = "scheduling" };
        origin.WriteMessage(new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "scheduled prompt",
            Sender = "scheduler",
            ChannelId = "scheduling",
            ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", null)]
        });
        origin.Complete();
        var fakeAgent = MonitorTestMocks.CreateAgent();

        var monitor = new ChatMonitor(
            [origin, targetA, targetB],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            MonitorTestMocks.CreateThreadResolver(),
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        targetA.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete);
        targetB.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete);
    }
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~Monitor_MultiTargetFanOut_DeliversToTargetsConcurrently"`
Expected: FAIL — sequential delivery: target A's gate times out (`TimeoutException` is caught per-target and the reply skipped), so `targetA.SentReplies` is missing the StreamComplete. Takes ~2s. Capture output.

- [ ] **Step 4: Implement parallel delivery**

In `ChatMonitor.DeliverUpdateAsync`, replace the nested target loop:

```csharp
        foreach (var mapped in MapResponseUpdate(update))
        {
            await Task.WhenAll(targets.Select(target =>
                DeliverToTargetAsync(target, mapped, update.MessageId, ct)));
        }
```

and add below it:

```csharp
    private async Task DeliverToTargetAsync(
        DeliveryTarget target,
        (string Content, ReplyContentType ContentType, bool IsComplete) mapped,
        string? messageId,
        CancellationToken ct)
    {
        try
        {
            await target.Channel.SendReplyAsync(
                target.ConversationId, mapped.Content, mapped.ContentType, mapped.IsComplete, messageId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Isolate per-target delivery failures: one channel being down must not
            // abort delivery to the other targets or tear down the agent run (which
            // would also suppress its schedule-execution metric).
            logger.LogWarning(ex, "Failed to deliver reply to {ChannelId}; skipping target",
                target.Channel.ChannelId);
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "agent",
                ErrorType = ex.GetType().Name,
                Message = ex.Message
            }, ct);
        }
    }
```

(The error-content publishing loop at the end of `DeliverUpdateAsync` stays unchanged.)

- [ ] **Step 5: Run the Monitor test classes**

Run: `dotnet test Tests --filter "FullyQualifiedName~MonitorTests|FullyQualifiedName~ChatMonitor"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/MonitorTests.cs
git commit -m "perf(agent): deliver fan-out replies to targets concurrently"
```

---

### Task 8: End-to-end `FirstReply` latency stage

None of the six existing stages measures what the user feels: message arrival → first delivered reply chunk. Add `FirstReply`, and pin `LatencyStage` values while touching it (metric enums persist as ints in Redis — appending with explicit values prevents a repeat of the VoiceMetric corruption).

**Files:**
- Modify: `Domain/DTOs/Metrics/Enums/LatencyStage.cs`
- Create: `Domain/Monitor/FirstReplyTracker.cs`
- Modify: `Domain/Monitor/ChatMonitor.cs` (selector tuple + publish on first delivered content)
- Test: `Tests/Unit/Domain/DTOs/Metrics/Enums/LatencyStageTests.cs` (create)
- Test: `Tests/Unit/Domain/MonitorTests.cs` (extend `FakeAiAgent` + new tests)

- [ ] **Step 1: Write the failing enum-pinning test**

Create `Tests/Unit/Domain/DTOs/Metrics/Enums/LatencyStageTests.cs` (model: `VoiceEnumsTests` in the same directory):

```csharp
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics.Enums;

public class LatencyStageTests
{
    [Theory]
    [InlineData(LatencyStage.SessionWarmup, 0)]
    [InlineData(LatencyStage.MemoryRecall, 1)]
    [InlineData(LatencyStage.LlmFirstToken, 2)]
    [InlineData(LatencyStage.LlmTotal, 3)]
    [InlineData(LatencyStage.ToolExec, 4)]
    [InlineData(LatencyStage.HistoryStore, 5)]
    [InlineData(LatencyStage.FirstReply, 6)]
    public void LatencyStage_ValuesArePinned(LatencyStage stage, int expected)
    {
        ((int)stage).ShouldBe(expected);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~LatencyStageTests"`
Expected: FAIL — `FirstReply` does not exist. Capture output.

- [ ] **Step 3: Pin the enum and add the stage**

Replace `Domain/DTOs/Metrics/Enums/LatencyStage.cs` body:

```csharp
namespace Domain.DTOs.Metrics.Enums;

// Persisted as integers in metric events (Redis): values are pinned explicitly — never
// renumber or reuse one; append new members with the next free number.
public enum LatencyStage
{
    SessionWarmup = 0,
    MemoryRecall = 1,
    LlmFirstToken = 2,
    LlmTotal = 3,
    ToolExec = 4,
    HistoryStore = 5,
    FirstReply = 6
}
```

Run: `dotnet test Tests --filter "FullyQualifiedName~LatencyStageTests"` → PASS.

- [ ] **Step 4: Write the failing monitor tests**

First extend `FakeAiAgent` in `Tests/Unit/Domain/MonitorTests.cs` so it can stream content:

```csharp
    public AgentResponseUpdate[] UpdatesToYield { get; init; } = [];
```

and in its `RunCoreStreamingAsync`, after the `ExceptionToThrow` check, replace `yield break;` with:

```csharp
        foreach (var update in UpdatesToYield)
        {
            yield return update;
        }
```

Then append to `ChatMonitorTests`:

```csharp
    [Fact]
    public async Task Monitor_AgentStreamsContent_PublishesFirstReplyLatencyOnce()
    {
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = new FakeAiAgent
        {
            UpdatesToYield =
            [
                new AgentResponseUpdate { Contents = [new TextContent("hello")] },
                new AgentResponseUpdate { Contents = [new TextContent("world")] }
            ]
        };
        var published = new List<MetricEvent>();
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback((MetricEvent e, CancellationToken _) => { lock (published) published.Add(e); })
            .Returns(Task.CompletedTask);

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            MonitorTestMocks.CreateThreadResolver(),
            metrics.Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        var firstReply = published.OfType<LatencyEvent>()
            .Where(e => e.Stage == LatencyStage.FirstReply)
            .ShouldHaveSingleItem();
        firstReply.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
        firstReply.ConversationId.ShouldBe("conv-1");
    }

    [Fact]
    public async Task Monitor_AgentYieldsNoContent_DoesNotPublishFirstReply()
    {
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var published = new List<MetricEvent>();
        var metrics = new Mock<IMetricsPublisher>();
        metrics.Setup(m => m.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback((MetricEvent e, CancellationToken _) => { lock (published) published.Add(e); })
            .Returns(Task.CompletedTask);

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            MonitorTestMocks.CreateThreadResolver(),
            metrics.Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        published.OfType<LatencyEvent>().ShouldNotContain(e => e.Stage == LatencyStage.FirstReply);
    }
```

Run: `dotnet test Tests --filter "FullyQualifiedName~Monitor_AgentStreamsContent_PublishesFirstReplyLatencyOnce"`
Expected: FAIL — no `FirstReply` event published. Capture output.

- [ ] **Step 5: Implement the tracker and wiring**

Create `Domain/Monitor/FirstReplyTracker.cs`:

```csharp
using System.Diagnostics;

namespace Domain.Monitor;

internal sealed class FirstReplyTracker
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _fired;

    public bool TryComplete(out long elapsedMs)
    {
        elapsedMs = _stopwatch.ElapsedMilliseconds;
        return Interlocked.Exchange(ref _fired, 1) == 0;
    }
}
```

In `ChatMonitor.ProcessChatThread`:

1. The selector's `Clear`/`Cancel` cases return
   `AsyncEnumerable.Empty<(AgentResponseUpdate Update, IReadOnlyList<DeliveryTarget> Targets, FirstReplyTracker? Tracker)>()`.
2. In the `default` case, create the tracker first (it must time from message pickup):

```csharp
                    default:
                        var tracker = new FirstReplyTracker();
```

   and change the final projection to:

```csharp
                            .Select(pair => (Update: pair.Item1, Targets: messageTargets, Tracker: (FirstReplyTracker?)tracker));
```

3. Change `DeliverUpdateAsync`'s signature to return `Task<bool>` — `true` when at least one mapped item whose `ContentType` is not `StreamComplete` was delivered to at least one target without throwing. With Task 7 in place:

```csharp
    private async Task<bool> DeliverUpdateAsync(
        AgentResponseUpdate update, IReadOnlyList<DeliveryTarget> targets, CancellationToken ct)
    {
        var deliveredContent = false;
        foreach (var mapped in MapResponseUpdate(update))
        {
            var results = await Task.WhenAll(targets.Select(target =>
                DeliverToTargetAsync(target, mapped, update.MessageId, ct)));
            deliveredContent |= mapped.ContentType != ReplyContentType.StreamComplete && results.Any(r => r);
        }

        foreach (var error in update.Contents.OfType<ErrorContent>())
        {
            await metricsPublisher.PublishAsync(new ErrorEvent
            {
                Service = "agent",
                ErrorType = error.ErrorCode ?? "Unknown",
                Message = error.Message
            }, ct);
        }

        return deliveredContent;
    }
```

   and make `DeliverToTargetAsync` return `Task<bool>` (`true` on success, `false` from the catch).

4. Replace the consume loop:

```csharp
        await foreach (var (update, replyTargets, tracker) in aiResponses.WithCancellation(ct))
        {
            var deliveredContent = await DeliverUpdateAsync(update, replyTargets, ct);
            if (deliveredContent && tracker is not null && tracker.TryComplete(out var firstReplyMs))
            {
                await metricsPublisher.PublishAsync(new LatencyEvent
                {
                    Stage = LatencyStage.FirstReply,
                    DurationMs = firstReplyMs,
                    ConversationId = agentKey.ConversationId
                }, ct);
            }

            yield return true;
        }
```

Add `using Domain.DTOs.Metrics.Enums;` to `ChatMonitor.cs` if missing. (The Observability collector and dashboard handle stages generically — no changes needed there; the new stage appears automatically.)

- [ ] **Step 6: Run the full Domain unit tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~Tests.Unit.Domain"`
Expected: PASS (both new tests and all existing monitor tests).

- [ ] **Step 7: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/LatencyStage.cs Domain/Monitor/FirstReplyTracker.cs Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/DTOs/Metrics/Enums/LatencyStageTests.cs Tests/Unit/Domain/MonitorTests.cs
git commit -m "feat(metrics): FirstReply end-to-end latency stage; pin LatencyStage values"
```

---

### Task 9: Final verification

- [ ] **Step 1: Full build + unit suite**

Run: `dotnet build && dotnet test Tests --filter "Category!=E2E&Category!=Integration&Category!=Llm"`
Expected: build clean; failures identical to the pre-existing baseline recorded in Task 5 Step 1. Any *new* failure is a regression — fix before proceeding.

- [ ] **Step 2: Integration suite (if Docker available)**

Run: `dotnet test Tests --filter "Category=Integration"`
Expected: in this WSL env most fail with `DockerUnavailableException` (baseline); with Docker, `RedisThreadStateStoreTests` (including the three new tests) must pass.

- [ ] **Step 3: Latency benchmark sanity check (optional, Docker required)**

The Jonas latency benchmarks (`Tests/Integration/Fixtures/JonasMcpStackFixture.cs` consumers) stub the LLM and measure framework overhead — exactly what Tasks 5/6 target. If runnable, compare session-warmup numbers before/after (`git stash` ↔ `git stash pop` for the before run). After deployment, the dashboard's `SessionWarmup`, `MemoryRecall`, and new `FirstReply` stages confirm the wins on real traffic.

- [ ] **Step 4: Verify no stray formatting damage**

Run: `git status` — working tree clean; `git log --oneline master -8` shows the seven commits above.
