# MCP 2026-07-28 Stateless Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Migrate every MCP server and the agent onto the stateless 2026-07-28 protocol, replacing the six channel servers' unsolicited notifications with a long-poll tool.

**Architecture:** A shared in-memory `ChannelInbox` in `Domain` buffers inbound items per subscriber. Each channel server exposes a hidden `channel_receive` tool that holds a request open until items arrive or 30s elapses. The agent runs one pump loop per channel that feeds the existing `Channel<ChannelMessage>`, so `ChatMonitor` and everything downstream is untouched. Per-conversation state that used to key off the removed MCP session id moves into a vendor-prefixed `_meta` entry.

**Tech Stack:** .NET 10, `ModelContextProtocol` 2.0.0, xUnit + Shouldly + Moq, `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`).

**Design spec:** `docs/superpowers/specs/2026-07-29-mcp-stateless-migration-design.md`

## Global Constraints

- **No trailing newline in any `.cs` file** — `.editorconfig` sets `insert_final_newline = false`. Applies to test files too.
- **Never set `HttpServerTransportOptions.Stateless`.** The 2.0.0 default (`true`) is correct; setting it `false` silently renegotiates down to protocol 2025-11-25.
- **TDD, Red-Green-Refactor.** Write the failing test, run it, watch it fail with the expected message, then implement.
- **Commit after every task.** The pre-commit hook (`.githooks/pre-commit`) runs `dotnet format` on staged `.cs` files and re-stages them **whole** — partial staging does not survive, so make the working tree match the commit.
- **`git add` explicit paths only.** Never `git add -A`; the user commits into this tree concurrently.
- **Commit on the current branch (`mcp-update`).** Never switch or create branches.
- **Serialize `dotnet` invocations.** Two concurrent `dotnet test`/`build` runs in WSL trigger an RCU stall that only `wsl --shutdown` clears.
- **Domain layer takes no external dependencies** — no `HttpClient`, no MCP SDK types, no ASP.NET types in `Domain/`.
- **LINQ over loops**, primary constructors for DI, `record` for DTOs, no XML doc comments.
- **Error handling is centralized** via each server's `AddCallToolFilter`. Do not add try/catch inside tool methods.
- **`FakeTimeProvider` trap:** `Task.Delay(ts, timeProvider, ct)` does not complete until the fake clock is advanced. Tests that exercise the wait path must `Advance` (in a loop if the code re-waits) or they hang and look like a wedged runner.

**Build/test commands:**

```bash
dotnet build agent.sln
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelInboxTests" -m:1
dotnet test Tests/Tests.csproj --filter "Category!=E2E" -m:1
```

---

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `Domain/Channels/ChannelInboxItem.cs` | Discriminated envelope for one queued item |
| `Domain/Channels/ChannelInbox.cs` | Per-subscriber bounded queues + wait/wake/drain |
| `Domain/DTOs/Channel/ChannelReceiveResult.cs` | Wire shape returned by `channel_receive` |
| `McpChannel{SignalR,Telegram,ServiceBus,Voice}/McpTools/ChannelReceiveTool.cs` | Thin MCP wrapper over `ChannelInbox` |
| `McpServer{Scheduling,Library}/McpTools/ChannelReceiveTool.cs` | Same, for the two dual-role servers |
| `Tests/Unit/Domain/Channels/ChannelInboxTests.cs` | `ChannelInbox` unit tests |
| `Tests/Integration/Channels/ChannelReceiveContractTests.cs` | Real Kestrel + real `McpClient` round trip |
| `Tests/Integration/Channels/StatelessProtocolGuardTests.cs` | Pins protocol 2026-07-28 / null SessionId |

**Modified:**

| File | Change |
|---|---|
| `Domain/DTOs/Channel/ChannelProtocol.cs` | Add `ReceiveTool`; re-prefix the `_meta` key |
| `Infrastructure/Clients/Channels/McpChannelConnection.cs` | Notification handlers → pump loop |
| `Infrastructure/Agents/ThreadSession.cs` | Filter `channel_receive`; drop sampling wiring |
| `Infrastructure/Agents/McpAgent.cs:283` | Always send conversation context |
| `Domain/Tools/SubAgents/SubAgentRunTool.cs` | Inherit parent conversation context |
| 6 × `*/Services/*NotificationEmitter.cs` | `SendNotificationAsync` → `inbox.Enqueue` |
| 6 × `*/Modules/ConfigModule.cs` | Delete `RunSessionHandler`; register `ChannelInbox` + tool |
| `McpServerLibrary/McpTools/McpFile{Search,Download}Tool.cs` | Key on conversation namespace |
| `McpServerWebSearch/McpTools/McpWeb{Browse,Snapshot,Action}Tool.cs` | Key on conversation namespace |
| `McpServerWebSearch/Modules/ConfigModule.cs` | Revert `Stateless`; delete cleanup middleware |

**Deleted:** `Infrastructure/Extensions/McpServerExtensions.cs`, `Infrastructure/Agents/Mcp/McpSamplingHandler.cs`, `Domain/Tools/ContentRecommendationTool.cs`, `McpServerLibrary/McpTools/McpContentRecommendationTool.cs`, `Tests/Integration/Agents/McpSamplingHandlerTests.cs`, `Tests/Integration/McpServerTests/McpWebSearchSessionCleanupTests.cs`.

---

### Task 1: Pin the stateless protocol and revert the diagnostic line

`McpServerWebSearch/Modules/ConfigModule.cs` currently carries an uncommitted `Stateless = false` added while diagnosing. Revert it, and add a guard test so nobody reintroduces it — that setting silently drops the whole stack to protocol 2025-11-25.

**Files:**
- Create: `Tests/Integration/Channels/StatelessProtocolGuardTests.cs`
- Modify: `McpServerWebSearch/Modules/ConfigModule.cs:50`

**Interfaces:**
- Consumes: nothing
- Produces: nothing (guard only)

- [ ] **Step 1: Write the failing test**

Create `Tests/Integration/Channels/StatelessProtocolGuardTests.cs`:

```csharp
using System.Net;
using System.ComponentModel;
using McpServerWebSearch.Modules;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Channels;

public class StatelessProtocolGuardTests
{
    [Fact]
    public async Task WebSearchServer_NegotiatesStatelessProtocol()
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new McpSettings
        {
            BraveSearch = new BraveSearchConfiguration { ApiKey = "test" },
            Camoufox = null,
            CapSolver = null
        });

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://localhost:{port}/mcp")
                }));

            client.NegotiatedProtocolVersion.ShouldBe("2026-07-28");
            client.SessionId.ShouldBeNull();
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~StatelessProtocolGuardTests" -m:1`
Expected: FAIL — `NegotiatedProtocolVersion` is `"2025-11-25"`, because `Stateless = false` is still set.

- [ ] **Step 3: Revert the diagnostic line**

In `McpServerWebSearch/Modules/ConfigModule.cs`, change:

```csharp
                .WithHttpTransport(options => options.Stateless = false)
```

back to:

```csharp
                .WithHttpTransport()
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~StatelessProtocolGuardTests" -m:1`
Expected: PASS

Note: `McpWebSearchSessionCleanupTests` fails again after this. That is expected — Task 13 deletes it.

- [ ] **Step 5: Commit**

```bash
git add Tests/Integration/Channels/StatelessProtocolGuardTests.cs McpServerWebSearch/Modules/ConfigModule.cs
git commit -m "test(mcp): pin the 2026-07-28 stateless protocol negotiation"
```

---

### Task 2: `ChannelInbox`

The buffer that closes the gap between the agent's polls. Pure `Domain` logic so it tests without a server.

**Files:**
- Create: `Domain/Channels/ChannelInboxItem.cs`, `Domain/Channels/ChannelInbox.cs`
- Test: `Tests/Unit/Domain/Channels/ChannelInboxTests.cs`

**Interfaces:**
- Consumes: `ChannelMessageNotification`, `ChannelCancelNotification` (`Domain/DTOs/Channel/`)
- Produces:
  - `ChannelInboxItemKind` — enum `{ Message, Cancel }`
  - `ChannelInboxItem.ForMessage(ChannelMessageNotification) -> ChannelInboxItem`
  - `ChannelInboxItem.ForCancel(ChannelCancelNotification) -> ChannelInboxItem`
  - `ChannelInbox(TimeProvider?, int capacity = 256, TimeSpan? subscriberIdleTimeout = null)`
  - `void Enqueue(ChannelInboxItem item)`
  - `Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync(string subscriberId, TimeSpan maxWait, CancellationToken ct)`
  - `bool HasSubscribers { get; }`

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/Domain/Channels/ChannelInboxTests.cs`:

```csharp
using Domain.Channels;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.Domain.Channels;

public class ChannelInboxTests
{
    private const string Subscriber = "channel-signalr";

    private static ChannelInboxItem Message(string conversationId) =>
        ChannelInboxItem.ForMessage(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = "user",
            Content = "hello"
        });

    private static ChannelInboxItem Cancel(string conversationId) =>
        ChannelInboxItem.ForCancel(new ChannelCancelNotification { ConversationId = conversationId });

    [Fact]
    public async Task ReceiveAsync_WithPendingItems_ReturnsImmediately()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task ReceiveAsync_PreservesMessageAndCancelOrdering()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Cancel("c1"));
        inbox.Enqueue(Message("c2"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        batch.Select(i => i.Kind).ShouldBe(
            [ChannelInboxItemKind.Message, ChannelInboxItemKind.Cancel, ChannelInboxItemKind.Message]);
    }

    [Fact]
    public async Task Enqueue_BeyondCapacity_DropsOldest()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider(), capacity: 2);
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));
        inbox.Enqueue(Message("c2"));
        inbox.Enqueue(Message("c3"));

        var batch = await inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        batch.Select(i => i.Message!.ConversationId).ShouldBe(["c2", "c3"]);
    }

    [Fact]
    public async Task ReceiveAsync_WhenEmpty_WakesOnEnqueue()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        var pending = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        // Give the waiter a moment to register before enqueueing.
        await Task.Delay(50);
        inbox.Enqueue(Message("c1"));

        var batch = await pending;

        batch.Count.ShouldBe(1);
        batch[0].Message!.ConversationId.ShouldBe("c1");
    }

    [Fact]
    public async Task ReceiveAsync_WhenNothingArrives_ReturnsEmptyAfterTimeout()
    {
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time);
        var pending = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        await Task.Delay(50);
        time.Advance(TimeSpan.FromSeconds(31));

        (await pending).ShouldBeEmpty();
    }

    [Fact]
    public async Task ReceiveAsync_SecondPollForSameSubscriber_DisplacesFirstWithEmptyBatch()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        var first = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);
        await Task.Delay(50);

        var second = inbox.ReceiveAsync(Subscriber, TimeSpan.FromSeconds(30), CancellationToken.None);

        (await first).ShouldBeEmpty();

        await Task.Delay(50);
        inbox.Enqueue(Message("c1"));
        (await second).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Enqueue_BroadcastsToEverySubscriber()
    {
        var inbox = new ChannelInbox(new FakeTimeProvider());
        await inbox.ReceiveAsync("a", TimeSpan.Zero, CancellationToken.None);
        await inbox.ReceiveAsync("b", TimeSpan.Zero, CancellationToken.None);

        inbox.Enqueue(Message("c1"));

        (await inbox.ReceiveAsync("a", TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
        (await inbox.ReceiveAsync("b", TimeSpan.Zero, CancellationToken.None)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task Subscriber_IsEvictedAfterIdleTimeout()
    {
        var time = new FakeTimeProvider();
        var inbox = new ChannelInbox(time, subscriberIdleTimeout: TimeSpan.FromMinutes(5));
        await inbox.ReceiveAsync(Subscriber, TimeSpan.Zero, CancellationToken.None);

        inbox.HasSubscribers.ShouldBeTrue();

        time.Advance(TimeSpan.FromMinutes(6));

        inbox.HasSubscribers.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelInboxTests" -m:1`
Expected: FAIL to compile — `Domain.Channels` namespace and `ChannelInbox` do not exist.

**Namespace note:** it must be `Domain.Channels` (plural). `Domain.Channel` shadows the *type* `System.Threading.Channels.Channel` for every file compiled under `namespace Domain.*` — the compiler searches enclosing namespaces before `using` directives — which breaks existing callers in `Domain/Memory/` and `Domain/Extensions/`.

- [ ] **Step 3: Implement**

Create `Domain/Channels/ChannelInboxItem.cs`:

```csharp
using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.Channels;

public enum ChannelInboxItemKind
{
    Message,
    Cancel
}

[PublicAPI]
public sealed record ChannelInboxItem
{
    public required ChannelInboxItemKind Kind { get; init; }
    public ChannelMessageNotification? Message { get; init; }
    public ChannelCancelNotification? Cancel { get; init; }

    public static ChannelInboxItem ForMessage(ChannelMessageNotification message) =>
        new() { Kind = ChannelInboxItemKind.Message, Message = message };

    public static ChannelInboxItem ForCancel(ChannelCancelNotification cancel) =>
        new() { Kind = ChannelInboxItemKind.Cancel, Cancel = cancel };
}
```

Create `Domain/Channels/ChannelInbox.cs`:

```csharp
using System.Collections.Concurrent;

namespace Domain.Channels;

public sealed class ChannelInbox(
    TimeProvider? timeProvider = null,
    int capacity = 256,
    TimeSpan? subscriberIdleTimeout = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly TimeSpan _idleTimeout = subscriberIdleTimeout ?? TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, Subscriber> _subscribers = new();

    public bool HasSubscribers
    {
        get
        {
            PruneIdle();
            return !_subscribers.IsEmpty;
        }
    }

    public void Enqueue(ChannelInboxItem item)
    {
        PruneIdle();
        foreach (var subscriber in _subscribers.Values)
        {
            subscriber.Enqueue(item, capacity);
        }
    }

    public Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync(
        string subscriberId,
        TimeSpan maxWait,
        CancellationToken ct)
    {
        var subscriber = _subscribers.GetOrAdd(subscriberId, _ => new Subscriber());
        subscriber.Touch(_timeProvider.GetUtcNow());
        return subscriber.ReceiveAsync(maxWait, _timeProvider, ct);
    }

    private void PruneIdle()
    {
        var cutoff = _timeProvider.GetUtcNow() - _idleTimeout;
        var stale = _subscribers.Where(kv => kv.Value.LastPolledAt < cutoff).Select(kv => kv.Key);
        foreach (var key in stale)
        {
            _subscribers.TryRemove(key, out _);
        }
    }

    private sealed class Subscriber
    {
        private readonly Lock _gate = new();
        private readonly Queue<ChannelInboxItem> _items = new();
        private TaskCompletionSource<bool>? _waiter;

        public DateTimeOffset LastPolledAt { get; private set; }

        public void Touch(DateTimeOffset now)
        {
            lock (_gate)
            {
                LastPolledAt = now;
            }
        }

        public void Enqueue(ChannelInboxItem item, int capacity)
        {
            TaskCompletionSource<bool>? toSignal;
            lock (_gate)
            {
                if (_items.Count >= capacity)
                {
                    _items.Dequeue();
                }

                _items.Enqueue(item);
                toSignal = _waiter;
                _waiter = null;
            }

            toSignal?.TrySetResult(true);
        }

        public async Task<IReadOnlyList<ChannelInboxItem>> ReceiveAsync(
            TimeSpan maxWait,
            TimeProvider timeProvider,
            CancellationToken ct)
        {
            TaskCompletionSource<bool> waiter;
            TaskCompletionSource<bool>? displaced;
            lock (_gate)
            {
                if (_items.Count > 0)
                {
                    return Drain();
                }

                displaced = _waiter;
                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiter = waiter;
            }

            // A second poll for the same subscriber retires the first with an empty batch,
            // otherwise two waiters would split the stream between them.
            displaced?.TrySetResult(false);

            if (maxWait <= TimeSpan.Zero)
            {
                lock (_gate)
                {
                    _waiter = null;
                    return Drain();
                }
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delay = Task.Delay(maxWait, timeProvider, timeoutCts.Token);
            var completed = await Task.WhenAny(waiter.Task, delay);
            await timeoutCts.CancelAsync();

            if (completed == waiter.Task && !waiter.Task.Result)
            {
                return [];
            }

            lock (_gate)
            {
                _waiter = null;
                return Drain();
            }
        }

        private IReadOnlyList<ChannelInboxItem> Drain()
        {
            var drained = _items.ToArray();
            _items.Clear();
            return drained;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelInboxTests" -m:1`
Expected: PASS, 8/8

- [ ] **Step 5: Commit**

```bash
git add Domain/Channels/ChannelInboxItem.cs Domain/Channels/ChannelInbox.cs Tests/Unit/Domain/Channels/ChannelInboxTests.cs
git commit -m "feat(channel): add ChannelInbox for stateless long-poll delivery"
```

---

### Task 3: Protocol constants and LLM tool hiding

`channel_receive` must never reach the model. `ThreadSession` strips channel-protocol tools by name; add the new one there.

**Files:**
- Modify: `Domain/DTOs/Channel/ChannelProtocol.cs`, `Infrastructure/Agents/ThreadSession.cs:82-88`
- Create: `Domain/DTOs/Channel/ChannelReceiveResult.cs`
- Test: `Tests/Unit/Infrastructure/Agents/ThreadSessionToolFilterTests.cs`

**Interfaces:**
- Consumes: `ChannelInboxItem` (Task 2)
- Produces:
  - `ChannelProtocol.ReceiveTool` = `"channel_receive"`
  - `ChannelProtocol.DefaultReceiveWaitMs` = `30000`
  - `ChannelReceiveResult { IReadOnlyList<ChannelInboxItem> Items }`

- [ ] **Step 1: Write the failing test**

Add to `Tests/Unit/Infrastructure/Agents/ThreadSessionToolFilterTests.cs`:

```csharp
    [Fact]
    public void FilterMcpTools_ChannelReceiveTool_IsAlwaysRemoved()
    {
        var tools = CreateTools(ChannelProtocol.ReceiveTool, "web_browse");

        var result = ThreadSessionBuilder.FilterMcpTools(tools, filesystemToolsActive: false);

        result.Select(t => t.Name).ShouldBe(["web_browse"]);
    }
```

If the existing file has no `CreateTools` helper, mirror whatever construction the neighbouring tests in that file already use for `tools`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ThreadSessionToolFilterTests" -m:1`
Expected: FAIL to compile — `ChannelProtocol.ReceiveTool` does not exist.

- [ ] **Step 3: Implement**

In `Domain/DTOs/Channel/ChannelProtocol.cs`, add beneath `RegisterAgentsTool`:

```csharp
    public const string ReceiveTool = "channel_receive";

    // How long a channel_receive call may be held open server-side before returning an empty
    // batch. Verified safe: a 45s hold completes on the SDK's default client timeout, and no
    // reverse proxy sits between the agent and a channel server (ChannelEndpoints are
    // container-to-container; Caddy only fronts the browser-facing /hubs/* route).
    public const int DefaultReceiveWaitMs = 30_000;
```

Create `Domain/DTOs/Channel/ChannelReceiveResult.cs`:

```csharp
using Domain.Channels;
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public sealed record ChannelReceiveResult
{
    public IReadOnlyList<ChannelInboxItem> Items { get; init; } = [];
}
```

In `Infrastructure/Agents/ThreadSession.cs`, add to `_channelProtocolToolNames`:

```csharp
        ChannelProtocol.RegisterAgentsTool,
        ChannelProtocol.ReceiveTool
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ThreadSessionToolFilterTests" -m:1`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Channel/ChannelProtocol.cs Domain/DTOs/Channel/ChannelReceiveResult.cs Infrastructure/Agents/ThreadSession.cs Tests/Unit/Infrastructure/Agents/ThreadSessionToolFilterTests.cs
git commit -m "feat(channel): add channel_receive protocol constants and hide the tool from the LLM"
```

---

### Task 4: SignalR server side — vertical slice

First server end-to-end. If the shape is wrong, find out here rather than six times over.

**Files:**
- Create: `McpChannelSignalR/McpTools/ChannelReceiveTool.cs`
- Modify: `McpChannelSignalR/Services/ChannelNotificationEmitter.cs`, `McpChannelSignalR/Modules/ConfigModule.cs:75-94`

**Interfaces:**
- Consumes: `ChannelInbox`, `ChannelInboxItem` (Task 2); `ChannelProtocol.ReceiveTool`, `ChannelReceiveResult` (Task 3)
- Produces: `ChannelNotificationEmitter(ChannelInbox inbox, ILogger<ChannelNotificationEmitter> logger)` with unchanged `EmitMessageNotificationAsync` / `EmitCancelNotificationAsync` signatures and `HasActiveSessions`

- [ ] **Step 1: Write the failing test**

Create `Tests/Integration/Channels/ChannelReceiveContractTests.cs`:

```csharp
using System.Net;
using Domain.Channels;
using Domain.DTOs.Channel;
using McpChannelSignalR.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Channels;

public class ChannelReceiveContractTests
{
    // One row per channel server. Tasks 6-10 each append exactly one row; the test body
    // is written once and never copied.
    public static TheoryData<string, string, Action<IMcpServerBuilder>> Servers => new()
    {
        { "signalr", "channel-signalr", b => b.WithTools<McpChannelSignalR.McpTools.ChannelReceiveTool>() }
    };

    [Theory]
    [MemberData(nameof(Servers))]
    public async Task EnqueuedMessage_IsDeliveredToAPollingClient(
        string channelId, string subscriberId, Action<IMcpServerBuilder> registerTool)
    {
        _ = channelId;
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(inbox);
        registerTool(builder.Services.AddMcpServer().WithHttpTransport());

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://localhost:{port}/mcp")
                }));

            // Register the subscriber, then enqueue while a poll is in flight.
            await Poll(client, subscriberId, maxWaitMs: 0);

            var pending = Poll(client, subscriberId, maxWaitMs: 10_000);
            await Task.Delay(200);
            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-1",
                Sender = "user",
                Content = "hello"
            }));

            var result = await pending;

            result.Items.Count.ShouldBe(1);
            result.Items[0].Kind.ShouldBe(ChannelInboxItemKind.Message);
            result.Items[0].Message!.Content.ShouldBe("hello");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static async Task<ChannelReceiveResult> Poll(McpClient client, string subscriberId, int maxWaitMs)
    {
        var call = await client.CallToolAsync(
            ChannelProtocol.ReceiveTool,
            new Dictionary<string, object?>
            {
                ["subscriberId"] = subscriberId,
                ["maxWaitMs"] = maxWaitMs
            });

        var text = call.Content.OfType<TextContentBlock>().First().Text;
        return ChannelProtocol.SerializerOptions is var options
            ? System.Text.Json.JsonSerializer.Deserialize<ChannelReceiveResult>(text, options)!
            : throw new InvalidOperationException();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelReceiveContractTests" -m:1`
Expected: FAIL to compile — `McpChannelSignalR.McpTools.ChannelReceiveTool` does not exist.

- [ ] **Step 3: Implement**

Create `McpChannelSignalR/McpTools/ChannelReceiveTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using Domain.Channels;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class ChannelReceiveTool
{
    [McpServerTool(Name = ChannelProtocol.ReceiveTool)]
    [Description("Internal channel transport. Long-polls for inbound channel items.")]
    public static async Task<string> McpRun(
        [Description("Stable subscriber id, e.g. channel-signalr")] string subscriberId,
        [Description("How long to hold the request open, in milliseconds")] int maxWaitMs,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var inbox = services.GetRequiredService<ChannelInbox>();
        var items = await inbox.ReceiveAsync(
            subscriberId, TimeSpan.FromMilliseconds(maxWaitMs), cancellationToken);

        return JsonSerializer.Serialize(
            new ChannelReceiveResult { Items = items }, ChannelProtocol.SerializerOptions);
    }
}
```

Replace the body of `McpChannelSignalR/Services/ChannelNotificationEmitter.cs`:

```csharp
using Domain.Channels;
using Domain.DTOs.Channel;

namespace McpChannelSignalR.Services;

public sealed class ChannelNotificationEmitter(ChannelInbox inbox)
{
    public Task EmitMessageNotificationAsync(
        string conversationId,
        string sender,
        string content,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Timestamp = DateTimeOffset.UtcNow
        }));

        return Task.CompletedTask;
    }

    public Task EmitCancelNotificationAsync(
        string conversationId,
        string agentId,
        CancellationToken cancellationToken = default)
    {
        inbox.Enqueue(ChannelInboxItem.ForCancel(new ChannelCancelNotification
        {
            ConversationId = conversationId,
            AgentId = agentId,
            Timestamp = DateTimeOffset.UtcNow
        }));

        return Task.CompletedTask;
    }

    public bool HasActiveSessions => inbox.HasSubscribers;
}
```

In `McpChannelSignalR/Modules/ConfigModule.cs`, delete the whole `WithHttpTransport(options => { … RunSessionHandler … })` lambda including the `#pragma warning disable/restore MCPEXP002` lines, and register the inbox plus the tool:

```csharp
        services.AddSingleton<ChannelInbox>();

        services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<CreateConversationTool>()
            .WithTools<RegisterAgentsTool>()
            .WithTools<ChannelReceiveTool>()
```

Add `using Domain.Channels;` and `using McpChannelSignalR.McpTools;` as needed. If `ConfigureMcp` took a `notificationEmitter` parameter purely to wire `RunSessionHandler`, drop that parameter and let DI construct the emitter from the registered `ChannelInbox`; update `Program.cs` accordingly.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelReceiveContractTests" -m:1`
Expected: PASS

Then confirm nothing else regressed: `dotnet build agent.sln`

- [ ] **Step 5: Commit**

```bash
git add McpChannelSignalR Tests/Integration/Channels/ChannelReceiveContractTests.cs
git commit -m "feat(signalr): serve inbound channel items over channel_receive"
```

---

### Task 5: Agent pump — vertical slice complete

Replace the two notification handlers with a pump loop. `HandleChannelMessageNotification` and `HandleChannelCancelNotification` stay exactly as they are; only their feed changes.

**Files:**
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs:27-66`, `:268-304`
- Test: `Tests/Integration/Channels/ChannelReceiveContractTests.cs`

**Interfaces:**
- Consumes: `ChannelProtocol.ReceiveTool`, `ChannelProtocol.DefaultReceiveWaitMs`, `ChannelReceiveResult` (Task 3); the SignalR `channel_receive` tool (Task 4)
- Produces: `McpChannelConnection.Messages` now fed by the pump; public surface unchanged

- [ ] **Step 1: Write the failing test**

Append to `Tests/Integration/Channels/ChannelReceiveContractTests.cs`:

```csharp
    [Fact]
    public async Task McpChannelConnection_SurfacesEnqueuedMessagesOnItsStream()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(inbox);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpChannelSignalR.McpTools.ChannelReceiveTool>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        await using var connection = new McpChannelConnection("signalr");
        try
        {
            await connection.ConnectAsync($"http://localhost:{port}/mcp", CancellationToken.None);

            await Task.Delay(300);
            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-1",
                Sender = "user",
                Content = "hello from the inbox",
                AgentId = "nabu"
            }));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = await connection.Messages.FirstAsync(cts.Token);

            received.ConversationId.ShouldBe("conv-1");
            received.Content.ShouldBe("hello from the inbox");
            received.ChannelId.ShouldBe("signalr");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
```

```csharp
    [Fact]
    public async Task McpChannelConnection_AfterReconnect_DrainsWhatBufferedDuringTheOutage()
    {
        var port = TestPort.GetAvailable();
        var inbox = new ChannelInbox();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(inbox);
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpChannelSignalR.McpTools.ChannelReceiveTool>();

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();

        var endpoint = $"http://localhost:{port}/mcp";
        await using var connection = new McpChannelConnection("signalr");
        try
        {
            await connection.ConnectAsync(endpoint, CancellationToken.None);
            await Task.Delay(300);

            // The subscriber id is stable across reconnects, so the queue survives.
            await connection.ReconnectAsync(endpoint, CancellationToken.None);

            inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
            {
                ConversationId = "conv-2",
                Sender = "user",
                Content = "buffered"
            }));

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            ChannelMessage? received = null;
            await foreach (var m in connection.Messages.WithCancellation(cts.Token))
            {
                received = m;
                break;
            }

            received!.Content.ShouldBe("buffered");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task McpChannelConnection_WhenServerIsDown_BacksOffInsteadOfSpinning()
    {
        // No server listening on this port at all.
        var port = TestPort.GetAvailable();
        await using var connection = new McpChannelConnection("signalr");

        // ConnectAsync itself retries via Polly and will throw; the pump must not be left
        // spinning hot if the server dies *after* a successful connect. Assert the pump exits
        // cleanly on dispose rather than hanging.
        await Should.ThrowAsync<Exception>(
            () => connection.ConnectAsync($"http://localhost:{port}/mcp", CancellationToken.None));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connection.DisposeAsync().AsTask().WaitAsync(cts.Token);
    }
```

Add `using System.Linq;`, `using Domain.DTOs;` and `using Infrastructure.Clients.Channels;`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelConnection_SurfacesEnqueuedMessages" -m:1`
Expected: FAIL — times out; the connection still registers notification handlers that never fire.

- [ ] **Step 3: Implement**

In `McpChannelConnection`, add fields:

```csharp
    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
```

Replace the two `RegisterNotificationHandler` blocks at the end of `ConnectAsync` with:

```csharp
        _pumpCts = new CancellationTokenSource();
        _pumpTask = PumpAsync(_pumpCts.Token);
```

Add:

```csharp
    private async Task PumpAsync(CancellationToken ct)
    {
        var subscriberId = $"{ChannelProtocol.ChannelClientNamePrefix}{ChannelId}";
        var backoff = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var call = await _client!.CallToolAsync(
                    ChannelProtocol.ReceiveTool,
                    new Dictionary<string, object?>
                    {
                        ["subscriberId"] = subscriberId,
                        ["maxWaitMs"] = ChannelProtocol.DefaultReceiveWaitMs
                    },
                    cancellationToken: ct);

                var text = call.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                var batch = JsonSerializer.Deserialize<ChannelReceiveResult>(
                    text, ChannelProtocol.SerializerOptions);

                foreach (var item in batch?.Items ?? [])
                {
                    Dispatch(item);
                }

                backoff = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "channel_receive failed on {ChannelId}; retrying", ChannelId);
                try
                {
                    await Task.Delay(backoff, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                backoff = TimeSpan.FromSeconds(Math.Min(backoff.TotalSeconds * 2, 30));
            }
        }
    }

    private void Dispatch(ChannelInboxItem item)
    {
        var payload = item.Kind == ChannelInboxItemKind.Message
            ? JsonSerializer.SerializeToElement(item.Message, ChannelProtocol.SerializerOptions)
            : JsonSerializer.SerializeToElement(item.Cancel, ChannelProtocol.SerializerOptions);

        if (item.Kind == ChannelInboxItemKind.Message)
        {
            HandleChannelMessageNotification(payload);
        }
        else
        {
            HandleChannelCancelNotification(payload);
        }
    }
```

Add `using Domain.Channels;`. Stop the pump in `DisposeAsync` and `ReconnectAsync`, before disposing the client:

```csharp
    private async Task StopPumpAsync()
    {
        if (_pumpCts is null)
        {
            return;
        }

        await _pumpCts.CancelAsync();
        if (_pumpTask is not null)
        {
            try { await _pumpTask; } catch (OperationCanceledException) { }
        }

        _pumpCts.Dispose();
        _pumpCts = null;
        _pumpTask = null;
    }
```

Call `await StopPumpAsync();` as the first statement of `ReconnectAsync` and of `DisposeAsync`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelReceiveContractTests" -m:1`
Expected: PASS, both tests

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Channels/McpChannelConnection.cs Tests/Integration/Channels/ChannelReceiveContractTests.cs
git commit -m "feat(agent): pump inbound channel items via channel_receive"
```

---

### Tasks 6–10: Replicate to the remaining five servers

One task per server, same shape as Task 4. Each is independently committable and independently reviewable.

For **each** of the following, in this order:

| Task | Project | Emitter file | Emitter class |
|---|---|---|---|
| 6 | `McpChannelTelegram` | `Services/ChannelNotificationEmitter.cs` | `ChannelNotificationEmitter` |
| 7 | `McpChannelServiceBus` | `Services/ChannelNotificationEmitter.cs` | `ChannelNotificationEmitter` |
| 8 | `McpChannelVoice` | `Services/ChannelNotificationEmitter.cs` | `ChannelNotificationEmitter` |
| 9 | `McpServerScheduling` | `Services/ScheduleNotificationEmitter.cs` | `ScheduleNotificationEmitter` |
| 10 | `McpServerLibrary` | `Services/DownloadNotificationEmitter.cs` | `DownloadNotificationEmitter` |

- [ ] **Step 1: Add this server's row to the contract theory**

In `Tests/Integration/Channels/ChannelReceiveContractTests.cs`, append **one row** to the `Servers` `TheoryData` — the test body is already written and is not copied:

```csharp
        { "telegram",    "channel-telegram",    b => b.WithTools<McpChannelTelegram.McpTools.ChannelReceiveTool>() },
        { "servicebus",  "channel-servicebus",  b => b.WithTools<McpChannelServiceBus.McpTools.ChannelReceiveTool>() },
        { "voice",       "channel-voice",       b => b.WithTools<McpChannelVoice.McpTools.ChannelReceiveTool>() },
        { "scheduling",  "channel-scheduling",  b => b.WithTools<McpServerScheduling.McpTools.ChannelReceiveTool>() },
        { "library",     "channel-library",     b => b.WithTools<McpServerLibrary.McpTools.ChannelReceiveTool>() },
```

Add only the row for the server this task covers; by Task 10 all six rows are present.

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelReceiveContractTests" -m:1`
Expected: FAIL to compile — that project has no `ChannelReceiveTool`.

- [ ] **Step 3: Implement**

Create `<Project>/McpTools/ChannelReceiveTool.cs` with the body from Task 4 Step 3, changing only the `namespace` to `<Project>.McpTools`.

Rewrite the emitter to take `ChannelInbox` and call `inbox.Enqueue(...)` instead of iterating `_activeSessions` and calling `SendNotificationAsync`. Keep every public method signature unchanged so callers do not move.

- **Voice (Task 8)** has extra logic around emission — preserve it, replacing only the `SendNotificationAsync` fan-out with `inbox.Enqueue`.
- **Scheduling (Task 9) and Library (Task 10)** currently filter sessions with `ChannelProtocol.IsChannelClientName` because they are dual-role. Delete that filter: only channel connections call `channel_receive`, so the distinction is now structural rather than something the emitter must enforce. `HasActiveSessions` becomes `inbox.HasSubscribers`. These two emitters also have interfaces (`IScheduleNotificationEmitter`, `IDownloadNotificationEmitter`) — leave the interfaces unchanged.

In `<Project>/Modules/ConfigModule.cs`, delete the `WithHttpTransport(options => { … RunSessionHandler … })` lambda and both `#pragma warning disable/restore MCPEXP002` lines, then:

```csharp
        services.AddSingleton<ChannelInbox>();
```

and add `.WithTools<ChannelReceiveTool>()` to the builder chain. Drop any now-unused `notificationEmitter` parameter from `ConfigureMcp` and update `Program.cs`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelReceiveContractTests" -m:1`
Expected: PASS

Also run that server's existing unit tests, e.g. for Task 10:
`dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~DownloadNotificationEmitterTests" -m:1`

Those tests mock `McpServer` and will no longer compile. Rewrite them against a real `ChannelInbox`: enqueue via the emitter, then assert `ReceiveAsync` returns the item. This is the point of the migration — a mocked `McpServer` accepts calls the real transport refuses, which is why the original regression went undetected.

- [ ] **Step 5: Commit**

```bash
git add <Project> Tests/Integration/Channels/ChannelReceiveContractTests.cs Tests/Unit/<Project>
git commit -m "feat(<channel>): serve inbound channel items over channel_receive"
```

- [ ] **After Task 10:** verify no `RunSessionHandler` remains:

```bash
grep -rn "RunSessionHandler\|MCPEXP002" --include=*.cs . | grep -v "/bin/\|/obj/"
```
Expected: no output.

---

### Task 11: Conversation context on every request

**Files:**
- Modify: `Domain/DTOs/Channel/ChannelProtocol.cs`, `Infrastructure/Agents/McpAgent.cs:283`, `Domain/Tools/SubAgents/SubAgentRunTool.cs:64`, `Domain/Tools/SubAgents/SubAgentFeatureConfig.cs`
- Test: `Tests/Unit/Infrastructure/Agents/ConversationContextMetaTests.cs`

**Interfaces:**
- Consumes: `ConversationContext(string AgentId, string ConversationId, string UserId, ReplyTarget Origin)`
- Produces: `ChannelProtocol.ConversationContextMetaKey` = `"com.herfluffness/conversationContext"`

- [ ] **Step 1: Write the failing test**

Add to `Tests/Unit/Infrastructure/Agents/ConversationContextMetaTests.cs`:

```csharp
    [Fact]
    public void MetaKey_UsesAVendorPrefix()
    {
        // The 2026-07-28 spec reserves any _meta prefix whose second label is "mcp" or
        // "modelcontextprotocol", and recommends reverse-DNS for everything else. A bare key
        // sits in the same namespace as progressToken/traceparent and could be claimed later.
        ChannelProtocol.ConversationContextMetaKey.ShouldBe("com.herfluffness/conversationContext");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConversationContextMetaTests" -m:1`
Expected: FAIL — actual is `"conversationContext"`.

- [ ] **Step 3: Implement**

In `Domain/DTOs/Channel/ChannelProtocol.cs`:

```csharp
    public const string ConversationContextMetaKey = "com.herfluffness/conversationContext";
```

In `Infrastructure/Agents/McpAgent.cs`, replace the conditional at line 283:

```csharp
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ConversationContextMeta.OptionsKey] =
                    conversationContext ?? throw new InvalidOperationException(
                        "ConversationContext is required for every agent run that may call MCP tools.")
            }
```

If that throw proves too strict for an existing call path, fall back to keeping the run working but *logging an error* — never silently omitting the key, which is what this task exists to prevent.

In `Domain/Tools/SubAgents/SubAgentFeatureConfig.cs`, add a `ConversationContext? ConversationContext` member alongside the existing `UserId`. In `SubAgentRunTool.cs`, after line 65:

```csharp
            userMessage.SetConversationContext(featureConfig.ConversationContext);
```

Wire the parent's context into the feature config wherever `UserId` is already supplied (`Agent/Modules/SubAgentModule.cs`). A subagent inherits the parent context **verbatim** — substituting its own agent id would namespace a `file_search` in the parent differently from a `file_download` in the subagent and silently break that flow.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" -m:1`
Expected: PASS. `QualifiedMcpToolMetaTests` and `ConversationContextStampingTests` exercise this key — update their literals if they hardcode the old value.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Channel/ChannelProtocol.cs Infrastructure/Agents/McpAgent.cs Domain/Tools/SubAgents Agent/Modules/SubAgentModule.cs Tests/Unit
git commit -m "feat(mcp): send a vendor-prefixed conversation context on every tool call"
```

---

### Task 12: Library — key search results on the conversation

Today `StateKey` is the MCP session id, which is one per `ThreadSession`, which is one per conversation. In stateless it falls back to `ClientInfo.Name` — the **agent name** — collapsing every user of an agent into one bucket.

**Files:**
- Create: `Domain/Channels/ConversationScope.cs` (shared — Task 13 reuses it, do **not** duplicate per server)
- Modify: `McpServerLibrary/McpTools/McpFileSearchTool.cs:23`, `McpServerLibrary/McpTools/McpFileDownloadTool.cs:36,56-70`
- Test: `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`

**Interfaces:**
- Consumes: `ChannelProtocol.ConversationContextMetaKey` (Task 11)
- Produces: `ConversationScope.TryResolve(JsonObject? meta, out string scope)` — `"{AgentId}:{ConversationId}"`

- [ ] **Step 1: Write the failing test**

Add to `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`:

```csharp
    [Fact]
    public async Task FileSearch_ResultsAreNotVisibleToAnotherConversation()
    {
        // Regression: with the MCP session id gone, StateKey falls back to ClientInfo.Name —
        // the agent name — so every conversation would share one search-result namespace.
        var scopeA = ConversationScope.Build("nabu", "conv-a");
        var scopeB = ConversationScope.Build("nabu", "conv-b");

        scopeA.ShouldNotBe(scopeB);
    }
```

And a test pinning the isolation property itself, against the real `SearchResultsManager`:

```csharp
    [Fact]
    public void SearchResults_CachedInOneConversation_AreInvisibleToAnother()
    {
        var manager = new SearchResultsManager(
            new MemoryCache(new MemoryCacheOptions()));

        var result = new SearchResult
        {
            Title = "Some Release",
            Id = 4242,
            Link = "magnet:?xt=urn:btih:deadbeef"
        };

        manager.Add(ConversationScope.Build("nabu", "conv-a"), [result]);

        manager.Get(ConversationScope.Build("nabu", "conv-a"), 4242).ShouldNotBeNull();
        manager.Get(ConversationScope.Build("nabu", "conv-b"), 4242).ShouldBeNull();
    }
```

Add `using Infrastructure.StateManagers;`, `using Microsoft.Extensions.Caching.Memory;`, `using Domain.Channels;`, `using Domain.DTOs;`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpLibraryServerTests" -m:1`
Expected: FAIL to compile — `ConversationScope` does not exist.

- [ ] **Step 3: Implement**

Create `Domain/Channels/ConversationScope.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;

namespace Domain.Channels;

public static class ConversationScope
{
    public static string Build(string agentId, string conversationId) => $"{agentId}:{conversationId}";

    public static ConversationContext? Parse(JsonObject? meta)
    {
        var node = meta?[ChannelProtocol.ConversationContextMetaKey];
        return node?.Deserialize<ConversationContext>(ChannelProtocol.SerializerOptions);
    }

    public static bool TryResolve(JsonObject? meta, out string scope)
    {
        var context = Parse(meta);
        if (context is null)
        {
            scope = string.Empty;
            return false;
        }

        scope = Build(context.AgentId, context.ConversationId);
        return true;
    }
}
```

In **both** `McpFileSearchTool` and `McpFileDownloadTool`, replace `var sessionId = context.Server.StateKey;` with:

```csharp
        if (!ConversationScope.TryResolve(context.Params?.Meta, out var sessionId))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot scope search results.",
                retryable: false));
        }
```

**Both must change together.** A search namespaced one way and a download the other breaks the flow outright. `McpFileSearchTool` does not currently read `_meta` at all.

Delete `ParseConversationContext` from `McpFileDownloadTool` and point its remaining caller at `ConversationScope.Parse`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpLibraryServerTests" -m:1`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add McpServerLibrary/McpTools Tests/Integration/McpServerTests/McpLibraryServerTests.cs
git commit -m "fix(library): scope search results to the conversation, not the MCP connection"
```

---

### Task 13: WebSearch — key browser sessions on the conversation

`RequireSessionId()` throws unconditionally in stateless mode, so all three browse tools fail on every call.

**Files:**
- Modify: `McpServerWebSearch/McpTools/McpWebBrowseTool.cs:37`, `McpWebSnapshotTool.cs:23`, `McpWebActionTool.cs:33`, `McpServerWebSearch/Modules/ConfigModule.cs:106-137`, `McpServerWebSearch/Program.cs:11`
- Delete: `Infrastructure/Extensions/McpServerExtensions.cs`, `Tests/Integration/McpServerTests/McpWebSearchSessionCleanupTests.cs`
- Test: `Tests/Integration/McpServerTests/McpWebSearchSessionScopeTests.cs`

**Interfaces:**
- Consumes: `ChannelProtocol.ConversationContextMetaKey` (Task 11)
- Produces: nothing downstream

- [ ] **Step 1: Write the failing test**

Create `Tests/Integration/McpServerTests/McpWebSearchSessionScopeTests.cs`:

```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpServerWebSearch.Modules;
using McpServerWebSearch.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.McpServerTests;

public class McpWebSearchSessionScopeTests
{
    [Fact]
    public async Task WebBrowse_KeysTheBrowserSessionOnTheConversation()
    {
        var browser = new RecordingBrowser();
        var port = TestPort.GetAvailable();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.ConfigureMcp(new McpSettings
        {
            BraveSearch = new BraveSearchConfiguration { ApiKey = "test" },
            Camoufox = null,
            CapSolver = null
        });
        builder.Services.RemoveAll<IWebBrowser>();
        builder.Services.AddSingleton<IWebBrowser>(browser);

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        try
        {
            await using var client = await McpClient.CreateAsync(
                new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = new Uri($"http://localhost:{port}/mcp")
                }));

            await Browse(client, "nabu", "conv-a");
            await Browse(client, "nabu", "conv-a");
            await Browse(client, "nabu", "conv-b");

            browser.SessionIds.Count.ShouldBe(3);
            browser.SessionIds.Distinct().Count().ShouldBe(2);
            browser.SessionIds.ShouldContain("nabu:conv-a");
            browser.SessionIds.ShouldContain("nabu:conv-b");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }

    private static Task Browse(McpClient client, string agentId, string conversationId)
    {
        var context = new ConversationContext(agentId, conversationId, "fran", ReplyTarget.None);
        return client.CallToolAsync(
            "web_browse",
            new Dictionary<string, object?> { ["url"] = "https://example.com" },
            new Dictionary<string, object?>
            {
                [ChannelProtocol.ConversationContextMetaKey] =
                    JsonSerializer.SerializeToElement(context, ChannelProtocol.SerializerOptions)
            });
    }

    private sealed class RecordingBrowser : IWebBrowser
    {
        public ConcurrentBag<string> SessionIds { get; } = [];

        public Task<BrowseResult> NavigateAsync(BrowseRequest request, CancellationToken ct = default)
        {
            SessionIds.Add(request.SessionId);
            return Task.FromResult(new BrowseResult(
                request.SessionId, request.Url, BrowseStatus.Success,
                null, null, 0, false, null, null, null, null));
        }

        public Task<BrowseResult> GetCurrentPageAsync(string sessionId, CancellationToken ct = default)
            => Task.FromResult(new BrowseResult(
                sessionId, "", BrowseStatus.SessionNotFound, null, null, 0, false, null, null, null, null));

        public Task<SnapshotResult> SnapshotAsync(SnapshotRequest request, CancellationToken ct = default)
            => Task.FromResult(new SnapshotResult(request.SessionId, null, null, 0, null));

        public Task<WebActionResult> ActionAsync(WebActionRequest request, CancellationToken ct = default)
            => Task.FromResult(new WebActionResult(
                request.SessionId, WebActionStatus.Success, null, false, null, null, null));

        public Task CloseSessionAsync(string sessionId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
```

The `_meta` overload on `CallToolAsync` may have a different shape in the SDK — check the signature and adapt; the requirement is that the serialized `ConversationContext` lands under `ChannelProtocol.ConversationContextMetaKey` in the request's `_meta`. Confirm `ReplyTarget.None` exists; if not, use whatever the `ReplyTarget` enum's default member is.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpWebSearchSessionScopeTests" -m:1`
Expected: FAIL — `RequireSessionId()` throws `InvalidOperationException: MCP SessionId is not available.`

- [ ] **Step 3: Implement**

Reuse `Domain.Channels.ConversationScope` from Task 12 — do **not** copy it into this project. In each of the three browse tools, replace `var sessionId = context.Server.RequireSessionId();` with:

```csharp
        if (!ConversationScope.TryResolve(context.Params?.Meta, out var sessionId))
        {
            return ToolResponse.Create(ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Conversation context is missing from request _meta; cannot scope the browser session.",
                retryable: false));
        }
```

adding `using Domain.Channels;`.

Delete `Infrastructure/Extensions/McpServerExtensions.cs` entirely — `StateKey` moved to `_meta` in Task 12, `RequireSessionId` is now unused, and `SessionIdHeader` loses its only consumer with the middleware below.

In `McpServerWebSearch/Modules/ConfigModule.cs`, delete the whole `extension(IApplicationBuilder app)` block containing `UseBrowserSessionCleanupOnMcpDelete`. Remove the call at `McpServerWebSearch/Program.cs:11`.

Delete `Tests/Integration/McpServerTests/McpWebSearchSessionCleanupTests.cs`. It asserts the `DELETE /mcp` hook, a mechanism the protocol no longer has. Cleanup is unaffected: `BrowserSessionManager` already prunes on a timer (`_idleTimeout`, default 30 min, `PruneIdleAsync`, `LastAccessedAt`); the DELETE hook was a promptness optimization on top of it, never the only path.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpWebSearch" -m:1`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add McpServerWebSearch Infrastructure/Extensions Tests/Integration/McpServerTests
git commit -m "fix(websearch): scope browser sessions to the conversation and drop the DELETE hook"
```

---

### Task 14: Delete sampling

Sampling is deprecated by the 2026-07-28 spec (`MCP9005`, 62 warnings) and does not work in stateless mode. Its only consumer costs a full extra LLM turn to ask the agent's own model a question the agent could answer directly.

**Files:**
- Delete: `Infrastructure/Agents/Mcp/McpSamplingHandler.cs`, `Domain/Tools/ContentRecommendationTool.cs`, `McpServerLibrary/McpTools/McpContentRecommendationTool.cs`, `Tests/Integration/Agents/McpSamplingHandlerTests.cs`
- Modify: `Infrastructure/Agents/ThreadSession.cs:94-95`, `Infrastructure/Agents/Mappers/ChatResponseUpdateExtensions.cs`, `McpServerLibrary/Modules/ConfigModule.cs:85`

**Interfaces:**
- Consumes: nothing
- Produces: nothing

- [ ] **Step 1: Establish the baseline**

Run: `dotnet build agent.sln --no-incremental -v n 2>&1 | grep -c "warning MCP9005"`
Expected: `62`

- [ ] **Step 2: Delete**

```bash
git rm Infrastructure/Agents/Mcp/McpSamplingHandler.cs \
       Domain/Tools/ContentRecommendationTool.cs \
       McpServerLibrary/McpTools/McpContentRecommendationTool.cs \
       Tests/Integration/Agents/McpSamplingHandlerTests.cs
```

In `Infrastructure/Agents/ThreadSession.cs`, delete these two lines from `BuildAsync`:

```csharp
        var samplingHandler = new McpSamplingHandler(agent, () => _tools);
        var handlers = new McpClientHandlers { SamplingHandler = samplingHandler.HandleAsync };
```

and pass `new McpClientHandlers()` to `McpClientManager.CreateAsync`. If `_tools` and the `agent` constructor parameter become unused afterwards, remove them too.

Delete `ToCreateMessageResult` from `Infrastructure/Agents/Mappers/ChatResponseUpdateExtensions.cs` (`McpSamplingHandler` was its only caller). If the file has no members left, delete the file.

Remove `.WithTools<McpContentRecommendationTool>()` from `McpServerLibrary/Modules/ConfigModule.cs:85`.

- [ ] **Step 3: Verify the warnings are gone**

Run: `dotnet build agent.sln --no-incremental -v n 2>&1 | grep -c "warning MCP9005"`
Expected: `0`

- [ ] **Step 4: Run the suite**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" -m:1`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ThreadSession.cs \
        Infrastructure/Agents/Mcp/McpSamplingHandler.cs \
        Infrastructure/Agents/Mappers/ChatResponseUpdateExtensions.cs \
        Domain/Tools/ContentRecommendationTool.cs \
        McpServerLibrary/McpTools/McpContentRecommendationTool.cs \
        McpServerLibrary/Modules/ConfigModule.cs \
        Tests/Integration/Agents/McpSamplingHandlerTests.cs
git commit -m "refactor: remove sampling and its only consumer"
```

Deletions staged via `git rm` are already in the index; the explicit `git add` above covers the
edited files. Never `git add -A` or `git add -u` without paths — the user commits into this tree
concurrently.

---

### Task 15: Full verification

**Files:** none modified.

- [ ] **Step 1: Confirm nothing stateful survives**

```bash
grep -rn "RunSessionHandler\|MCPEXP002\|RequireSessionId\|StateKey\|Stateless" --include=*.cs . | grep -v "/bin/\|/obj/" | grep -v "StatelessProtocolGuardTests\|StatelessOnes"
```
Expected: no output.

- [ ] **Step 2: Clean build**

Run: `dotnet build agent.sln --no-incremental`
Expected: 0 errors, 0 warnings.

- [ ] **Step 3: Full non-E2E suite**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" -m:1`
Expected: 0 failures. Baseline before this work was 2595 passing; expect a slightly different total (tests added and deleted).

- [ ] **Step 4: E2E against the compose stack**

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build \
  agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-homeassistant mcp-library \
  mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus mcp-channel-voice mcp-scheduling mcp-printer mcp-timers \
  lemonade tse-extractor qbittorrent jackett redis caddy camoufox homeassistant music-assistant
dotnet test Tests/Tests.csproj --filter "Category=E2E" -m:1
```

Add `-f DockerCompose/docker-compose.override.no-dri.yml` last if the host has no `/dev/dri` render node.

- [ ] **Step 5: Manual smoke — the thing the unit tests cannot prove**

Send a message from WebChat and confirm the agent replies. This exercises the full inbound path (`channel_receive` → pump → `ChatMonitor` → reply fan-out) against a real stack. The original regression was invisible precisely because every emitter caught its own failure and logged at Warning, so also check for absence of `channel_receive failed` in `docker logs jackbot-agent-1`.

- [ ] **Step 6: Commit any fixes**

```bash
git add <paths>
git commit -m "fix: address issues found in end-to-end verification"
```
