# Library Download Alerts via Channel Protocol — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the MCP resource-subscription system with a dual-role McpServerLibrary that pushes download-finished alerts as `channel/message` notifications routed to the originating conversation, plus a `filesystem://downloads` VFS, deleting the entire agent-side subscription machinery.

**Architecture:** The agent stamps a `ConversationContext` (agentId, conversationId, userId, origin ReplyTarget) onto each user `ChatMessage`; `McpAgent` lifts it into `ChatOptions.AdditionalProperties`; `QualifiedMcpTool` reads it via `FunctionInvokingChatClient.CurrentContext` and attaches it as MCP `_meta` on every `tools/call`. The library's `download_file` snapshots it into a Redis-backed `IDownloadRoutingStore`. A `DownloadCompletionWatcher` polls qBittorrent and emits schedule-style `ChannelMessageNotification`s with `replyTo` = the stored origin target; `ChatMonitor` already routes concrete-conversationId ReplyTargets without minting. Downloads also become a VFS (`/downloads/<id>/status.json`, `fs_delete` = cleanup), which requires a `filesystem` discriminator argument in the VFS wire protocol because the library now serves two mounts from one server.

**Tech Stack:** .NET 10, ModelContextProtocol SDK 1.4.0 (`McpClientTool.WithMeta`, `RequestParams.Meta` — both verified present), Microsoft.Extensions.AI 10.6.0 (`FunctionInvokingChatClient.CurrentContext` — verified, settable), Microsoft.Agents.AI 1.9.0, StackExchange.Redis, xUnit + Shouldly + Moq.

**Spec:** `docs/superpowers/specs/2026-06-12-library-channel-refactor-design.md`

---

## Repo conventions (read before any task)

- **NO trailing newline in any `.cs` file** (including tests). The pre-commit hook runs `dotnet format` and re-stages whole files.
- File-scoped namespaces, primary constructors, records for DTOs, LINQ over loops, no XML doc comments. See `.claude/rules/dotnet-style.md`.
- MCP tools: no try/catch in tool methods — the global `AddCallToolFilter` handles errors. See `.claude/rules/mcp-tools.md`.
- Domain must not reference Infrastructure; Infrastructure must not reference Agent.
- ~148 `Category!=E2E` test failures with `DockerUnavailableException` are a pre-existing baseline in this environment, NOT regressions. Run targeted filters; treat only non-Docker failures as real.
- Build: `dotnet build` at repo root. Test: `dotnet test Tests --filter "<filter>"`.
- Commit after each task (the auto-commit-after-triplets rule). Stay on branch `subscription-refactor`.

---

### Task 1: `ConversationContext` DTO + ChatMessage stamping extensions

**Files:**
- Create: `Domain/DTOs/Channel/ConversationContext.cs`
- Modify: `Domain/Extensions/ChatMessageExtensions.cs`
- Test: `Tests/Unit/Domain/Channel/ConversationContextStampingTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain.Channel;

public class ConversationContextStampingTests
{
    private static readonly ConversationContext _context = new(
        "jonas", "conv-7", "fran", new ReplyTarget("signalr", "conv-7"));

    [Fact]
    public void SetThenGet_RoundTripsContext()
    {
        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConversationContext(_context);
        message.GetConversationContext().ShouldBe(_context);
    }

    [Fact]
    public void Get_AfterJsonRoundTrip_DeserializesContext()
    {
        // Chat history persists AdditionalProperties as JSON; on restore the value
        // comes back as a JsonElement, not the original record.
        var message = new ChatMessage(ChatRole.User, "hi");
        message.AdditionalProperties = new AdditionalPropertiesDictionary
        {
            ["ConversationContext"] = System.Text.Json.JsonSerializer.SerializeToElement(
                _context, ChannelProtocol.SerializerOptions)
        };
        message.GetConversationContext().ShouldBe(_context);
    }

    [Fact]
    public void Get_WhenUnset_ReturnsNull()
    {
        new ChatMessage(ChatRole.User, "hi").GetConversationContext().ShouldBeNull();
    }

    [Fact]
    public void Set_Null_IsNoOp()
    {
        var message = new ChatMessage(ChatRole.User, "hi");
        message.SetConversationContext(null);
        message.AdditionalProperties.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~ConversationContextStampingTests"`
Expected: FAIL — `ConversationContext` does not exist / `SetConversationContext` not found.

- [ ] **Step 3: Write the DTO**

`Domain/DTOs/Channel/ConversationContext.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ConversationContext(string AgentId, string ConversationId, string UserId, ReplyTarget Origin);
```

- [ ] **Step 4: Add the extensions**

In `Domain/Extensions/ChatMessageExtensions.cs`, add `using Domain.DTOs.Channel;`, a key constant next to the existing ones, and two extension members inside the existing `extension(ChatMessage message)` block (mirror `GetSenderId`/`SetSenderId` exactly):

```csharp
private const string ConversationContextKey = "ConversationContext";
```

```csharp
public ConversationContext? GetConversationContext()
{
    var value = message.AdditionalProperties?.GetValueOrDefault(ConversationContextKey);
    return value switch
    {
        ConversationContext context => context,
        JsonElement je => je.Deserialize<ConversationContext>(ChannelProtocol.SerializerOptions),
        _ => null
    };
}

public void SetConversationContext(ConversationContext? context)
{
    if (context is null)
    {
        return;
    }

    message.AdditionalProperties ??= [];
    message.AdditionalProperties[ConversationContextKey] = context;
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~ConversationContextStampingTests"`
Expected: 4 PASS

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: ConversationContext DTO and ChatMessage stamping extensions"
```

---

### Task 2: ChatMonitor stamps the context on every user message

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs` (`ProcessChatThread`, around line 191; new static helper)
- Test: `Tests/Unit/Domain/Monitor/ChatMonitorConversationContextTests.cs`

- [ ] **Step 1: Write the failing tests**

Use the existing fakes in `Tests/Unit/Domain/MonitorTests.cs` (`MonitorTestMocks`, `FakeChannelConnection`, `FakeAiAgent` — `FakeAiAgent.ReceivedMessages` records the `ChatMessage`s each run receives). Mirror the arrange style of `Tests/Unit/Domain/Monitor/ChatMonitorPersistenceKeyTests.cs`.

```csharp
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Domain.Monitor;
using Shouldly;
using Tests.Unit.Domain;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorConversationContextTests
{
    [Fact]
    public async Task Monitor_InteractiveMessage_StampsOriginContextOnUserMessage()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-1", channelId: "signalr", agentId: "jonas");
        var signalr = MonitorTestMocks.CreateChannel("signalr", message);
        var fakeAgent = MonitorTestMocks.CreateAgent();
        var monitor = new ChatMonitor([signalr], MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateApprovalHandlerFactory(), MonitorTestMocks.CreateThreadResolver(),
            MonitorTestMocks.CreateMetricsPublisher(), null, MonitorTestMocks.CreateLogger());

        await monitor.Monitor(CancellationToken.None);

        var received = fakeAgent.ReceivedMessages.ShouldHaveSingleItem();
        var context = received.GetConversationContext().ShouldNotBeNull();
        context.AgentId.ShouldBe("jonas");
        context.ConversationId.ShouldBe("conv-1");
        context.Origin.ShouldBe(new ReplyTarget("signalr", "conv-1"));
    }

    [Fact]
    public void BuildConversationContext_UsesFirstDeliveryTarget()
    {
        var channel = new FakeChannelConnection { ChannelId = "telegram" };
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "fire-1", channelId: "scheduling", agentId: "jonas");
        var targets = new[] { new ChatMonitor.DeliveryTarget(channel, "t-9") };

        var context = ChatMonitor.BuildConversationContext(message, targets);

        context.ConversationId.ShouldBe("t-9");
        context.Origin.ShouldBe(new ReplyTarget("telegram", "t-9"));
    }

    [Fact]
    public void BuildConversationContext_NoTargets_FallsBackToMessageOrigin()
    {
        var message = MonitorTestMocks.CreateChannelMessage(
            conversationId: "conv-2", channelId: "voice", agentId: "jonas") with { SatelliteId = "fran-office-01" };

        var context = ChatMonitor.BuildConversationContext(message, []);

        context.ConversationId.ShouldBe("conv-2");
        context.Origin.ShouldBe(new ReplyTarget("voice", "conv-2", "fran-office-01"));
    }
}
```

Adapt constructor arguments and `MonitorTestMocks` factory-method names to what actually exists in `Tests/Unit/Domain/MonitorTests.cs` (open it first; the mock factory exposes helpers for channel, agent, factory, thread resolver, approval handler, metrics publisher, logger — names may differ slightly from the sketch above).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~ChatMonitorConversationContextTests"`
Expected: FAIL — `BuildConversationContext` not defined; no context stamped.

- [ ] **Step 3: Implement**

In `Domain/Monitor/ChatMonitor.cs` add the static helper (near `BuildScheduleEvent`):

```csharp
internal static ConversationContext BuildConversationContext(
    ChannelMessage message, IReadOnlyList<DeliveryTarget> targets)
{
    var (channelId, conversationId) = targets.Count > 0
        ? (targets[0].Channel.ChannelId, targets[0].ConversationId)
        : (message.ChannelId, message.ConversationId);
    var address = channelId == message.ChannelId ? message.SatelliteId : null;
    return new ConversationContext(
        message.AgentId ?? "default",
        conversationId,
        message.Sender,
        new ReplyTarget(channelId, conversationId, address));
}
```

Note `internal` — Domain already exposes internals to Tests; if the test cannot see it, check `Domain.csproj` for `InternalsVisibleTo` and mirror how `ChatMonitor.ResolveDeliveryTargetsAsync` (public static) is tested; making the helper `public static` is acceptable.

In `ProcessChatThread`, immediately after `userMessage.SetTimestamp(DateTimeOffset.UtcNow);` (line ~195) add:

```csharp
userMessage.SetConversationContext(BuildConversationContext(x.Message, messageTargets));
```

- [ ] **Step 4: Run tests to verify they pass, plus the existing monitor suites**

Run: `dotnet test Tests --filter "FullyQualifiedName~ChatMonitor"`
Expected: all PASS (new + existing delivery/persistence/metrics suites).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: ChatMonitor stamps ConversationContext on user messages"
```

---

### Task 3: `_meta` injection — McpAgent run options + QualifiedMcpTool

**Files:**
- Create: `Infrastructure/Agents/Mcp/ConversationContextMeta.cs`
- Modify: `Infrastructure/Agents/McpAgent.cs` (`RunCoreStreamingInnerAsync` line 242, `CreateRunOptions` line 303)
- Modify: `Infrastructure/Agents/Mcp/QualifiedMcpTool.cs` (`InvokeCoreAsync`)
- Test: `Tests/Unit/Infrastructure/Agents/ConversationContextMetaTests.cs`

- [ ] **Step 1: Write the failing unit tests**

```csharp
using Domain.DTOs.Channel;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class ConversationContextMetaTests
{
    private static readonly ConversationContext _context = new(
        "jack", "conv-9", "fran", new ReplyTarget("signalr", "conv-9"));

    [Fact]
    public void TryBuild_WithContextInOptions_ProducesMetaJson()
    {
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [ConversationContextMeta.OptionsKey] = _context
            }
        };

        var meta = ConversationContextMeta.TryBuild(options).ShouldNotBeNull();

        var node = meta[ConversationContextMeta.MetaKey].ShouldNotBeNull();
        node["conversationId"]!.GetValue<string>().ShouldBe("conv-9");
        node["agentId"]!.GetValue<string>().ShouldBe("jack");
        node["userId"]!.GetValue<string>().ShouldBe("fran");
        node["origin"]!["channelId"]!.GetValue<string>().ShouldBe("signalr");
    }

    [Fact]
    public void TryBuild_NullOptionsOrMissingKey_ReturnsNull()
    {
        ConversationContextMeta.TryBuild(null).ShouldBeNull();
        ConversationContextMeta.TryBuild(new ChatOptions()).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~ConversationContextMetaTests"`
Expected: FAIL — `ConversationContextMeta` does not exist.

- [ ] **Step 3: Implement `ConversationContextMeta`**

`Infrastructure/Agents/Mcp/ConversationContextMeta.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents.Mcp;

internal static class ConversationContextMeta
{
    public const string OptionsKey = "ConversationContext";
    public const string MetaKey = "conversationContext";

    public static JsonObject? TryBuild(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.GetValueOrDefault(OptionsKey) is not ConversationContext context)
        {
            return null;
        }

    return new JsonObject
        {
            [MetaKey] = JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
        };
    }
}
```

(`ChannelProtocol.SerializerOptions` is camelCase web defaults with string enums — the same options the server side will use to deserialize.)

- [ ] **Step 4: Wire McpAgent**

In `Infrastructure/Agents/McpAgent.cs`:

`RunCoreStreamingInnerAsync` — extract the context before creating options (the incoming `messages` are the current turn's stamped messages, not history):

```csharp
var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
var conversationContext = messageList
    .Select(m => m.GetConversationContext())
    .FirstOrDefault(c => c is not null);
options ??= CreateRunOptions(session, conversationContext);
```

(Replace subsequent uses of `messages` in the method body with `messageList`. Add `using Domain.Extensions;` and `using Domain.DTOs.Channel;`.)

`CreateRunOptions` — new signature and `AdditionalProperties`:

```csharp
private ChatClientAgentRunOptions CreateRunOptions(ThreadSession session, ConversationContext? conversationContext = null)
{
    return new ChatClientAgentRunOptions(new ChatOptions
    {
        Tools = [.. session.Tools],
        Instructions = BuildInstructions(
            _name,
            _description,
            _customInstructions,
            _domainPrompts,
            session.FileSystemPrompts,
            session.ClientManager.Prompts,
            _timeProvider.GetLocalNow()),
        Reasoning = _reasoningEffort is null
            ? null
            : new ReasoningOptions { Effort = _reasoningEffort.Value },
        AdditionalProperties = conversationContext is null
            ? null
            : new AdditionalPropertiesDictionary { [ConversationContextMeta.OptionsKey] = conversationContext }
    });
}
```

- [ ] **Step 5: Wire QualifiedMcpTool**

In `Infrastructure/Agents/Mcp/QualifiedMcpTool.cs`, replace `InvokeCoreAsync`:

```csharp
protected override async ValueTask<object?> InvokeCoreAsync(
    AIFunctionArguments arguments,
    CancellationToken cancellationToken)
{
    var meta = ConversationContextMeta.TryBuild(FunctionInvokingChatClient.CurrentContext?.Options);
    var tool = meta is null ? innerTool : innerTool.WithMeta(meta);
    var result = await tool.InvokeAsync(arguments, cancellationToken);
    return Flatten(result);
}
```

(`FunctionInvokingChatClient.CurrentContext` is the framework AsyncLocal set around every function invocation by Microsoft.Agents.AI's tool loop; `McpClientTool.WithMeta(JsonObject)` returns a copy whose calls carry the object as JSON-RPC `_meta`. Both verified in the pinned packages.)

- [ ] **Step 6: Run unit tests + build**

Run: `dotnet build && dotnet test Tests --filter "FullyQualifiedName~ConversationContextMetaTests"`
Expected: build clean, 2 PASS.

- [ ] **Step 7: Write the wire-level integration test (in-process MCP server, no Docker)**

Create `Tests/Integration/Fixtures/MetaEchoServerFixture.cs` — minimal in-process Kestrel MCP server with one tool that echoes `context.Params.Meta` (mirror the host-building style of `Tests/Integration/Fixtures/McpSchedulingServerFixture.cs`, including `TestPort.GetAvailable()`):

```csharp
using System.ComponentModel;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Tests.Integration.Fixtures;

[McpServerToolType]
public class MetaEchoTool
{
    [McpServerTool(Name = "echo_meta")]
    [Description("Returns the request _meta as JSON text")]
    public static string McpRun(RequestContext<CallToolRequestParams> context)
        => context.Params?.Meta?.ToJsonString() ?? "null";
}

public class MetaEchoServerFixture : IAsyncLifetime
{
    private IHost _host = null!;

    public string McpEndpoint { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var port = TestPort.GetAvailable();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port));
        builder.Services.AddMcpServer().WithHttpTransport().WithTools<MetaEchoTool>();
        var app = builder.Build();
        app.MapMcp("/mcp");
        _host = app;
        await app.StartAsync();
        McpEndpoint = $"http://127.0.0.1:{port}/mcp";
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }
}
```

Create `Tests/Integration/Agents/QualifiedMcpToolMetaTests.cs`:

```csharp
using Domain.DTOs.Channel;
using Infrastructure.Agents.Mcp;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class QualifiedMcpToolMetaTests(MetaEchoServerFixture fixture) : IClassFixture<MetaEchoServerFixture>
{
    [Fact]
    public async Task InvokeCore_WithCurrentContext_DeliversConversationContextAsMeta()
    {
        await using var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri(fixture.McpEndpoint) }));
        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "echo_meta");
        var qualified = new QualifiedMcpTool("echo", tool);

        var context = new ConversationContext("jack", "conv-1", "fran", new ReplyTarget("signalr", "conv-1"));
        FunctionInvokingChatClient.CurrentContext = new FunctionInvocationContext
        {
            Options = new ChatOptions
            {
                AdditionalProperties = new AdditionalPropertiesDictionary
                {
                    [ConversationContextMeta.OptionsKey] = context
                }
            }
        };
        try
        {
            var result = await qualified.InvokeAsync(new AIFunctionArguments(), CancellationToken.None);
            var text = result!.ToString()!;
            text.ShouldContain("conversationContext");
            text.ShouldContain("conv-1");
            text.ShouldContain("signalr");
        }
        finally
        {
            FunctionInvokingChatClient.CurrentContext = null;
        }
    }
}
```

Adjust to the SDK/test-infra reality you find: `FunctionInvocationContext` requires a `Function` property in some versions — if construction fails, set the minimum required members (`Function = qualified` works). `QualifiedMcpTool` is `internal`; Infrastructure already has `InternalsVisibleTo` for Tests (ThreadSession internals are tested) — verify, and if absent for this assembly add the attribute alongside the existing one.

- [ ] **Step 8: Run the integration test**

Run: `dotnet test Tests --filter "FullyQualifiedName~QualifiedMcpToolMetaTests"`
Expected: PASS — the echoed `_meta` contains the camelCase conversation context.

- [ ] **Step 9: Commit**

```bash
git add -A && git commit -m "feat: inject ConversationContext as MCP _meta on every tool call"
```

---

### Task 4: `MessageOriginKind.Download`, `DownloadRouting`, `IDownloadRoutingStore` + Redis implementation

**Files:**
- Modify: `Domain/DTOs/Channel/MessageOriginKind.cs`
- Create: `Domain/DTOs/DownloadRouting.cs`
- Create: `Domain/Contracts/IDownloadRoutingStore.cs`
- Create: `Infrastructure/StateManagers/RedisDownloadRoutingStore.cs`
- Test: `Tests/Integration/StateManagers/RedisDownloadRoutingStoreTests.cs`

- [ ] **Step 1: Add the enum member and DTOs (no test needed for declarations)**

`Domain/DTOs/Channel/MessageOriginKind.cs`:

```csharp
namespace Domain.DTOs.Channel;

public enum MessageOriginKind
{
    Schedule,
    Download
}
```

(Wire-safe: `ChannelProtocol.SerializerOptions` serializes enums as strings.)

`Domain/DTOs/DownloadRouting.cs`:

```csharp
using Domain.DTOs.Channel;
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record DownloadRouting
{
    public required int DownloadId { get; init; }
    public required string Title { get; init; }
    public required ConversationContext Context { get; init; }
    public DateTimeOffset SubmittedAt { get; init; }
}
```

`Domain/Contracts/IDownloadRoutingStore.cs`:

```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface IDownloadRoutingStore
{
    Task SetAsync(DownloadRouting routing, CancellationToken ct = default);
    Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default);
    Task RemoveAsync(int downloadId, CancellationToken ct = default);
}
```

- [ ] **Step 2: Write the failing Redis store tests**

First open `Tests/Integration/` and find how Redis-backed stores are tested (the scheduling suite spins a Redis Testcontainer — look for the fixture used by `RedisScheduleStore` tests or inside `McpSchedulingServerFixture`). Reuse that fixture type. Test shape:

```csharp
using Domain.DTOs;
using Domain.DTOs.Channel;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

public class RedisDownloadRoutingStoreTests(RedisFixture redis) : IClassFixture<RedisFixture>
{
    private static DownloadRouting Routing(int id) => new()
    {
        DownloadId = id,
        Title = $"Title {id}",
        Context = new ConversationContext("jack", $"conv-{id}", "fran", new ReplyTarget("signalr", $"conv-{id}")),
        SubmittedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task SetListRemove_RoundTrips()
    {
        var store = new RedisDownloadRoutingStore(redis.Connection);

        await store.SetAsync(Routing(101));
        await store.SetAsync(Routing(102));

        var listed = await store.ListAsync();
        listed.Select(r => r.DownloadId).ShouldBe([101, 102], ignoreOrder: true);
        listed.Single(r => r.DownloadId == 101).Context.Origin.ChannelId.ShouldBe("signalr");

        await store.RemoveAsync(101);
        (await store.ListAsync()).Select(r => r.DownloadId).ShouldBe([102]);
    }

    [Fact]
    public async Task Set_SameId_Overwrites()
    {
        var store = new RedisDownloadRoutingStore(redis.Connection);
        await store.SetAsync(Routing(201));
        await store.SetAsync(Routing(201) with { Title = "updated" });
        (await store.ListAsync()).Single(r => r.DownloadId == 201).Title.ShouldBe("updated");
    }
}
```

Adapt `RedisFixture`/`redis.Connection` to the actual fixture name and member. These tests require Docker — in this environment they will fail with `DockerUnavailableException` like the rest of the baseline; that is expected and acceptable. Verify compilation and logic by review; they run in CI.

- [ ] **Step 3: Implement the Redis store**

`Infrastructure/StateManagers/RedisDownloadRoutingStore.cs` (mirror `RedisScheduleStore` conventions):

```csharp
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using StackExchange.Redis;

namespace Infrastructure.StateManagers;

public sealed class RedisDownloadRoutingStore(IConnectionMultiplexer redis) : IDownloadRoutingStore
{
    private const string IndexKey = "download-routing";
    private static readonly TimeSpan _expiry = TimeSpan.FromDays(60);

    private readonly IDatabase _db = redis.GetDatabase();

    public async Task SetAsync(DownloadRouting routing, CancellationToken ct = default)
    {
        var transaction = _db.CreateTransaction();
        _ = transaction.StringSetAsync(EntryKey(routing.DownloadId), JsonSerializer.Serialize(routing), _expiry);
        _ = transaction.SetAddAsync(IndexKey, routing.DownloadId);
        await transaction.ExecuteAsync();
    }

    public async Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default)
    {
        var ids = await _db.SetMembersAsync(IndexKey);
        var entries = await Task.WhenAll(ids.Select(async id =>
        {
            var json = await _db.StringGetAsync(EntryKey((int)id));
            return json.IsNullOrEmpty ? null : JsonSerializer.Deserialize<DownloadRouting>(json.ToString());
        }));
        return entries.Where(e => e is not null).Select(e => e!).ToList();
    }

    public async Task RemoveAsync(int downloadId, CancellationToken ct = default)
    {
        var transaction = _db.CreateTransaction();
        _ = transaction.KeyDeleteAsync(EntryKey(downloadId));
        _ = transaction.SetRemoveAsync(IndexKey, downloadId);
        await transaction.ExecuteAsync();
    }

    private static string EntryKey(int downloadId) => $"download-routing:{downloadId}";
}
```

- [ ] **Step 4: Build + run what's runnable**

Run: `dotnet build && dotnet test Tests --filter "FullyQualifiedName~RedisDownloadRoutingStoreTests"`
Expected: build clean; tests PASS where Docker is available, `DockerUnavailableException` otherwise (baseline).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: DownloadRouting store (Redis) and MessageOriginKind.Download"
```

---

### Task 5: `FileDownloadTool` captures routing; `McpFileDownloadTool` parses `_meta`

**Files:**
- Modify: `Domain/Tools/Downloads/FileDownloadTool.cs`
- Modify: `McpServerLibrary/McpTools/McpFileDownloadTool.cs`
- Modify: `Tests/Unit/Domain/FileDownloadToolTests.cs`
- Test: `Tests/Unit/McpServerLibrary/McpFileDownloadToolMetaTests.cs`

- [ ] **Step 1: Update the failing unit tests first**

Open `Tests/Unit/Domain/FileDownloadToolTests.cs`; replace its `ITrackedDownloadsManager` usage with a fake `IDownloadRoutingStore`, and add:

```csharp
private sealed class FakeRoutingStore : IDownloadRoutingStore
{
    public List<DownloadRouting> Entries { get; } = [];
    public Task SetAsync(DownloadRouting routing, CancellationToken ct = default)
    {
        Entries.RemoveAll(e => e.DownloadId == routing.DownloadId);
        Entries.Add(routing);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DownloadRouting>>(Entries.ToList());
    public Task RemoveAsync(int downloadId, CancellationToken ct = default)
    {
        Entries.RemoveAll(e => e.DownloadId == downloadId);
        return Task.CompletedTask;
    }
}

[Fact]
public async Task Run_WithContext_StoresRoutingSnapshotWithTitle()
{
    // arrange a search result id 5 titled "Some Movie" the way existing tests do
    var context = new ConversationContext("jack", "conv-1", "fran", new ReplyTarget("signalr", "conv-1"));

    await RunDownload(searchResultId: 5, context);   // adapt to this file's existing invocation helper

    var entry = _routingStore.Entries.ShouldHaveSingleItem();
    entry.DownloadId.ShouldBe(5);
    entry.Title.ShouldBe("Some Movie");
    entry.Context.ShouldBe(context);
}

[Fact]
public async Task Run_WithoutContext_StoresNothing_AndWarnsInMessage()
{
    var result = await RunDownload(searchResultId: 5, context: null);

    _routingStore.Entries.ShouldBeEmpty();
    result["message"]!.GetValue<string>().ShouldContain("alert");
}
```

(Adapt arrange/invoke helpers to the existing test file's structure — it already fakes `IDownloadClient` and `ISearchResultsManager`.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests --filter "FullyQualifiedName~FileDownloadToolTests"`
Expected: FAIL — compile errors (constructor/Run signatures).

- [ ] **Step 3: Rewrite `FileDownloadTool`**

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Tools.Config;

namespace Domain.Tools.Downloads;

public class FileDownloadTool(
    IDownloadClient client,
    ISearchResultsManager searchResultsManager,
    IDownloadRoutingStore routingStore,
    DownloadPathConfig pathConfig,
    TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    protected const string Name = "download_file";

    protected const string Description = """
                                         Download a file from the internet.

                                         Provide ONE of:
                                           - searchResultId: an id from a prior file_search call.
                                           - link + title: a magnet URI or .torrent URL obtained from any other tool, plus a
                                             descriptive title (e.g. the release name with quality and group, taken from
                                             wherever the link was found).

                                         Do not provide both. The link path is intended as a fallback when file_search returns
                                         no usable results.
                                         """;

    protected async Task<JsonNode> Run(string sessionId, int searchResultId, ConversationContext? context, CancellationToken ct)
    {
        var existing = await client.GetDownloadItem(searchResultId, ct);
        if (existing is not null)
        {
            return ToolError.Create(
                ToolError.Codes.AlreadyExists,
                "Download with this id already exists, try another id",
                retryable: false);
        }

        var itemToDownload = searchResultsManager.Get(sessionId, searchResultId);
        if (itemToDownload == null)
        {
            return ToolError.Create(
                ToolError.Codes.NotFound,
                $"No search result found for id {searchResultId}. " +
                "Make sure to run the file_search tool first and use the correct id.",
                retryable: false);
        }

        return await StartDownload(searchResultId, itemToDownload.Link, itemToDownload.Title, context, ct);
    }

    protected async Task<JsonNode> Run(string sessionId, string link, string title, ConversationContext? context, CancellationToken ct)
    {
        var id = link.GetHashCode();

        var existing = await client.GetDownloadItem(id, ct);
        if (existing is not null)
        {
            return ToolError.Create(
                ToolError.Codes.AlreadyExists,
                "Download with this link already exists, choose a different link",
                retryable: false);
        }

        var synthetic = new SearchResult
        {
            Id = id,
            Title = title,
            Link = link
        };
        searchResultsManager.Add(sessionId, [synthetic]);

        return await StartDownload(id, link, title, context, ct);
    }

    private async Task<JsonNode> StartDownload(int id, string link, string title, ConversationContext? context, CancellationToken ct)
    {
        var savePath = $"{pathConfig.BaseDownloadPath}/{id}";
        await client.Download(link, savePath, id, ct);

        if (context is not null)
        {
            await routingStore.SetAsync(new DownloadRouting
            {
                DownloadId = id,
                Title = title,
                Context = context,
                SubmittedAt = _timeProvider.GetUtcNow()
            }, ct);
        }

        var completionNote = context is not null
            ? "A completion message will arrive in this conversation when it finishes."
            : "No conversation context was provided, so no completion alert will fire; check /downloads for status.";
        return new JsonObject
        {
            ["status"] = "success",
            ["message"] = $"Download with id {id} started successfully. {completionNote}"
        };
    }
}
```

(If `routingStore.SetAsync` throws, the exception propagates to the server's global tool filter and the call returns an error — intended: a download whose alert can never fire must not start silently.)

- [ ] **Step 4: Update `McpFileDownloadTool`**

Replace the class body (note: `ITrackedDownloadsManager` dependency and the `list_changed` notification are gone; the meta parser is a testable static):

```csharp
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Downloads;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpFileDownloadTool(
    IDownloadClient client,
    ISearchResultsManager searchResultsManager,
    IDownloadRoutingStore routingStore,
    DownloadPathConfig pathConfig)
    : FileDownloadTool(client, searchResultsManager, routingStore, pathConfig)
{
    internal const string ConversationContextMetaKey = "conversationContext";

    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        [Description("Id from a prior file_search result. Mutually exclusive with link.")]
        int? searchResultId,
        [Description("Magnet URI or http(s) .torrent URL obtained from any other tool. Requires title. Mutually exclusive with searchResultId.")]
        string? link,
        [Description("Descriptive title for the download (required when link is provided; ignored otherwise).")]
        string? title,
        CancellationToken cancellationToken)
    {
        var sessionId = context.Server.StateKey;
        var conversationContext = ParseConversationContext(context.Params?.Meta);

        var validation = ValidateInputs(searchResultId, link, title);
        if (validation is not null)
        {
            return ToolResponse.Create(validation);
        }

        var result = searchResultId.HasValue
            ? await Run(sessionId, searchResultId.Value, conversationContext, cancellationToken)
            : await Run(sessionId, link!, title!, conversationContext, cancellationToken);
        return ToolResponse.Create(result);
    }

    internal static ConversationContext? ParseConversationContext(JsonObject? meta)
        => meta?[ConversationContextMetaKey]?.Deserialize<ConversationContext>(ChannelProtocol.SerializerOptions);

    public static JsonNode? ValidateInputs(int? searchResultId, string? link, string? title)
    {
        // unchanged from the current file
    }

    private static bool IsAcceptedLink(string link)
    {
        // unchanged from the current file
    }
}
```

(Keep `ValidateInputs`/`IsAcceptedLink` bodies exactly as they are today.)

- [ ] **Step 5: Write meta-parsing unit tests**

`Tests/Unit/McpServerLibrary/McpFileDownloadToolMetaTests.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.DTOs.Channel;
using McpServerLibrary.McpTools;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class McpFileDownloadToolMetaTests
{
    [Fact]
    public void ParseConversationContext_RoundTripsWhatTheAgentSends()
    {
        var context = new ConversationContext("jack", "conv-1", "fran", new ReplyTarget("signalr", "conv-1"));
        var meta = new JsonObject
        {
            ["conversationContext"] = JsonSerializer.SerializeToNode(context, ChannelProtocol.SerializerOptions)
        };

        McpFileDownloadTool.ParseConversationContext(meta).ShouldBe(context);
    }

    [Fact]
    public void ParseConversationContext_MissingOrNullMeta_ReturnsNull()
    {
        McpFileDownloadTool.ParseConversationContext(null).ShouldBeNull();
        McpFileDownloadTool.ParseConversationContext([]).ShouldBeNull();
    }
}
```

- [ ] **Step 6: Run tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~FileDownloadToolTests|FullyQualifiedName~McpFileDownloadToolMetaTests"`
Expected: PASS. The solution will NOT fully build yet only if other call sites of the old constructor exist — fix them as you find them (the library `ConfigModule` still registers `ITrackedDownloadsManager`; leave that registration in place until Task 11, it is independent of this constructor).

Note: `Tests/Integration/Fixtures/McpLibraryServerFixture.cs` registers `ITrackedDownloadsManager` and the old tool set — patch it minimally now (swap to `IDownloadRoutingStore` with the `FakeRoutingStore` from Step 1 moved to a shared location `Tests/Integration/Fixtures/FakeDownloadRoutingStore.cs` if needed) so the Tests project compiles. Full fixture rework happens in Task 14.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: download_file captures conversation routing from MCP _meta"
```

---

### Task 6: `IDownloadClient.GetDownloadItems` (list all)

**Files:**
- Modify: `Domain/Contracts/IDownloadClient.cs`
- Modify: `Infrastructure/Clients/Torrent/QBittorrentDownloadClient.cs`
- Test: extend `Tests/Integration/McpServerTests/McpLibraryServerTests.cs` only if it already exercises `IDownloadClient` directly; otherwise unit-test the mapping via the fakes below (the qBittorrent path is covered by the Docker-based fixture in CI).

- [ ] **Step 1: Extend the contract**

```csharp
using Domain.DTOs;

namespace Domain.Contracts;

public interface IDownloadClient
{
    Task Download(string link, string savePath, int id, CancellationToken cancellationToken = default);
    Task Cleanup(int id, CancellationToken cancellationToken = default);
    Task<DownloadItem?> GetDownloadItem(int id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DownloadItem>> GetDownloadItems(CancellationToken cancellationToken = default);
}
```

- [ ] **Step 2: Implement in `QBittorrentDownloadClient`**

The client renames torrents to `"{id}"` on add and finds them by name — so "our" torrents are exactly the integer-named ones. Extract the existing mapping in `GetDownloadItemWithoutRetries` into a shared `MapDownloadItem(int id, JsonNode torrent)` and add:

```csharp
public async Task<IReadOnlyList<DownloadItem>> GetDownloadItems(CancellationToken cancellationToken = default)
{
    var torrents = await CallApi(GetAllTorrents, cancellationToken);
    return torrents
        .Select(t => (Torrent: t, Name: t?["name"]?.GetValue<string>()))
        .Where(x => int.TryParse(x.Name, out _))
        .Select(x => MapDownloadItem(int.Parse(x.Name!), x.Torrent!))
        .ToList();
}

private static DownloadItem MapDownloadItem(int id, JsonNode torrent) => new()
{
    Id = id,
    Title = torrent["name"]?.GetValue<string>() ?? string.Empty,
    Size = (torrent["total_size"]?.GetValue<long>() ?? 0) / 1024 / 1024,
    State = GetDownloadStatus(torrent),
    Seeders = torrent["num_seeds"]?.GetValue<int>() ?? 0,
    Peers = torrent["num_leechs"]?.GetValue<int>() ?? 0,
    SavePath = torrent["save_path"]?.GetValue<string>() ?? string.Empty,
    Link = torrent["magnet_uri"]?.GetValue<string>() ?? string.Empty,
    Progress = torrent["progress"]?.GetValue<double>() ?? 0.0,
    DownSpeed = (torrent["dlspeed"]?.GetValue<double>() ?? 0.0) / 1024 / 1024,
    UpSpeed = (torrent["upspeed"]?.GetValue<double>() ?? 0.0) / 1024 / 1024,
    Eta = (torrent["eta"]?.GetValue<double>() ?? 0.0) / 60
};
```

Refactor `GetDownloadItemWithoutRetries` to call `MapDownloadItem(id, torrent)` so the mapping isn't duplicated. (`GetDownloadStatus` is the existing private state mapper — unchanged.)

- [ ] **Step 3: Fix all `IDownloadClient` fakes/mocks that now fail to compile**

Search: `grep -rl "IDownloadClient" Tests/ --include="*.cs"`. Hand-rolled fakes need the new member (`Task.FromResult<IReadOnlyList<DownloadItem>>([])` default); Moq-based mocks compile as-is.

- [ ] **Step 4: Build and run the affected suites**

Run: `dotnet build && dotnet test Tests --filter "FullyQualifiedName~FileDownloadToolTests"`
Expected: clean build, PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: IDownloadClient.GetDownloadItems lists all owned torrents"
```

---

### Task 7: `DownloadsFileSystem` VFS engine

**Files:**
- Create: `Domain/Tools/Downloads/Vfs/DownloadsFileSystem.cs`
- Test: `Tests/Unit/Domain/Downloads/Vfs/DownloadsFileSystemTests.cs`

Closest analogue: `Domain/Tools/Printing/Vfs/PrinterQueueFileSystem.cs` (non-disk backend, read-only status, fs_delete with side effects, Unsupported for the rest). Read it before starting.

- [ ] **Step 1: Write the failing tests**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.FileSystem;
using Domain.Tools.Downloads.Vfs;
using Shouldly;

namespace Tests.Unit.Domain.Downloads.Vfs;

public class DownloadsFileSystemTests
{
    private sealed class FakeDownloadClient : IDownloadClient
    {
        public List<DownloadItem> Items { get; } = [];
        public List<int> CleanedUp { get; } = [];

        public Task Download(string link, string savePath, int id, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task Cleanup(int id, CancellationToken ct = default)
        {
            CleanedUp.Add(id);
            Items.RemoveAll(i => i.Id == id);
            return Task.CompletedTask;
        }
        public Task<DownloadItem?> GetDownloadItem(int id, CancellationToken ct = default)
            => Task.FromResult(Items.FirstOrDefault(i => i.Id == id));
        public Task<IReadOnlyList<DownloadItem>> GetDownloadItems(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DownloadItem>>(Items.ToList());
    }

    private sealed class FakeRoutingStore : IDownloadRoutingStore
    {
        public List<DownloadRouting> Entries { get; } = [];
        public Task SetAsync(DownloadRouting routing, CancellationToken ct = default)
        { Entries.Add(routing); return Task.CompletedTask; }
        public Task<IReadOnlyList<DownloadRouting>> ListAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DownloadRouting>>(Entries.ToList());
        public Task RemoveAsync(int downloadId, CancellationToken ct = default)
        { Entries.RemoveAll(e => e.DownloadId == downloadId); return Task.CompletedTask; }
    }

    private sealed class FakeFileSystemClient : IFileSystemClient
    {
        public List<string> RemovedDirectories { get; } = [];
        // implement only RemoveDirectory meaningfully; stub the rest of the interface
        // (open Domain/Contracts/IFileSystemClient.cs and stub every member with
        //  throw new NotSupportedException() except RemoveDirectory)
        public Task RemoveDirectory(string path, CancellationToken ct = default)
        { RemovedDirectories.Add(path); return Task.CompletedTask; }
    }

    private static DownloadItem Item(int id, DownloadState state = DownloadState.InProgress) => new()
    {
        Id = id, Title = $"Title {id}", Link = "magnet:x", State = state,
        Progress = 0.5, DownSpeed = 1, UpSpeed = 1, Eta = 10, SavePath = $"/downloads/{id}", Size = 700
    };

    private readonly FakeDownloadClient _client = new();
    private readonly FakeRoutingStore _routing = new();
    private readonly FakeFileSystemClient _disk = new();

    private DownloadsFileSystem Build()
        => new(_client, _routing, _disk, new Domain.Tools.Config.DownloadPathConfig("/downloads"));

    [Fact]
    public async Task Contract_NameAndUnsupportedOps()
    {
        var fs = Build();
        fs.ShouldBeAssignableTo<IFileSystemBackend>();
        fs.FilesystemName.ShouldBe("downloads");

        (await fs.MoveAsync("1", "2", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsMoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
        (await fs.ExecAsync("1", "x", null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsExecResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
        (await fs.CreateAsync("1/status.json", "{}", true, true, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCreateResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
        (await fs.CopyAsync("1", "2", false, false, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsCopyResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
        (await fs.EditAsync("1/status.json", [new TextEdit("a", "b")], CancellationToken.None))
            .ShouldBeOfType<FsResult<FsEditResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
    }

    [Fact]
    public async Task Glob_ListsDownloadDirsAndStatusFiles()
    {
        _client.Items.Add(Item(42));
        _client.Items.Add(Item(7, DownloadState.Completed));
        var fs = Build();

        var all = (await fs.GlobAsync("/", "**", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        all.Entries.ShouldContain("/42/");
        all.Entries.ShouldContain("/42/status.json");
        all.Entries.ShouldContain("/7/status.json");

        var statusOnly = (await fs.GlobAsync("/", "*/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsGlobResult>.Ok>().Value;
        statusOnly.Entries.ShouldBe(["/42/status.json", "/7/status.json"], ignoreOrder: true);
    }

    [Fact]
    public async Task Read_StatusJson_RendersDownloadState()
    {
        _client.Items.Add(Item(42));
        var fs = Build();

        var read = (await fs.ReadAsync("42/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Ok>().Value;
        read.Content.ShouldContain("\"id\": 42");
        read.Content.ShouldContain("InProgress");
        read.Content.ShouldContain("Title 42");

        (await fs.ReadAsync("99/status.json", null, null, CancellationToken.None))
            .ShouldBeOfType<FsResult<FsReadResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }

    [Fact]
    public async Task Info_ReportsExistence()
    {
        _client.Items.Add(Item(42));
        var fs = Build();

        (await fs.InfoAsync("", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value.IsDirectory.ShouldBe(true);
        (await fs.InfoAsync("42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value.Exists.ShouldBeTrue();
        (await fs.InfoAsync("42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value.Exists.ShouldBeTrue();
        (await fs.InfoAsync("99", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsInfoResult>.Ok>().Value.Exists.ShouldBeFalse();
    }

    [Fact]
    public async Task Delete_DownloadDir_CleansUpEverything()
    {
        _client.Items.Add(Item(42));
        _routing.Entries.Add(new DownloadRouting
        {
            DownloadId = 42, Title = "Title 42",
            Context = new ConversationContext("jack", "c", "u", new ReplyTarget("signalr", "c"))
        });
        var fs = Build();

        var result = (await fs.DeleteAsync("42", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Ok>().Value;
        result.Status.ShouldBe("removed");

        _client.CleanedUp.ShouldBe([42]);
        _routing.Entries.ShouldBeEmpty();
        _disk.RemovedDirectories.ShouldBe(["/downloads/42"]);
    }

    [Fact]
    public async Task Delete_StatusFileOrUnknown_IsRejected()
    {
        _client.Items.Add(Item(42));
        var fs = Build();

        (await fs.DeleteAsync("42/status.json", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("unsupported_operation");
        (await fs.DeleteAsync("99", CancellationToken.None))
            .ShouldBeOfType<FsResult<FsRemoveResult>.Err>().Error.ErrorCode.ShouldBe("not_found");
    }
}
```

(Open `Domain/Contracts/IFileSystemClient.cs` and stub the remaining members of `FakeFileSystemClient`. Check `TextEdit`'s constructor shape in `Domain/DTOs` before using it.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~DownloadsFileSystemTests"`
Expected: FAIL — `DownloadsFileSystem` does not exist.

- [ ] **Step 3: Implement the engine**

`Domain/Tools/Downloads/Vfs/DownloadsFileSystem.cs`:

```csharp
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.FileSystem;
using Domain.Tools.Config;
using Domain.Tools.FileSystem;

namespace Domain.Tools.Downloads.Vfs;

public sealed class DownloadsFileSystem(
    IDownloadClient client,
    IDownloadRoutingStore routingStore,
    IFileSystemClient fileSystemClient,
    DownloadPathConfig pathConfig) : IFileSystemBackend
{
    public const string Name = "downloads";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string FilesystemName => Name;

    public async Task<FsResult<FsReadResult>> ReadAsync(string path, int? offset, int? limit, CancellationToken ct)
    {
        var node = DownloadsPath.Parse(path);
        if (node.Kind != DownloadsNodeKind.StatusFile)
        {
            return node.Kind == DownloadsNodeKind.Unknown
                ? Err<FsReadResult>(ToolError.Codes.NotFound, $"'{path}' does not exist on the downloads filesystem.")
                : Err<FsReadResult>(ToolError.Codes.UnsupportedOperation, "Only <id>/status.json is readable.");
        }

        var item = await client.GetDownloadItem(node.DownloadId!.Value, ct);
        if (item is null)
        {
            return Err<FsReadResult>(ToolError.Codes.NotFound, $"Download {node.DownloadId} does not exist.");
        }

        var content = RenderStatus(item);
        return new FsResult<FsReadResult>.Ok(new FsReadResult
        {
            FilePath = path,
            Content = content,
            TotalLines = content.Split('\n').Length,
            Truncated = false
        });
    }

    public async Task<FsResult<FsInfoResult>> InfoAsync(string path, CancellationToken ct)
    {
        var node = DownloadsPath.Parse(path);
        if (node.Kind == DownloadsNodeKind.Root)
        {
            return new FsResult<FsInfoResult>.Ok(new FsInfoResult { Exists = true, Path = path, IsDirectory = true });
        }

        var item = node.DownloadId is { } id ? await client.GetDownloadItem(id, ct) : null;
        return new FsResult<FsInfoResult>.Ok(node.Kind switch
        {
            DownloadsNodeKind.DownloadDir => new FsInfoResult
                { Exists = item is not null, Path = path, IsDirectory = item is null ? null : true },
            DownloadsNodeKind.StatusFile when item is not null => new FsInfoResult
                { Exists = true, Path = path, IsDirectory = false, Size = RenderStatus(item).Length },
            _ => new FsInfoResult { Exists = false, Path = path }
        });
    }

    public async Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct)
    {
        var items = await client.GetDownloadItems(ct);
        var entries = items
            .SelectMany(i => new[] { $"/{i.Id}/", $"/{i.Id}/status.json" })
            .ToList();

        var trimmedBase = basePath.Trim('/');
        var effective = trimmedBase.Length == 0 ? pattern : $"{trimmedBase}/{pattern}";
        var dirsOnly = effective.EndsWith('/');
        var matcher = GlobRegex.CompileMatcher(dirsOnly ? effective.TrimEnd('/') : effective);

        var matched = entries
            .Where(e => !dirsOnly || e.EndsWith('/'))
            .Where(e => matcher(e.Trim('/')))
            .ToList();
        return new FsResult<FsGlobResult>.Ok(new FsGlobResult
        {
            Entries = matched,
            Truncated = false,
            Total = matched.Count
        });
    }

    public Task<FsResult<FsSearchResult>> SearchAsync(string query, bool regex, string? path, string? directoryPath,
        string? filePattern, int maxResults, int contextLines, VfsTextSearchOutputMode outputMode, CancellationToken ct)
        => Task.FromResult(Err<FsSearchResult>(ToolError.Codes.UnsupportedOperation,
            "The downloads filesystem does not support search; read <id>/status.json instead."));

    public Task<FsResult<FsCreateResult>> CreateAsync(string path, string content, bool overwrite,
        bool createDirectories, CancellationToken ct)
        => Task.FromResult(Err<FsCreateResult>(ToolError.Codes.UnsupportedOperation,
            "The downloads filesystem is read-only; use the download_file tool to start downloads."));

    public Task<FsResult<FsEditResult>> EditAsync(string path, IReadOnlyList<TextEdit> edits, CancellationToken ct)
        => Task.FromResult(Err<FsEditResult>(ToolError.Codes.UnsupportedOperation,
            "The downloads filesystem is read-only."));

    public Task<FsResult<FsMoveResult>> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct)
        => Task.FromResult(Err<FsMoveResult>(ToolError.Codes.UnsupportedOperation,
            "Downloads cannot be moved; organize finished files from the media filesystem instead."));

    public async Task<FsResult<FsRemoveResult>> DeleteAsync(string path, CancellationToken ct)
    {
        var node = DownloadsPath.Parse(path);
        if (node.Kind != DownloadsNodeKind.DownloadDir)
        {
            return node.Kind == DownloadsNodeKind.StatusFile
                ? Err<FsRemoveResult>(ToolError.Codes.UnsupportedOperation,
                    "status.json is read-only; delete the download directory to cancel/clean up.")
                : Err<FsRemoveResult>(ToolError.Codes.NotFound, $"'{path}' does not exist on the downloads filesystem.");
        }

        var id = node.DownloadId!.Value;
        var item = await client.GetDownloadItem(id, ct);
        if (item is null)
        {
            return Err<FsRemoveResult>(ToolError.Codes.NotFound, $"Download {id} does not exist.");
        }

        await client.Cleanup(id, ct);
        await routingStore.RemoveAsync(id, ct);
        await RemoveDownloadDirectoryBestEffort(id, ct);

        return new FsResult<FsRemoveResult>.Ok(new FsRemoveResult
        {
            Status = "removed",
            Message = $"Download {id} removed and its download directory cleaned up.",
            OriginalPath = path,
            TrashPath = ""
        });
    }

    public Task<FsResult<FsExecResult>> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
        => Task.FromResult(Err<FsExecResult>(ToolError.Codes.UnsupportedOperation,
            "The downloads filesystem does not support exec."));

    public Task<FsResult<FsCopyResult>> CopyAsync(string sourcePath, string destinationPath,
        bool overwrite, bool createDirectories, CancellationToken ct)
        => Task.FromResult(Err<FsCopyResult>(ToolError.Codes.UnsupportedOperation,
            "The downloads filesystem does not support copy."));

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(string path, CancellationToken ct)
        => throw new NotSupportedException("The downloads filesystem does not support blob reads.");

    public Task<long> WriteChunksAsync(string path, IAsyncEnumerable<ReadOnlyMemory<byte>> chunks,
        bool overwrite, bool createDirectories, CancellationToken ct)
        => throw new NotSupportedException("The downloads filesystem does not support blob writes.");

    private async Task RemoveDownloadDirectoryBestEffort(int id, CancellationToken ct)
    {
        try
        {
            await fileSystemClient.RemoveDirectory(Path.Combine(pathConfig.BaseDownloadPath, id.ToString()), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The torrent task is already gone; leftover files are non-fatal and the
            // directory may legitimately not exist anymore.
        }
    }

    private static string RenderStatus(DownloadItem item) => JsonSerializer.Serialize(new
    {
        id = item.Id,
        title = item.Title,
        state = item.State.ToString(),
        progressPercent = Math.Round(item.Progress * 100.0, 2),
        sizeMb = item.Size,
        downSpeedMbps = item.DownSpeed,
        upSpeedMbps = item.UpSpeed,
        etaMinutes = item.Eta,
        savePath = item.SavePath
    }, _json);

    private static FsResult<T> Err<T>(string code, string message) where T : class
        => new FsResult<T>.Err(ToolErrorResult.FromError(code, message));
}

internal enum DownloadsNodeKind
{
    Root,
    DownloadDir,
    StatusFile,
    Unknown
}

internal sealed record DownloadsNode(DownloadsNodeKind Kind, int? DownloadId = null);

internal static class DownloadsPath
{
    public static DownloadsNode Parse(string path)
    {
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments switch
        {
            [] => new DownloadsNode(DownloadsNodeKind.Root),
            [var id] when int.TryParse(id, out var parsed) => new DownloadsNode(DownloadsNodeKind.DownloadDir, parsed),
            [var id, "status.json"] when int.TryParse(id, out var parsed) =>
                new DownloadsNode(DownloadsNodeKind.StatusFile, parsed),
            _ => new DownloadsNode(DownloadsNodeKind.Unknown)
        };
    }
}
```

Before finalizing, check the real helper APIs and mirror them:
- `GlobRegex.CompileMatcher` — open `Domain/Tools/FileSystem/GlobRegex.cs` for the exact signature and the path shape it matches (with/without leading slash); also check how `ScheduleFileSystem.GlobAsync` combines `basePath` + `pattern` + the trailing-slash dirs-only rule, and copy that semantics.
- Error construction — open `Domain/Tools/ToolError.cs` / `ToolErrorResult` and `PrinterQueueFileSystem`'s private `Ok/NotFound/Unsupported` helpers; use the same construction (the `ToolErrorResult.FromError` call above is a placeholder for whatever factory the printer engine actually uses — copy the printer's helpers verbatim).
- `VfsTextSearchOutputMode` namespace.

- [ ] **Step 4: Run tests until green**

Run: `dotnet test Tests --filter "FullyQualifiedName~DownloadsFileSystemTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: DownloadsFileSystem VFS engine"
```

---

### Task 8: `filesystem` discriminator in the VFS wire protocol

One MCP server can expose several `filesystem://` resources, but `fs_*` tool calls carry only mount-relative paths — two mounts on one server are indistinguishable. Fix: the agent always sends the target filesystem's name.

**Files:**
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`
- Test: `Tests/Unit/Infrastructure/Agents/McpFileSystemBackendFilesystemArgTests.cs`
- Modify: `Tests/Integration/McpServerTests/McpSchedulingServerTests.cs` (extra-arg tolerance pin)

- [ ] **Step 1: Write the failing unit test**

`McpFileSystemBackend.CallToolAsync` is `protected internal virtual` — subclass it to capture arguments:

```csharp
using System.Text.Json.Nodes;
using Infrastructure.Agents.Mcp;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class McpFileSystemBackendFilesystemArgTests
{
    private sealed class CapturingBackend() : McpFileSystemBackend(null!, "downloads")
    {
        public List<(string Tool, Dictionary<string, object?> Args)> Calls { get; } = [];

        protected internal override Task<JsonNode> CallToolAsync(
            string toolName, Dictionary<string, object?> args, CancellationToken ct)
        {
            Calls.Add((toolName, args));
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["entries"] = new JsonArray(),
                ["truncated"] = false,
                ["total"] = 0
            });
        }
    }

    [Fact]
    public async Task EveryCall_CarriesTheFilesystemName()
    {
        var backend = new CapturingBackend();

        await backend.GlobAsync("/", "*", CancellationToken.None);
        await backend.DeleteAsync("42", CancellationToken.None);

        backend.Calls.ShouldAllBe(c => Equals(c.Args["filesystem"], "downloads"));
    }
}
```

(The fake glob payload must satisfy `FsResultContract.TryValidate` for `fs_glob`; the delete call will fail validation and return an error envelope — that's fine, the assertion is on captured args. If the base constructor null-`McpClient` is a problem, make the capturing subclass override happen before any client use — it does, since `CallToolAsync` is the only client touchpoint.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests --filter "FullyQualifiedName~McpFileSystemBackendFilesystemArgTests"`
Expected: FAIL — `KeyNotFoundException` on `"filesystem"`.

- [ ] **Step 3: Implement**

In `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs` the injection must live in the **callers**, not inside `CallToolAsync` (the unit test overrides `CallToolAsync` itself, and it must observe the already-injected args). Add a private helper and use it in all 12 call sites:

```csharp
private Dictionary<string, object?> WithFilesystem(Dictionary<string, object?> args)
{
    args["filesystem"] = filesystemName;
    return args;
}
```

and wrap each args literal, e.g.:

```csharp
public Task<FsResult<FsGlobResult>> GlobAsync(string basePath, string pattern, CancellationToken ct) =>
    CallTypedAsync<FsGlobResult>("fs_glob", WithFilesystem(new Dictionary<string, object?>
    {
        ["basePath"] = basePath,
        ["pattern"] = pattern
    }), ct);
```

Apply to: `ReadAsync`, `CreateAsync`, `EditAsync`, `GlobAsync`, `SearchAsync`, `MoveAsync`, `DeleteAsync`, `InfoAsync`, `ExecAsync`, `CopyAsync`, and both `fs_blob_read`/`fs_blob_write` call sites in `ReadChunksAsync`/`WriteChunksAsync`.

- [ ] **Step 4: Pin that existing servers tolerate the extra argument**

Add to `Tests/Integration/McpServerTests/McpSchedulingServerTests.cs` (uses the existing fixture):

```csharp
[Fact]
public async Task FsGlob_WithUnknownFilesystemArgument_IsIgnoredByServer()
{
    await using var client = await CreateClientAsync();   // reuse this file's existing client helper
    var result = await client.CallToolAsync("fs_glob", new Dictionary<string, object?>
    {
        ["pattern"] = "*",
        ["basePath"] = "/",
        ["filesystem"] = "schedules"
    });
    result.IsError.ShouldNotBe(true);
}
```

(Adapt the client-creation call to the test file's existing pattern. This pins the SDK behavior that unmatched arguments are ignored by `[McpServerTool]` binding — the safety assumption behind sending the arg unconditionally.)

- [ ] **Step 5: Run tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~McpFileSystemBackendFilesystemArgTests|FullyQualifiedName~McpSchedulingServerTests"`
Expected: unit PASS; scheduling integration PASS where Docker available (Redis container), baseline failure otherwise.

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: fs_* calls carry the target filesystem name"
```

---

### Task 9: Library server — fs tool routing + `filesystem://downloads` resource

**Files:**
- Create: `McpServerLibrary/McpTools/FsReadTool.cs`
- Create: `McpServerLibrary/McpTools/FsDeleteTool.cs`
- Modify: `McpServerLibrary/McpTools/FsGlobTool.cs`, `FsInfoTool.cs`, `FsMoveTool.cs`, `FsCopyTool.cs`, `FsBlobReadTool.cs`, `FsBlobWriteTool.cs`
- Modify: `McpServerLibrary/McpResources/FileSystemResource.cs`
- Test: `Tests/Unit/McpServerLibrary/LibraryFsRoutingTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using Domain.Tools.Downloads.Vfs;
using McpServerLibrary.McpTools;
using ModelContextProtocol.Protocol;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class LibraryFsRoutingTests
{
    // Build a DownloadsFileSystem over the same fakes as DownloadsFileSystemTests
    // (extract those fakes to Tests/Unit/Domain/Downloads/Vfs/DownloadFakes.cs and share).

    [Fact]
    public async Task FsRead_DownloadsFilesystem_ReadsStatus()
    {
        var (fs, client, _, _) = DownloadFakes.Build();
        client.Items.Add(DownloadFakes.Item(42));
        var result = await new FsReadTool(fs).McpRun("42/status.json", null, null, "downloads");
        ExtractText(result).ShouldContain("\"id\": 42");
    }

    [Fact]
    public async Task FsRead_WithoutDownloadsFilesystem_IsUnsupported()
    {
        var (fs, _, _, _) = DownloadFakes.Build();
        var result = await new FsReadTool(fs).McpRun("anything.txt", null, null, null);
        ExtractText(result).ShouldContain("unsupported_operation");
    }

    [Fact]
    public async Task FsDelete_DownloadsFilesystem_CleansUp()
    {
        var (fs, client, _, _) = DownloadFakes.Build();
        client.Items.Add(DownloadFakes.Item(42));
        var result = await new FsDeleteTool(fs).McpRun("42", "downloads");
        ExtractText(result).ShouldContain("removed");
        client.CleanedUp.ShouldBe([42]);
    }

    private static string ExtractText(CallToolResult result)
        => string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test Tests --filter "FullyQualifiedName~LibraryFsRoutingTests"`
Expected: FAIL — tools don't exist.

- [ ] **Step 3: Create the downloads-only tools**

`McpServerLibrary/McpTools/FsReadTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsReadTool(DownloadsFileSystem downloads)
{
    [McpServerTool(Name = "fs_read")]
    [Description("Read a downloads filesystem file (<id>/status.json)")]
    public async Task<CallToolResult> McpRun(
        string path, int? offset = null, int? limit = null, string? filesystem = null,
        CancellationToken ct = default)
        => filesystem == DownloadsFileSystem.Name
            ? ToolResponse.Create((await downloads.ReadAsync(path, offset, limit, ct)).ToNode())
            : ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "fs_read on the library server is only available for the downloads filesystem.",
                retryable: false));
}
```

`McpServerLibrary/McpTools/FsDeleteTool.cs` — same shape:

```csharp
using System.ComponentModel;
using Domain.Tools;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class FsDeleteTool(DownloadsFileSystem downloads)
{
    [McpServerTool(Name = "fs_delete")]
    [Description("Delete a download directory: cancels/removes the torrent task and cleans up its files")]
    public async Task<CallToolResult> McpRun(string path, string? filesystem = null, CancellationToken ct = default)
        => filesystem == DownloadsFileSystem.Name
            ? ToolResponse.Create((await downloads.DeleteAsync(path, ct)).ToNode())
            : ToolResponse.Create(ToolError.Create(
                ToolError.Codes.UnsupportedOperation,
                "fs_delete on the library server is only available for the downloads filesystem.",
                retryable: false));
}
```

(Check how scheduling's `FsReadTool` wraps the `FsResult` — it calls `ToolResponse.Create(await fs.ReadAsync(...))` directly; if `ToolResponse.Create` has an `FsResult<T>` overload use that instead of `.ToNode()` — mirror whatever scheduling does.)

- [ ] **Step 4: Route the existing media tools**

`FsGlobTool` and `FsInfoTool` serve BOTH filesystems — downloads via the engine, default via current media behavior:

```csharp
[McpServerToolType]
public class FsGlobTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath,
    DownloadsFileSystem downloads) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_glob")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string pattern,
        string basePath = "",
        string? filesystem = null,
        CancellationToken cancellationToken = default)
        => filesystem == DownloadsFileSystem.Name
            ? ToolResponse.Create((await downloads.GlobAsync(basePath, pattern, cancellationToken)).ToNode())
            : ToolResponse.Create(await Run(pattern, cancellationToken, basePath));
}
```

`FsInfoTool` — same guard, delegating to `downloads.InfoAsync(path, ct)` for the downloads branch, the existing base call otherwise (its `McpRun` is currently sync — make it `async Task<CallToolResult>`).

`FsMoveTool`, `FsCopyTool`, `FsBlobReadTool`, `FsBlobWriteTool` — media-only; add the parameter and reject the downloads branch with this exact guard at the top of each `McpRun` (keep the rest of each method unchanged):

```csharp
if (filesystem == DownloadsFileSystem.Name)
{
    return ToolResponse.Create(ToolError.Create(
        ToolError.Codes.UnsupportedOperation,
        "The downloads filesystem does not support this operation.",
        retryable: false));
}
```

(Sync `McpRun`s gain no async; just return the error result. Add `string? filesystem = null` as the LAST optional parameter before any `CancellationToken`.)

- [ ] **Step 5: Add the downloads filesystem resource**

In `McpServerLibrary/McpResources/FileSystemResource.cs` add a second method to the existing class:

```csharp
[McpServerResource(
    UriTemplate = "filesystem://downloads",
    Name = "Downloads Filesystem",
    MimeType = "application/json")]
[Description("Active downloads exposed as a filesystem")]
public string GetDownloadsInfo()
{
    return JsonSerializer.Serialize(new
    {
        name = "downloads",
        mountPoint = "/downloads",
        description = "Active torrent downloads. Each download is a directory /downloads/<id>/ with a " +
                      "read-only status.json (state, progress, eta, savePath). Deleting /downloads/<id> " +
                      "cancels the download, removes the torrent task, and cleans up its files. " +
                      "Read-only otherwise; downloads are started with the download_file tool."
    });
}
```

- [ ] **Step 6: Run tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~LibraryFsRoutingTests"`
Expected: PASS. (Full build still has the old subscription code — untouched until Task 11.)

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "feat: library server routes fs tools to downloads VFS and exposes filesystem://downloads"
```

---

### Task 10: Library channel role — emitter, planner, watcher, channel tools

**Files:**
- Create: `McpServerLibrary/Services/IDownloadNotificationEmitter.cs`
- Create: `McpServerLibrary/Services/DownloadNotificationEmitter.cs`
- Create: `McpServerLibrary/Services/DownloadCompletionPlanner.cs`
- Create: `McpServerLibrary/Services/DownloadCompletionWatcher.cs`
- Create: `McpServerLibrary/McpTools/SendReplyTool.cs`, `McpServerLibrary/McpTools/RequestApprovalTool.cs`, `McpServerLibrary/McpTools/RegisterAgentsTool.cs`
- Modify: `McpServerLibrary/Settings/McpSettings.cs`
- Test: `Tests/Unit/McpServerLibrary/DownloadCompletionPlannerTests.cs`, `Tests/Unit/McpServerLibrary/DownloadCompletionWatcherTests.cs`

- [ ] **Step 1: Write the failing planner tests**

```csharp
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpServerLibrary.Services;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class DownloadCompletionPlannerTests
{
    [Fact]
    public void BuildPayload_TargetsTheOriginatingConversation()
    {
        var routing = new DownloadRouting
        {
            DownloadId = 42,
            Title = "The Lost City of Z 1080p",
            Context = new ConversationContext("jack", "conv-7", "fran",
                new ReplyTarget("signalr", "conv-7"))
        };
        var item = new DownloadItem
        {
            Id = 42, Title = "42", Link = "magnet:x", State = DownloadState.Completed,
            Progress = 1, DownSpeed = 0, UpSpeed = 0, Eta = 0, SavePath = "/downloads/42", Size = 700
        };

        var payload = DownloadCompletionPlanner.BuildPayload(routing, item);

        payload.ConversationId.ShouldBe("conv-7");
        payload.AgentId.ShouldBe("jack");
        payload.Sender.ShouldBe("fran");
        payload.ReplyTo.ShouldBe([new ReplyTarget("signalr", "conv-7")]);
        payload.Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Download, null));
        payload.Content.ShouldContain("The Lost City of Z 1080p");
        payload.Content.ShouldContain("42");
        payload.Content.ShouldContain("/downloads/42");
    }
}
```

- [ ] **Step 2: Write the failing watcher tests** (mirror `Tests/Unit/McpServerScheduling/ScheduleDispatcherServiceTests.cs` style — test `SweepAsync` directly with fakes)

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using McpServerLibrary.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpServerLibrary;

public class DownloadCompletionWatcherTests
{
    private sealed class FakeEmitter : IDownloadNotificationEmitter
    {
        public bool HasActiveSessions { get; set; } = true;
        public bool EmitResult { get; set; } = true;
        public List<ChannelMessageNotification> Emitted { get; } = [];

        public Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default)
        {
            Emitted.Add(payload);
            return Task.FromResult(EmitResult);
        }
    }

    // Reuse FakeDownloadClient/FakeRoutingStore from DownloadFakes (Task 9 extraction).

    private static DownloadRouting Routing(int id) => new()
    {
        DownloadId = id, Title = $"Title {id}",
        Context = new ConversationContext("jack", $"conv-{id}", "fran", new ReplyTarget("signalr", $"conv-{id}"))
    };

    [Fact]
    public async Task Sweep_CompletedDownload_EmitsAndRemovesEntry()
    {
        var (client, store, emitter) = Build();
        client.Items.Add(DownloadFakes.Item(42, DownloadState.Completed));
        store.Entries.Add(Routing(42));

        await Watcher(client, store, emitter).SweepAsync(CancellationToken.None);

        emitter.Emitted.ShouldHaveSingleItem().ConversationId.ShouldBe("conv-42");
        store.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_InProgressDownload_DoesNothing()
    {
        var (client, store, emitter) = Build();
        client.Items.Add(DownloadFakes.Item(42, DownloadState.InProgress));
        store.Entries.Add(Routing(42));

        await Watcher(client, store, emitter).SweepAsync(CancellationToken.None);

        emitter.Emitted.ShouldBeEmpty();
        store.Entries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Sweep_EmitFails_RetainsEntryForRetry()
    {
        var (client, store, emitter) = Build();
        emitter.EmitResult = false;
        client.Items.Add(DownloadFakes.Item(42, DownloadState.Completed));
        store.Entries.Add(Routing(42));

        await Watcher(client, store, emitter).SweepAsync(CancellationToken.None);

        store.Entries.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Sweep_VanishedTorrent_DropsEntrySilently()
    {
        var (client, store, emitter) = Build();
        store.Entries.Add(Routing(42));

        await Watcher(client, store, emitter).SweepAsync(CancellationToken.None);

        emitter.Emitted.ShouldBeEmpty();
        store.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sweep_NoActiveSessions_DoesNotTouchAnything()
    {
        var (client, store, emitter) = Build();
        emitter.HasActiveSessions = false;
        client.Items.Add(DownloadFakes.Item(42, DownloadState.Completed));
        store.Entries.Add(Routing(42));

        await Watcher(client, store, emitter).SweepAsync(CancellationToken.None);

        emitter.Emitted.ShouldBeEmpty();
        store.Entries.ShouldHaveSingleItem();
    }

    private static (DownloadFakes.FakeDownloadClient, DownloadFakes.FakeRoutingStore, FakeEmitter) Build()
        => (new DownloadFakes.FakeDownloadClient(), new DownloadFakes.FakeRoutingStore(), new FakeEmitter());

    private static DownloadCompletionWatcher Watcher(
        IDownloadClient client, IDownloadRoutingStore store, IDownloadNotificationEmitter emitter)
        => new(store, client, emitter, new McpServerLibrary.Settings.McpSettings
        {
            Jackett = new() { ApiKey = "", ApiUrl = "http://x" },
            QBittorrent = new() { ApiUrl = "http://x", UserName = "", Password = "" },
            DownloadLocation = "/downloads",
            BaseLibraryPath = "/media",
            RedisConnectionString = "unused"
        }, NullLogger<DownloadCompletionWatcher>.Instance);
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test Tests --filter "FullyQualifiedName~DownloadCompletion"`
Expected: FAIL — types don't exist.

- [ ] **Step 4: Settings additions**

In `McpServerLibrary/Settings/McpSettings.cs` add to the record:

```csharp
public required string RedisConnectionString { get; init; }
public int CompletionPollSeconds { get; init; } = 5;
```

- [ ] **Step 5: Implement emitter (clone of `ScheduleNotificationEmitter`)**

`McpServerLibrary/Services/IDownloadNotificationEmitter.cs`:

```csharp
using Domain.DTOs.Channel;

namespace McpServerLibrary.Services;

public interface IDownloadNotificationEmitter
{
    bool HasActiveSessions { get; }
    Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default);
}
```

`McpServerLibrary/Services/DownloadNotificationEmitter.cs` — copy `McpServerScheduling/Services/ScheduleNotificationEmitter.cs` verbatim minus the static `BuildPayload`, renamed:

```csharp
using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpServerLibrary.Services;

public sealed class DownloadNotificationEmitter(ILogger<DownloadNotificationEmitter> logger)
    : IDownloadNotificationEmitter
{
    private readonly ConcurrentDictionary<string, McpServer> _activeSessions = new();

    public void RegisterSession(string sessionId, McpServer server)
    {
        _activeSessions[sessionId] = server;
        logger.LogInformation("MCP session registered: {SessionId}", sessionId);
    }

    public void UnregisterSession(string sessionId)
    {
        _activeSessions.TryRemove(sessionId, out _);
        logger.LogInformation("MCP session unregistered: {SessionId}", sessionId);
    }

    public bool HasActiveSessions => !_activeSessions.IsEmpty;

    public async Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default)
    {
        var tasks = _activeSessions.Values.Select(async server =>
        {
            try
            {
                await server.SendNotificationAsync(
                    ChannelProtocol.MessageNotification, payload, ChannelProtocol.SerializerOptions, ct);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to emit channel/message notification");
                return false;
            }
        });

        var results = await Task.WhenAll(tasks);
        return Array.Exists(results, delivered => delivered);
    }
}
```

(`ChannelProtocol.SerializerOptions` carries the mandatory `TypeInfoResolver` — without it the SDK's `MakeReadOnly()` throws and notifications are silently dropped.)

- [ ] **Step 6: Implement planner**

`McpServerLibrary/Services/DownloadCompletionPlanner.cs`:

```csharp
using Domain.DTOs;
using Domain.DTOs.Channel;

namespace McpServerLibrary.Services;

public static class DownloadCompletionPlanner
{
    public static ChannelMessageNotification BuildPayload(DownloadRouting routing, DownloadItem item) => new()
    {
        ConversationId = routing.Context.ConversationId,
        Sender = routing.Context.UserId,
        Content = BuildPrompt(routing, item),
        AgentId = routing.Context.AgentId,
        ReplyTo = [routing.Context.Origin],
        Origin = new MessageOrigin(MessageOriginKind.Download, null),
        Timestamp = DateTimeOffset.UtcNow
    };

    private static string BuildPrompt(DownloadRouting routing, DownloadItem item) =>
        $"""
         [download-complete] Download '{routing.Title}' (id {routing.DownloadId}) has finished downloading to {item.SavePath}.
         Inform the user their download is ready and carry out any follow-up steps you promised for it (e.g. organizing it into the library).
         """;
}
```

- [ ] **Step 7: Implement watcher**

`McpServerLibrary/Services/DownloadCompletionWatcher.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpServerLibrary.Services;

public sealed class DownloadCompletionWatcher(
    IDownloadRoutingStore store,
    IDownloadClient client,
    IDownloadNotificationEmitter emitter,
    McpSettings settings,
    ILogger<DownloadCompletionWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, settings.CompletionPollSeconds));
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error sweeping downloads for completion");
            }

            try
            { await Task.Delay(interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    internal async Task SweepAsync(CancellationToken ct)
    {
        if (!emitter.HasActiveSessions)
        {
            return;
        }

        var entries = await store.ListAsync(ct);
        if (entries.Count == 0)
        {
            return;
        }

        var items = (await client.GetDownloadItems(ct)).ToDictionary(i => i.Id);
        foreach (var entry in entries)
        {
            if (!items.TryGetValue(entry.DownloadId, out var item))
            {
                await store.RemoveAsync(entry.DownloadId, ct);
                continue;
            }

            if (item.State is not DownloadState.Completed)
            {
                continue;
            }

            if (!await emitter.EmitAsync(DownloadCompletionPlanner.BuildPayload(entry, item), ct))
            {
                logger.LogWarning(
                    "No active session received completion for download {DownloadId}; will retry", entry.DownloadId);
                continue;
            }

            await store.RemoveAsync(entry.DownloadId, ct);
            logger.LogInformation("Emitted completion for download {DownloadId} ('{Title}')", entry.DownloadId, entry.Title);
        }
    }
}
```

- [ ] **Step 8: Channel-protocol tools** — copy the three scheduling tool files, adjusting namespace and descriptions; the library has no agent catalog or inbound surface:

`McpServerLibrary/McpTools/SendReplyTool.cs`:

```csharp
using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Receive a reply chunk — the library channel has no inbound surface; chunks are dropped")]
    public static string McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID")] string? messageId)
        => "ok";
}
```

`McpServerLibrary/McpTools/RequestApprovalTool.cs`:

```csharp
using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request tool approval — the library channel auto-approves all tools")]
    public static string McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests)
        => mode == ApprovalMode.Notify ? "notified" : "approved";
}
```

`McpServerLibrary/McpTools/RegisterAgentsTool.cs`:

```csharp
using System.ComponentModel;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool
{
    [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
    [Description("Register the agent catalog — the library channel does not need it; accepted and ignored")]
    public static string McpRun([Description("Agent catalog")] IReadOnlyList<AgentCatalogEntry> agents)
        => $"registered {agents.Count} agents";
}
```

- [ ] **Step 9: Run tests**

Run: `dotnet test Tests --filter "FullyQualifiedName~DownloadCompletion"`
Expected: PASS (planner + 5 watcher tests).

- [ ] **Step 10: Commit**

```bash
git add -A && git commit -m "feat: library channel role - completion watcher, emitter, planner, channel tools"
```

---

### Task 11: Library `ConfigModule` rewire, subscription deletions, prompt update

**Files:**
- Modify: `McpServerLibrary/Modules/ConfigModule.cs`
- Delete: `McpServerLibrary/ResourceSubscriptions/` (all 3 files), `McpServerLibrary/McpResources/McpDownloadResource.cs`, `McpServerLibrary/McpTools/McpGetDownloadStatusTool.cs`, `McpServerLibrary/McpTools/McpCleanupDownloadTool.cs`, `McpServerLibrary/McpTools/McpResubscribeDownloadsTool.cs`
- Delete: `Domain/Resources/DownloadResource.cs`, `Domain/Tools/Downloads/GetDownloadStatusTool.cs`, `Domain/Tools/Downloads/CleanupDownloadTool.cs`, `Domain/Tools/Downloads/ResubscribeDownloadsTool.cs`, `Domain/Contracts/ITrackedDownloadsManager.cs`, `Infrastructure/StateManagers/TrackedDownloadsManager.cs`
- Delete tests: `Tests/Unit/McpServerLibrary/SubscriptionTrackerTests.cs`, `Tests/Unit/Domain/ResubscribeDownloadsToolTests.cs`, `Tests/Integration/McpServerTests/SubscriptionMonitorTests.cs`, `Tests/Integration/McpTools/ResubscribeDownloadsToolTests.cs`
- Modify: `Tests/Unit/Domain/StateManagerTests.cs` (drop the `TrackedDownloadsManager` cases), `Domain/Prompts/DownloaderPrompt.cs`

- [ ] **Step 1: Rewrite `ConfigModule.ConfigureMcp`**

```csharp
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Downloads.Vfs;
using Infrastructure.Extensions;
using Infrastructure.StateManagers;
using Infrastructure.Utils;
using McpServerLibrary.McpPrompts;
using McpServerLibrary.McpResources;
using McpServerLibrary.McpTools;
using McpServerLibrary.Services;
using McpServerLibrary.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace McpServerLibrary.Modules;

public static class ConfigModule
{
    public static McpSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<McpSettings>();
        return settings ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        var emitter = new DownloadNotificationEmitter(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<DownloadNotificationEmitter>());

        services
            .AddMemoryCache()
            .AddSingleton(settings)
            .AddSingleton(emitter)
            .AddSingleton<IDownloadNotificationEmitter>(emitter)
            .AddTransient<DownloadPathConfig>(_ => new DownloadPathConfig(settings.DownloadLocation))
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.BaseLibraryPath))
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(settings.RedisConnectionString))
            .AddSingleton<IDownloadRoutingStore, RedisDownloadRoutingStore>()
            .AddSingleton<ISearchResultsManager, SearchResultsManager>()
            .AddJacketClient(settings)
            .AddQBittorrentClient(settings)
            .AddFileSystemClient()
            .AddSingleton<DownloadsFileSystem>()
            .AddHostedService<DownloadCompletionWatcher>()
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
#pragma warning disable MCPEXP002 // RunSessionHandler is experimental
                options.RunSessionHandler = async (_, server, ct) =>
                {
                    var sessionId = server.SessionId ?? Guid.NewGuid().ToString();
                    emitter.RegisterSession(sessionId, server);
                    try
                    {
                        await server.RunAsync(ct);
                    }
                    finally
                    {
                        emitter.UnregisterSession(sessionId);
                    }
                };
#pragma warning restore MCPEXP002
            })
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                    logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    return ToolResponse.Create(ex);
                }
            }))
            // Download tools
            .WithTools<McpFileSearchTool>()
            .WithTools<McpFileDownloadTool>()
            .WithTools<McpContentRecommendationTool>()
            // Channel-protocol tools (invoked by the agent's channel connection, hidden from the LLM)
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            // Filesystem backend tools
            .WithTools<FsGlobTool>()
            .WithTools<FsReadTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsInfoTool>()
            .WithTools<FsCopyTool>()
            .WithTools<FsBlobReadTool>()
            .WithTools<FsBlobWriteTool>()
            // Prompts
            .WithPrompts<McpSystemPrompt>()
            // Resources
            .WithResources<FileSystemResource>();

        return services;
    }
}
```

Notes: the custom `WithSubscribeToResourcesHandler`/`WithUnsubscribeFromResourcesHandler`/`WithListResourcesHandler` registrations are GONE — the SDK's default attribute-based resource listing now serves `filesystem://media` + `filesystem://downloads`, which is exactly what `McpFileSystemDiscovery` needs. `server.SessionId` replaces `StateKey` in the session handler (mirroring scheduling); the per-session `SearchResultsManager` keying via `StateKey` inside tools is unchanged.

- [ ] **Step 2: Delete the dead files**

```bash
git rm -r McpServerLibrary/ResourceSubscriptions
git rm McpServerLibrary/McpResources/McpDownloadResource.cs \
       McpServerLibrary/McpTools/McpGetDownloadStatusTool.cs \
       McpServerLibrary/McpTools/McpCleanupDownloadTool.cs \
       McpServerLibrary/McpTools/McpResubscribeDownloadsTool.cs \
       Domain/Resources/DownloadResource.cs \
       Domain/Tools/Downloads/GetDownloadStatusTool.cs \
       Domain/Tools/Downloads/CleanupDownloadTool.cs \
       Domain/Tools/Downloads/ResubscribeDownloadsTool.cs \
       Domain/Contracts/ITrackedDownloadsManager.cs \
       Infrastructure/StateManagers/TrackedDownloadsManager.cs \
       Tests/Unit/McpServerLibrary/SubscriptionTrackerTests.cs \
       Tests/Unit/Domain/ResubscribeDownloadsToolTests.cs \
       Tests/Integration/McpServerTests/SubscriptionMonitorTests.cs \
       Tests/Integration/McpTools/ResubscribeDownloadsToolTests.cs
```

Then `dotnet build` and chase every remaining reference (e.g. `Tests/Unit/Domain/StateManagerTests.cs` TrackedDownloadsManager cases, `McpLibraryServerFixture` registrations, any `GetDownloadStatusTool` usages in tests). `Domain/Resources/` may now be empty — remove the folder if so.

- [ ] **Step 3: Update the prompt**

In `Domain/Prompts/DownloaderPrompt.cs` (`AgentSystemPrompt`), make these surgical edits:

1. **Phase 3 intro** — replace
   `You will be notified by the system when a download is complete. **DO NOT** attempt to organize a file until you receive this `download_finished` notification.`
   with:
   `When a download finishes, a [download-complete] message arrives in this conversation telling you the download id and its location. **DO NOT** attempt to organize a file before that message arrives. You can check progress at any time by reading /downloads/<id>/status.json.`

2. **Phase 4** — replace the `*   **Clean Up:** Call the **cleanup tool** ...` bullet with:
   `*   **Clean Up:** Delete the download's directory in the downloads filesystem (fs_delete on /downloads/<id>). This removes the torrent task and any leftover files in the download directory in one step.`

3. **Status Report bullet** — replace
   `you must reply with a report for all active downloads, including: name, progress (%), speed, total size, and ETA.`
   with:
   `you must reply with a report for all active downloads, including: name, progress (%), speed, total size, and ETA. Get this by globbing /downloads/*/status.json and reading each file.`

4. **Abandon Ship bullet** — replace `This means executing **Phase 4** for every task in progress (cleanup task first, then cleanup directory).` with `This means deleting /downloads/<id> for every task in progress.`

- [ ] **Step 4: Build + run library unit suites**

Run: `dotnet build && dotnet test Tests --filter "FullyQualifiedName~McpServerLibrary|FullyQualifiedName~FileDownloadToolTests|FullyQualifiedName~DownloadsFileSystemTests"`
Expected: clean build; PASS (Docker-dependent integration excluded — next task handles fixtures).

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "refactor: library server dual-role wiring; delete resource-subscription system"
```

---

### Task 12: Agent-side subscription machinery removal

**Files:**
- Delete: `Infrastructure/Agents/Mcp/McpSubscriptionManager.cs`, `Infrastructure/Agents/Mcp/ResourceUpdateProcessor.cs`, `Infrastructure/Agents/Mcp/McpResourceManager.cs`
- Modify: `Infrastructure/Agents/McpAgent.cs`, `Infrastructure/Agents/ThreadSession.cs`, `Infrastructure/Agents/MultiAgentFactory.cs`
- Delete: `Tests/Integration/Agents/McpSubscriptionManagerTests.cs`
- Modify: `Tests/Integration/Agents/ThreadSessionTests.cs`, `Tests/Integration/Fixtures/ThreadSessionServerFixture.cs` (drop subscription-specific setup/assertions)

- [ ] **Step 1: Simplify `McpAgent`**

Replace `RunCoreStreamingInnerAsync` (and DELETE `RunStreamingCoreAsync` entirely):

```csharp
private async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingInnerAsync(
    IEnumerable<ChatMessage> messages,
    AgentSession? thread = null,
    AgentRunOptions? options = null,
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
    thread ??= await CreateSessionAsync(cancellationToken);
    var session = await GetOrCreateSessionAsync(thread, cancellationToken);

    var messageList = messages as IReadOnlyList<ChatMessage> ?? messages.ToList();
    var conversationContext = messageList
        .Select(m => m.GetConversationContext())
        .FirstOrDefault(c => c is not null);
    options ??= CreateRunOptions(session, conversationContext);

    await foreach (var update in _innerAgent.RunStreamingAsync(messageList, thread, options, cancellationToken))
    {
        yield return update;
    }
}
```

Remove the `_enableResourceSubscriptions` field, the `enableResourceSubscriptions` constructor parameter, and its pass-through in `GetOrCreateSessionAsync`.

- [ ] **Step 2: Simplify `ThreadSession`**

- `ThreadSessionData`: drop `McpResourceManager? ResourceManager`.
- `ThreadSession`: drop the `ResourceManager` property and its disposal block.
- `CreateAsync`/`ThreadSessionBuilder.BuildAsync`: drop the `enableResourceSubscriptions` parameter, Step 5 (`CreateResourceManagerAsync`), and the `CreateResourceManagerAsync` method.

- [ ] **Step 3: Simplify `MultiAgentFactory`**

Remove `enableResourceSubscriptions: false` from `CreateSubAgent`'s `McpAgent` construction (the parameter no longer exists). `CreateFromDefinition` needs no change beyond compiling.

- [ ] **Step 4: Delete the three files + their test file**

```bash
git rm Infrastructure/Agents/Mcp/McpSubscriptionManager.cs \
       Infrastructure/Agents/Mcp/ResourceUpdateProcessor.cs \
       Infrastructure/Agents/Mcp/McpResourceManager.cs \
       Tests/Integration/Agents/McpSubscriptionManagerTests.cs
```

- [ ] **Step 5: Fix remaining references**

`dotnet build` and chase: `ThreadSessionTests` / `ThreadSessionServerFixture` reference subscriptions (grep `ResourceManager|enableResourceSubscriptions|Subscribe` under `Tests/`); remove those assertions/setup, keep the rest of the session tests intact.

- [ ] **Step 6: Run agent suites**

Run: `dotnet build && dotnet test Tests --filter "FullyQualifiedName~ChatMonitor|FullyQualifiedName~ConversationContextMeta|FullyQualifiedName~McpAgent"`
Expected: clean build, PASS.

- [ ] **Step 7: Commit**

```bash
git add -A && git commit -m "refactor: remove agent-side MCP resource-subscription machinery"
```

---

### Task 13: Configuration — agent channel endpoint, jack features, library settings, compose

**Files:**
- Modify: `Agent/appsettings.json`
- Modify: `McpServerLibrary/appsettings.json`, `McpServerLibrary/appsettings.Local.json`
- Modify: `DockerCompose/docker-compose.yml`

- [ ] **Step 1: Agent config**

In `Agent/appsettings.json`:

1. Add to `channelEndpoints` (after the scheduling entry):

```json
{
    "channelId": "library",
    "endpoint": "http://mcp-library:8080/mcp"
}
```

2. In the `jack` agent definition, extend `enabledFeatures` so the downloads VFS read/delete domain tools are exposed:

```json
"enabledFeatures": [
    "filesystem.glob",
    "filesystem.move",
    "filesystem.info",
    "filesystem.read",
    "filesystem.remove"
]
```

(Verify the exact feature-key strings: keys are declared as `VfsTextReadTool.Key = "read"` / `VfsRemoveTool.Key = "remove"` and the existing config uses the `filesystem.` prefix — match whatever parser maps `enabledFeatures` to `FeatureConfig.EnabledTools`; the existing three entries are the template.)

Check `Agent/appsettings.Development.json` (if it exists) for a parallel `channelEndpoints` list and mirror the addition.

- [ ] **Step 2: Library config**

`McpServerLibrary/appsettings.json` — add:

```json
"RedisConnectionString": "redis:6379",
"CompletionPollSeconds": 5,
```

`McpServerLibrary/appsettings.Local.json` — add:

```json
"RedisConnectionString": "localhost:6379",
```

- [ ] **Step 3: Compose**

In `DockerCompose/docker-compose.yml`, `mcp-library` service: change `depends_on` to include Redis with health condition (matching `mcp-scheduling`'s shape):

```yaml
    depends_on:
      qbittorrent:
        condition: service_started
      jackett:
        condition: service_started
      base-sdk:
        condition: service_started
      redis:
        condition: service_healthy
```

(The current list-form `depends_on` must become map-form. No `.env` change — the Redis connection string is non-secret and lives in appsettings, same as scheduling.)

- [ ] **Step 4: Build + commit**

Run: `dotnet build`
Expected: clean.

```bash
git add -A && git commit -m "config: library dual-role endpoints, jack fs features, redis dependency"
```

---

### Task 14: Routing pin-tests, integration fixture rework, full verification

**Files:**
- Modify: `Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs` (or new `ChatMonitorDownloadRoutingTests.cs`)
- Modify: `Tests/Unit/Domain/MonitorTests.cs` (BuildScheduleEvent pin)
- Modify: `Tests/Integration/Fixtures/McpLibraryServerFixture.cs`, `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`

- [ ] **Step 1: Pin download-shaped routing in ChatMonitor (tests only — behavior should already hold)**

```csharp
[Fact]
public async Task ResolveDeliveryTargets_DownloadCompletion_UsesConcreteConversationWithoutMinting()
{
    var origin = Channel("library");
    var signalr = Channel("signalr");
    var msg = new ChannelMessage
    {
        ConversationId = "conv-7", Content = "[download-complete] ...", Sender = "fran",
        ChannelId = "library", AgentId = "jack",
        Origin = new MessageOrigin(MessageOriginKind.Download, null),
        ReplyTo = [new ReplyTarget("signalr", "conv-7")]
    };

    var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, [origin, signalr], CancellationToken.None);

    var target = targets.ShouldHaveSingleItem();
    target.ConversationId.ShouldBe("conv-7");
    target.Channel.ChannelId.ShouldBe("signalr");
    ((FakeChannelConnection)signalr).CreatedConversations.ShouldBeEmpty();   // no minting
}

[Fact]
public async Task ResolveDeliveryTargets_VoiceOriginDownload_CarriesSatelliteAddress()
{
    var origin = Channel("library");
    var voice = Channel("voice");
    var msg = new ChannelMessage
    {
        ConversationId = "conv-9", Content = "[download-complete] ...", Sender = "fran",
        ChannelId = "library", AgentId = "jack",
        ReplyTo = [new ReplyTarget("voice", "conv-9", "fran-office-01")]
    };

    var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, [origin, voice], CancellationToken.None);

    targets.ShouldHaveSingleItem().ConversationId.ShouldBe("conv-9");
}

[Fact]
public void BuildScheduleEvent_DownloadOrigin_ProducesNoScheduleMetric()
{
    var msg = MonitorTestMocks.CreateChannelMessage(conversationId: "c", channelId: "library", agentId: "jack")
        with { Origin = new MessageOrigin(MessageOriginKind.Download, null) };
    ChatMonitor.BuildScheduleEvent(msg, 100, true, null).ShouldBeNull();
}
```

Also pin the wire round-trip of the new payload shape in `Tests/Unit/Domain/Channel/ChannelProtocolTests.cs` (mirror the existing round-trip test):

```csharp
[Fact]
public void DownloadCompletionNotification_RoundTripsThroughChannelProtocol()
{
    var payload = new ChannelMessageNotification
    {
        ConversationId = "conv-7",
        Sender = "fran",
        Content = "[download-complete] ...",
        AgentId = "jack",
        ReplyTo = [new ReplyTarget("signalr", "conv-7")],
        Origin = new MessageOrigin(MessageOriginKind.Download, null),
        Timestamp = DateTimeOffset.UtcNow
    };

    var element = System.Text.Json.JsonSerializer.SerializeToElement(payload, ChannelProtocol.SerializerOptions);
    var restored = ChannelProtocol.Deserialize<ChannelMessageNotification>(element).ShouldNotBeNull();

    restored.Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Download, null));
    restored.ReplyTo.ShouldBe([new ReplyTarget("signalr", "conv-7")]);
    element.GetProperty("origin").GetProperty("kind").GetString().ShouldBe("Download");   // string enum on the wire
}
```

(Adapt the `Channel(...)` helper to this file's existing helper.)

- [ ] **Step 2: Run**

Run: `dotnet test Tests --filter "FullyQualifiedName~ChatMonitor"`
Expected: PASS without production changes. If the concrete-conversation case fails, the bug is in `ResolveDeliveryTargetsAsync` — fix there, nothing else.

- [ ] **Step 3: Rework the library integration fixture**

`Tests/Integration/Fixtures/McpLibraryServerFixture.cs`: replace the old tool registrations with the new server shape — drop `ITrackedDownloadsManager`/subscription handlers/`McpDownloadResource`/status/cleanup/resubscribe tools, add `DownloadsFileSystem`, the in-memory `FakeDownloadRoutingStore` (registered as `IDownloadRoutingStore` — keeps the fixture free of a Redis container), `FsReadTool`/`FsDeleteTool`/channel tools, and `.WithResources<FileSystemResource>()`. Update `Tests/Integration/McpServerTests/McpLibraryServerTests.cs`: remove tests for deleted tools (`get_download_status`, `download_cleanup`, `resubscribe_downloads`, `download://` resources); add a journey test (Docker-gated like its siblings): `download_file` with `Meta` (use `client.CallToolAsync(new CallToolRequestParams { Name = "download_file", Arguments = ..., Meta = ... }, ct)`) → routing entry recorded → `fs_glob`/`fs_read` of `/downloads/<id>/status.json` with `["filesystem"] = "downloads"` → `fs_delete` cancels.

- [ ] **Step 4: Full verification**

```bash
dotnet build
dotnet test Tests --filter "Category!=E2E"
```

Expected: build clean. Test failures must be ONLY the pre-existing `DockerUnavailableException` baseline (~148 in this environment) — diff the failure list against a `master` run if unsure. Zero new non-Docker failures.

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "test: pin download completion routing; rework library integration fixture"
```

---

## Out of scope (explicitly deferred)

- Failure/stall alerts (`DownloadState.Failed`) — only `Completed` fires, matching today's semantics; routing-store TTL bounds leakage.
- Per-conversation turn serialization — unblocked by this refactor (the stream merge is gone) but a separate effort.
- Subagent-initiated downloads carry no conversation context (their messages aren't stamped) — the tool reports that no alert will fire; acceptable because jack is a primary agent.
- Extracting a shared dual-role channel-server scaffolding (emitter duplication with scheduling is ~60 lines).

## Verification checklist for the final review

1. `notifications/resources/updated` no longer appears anywhere outside git history: `grep -rn "resources/updated" --include="*.cs" .` → empty.
2. `McpResubscribeDownloadsTool`, `SubscriptionMonitor`, `McpResourceManager` → no hits.
3. The LLM-visible tool list for jack: `download_file`, `file_search`, `content_recommendation` + domain fs tools; `send_reply`/`request_approval`/`register_agents` stripped by `ThreadSession.FilterMcpTools` (suffix match — already covers the library server).
4. `ChannelProtocol.SerializerOptions` used on BOTH serialize (emitter, meta build) and deserialize (meta parse, channel connection) sides.
