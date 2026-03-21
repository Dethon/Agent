# MCP Channel Protocol Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract transport logic (Telegram, WebChat, ServiceBus) into independent MCP channel servers so the agent core sees a single unified message flow.

**Architecture:** Each transport becomes an MCP server exposing a standard channel protocol (one notification type, two tools). ChatMonitor merges message streams from all channels and routes responses back via tool calls. New transports require zero agent changes.

**Tech Stack:** .NET 10, ModelContextProtocol 1.1.0, ModelContextProtocol.AspNetCore, System.Threading.Channels, Redis, SignalR, Telegram.Bot, Azure.Messaging.ServiceBus

**Spec:** `docs/superpowers/specs/2026-03-21-mcp-channel-protocol-design.md`

---

## File Structure

### New files (Domain)
| File | Purpose |
|------|---------|
| `Domain/Contracts/IChannelConnection.cs` | Channel abstraction — messages in, replies + approvals out |
| `Domain/DTOs/ChannelMessage.cs` | Inbound message from a channel |
| `Domain/DTOs/ReplyContentType.cs` | Content type enum for send_reply |

### New files (Infrastructure)
| File | Purpose |
|------|---------|
| `Infrastructure/Clients/Channels/McpChannelConnection.cs` | MCP client wrapper implementing IChannelConnection |
| `Infrastructure/Clients/Channels/ChannelToolApprovalHandler.cs` | Delegates approval to originating channel |

### New projects
| Project | Purpose |
|---------|---------|
| `McpChannelSignalR/` | WebChat/SignalR channel server |
| `McpChannelTelegram/` | Telegram bot channel server |
| `McpChannelServiceBus/` | Azure Service Bus channel server |

### Modified files
| File | Change |
|------|--------|
| `Domain/Agents/AgentKey.cs` | `(long, long, string?)` → `(string, string?)` |
| `Domain/Monitor/ChatMonitor.cs` | Use `IReadOnlyList<IChannelConnection>` instead of `IChatMessengerClient` |
| `Domain/Monitor/ScheduleExecutor.cs` | Use `IChannelConnection` instead of `IChatMessengerClient` |
| `Domain/Agents/ChatThreadResolver.cs` | Works with new AgentKey |
| `Infrastructure/Agents/MultiAgentFactory.cs` | Accept `IToolApprovalHandler` directly, remove factory |
| `Infrastructure/StateManagers/RedisThreadStateStore.cs` | Dual-read fallback for key migration |
| `Agent/Modules/InjectorModule.cs` | Wire channels instead of messenger clients |
| `DockerCompose/docker-compose.yml` | Add channel containers |

### Files to delete (after migration)
| File | Reason |
|------|--------|
| `Domain/Contracts/IChatMessengerClient.cs` | Replaced by IChannelConnection |
| `Domain/Contracts/IToolApprovalHandlerFactory.cs` | Replaced by ChannelToolApprovalHandler |
| `Domain/DTOs/MessageSource.cs` | Replaced by ChannelId string |
| `Domain/DTOs/ChatPrompt.cs` | Replaced by ChannelMessage |
| `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs` | ChatMonitor multiplexes directly |
| `Domain/Routers/MessageSourceRouter.cs` | Routing by ChannelId in ChatMonitor |
| `Domain/Contracts/IMessageSourceRouter.cs` | Interface for removed router |
| `Infrastructure/Clients/Messaging/Cli/` | Removed (unused) |
| `Infrastructure/Clients/Messaging/WebChat/` | Entire directory — migrated to McpChannelSignalR |
| `Infrastructure/Clients/Messaging/Telegram/` | Entire directory — migrated to McpChannelTelegram |
| `Infrastructure/Clients/Messaging/ServiceBus/` | Entire directory — migrated to McpChannelServiceBus |
| `Infrastructure/Clients/ToolApproval/` | Entire directory — all handlers and factories replaced by ChannelToolApprovalHandler |

---

## Phase 1: Core Channel Infrastructure

### Task 1: Domain types — ChannelMessage and ReplyContentType

**Files:**
- Create: `Domain/DTOs/ChannelMessage.cs`
- Create: `Domain/DTOs/ReplyContentType.cs`
- Test: `Tests/Unit/Domain/ChannelMessageTests.cs`

- [ ] **Step 1: Write test for ChannelMessage record**

```csharp
// Tests/Unit/Domain/ChannelMessageTests.cs
using Domain.DTOs;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChannelMessageTests
{
    [Fact]
    public void ChannelMessage_RequiredProperties_AreSetCorrectly()
    {
        var msg = new ChannelMessage
        {
            ConversationId = "conv-123",
            Content = "Hello",
            Sender = "alice",
            ChannelId = "signalr"
        };

        msg.ConversationId.ShouldBe("conv-123");
        msg.Content.ShouldBe("Hello");
        msg.Sender.ShouldBe("alice");
        msg.ChannelId.ShouldBe("signalr");
        msg.AgentId.ShouldBeNull();
    }

    [Fact]
    public void ChannelMessage_WithAgentId_SetsOptionalProperty()
    {
        var msg = new ChannelMessage
        {
            ConversationId = "conv-123",
            Content = "Hello",
            Sender = "alice",
            ChannelId = "telegram",
            AgentId = "jack"
        };

        msg.AgentId.ShouldBe("jack");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChannelMessageTests" -v minimal`
Expected: FAIL — `ChannelMessage` type does not exist

- [ ] **Step 3: Implement ChannelMessage and ReplyContentType**

```csharp
// Domain/DTOs/ChannelMessage.cs
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public record ChannelMessage
{
    public required string ConversationId { get; init; }
    public required string Content { get; init; }
    public required string Sender { get; init; }
    public required string ChannelId { get; init; }
    public string? AgentId { get; init; }
}
```

```csharp
// Domain/DTOs/ReplyContentType.cs
using JetBrains.Annotations;

namespace Domain.DTOs;

[PublicAPI]
public static class ReplyContentType
{
    public const string Text = "text";
    public const string Reasoning = "reasoning";
    public const string ToolCall = "tool_call";
    public const string Error = "error";
    public const string StreamComplete = "stream_complete";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChannelMessageTests" -v minimal`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/ChannelMessage.cs Domain/DTOs/ReplyContentType.cs Tests/Unit/Domain/ChannelMessageTests.cs
git commit -m "feat: add ChannelMessage record and ReplyContentType constants"
```

---

### Task 2: Domain type — IChannelConnection interface

**Files:**
- Create: `Domain/Contracts/IChannelConnection.cs`

No test needed — this is a pure interface with no logic.

- [ ] **Step 1: Create IChannelConnection interface**

```csharp
// Domain/Contracts/IChannelConnection.cs
using Domain.DTOs;

namespace Domain.Contracts;

public interface IChannelConnection
{
    string ChannelId { get; }

    IAsyncEnumerable<ChannelMessage> Messages { get; }

    Task SendReplyAsync(
        string conversationId,
        string content,
        string contentType,
        bool isComplete,
        CancellationToken ct);

    Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct);

    Task NotifyAutoApprovedAsync(
        string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct);

    /// <summary>
    /// Create a new conversation (for scheduled execution). Returns null if unsupported.
    /// </summary>
    Task<string?> CreateConversationAsync(
        string agentId,
        string topicName,
        string sender,
        CancellationToken ct);
}
```

- [ ] **Step 2: Verify it compiles**

Run: `dotnet build Domain/`
Expected: Build succeeded

- [ ] **Step 3: Commit**

```bash
git add Domain/Contracts/IChannelConnection.cs
git commit -m "feat: add IChannelConnection interface"
```

---

### Task 3: AgentKey simplification

**Files:**
- Modify: `Domain/Agents/AgentKey.cs`
- Modify: `Tests/Unit/Domain/ChatThreadResolverTests.cs` (update AgentKey construction)
- Test: `Tests/Unit/Domain/AgentKeyTests.cs` (new)

This is a cross-cutting change. Many files reference `AgentKey(long, long, string?)`. The approach:
1. Change AgentKey definition
2. Fix all compilation errors across the solution

- [ ] **Step 1: Write tests for new AgentKey**

```csharp
// Tests/Unit/Domain/AgentKeyTests.cs
using Domain.Agents;
using Shouldly;

namespace Tests.Unit.Domain;

public class AgentKeyTests
{
    [Fact]
    public void ToString_WithAgentId_FormatsCorrectly()
    {
        var key = new AgentKey("conv-123", "jack");
        key.ToString().ShouldBe("agent-key:jack:conv-123");
    }

    [Fact]
    public void ToString_WithoutAgentId_FormatsCorrectly()
    {
        var key = new AgentKey("conv-123");
        key.ToString().ShouldBe("agent-key::conv-123");
    }

    [Fact]
    public void Equality_SameValues_AreEqual()
    {
        var a = new AgentKey("conv-123", "jack");
        var b = new AgentKey("conv-123", "jack");
        a.ShouldBe(b);
    }

    [Fact]
    public void Equality_DifferentConversationId_AreNotEqual()
    {
        var a = new AgentKey("conv-123", "jack");
        var b = new AgentKey("conv-456", "jack");
        a.ShouldNotBe(b);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~AgentKeyTests" -v minimal`
Expected: FAIL — constructor signature mismatch

- [ ] **Step 3: Change AgentKey definition**

```csharp
// Domain/Agents/AgentKey.cs
namespace Domain.Agents;

public readonly record struct AgentKey(string ConversationId, string? AgentId = null)
{
    public override string ToString()
    {
        return $"agent-key:{AgentId}:{ConversationId}";
    }
}
```

- [ ] **Step 4: Fix all compilation errors across the solution**

This is a large mechanical change. Search for all usages of `AgentKey` and update:

- `new AgentKey(chatId, threadId, agentId)` → uses will be updated when each consuming file is migrated
- `agentKey.ChatId` / `agentKey.ThreadId` → `agentKey.ConversationId`
- `$"{definition.Name}-{agentKey.ChatId}-{agentKey.ThreadId}"` → `$"{definition.Name}-{agentKey.ConversationId}"` in MultiAgentFactory

**Strategy:** Run `dotnet build` iteratively, fixing errors file by file. For files that will be deleted later, apply minimal fixes to keep things compiling (e.g., convert `new AgentKey(chatId, threadId, agentId)` to `new AgentKey($"{chatId}:{threadId}", agentId)`). For files that remain, apply the correct new semantics.

**Files that will need updating** (search for `new AgentKey` and `agentKey.ChatId` / `agentKey.ThreadId`):

Domain (kept):
- `Domain/Monitor/ChatMonitor.cs` — will be fully refactored in Task 6; temporary fix: `new AgentKey($"{x.ChatId}:{x.ThreadId}", x.AgentId)`
- `Domain/Monitor/ScheduleExecutor.cs:72` — `new AgentKey(0, 0, schedule.Agent.Id)` → `new AgentKey("scheduled", schedule.Agent.Id)` (temporary)
- `Domain/Agents/ChatThreadResolver.cs` — already generic over AgentKey, should work with no changes

Infrastructure (kept):
- `Infrastructure/Agents/MultiAgentFactory.cs:96` — `$"{definition.Name}-{agentKey.ChatId}-{agentKey.ThreadId}"` → `$"{definition.Name}-{agentKey.ConversationId}"`
- `Infrastructure/StateManagers/RedisThreadStateStore.cs` — `DeleteAsync(AgentKey)` uses `key.ToString()`, format changes but still works

Infrastructure (will be deleted — apply minimal fixes):
- `Infrastructure/Clients/Messaging/WebChat/WebChatMessengerClient.cs` — uses `agentKey.ChatId`, `agentKey.ThreadId`
- `Infrastructure/Clients/Messaging/Telegram/TelegramChatClient.cs` — same
- `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusChatMessengerClient.cs` — same
- `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs` — same
- `Infrastructure/Clients/ToolApproval/WebToolApprovalHandler.cs` — uses `agentKey.ChatId`
- `Infrastructure/Clients/ToolApproval/TelegramToolApprovalHandler.cs` — same
- `Domain/Routers/MessageSourceRouter.cs` — same

Tests:
- `Tests/Unit/Domain/ChatThreadResolverTests.cs` — constructs `AgentKey(1, 1)` etc.
- `Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs` — constructs AgentKey
- `Tests/Unit/Infrastructure/TelegramToolApprovalHandlerTests.cs` — constructs AgentKey
- `Tests/Unit/Infrastructure/ToolApprovalChatClientTests.cs` — constructs AgentKey
- All integration test files that reference AgentKey

Run: `dotnet build` and fix each error. Then `dotnet test Tests/` to verify existing tests still pass where possible.

- [ ] **Step 5: Run tests**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~AgentKeyTests" -v minimal`
Expected: PASS

Run: `dotnet build`
Expected: Build succeeded (some tests may need updating)

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor: simplify AgentKey to (ConversationId, AgentId)"
```

---

### Task 4: ChannelToolApprovalHandler

**Files:**
- Create: `Infrastructure/Clients/Channels/ChannelToolApprovalHandler.cs`
- Test: `Tests/Unit/Infrastructure/ChannelToolApprovalHandlerTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/Infrastructure/ChannelToolApprovalHandlerTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Clients.Channels;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class ChannelToolApprovalHandlerTests
{
    private readonly Mock<IChannelConnection> _channel = new();
    private const string ConversationId = "conv-123";

    [Fact]
    public async Task RequestApprovalAsync_DelegatesToChannel()
    {
        var requests = new List<ToolApprovalRequest>
        {
            new("msg-1", "search", new Dictionary<string, object?> { ["query"] = "test" })
        };
        _channel
            .Setup(c => c.RequestApprovalAsync(ConversationId, requests, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ToolApprovalResult.Approved);

        var sut = new ChannelToolApprovalHandler(_channel.Object, ConversationId);

        var result = await sut.RequestApprovalAsync(requests, CancellationToken.None);

        result.ShouldBe(ToolApprovalResult.Approved);
        _channel.Verify(c => c.RequestApprovalAsync(ConversationId, requests, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyAutoApprovedAsync_DelegatesToChannel()
    {
        var requests = new List<ToolApprovalRequest>
        {
            new("msg-1", "read_file", new Dictionary<string, object?> { ["path"] = "/tmp" })
        };

        var sut = new ChannelToolApprovalHandler(_channel.Object, ConversationId);

        await sut.NotifyAutoApprovedAsync(requests, CancellationToken.None);

        _channel.Verify(c => c.NotifyAutoApprovedAsync(ConversationId, requests, It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChannelToolApprovalHandlerTests" -v minimal`
Expected: FAIL — type does not exist

- [ ] **Step 3: Implement ChannelToolApprovalHandler**

```csharp
// Infrastructure/Clients/Channels/ChannelToolApprovalHandler.cs
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients.Channels;

public sealed class ChannelToolApprovalHandler(
    IChannelConnection channel,
    string conversationId) : IToolApprovalHandler
{
    public Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
        => channel.RequestApprovalAsync(conversationId, requests, cancellationToken);

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
        => channel.NotifyAutoApprovedAsync(conversationId, requests, cancellationToken);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChannelToolApprovalHandlerTests" -v minimal`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Channels/ChannelToolApprovalHandler.cs Tests/Unit/Infrastructure/ChannelToolApprovalHandlerTests.cs
git commit -m "feat: add ChannelToolApprovalHandler delegating approval to channel"
```

---

### Task 5: McpChannelConnection

**Files:**
- Create: `Infrastructure/Clients/Channels/McpChannelConnection.cs`
- Test: `Tests/Unit/Infrastructure/McpChannelConnectionTests.cs`

This wraps an MCP client to implement `IChannelConnection`. It converts MCP notifications into `ChannelMessage` items via a `Channel<T>`, and delegates `SendReplyAsync`/`RequestApprovalAsync` to MCP tool calls.

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/Infrastructure/McpChannelConnectionTests.cs
using System.Text.Json;
using Domain.DTOs;
using Infrastructure.Clients.Channels;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class McpChannelConnectionTests
{
    [Fact]
    public void ChannelId_ReturnsConfiguredId()
    {
        var sut = new McpChannelConnection("signalr");
        sut.ChannelId.ShouldBe("signalr");
    }

    [Fact]
    public async Task HandleNotification_WritesChannelMessage()
    {
        var sut = new McpChannelConnection("telegram");
        var notification = JsonSerializer.SerializeToElement(new
        {
            conversationId = "conv-1",
            sender = "alice",
            content = "Hello",
            agentId = "jack"
        });

        sut.HandleChannelMessageNotification(notification);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var msg = await sut.Messages.FirstAsync(cts.Token);

        msg.ConversationId.ShouldBe("conv-1");
        msg.Sender.ShouldBe("alice");
        msg.Content.ShouldBe("Hello");
        msg.AgentId.ShouldBe("jack");
        msg.ChannelId.ShouldBe("telegram");
    }

    [Fact]
    public async Task HandleNotification_WithoutAgentId_DefaultsToNull()
    {
        var sut = new McpChannelConnection("servicebus");
        var notification = JsonSerializer.SerializeToElement(new
        {
            conversationId = "conv-2",
            sender = "system",
            content = "trigger"
        });

        sut.HandleChannelMessageNotification(notification);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var msg = await sut.Messages.FirstAsync(cts.Token);

        msg.AgentId.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~McpChannelConnectionTests" -v minimal`
Expected: FAIL — type does not exist

- [ ] **Step 3: Implement McpChannelConnection**

```csharp
// Infrastructure/Clients/Channels/McpChannelConnection.cs
using System.Text.Json;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using ModelContextProtocol.Client;

namespace Infrastructure.Clients.Channels;

public sealed class McpChannelConnection(string channelId) : IChannelConnection, IAsyncDisposable
{
    private readonly Channel<ChannelMessage> _messageChannel = Channel.CreateUnbounded<ChannelMessage>();
    private IMcpClient? _client;

    public string ChannelId => channelId;

    public IAsyncEnumerable<ChannelMessage> Messages => _messageChannel.Reader.ReadAllAsync();

    /// <summary>
    /// Connect to the channel MCP server at the given SSE endpoint.
    /// Registers notification handler for channel/message.
    /// </summary>
    public async Task ConnectAsync(string endpoint, CancellationToken ct)
    {
        // Uses HttpClientTransport matching existing pattern in McpClientManager.cs
        _client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(endpoint)
            }),
            new McpClientOptions
            {
                ClientInfo = new() { Name = channelId, Version = "1.0.0" }
            },
            cancellationToken: ct);

        // Register notification handler for channel/message.
        // NOTE: The exact notification subscription API depends on the ModelContextProtocol 1.1.0 SDK.
        // Check McpClient for AddNotificationHandler, OnNotification, or similar method.
        // The handler should call HandleChannelMessageNotification(params).
        // If the SDK uses McpClientOptions.Handlers, wire it before CreateAsync.
    }

    /// <summary>
    /// Called by the MCP client when a channel/message notification arrives.
    /// Also callable directly for testing without a real MCP connection.
    /// </summary>
    public void HandleChannelMessageNotification(JsonElement paramsElement)
    {
        var conversationId = paramsElement.GetProperty("conversationId").GetString()!;
        var sender = paramsElement.GetProperty("sender").GetString()!;
        var content = paramsElement.GetProperty("content").GetString()!;
        string? agentId = paramsElement.TryGetProperty("agentId", out var agentProp)
            ? agentProp.GetString()
            : null;

        var msg = new ChannelMessage
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            ChannelId = channelId,
            AgentId = agentId
        };

        _messageChannel.Writer.TryWrite(msg);
    }

    public async Task SendReplyAsync(
        string conversationId, string content, string contentType, bool isComplete, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(_client);
        await _client.CallToolAsync("send_reply", new Dictionary<string, object?>
        {
            ["conversationId"] = conversationId,
            ["content"] = content,
            ["contentType"] = contentType,
            ["isComplete"] = isComplete
        }, ct);
    }

    public async Task<ToolApprovalResult> RequestApprovalAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(_client);
        var result = await _client.CallToolAsync("request_approval", new Dictionary<string, object?>
        {
            ["conversationId"] = conversationId,
            ["mode"] = "request",
            ["requests"] = requests.Select(r => new { r.ToolName, r.Arguments }).ToArray()
        }, ct);

        var resultText = result.Content.OfType<ModelContextProtocol.Protocol.Types.TextContent>().FirstOrDefault()?.Text;
        return resultText switch
        {
            "approved" => ToolApprovalResult.Approved,
            "approved_and_remember" => ToolApprovalResult.ApprovedAndRemember,
            _ => ToolApprovalResult.Rejected
        };
    }

    public async Task NotifyAutoApprovedAsync(
        string conversationId, IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(_client);
        await _client.CallToolAsync("request_approval", new Dictionary<string, object?>
        {
            ["conversationId"] = conversationId,
            ["mode"] = "notify",
            ["requests"] = requests.Select(r => new { r.ToolName, r.Arguments }).ToArray()
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        _messageChannel.Writer.TryComplete();
        if (_client is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }
    }

    private Task HandleNotificationAsync(JsonElement paramsElement, CancellationToken ct)
    {
        HandleChannelMessageNotification(paramsElement);
        return Task.CompletedTask;
    }
}
```

> **Note for implementer:** The exact MCP client notification handler API may differ from what's shown. Check the `ModelContextProtocol` 1.1.0 SDK for the current notification subscription pattern. The `HandleChannelMessageNotification` method is intentionally public so unit tests can call it directly without a real MCP connection. The `ConnectAsync` / tool call methods need integration testing against a real MCP server.

- [ ] **Step 4: Run unit tests to verify they pass**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~McpChannelConnectionTests" -v minimal`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Channels/McpChannelConnection.cs Tests/Unit/Infrastructure/McpChannelConnectionTests.cs
git commit -m "feat: add McpChannelConnection implementing IChannelConnection via MCP client"
```

---

### Task 6: Refactor ChatMonitor and MultiAgentFactory

> **Depends on:** Tasks 2, 3, 4, 7. ChatMonitor creates `ChannelToolApprovalHandler` (Task 4) and passes it to `IAgentFactory.Create` which must already accept `IToolApprovalHandler` (Task 7). **Execute Task 7 before this task.**

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs`
- Test: `Tests/Unit/Domain/ChatMonitorTests.cs` (new)

- [ ] **Step 1: Write failing tests for the new ChatMonitor**

```csharp
// Tests/Unit/Domain/ChatMonitorTests.cs
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatMonitorTests
{
    private readonly Mock<IAgentFactory> _agentFactory = new();
    private readonly ChatThreadResolver _threadResolver = new();
    private readonly Mock<ILogger<ChatMonitor>> _logger = new();

    [Fact]
    public async Task Monitor_SingleChannel_SingleMessage_RoutesSendReplyToOriginatingChannel()
    {
        // Arrange: channel emits one message
        var channel = new FakeChannelConnection("ch1",
        [
            new ChannelMessage
            {
                ConversationId = "conv-1",
                Content = "hello",
                Sender = "alice",
                ChannelId = "ch1",
                AgentId = "jack"
            }
        ]);

        var mockAgent = CreateMockAgent("response text");
        _agentFactory
            .Setup(f => f.Create(It.IsAny<AgentKey>(), "alice", "jack"))
            .Returns(mockAgent);

        var sut = new ChatMonitor([channel], _agentFactory.Object, _threadResolver, _logger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.Monitor(cts.Token);

        // Assert: channel received send_reply calls
        channel.SentReplies.ShouldNotBeEmpty();
        channel.SentReplies.ShouldContain(r => r.ConversationId == "conv-1");
    }

    [Fact]
    public async Task Monitor_TwoChannels_RoutesRepliesToCorrectChannel()
    {
        var ch1 = new FakeChannelConnection("ch1",
        [
            new ChannelMessage { ConversationId = "conv-1", Content = "msg1", Sender = "alice", ChannelId = "ch1" }
        ]);
        var ch2 = new FakeChannelConnection("ch2",
        [
            new ChannelMessage { ConversationId = "conv-2", Content = "msg2", Sender = "bob", ChannelId = "ch2" }
        ]);

        _agentFactory
            .Setup(f => f.Create(It.IsAny<AgentKey>(), It.IsAny<string>(), It.IsAny<string?>()))
            .Returns(() => CreateMockAgent("reply"));

        var sut = new ChatMonitor([ch1, ch2], _agentFactory.Object, _threadResolver, _logger.Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await sut.Monitor(cts.Token);

        ch1.SentReplies.ShouldContain(r => r.ConversationId == "conv-1");
        ch1.SentReplies.ShouldNotContain(r => r.ConversationId == "conv-2");
        ch2.SentReplies.ShouldContain(r => r.ConversationId == "conv-2");
        ch2.SentReplies.ShouldNotContain(r => r.ConversationId == "conv-1");
    }

    // Helper: creates a mock DisposableAgent that yields a text response
    private static DisposableAgent CreateMockAgent(string text)
    {
        // Implementation depends on how DisposableAgent/McpAgent can be mocked.
        // Use Moq or a test double. The agent should yield an AgentResponseUpdate
        // with TextContent(text) followed by StreamCompleteContent.
        throw new NotImplementedException("Wire up mock agent — see existing test patterns");
    }
}

/// <summary>
/// Test double for IChannelConnection that emits pre-configured messages
/// and records sent replies.
/// </summary>
internal sealed class FakeChannelConnection(string channelId, ChannelMessage[] messages) : IChannelConnection
{
    private readonly List<(string ConversationId, string Content, string ContentType, bool IsComplete)> _replies = [];

    public string ChannelId => channelId;

    public IReadOnlyList<(string ConversationId, string Content, string ContentType, bool IsComplete)> SentReplies
        => _replies;

    public IAsyncEnumerable<ChannelMessage> Messages => messages.ToAsyncEnumerable();

    public Task SendReplyAsync(string conversationId, string content, string contentType, bool isComplete,
        CancellationToken ct)
    {
        _replies.Add((conversationId, content, contentType, isComplete));
        return Task.CompletedTask;
    }

    public Task<ToolApprovalResult> RequestApprovalAsync(string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
        => Task.FromResult(ToolApprovalResult.AutoApproved);

    public Task NotifyAutoApprovedAsync(string conversationId, IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct)
        => Task.CompletedTask;

    public Task<string?> CreateConversationAsync(string agentId, string topicName, string sender,
        CancellationToken ct)
        => Task.FromResult<string?>(null);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChatMonitorTests" -v minimal`
Expected: FAIL — ChatMonitor constructor signature mismatch

- [ ] **Step 3: Refactor ChatMonitor**

```csharp
// Domain/Monitor/ChatMonitor.cs
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ChatMonitor(
    IReadOnlyList<IChannelConnection> channels,
    IAgentFactory agentFactory,
    ChatThreadResolver threadResolver,
    ILogger<ChatMonitor> logger)
{
    public async Task Monitor(CancellationToken cancellationToken)
    {
        try
        {
            var allMessages = channels
                .Select(ch => ch.Messages.Select(m => (Channel: ch, Message: m)))
                .Merge(cancellationToken);

            var responses = allMessages
                .GroupByStreaming(
                    (x, _) => ValueTask.FromResult(
                        new AgentKey(x.Message.ConversationId, x.Message.AgentId)),
                    cancellationToken)
                .Select(group => ProcessChatThread(group.Key, group, cancellationToken))
                .Merge(cancellationToken);

            try
            {
                await foreach (var (channel, conversationId, update, aiResponse) in
                    responses.WithCancellation(cancellationToken))
                {
                    await SendUpdateToChannel(channel, conversationId, update, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inner ChatMonitor exception: {exceptionMessage}", ex.Message);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
        }
    }

    private async IAsyncEnumerable<(IChannelConnection, string, AgentResponseUpdate, AiResponse?)>
        ProcessChatThread(
            AgentKey agentKey,
            IAsyncGrouping<AgentKey, (IChannelConnection Channel, ChannelMessage Message)> group,
            [EnumeratorCancellation] CancellationToken ct)
    {
        var first = await group.FirstAsync(ct);
        var originChannel = first.Channel;
        var approvalHandler = new ChannelToolApprovalHandler(originChannel, agentKey.ConversationId);
        await using var agent = agentFactory.Create(agentKey, first.Message.Sender, first.Message.AgentId, approvalHandler);
        var context = threadResolver.Resolve(agentKey);
        var thread = await GetOrRestoreThread(agent, agentKey, ct);

        context.RegisterCompletionCallback(group.Complete);

        using var linkedCts = context.GetLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;

        var aiResponses = group.Prepend(first)
            .Select(async (x, _, _) =>
            {
                var command = ChatCommandParser.Parse(x.Message.Content);
                switch (command)
                {
                    case ChatCommand.Clear:
                        await threadResolver.ClearAsync(agentKey);
                        return AsyncEnumerable.Empty<(AgentResponseUpdate, AiResponse?)>();
                    case ChatCommand.Cancel:
                        threadResolver.Cancel(agentKey);
                        return AsyncEnumerable.Empty<(AgentResponseUpdate, AiResponse?)>();
                    default:
                        var userMessage = new ChatMessage(ChatRole.User, x.Message.Content);
                        userMessage.SetSenderId(x.Message.Sender);
                        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
                        return agent
                            .RunStreamingAsync([userMessage], thread, cancellationToken: linkedCt)
                            .WithErrorHandling(linkedCt)
                            .ToUpdateAiResponsePairs()
                            .Append((
                                new AgentResponseUpdate { Contents = [new StreamCompleteContent()] },
                                (AiResponse?)null));
                }
            })
            .Merge(linkedCt);

        await foreach (var (update, aiResponse) in aiResponses.WithCancellation(ct))
        {
            yield return (originChannel, agentKey.ConversationId, update, aiResponse);
        }
    }

    private static async Task SendUpdateToChannel(
        IChannelConnection channel,
        string conversationId,
        AgentResponseUpdate update,
        CancellationToken ct)
    {
        foreach (var content in update.Contents)
        {
            var (text, contentType, isComplete) = content switch
            {
                TextContent tc => (tc.Text ?? "", ReplyContentType.Text, false),
                TextReasoningContent rc => (rc.ReasoningText ?? "", ReplyContentType.Reasoning, false),
                FunctionCallContent fc => (
                    JsonSerializer.Serialize(new { fc.Name, fc.Arguments }),
                    ReplyContentType.ToolCall, false),
                ErrorContent ec => (ec.Message, ReplyContentType.Error, false),
                StreamCompleteContent => ("", ReplyContentType.StreamComplete, true),
                _ => (content.ToString() ?? "", ReplyContentType.Text, false)
            };

            await channel.SendReplyAsync(conversationId, text, contentType, isComplete, ct);
        }
    }

    private static ValueTask<AgentSession> GetOrRestoreThread(
        DisposableAgent agent, AgentKey agentKey, CancellationToken ct)
    {
        return agent.DeserializeSessionAsync(
            JsonSerializer.SerializeToElement(agentKey.ToString()), null, ct);
    }
}
```

> **Note for implementer:** The `TextReasoningContent`, `ErrorContent`, `StreamCompleteContent` types come from the `Microsoft.Agents.AI` / `Microsoft.Extensions.AI` namespaces. Verify the exact property names. The `ToUpdateAiResponsePairs()` extension method is defined elsewhere in the codebase — find it via grep and verify the tuple shape matches.

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChatMonitorTests" -v minimal`
Expected: PASS (after wiring up the mock agent helper)

- [ ] **Step 5: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/ChatMonitorTests.cs
git commit -m "refactor: ChatMonitor uses IChannelConnection instead of IChatMessengerClient"
```

---

### Task 7: Refactor MultiAgentFactory — remove IToolApprovalHandlerFactory

**Files:**
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs`
- Modify: `Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs`

The factory no longer resolves `IToolApprovalHandlerFactory` from DI. Instead, it receives an `IToolApprovalHandler` directly in `Create()` and `CreateFromDefinition()`.

- [ ] **Step 1: Update MultiAgentFactory tests**

Update existing tests to pass `IToolApprovalHandler` instead of expecting factory resolution. Add a new test:

```csharp
[Fact]
public void Create_WithApprovalHandler_UsesProvidedHandler()
{
    var handler = new Mock<IToolApprovalHandler>();
    var agent = _sut.Create(
        new AgentKey("conv-1", "jack"),
        "user1",
        "jack",
        handler.Object);

    agent.ShouldNotBeNull();
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~MultiAgentFactoryTests" -v minimal`
Expected: FAIL — signature mismatch

- [ ] **Step 3: Update MultiAgentFactory**

Change `Create` and `CreateFromDefinition` to accept `IToolApprovalHandler`:

```csharp
// Key changes in MultiAgentFactory:

public DisposableAgent Create(AgentKey agentKey, string userId, string? agentId, IToolApprovalHandler approvalHandler)
{
    // ... resolve agent definition (same as before) ...
    return CreateFromDefinition(agentKey, userId, agent, approvalHandler);
}

public DisposableAgent CreateFromDefinition(
    AgentKey agentKey, string userId, AgentDefinition definition, IToolApprovalHandler approvalHandler)
{
    var chatClient = CreateChatClient(definition.Model);
    var stateStore = serviceProvider.GetRequiredService<IThreadStateStore>();

    var name = $"{definition.Name}-{agentKey.ConversationId}";
    var effectiveClient = new ToolApprovalChatClient(chatClient, approvalHandler, definition.WhitelistPatterns);

    var domainTools = domainToolRegistry
        .GetToolsForFeatures(definition.EnabledFeatures)
        .ToList();

    return new McpAgent(
        definition.McpServerEndpoints,
        effectiveClient,
        name,
        definition.Description ?? "",
        stateStore,
        userId,
        definition.CustomInstructions,
        domainTools);
}
```

Update `IAgentFactory` interface to match (add `IToolApprovalHandler` parameter). Update `IScheduleAgentFactory.CreateFromDefinition` similarly.

- [ ] **Step 4: Fix compilation errors and run tests**

Run: `dotnet build && dotnet test Tests/ --filter "FullyQualifiedName~MultiAgentFactoryTests" -v minimal`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/MultiAgentFactory.cs Domain/Contracts/IAgentFactory.cs Domain/Contracts/IScheduleAgentFactory.cs Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs
git commit -m "refactor: MultiAgentFactory accepts IToolApprovalHandler directly"
```

---

### Task 8: Redis key migration strategy

The `AgentKey.ToString()` format changes from `agent-key:{AgentId}:{ChatId}:{ThreadId}` to `agent-key:{AgentId}:{ConversationId}`.

**Decision:** Each channel server constructs `conversationId` as `"{ChatId}:{ThreadId}"` (preserving the legacy numeric format). This means the Redis key format remains **backward compatible** with no migration needed:
- Old: `agent-key:jack:123:456` (from `AgentKey(123, 456, "jack")`)
- New: `agent-key:jack:123:456` (from `AgentKey("123:456", "jack")`)

**No code changes needed.** This compatibility constraint must be documented for channel implementations:

- [ ] **Step 1: Add compatibility note to each channel server task**

Each channel must construct `conversationId` using the format `"{chatId}:{threadId}"` to maintain Redis key compatibility. For example:
- SignalR: `$"{space.ChatId}:{topic.ThreadId}"`
- Telegram: `$"{chatId}:{forumTopicId}"`
- ServiceBus: `$"{queueHash}:{correlationId}"` (new conversations, no legacy keys)

- [ ] **Step 2: Commit documentation note**

```bash
git commit -m "docs: document conversationId format for Redis key compatibility"
```

---

### Task 9: Refactor ScheduleExecutor

**Files:**
- Modify: `Domain/Monitor/ScheduleExecutor.cs`
- Test: `Tests/Unit/Domain/ScheduleExecutorTests.cs` (new or extend existing)

- [ ] **Step 1: Write failing test**

```csharp
[Fact]
public async Task ProcessScheduleAsync_WithScheduleCapableChannel_CreatesConversationAndSendsReplies()
{
    // Arrange: channel that supports create_conversation
    var channel = new FakeScheduleChannel("signalr", supportsScheduling: true, conversationId: "sched-conv-1");

    var sut = new ScheduleExecutor(
        _store.Object,
        _agentFactory.Object,
        [channel],
        _scheduleChannel,
        "signalr", // default schedule channel ID
        _logger.Object);

    // ... enqueue a schedule, verify channel receives create_conversation + send_reply calls
}
```

- [ ] **Step 2: Run test to verify it fails**

Expected: FAIL — ScheduleExecutor constructor signature mismatch

- [ ] **Step 3: Refactor ScheduleExecutor**

Replace `IChatMessengerClient messengerClient` with `IReadOnlyList<IChannelConnection> channels` and a `string defaultScheduleChannelId` config parameter.

Key changes:
- Find target channel: `channels.FirstOrDefault(c => c.ChannelId == defaultScheduleChannelId)`
- Check scheduling support: call `create_conversation` tool (via a new `IChannelConnection.CreateConversationAsync` method, or by adding the method to the interface)
- Send responses via `channel.SendReplyAsync()` instead of `messengerClient.ProcessResponseStreamAsync()`

> **Note for implementer:** Consider adding `Task<string?> CreateConversationAsync(string agentId, string topicName, string sender, CancellationToken ct)` to `IChannelConnection`. Channels that don't support scheduling return `null`. This is cleaner than catching tool-not-found exceptions.

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ScheduleExecutorTests" -v minimal`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add Domain/Monitor/ScheduleExecutor.cs Domain/Contracts/IChannelConnection.cs Tests/Unit/Domain/ScheduleExecutorTests.cs
git commit -m "refactor: ScheduleExecutor uses IChannelConnection instead of IChatMessengerClient"
```

---

## Phase 2: McpChannelSignalR Server

### Task 10: Project scaffolding

**Files:**
- Create: `McpChannelSignalR/McpChannelSignalR.csproj`
- Create: `McpChannelSignalR/Program.cs`
- Create: `McpChannelSignalR/Dockerfile`
- Create: `McpChannelSignalR/appsettings.json`
- Create: `McpChannelSignalR/Settings/ChannelSettings.cs`
- Create: `McpChannelSignalR/Modules/ConfigModule.cs`
- Modify: `agent.sln` (add project reference)

Follow the exact pattern from `McpServerText/`:

- [ ] **Step 1: Create .csproj**

```xml
<!-- McpChannelSignalR/McpChannelSignalR.csproj -->
<!-- NOTE: Use Microsoft.NET.Sdk.Web to get SignalR included via the framework (not a separate package) -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <LangVersion>14</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.1.0" />
    <PackageReference Include="StackExchange.Redis" Version="2.8.41" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Domain\Domain.csproj"/>
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

> **Note:** This channel server references Domain (for shared DTOs like `ChannelMessage`, `ReplyContentType`). It does NOT reference Infrastructure — the channel is a separate deployable unit.

- [ ] **Step 2: Create Program.cs following McpServerText pattern**

```csharp
// McpChannelSignalR/Program.cs
var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureChannel(settings);

var app = builder.Build();
app.MapMcp();
// SignalR hub mapping will be added in a later step

await app.RunAsync();
```

- [ ] **Step 3: Create Dockerfile following existing pattern**

Copy `McpServerText/Dockerfile`, replace project name references.

- [ ] **Step 4: Create settings and config module**

```csharp
// McpChannelSignalR/Settings/ChannelSettings.cs
namespace McpChannelSignalR.Settings;

public record ChannelSettings
{
    public required string RedisConnectionString { get; init; }
}
```

```csharp
// McpChannelSignalR/Modules/ConfigModule.cs
using McpChannelSignalR.Settings;

namespace McpChannelSignalR.Modules;

public static class ConfigModule
{
    public static ChannelSettings GetSettings(this IConfigurationBuilder builder)
    {
        return builder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build()
            .Get<ChannelSettings>() ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureChannel(this IServiceCollection services, ChannelSettings settings)
    {
        services
            .AddSingleton(settings)
            .AddMcpServer()
            .WithHttpTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, ct) =>
            {
                try { return await next(context, ct); }
                catch (Exception ex)
                {
                    var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                    logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    return ToolResponse.Create(ex);
                }
            }));
            // .WithTools<SendReplyTool>()  — added in Task 11
            // .WithTools<RequestApprovalTool>()  — added in Task 12

        return services;
    }
}
```

- [ ] **Step 5: Add to solution and verify build**

```bash
dotnet sln agent.sln add McpChannelSignalR/McpChannelSignalR.csproj
dotnet build McpChannelSignalR/
```

- [ ] **Step 6: Commit**

```bash
git add McpChannelSignalR/ agent.sln
git commit -m "feat: scaffold McpChannelSignalR project"
```

---

### Task 11: McpChannelSignalR — send_reply tool

**Files:**
- Create: `McpChannelSignalR/McpTools/SendReplyTool.cs`
- Test: `Tests/Unit/McpChannelSignalR/SendReplyToolTests.cs`

- [ ] **Step 1: Write failing test**

Test that the tool receives parameters and pushes content to a SignalR hub context.

- [ ] **Step 2: Implement SendReplyTool**

```csharp
// McpChannelSignalR/McpTools/SendReplyTool.cs
using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = "send_reply")]
    [Description("Send a response chunk to a conversation")]
    public async Task<string> McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Response content")] string content,
        [Description("Content type: text, reasoning, tool_call, error, stream_complete")] string contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        IServiceProvider services)
    {
        // Resolve SignalR hub context and push to the conversation's group
        // Implementation depends on how the SignalR hub is set up — this is a placeholder
        // The real implementation will use IHubContext<ChatHub> to send to the conversation group
        return "ok";
    }
}
```

- [ ] **Step 3: Run tests, verify pass**
- [ ] **Step 4: Commit**

```bash
git commit -m "feat: add send_reply MCP tool to McpChannelSignalR"
```

---

### Task 12: McpChannelSignalR — request_approval tool

**Files:**
- Create: `McpChannelSignalR/McpTools/RequestApprovalTool.cs`
- Test: `Tests/Unit/McpChannelSignalR/RequestApprovalToolTests.cs`

Same pattern as Task 11. The tool sends approval UI to the WebChat client and waits for user response (using a `TaskCompletionSource` keyed by conversation ID).

- [ ] **Step 1-4: TDD cycle** (same pattern as Task 11)
- [ ] **Step 5: Commit**

```bash
git commit -m "feat: add request_approval MCP tool to McpChannelSignalR"
```

---

### Task 13: McpChannelSignalR — SignalR hub and notification emission

**Files:**
- Create: `McpChannelSignalR/Hubs/ChatHub.cs` (migrated from Agent project)
- Create: `McpChannelSignalR/Services/ChannelNotificationEmitter.cs`

The hub receives messages from WebChat clients and emits `notifications/channel/message` to the connected MCP client (the agent).

- [ ] **Step 1: Write tests for notification emission**
- [ ] **Step 2: Migrate ChatHub from Agent project** — adapt to call `ChannelNotificationEmitter` instead of enqueueing to `IChatMessengerClient`
- [ ] **Step 3: Implement ChannelNotificationEmitter** — uses the MCP server's notification mechanism to push `notifications/channel/message` to the agent

> **Note for implementer:** Research how the `ModelContextProtocol.AspNetCore` server sends notifications to connected clients. The server needs access to the client session to push notifications. This may require `IMcpServer` injection or a custom notification service. Check the SDK docs/examples for `SendNotificationAsync`.

- [ ] **Step 4: Run tests, verify pass**
- [ ] **Step 5: Commit**

```bash
git commit -m "feat: migrate SignalR hub to McpChannelSignalR with notification emission"
```

---

### Task 14: McpChannelSignalR — create_conversation tool (for scheduling)

**Files:**
- Create: `McpChannelSignalR/McpTools/CreateConversationTool.cs`

- [ ] **Step 1-4: TDD cycle**

The tool creates a new WebChat topic/space entry in Redis and returns a `conversationId`.

- [ ] **Step 5: Commit**

```bash
git commit -m "feat: add create_conversation MCP tool to McpChannelSignalR"
```

---

## Phase 3: McpChannelTelegram Server

### Task 15: Project scaffolding

Same pattern as Task 10 but for Telegram. Reference `Telegram.Bot` package.

**Files:**
- Create: `McpChannelTelegram/McpChannelTelegram.csproj`
- Create: `McpChannelTelegram/Program.cs`
- Create: `McpChannelTelegram/Dockerfile`
- Create: `McpChannelTelegram/Settings/ChannelSettings.cs`
- Create: `McpChannelTelegram/Modules/ConfigModule.cs`

- [ ] **Step 1-5: Scaffold, build, add to solution**
- [ ] **Step 6: Commit**

```bash
git commit -m "feat: scaffold McpChannelTelegram project"
```

---

### Task 16: McpChannelTelegram — tools and bot polling

**Files:**
- Create: `McpChannelTelegram/McpTools/SendReplyTool.cs`
- Create: `McpChannelTelegram/McpTools/RequestApprovalTool.cs`
- Create: `McpChannelTelegram/Services/TelegramBotPoller.cs`
- Create: `McpChannelTelegram/Services/ChannelNotificationEmitter.cs`

- [ ] **Step 1: TDD for send_reply** — formats content as Telegram messages (Markdown, message splitting for >4096 chars)
- [ ] **Step 2: TDD for request_approval** — sends inline keyboard buttons, waits for callback query
- [ ] **Step 3: Migrate TelegramChatClient polling logic** — poll for updates, emit `channel/message` notifications
- [ ] **Step 4: Migrate forum topic management** — channel internally manages topic creation
- [ ] **Step 5: Migrate sender allowlist** — channel validates senders before emitting notifications
- [ ] **Step 6: Run tests, commit**

```bash
git commit -m "feat: implement McpChannelTelegram with bot polling and MCP tools"
```

---

## Phase 4: McpChannelServiceBus Server

### Task 17: Project scaffolding

Same pattern. Reference `Azure.Messaging.ServiceBus` package.

- [ ] **Step 1-5: Scaffold, build, add to solution**
- [ ] **Step 6: Commit**

```bash
git commit -m "feat: scaffold McpChannelServiceBus project"
```

---

### Task 18: McpChannelServiceBus — tools and queue processing

**Files:**
- Create: `McpChannelServiceBus/McpTools/SendReplyTool.cs`
- Create: `McpChannelServiceBus/McpTools/RequestApprovalTool.cs`
- Create: `McpChannelServiceBus/Services/ServiceBusPoller.cs`
- Create: `McpChannelServiceBus/Services/ChannelNotificationEmitter.cs`

- [ ] **Step 1: TDD for send_reply** — sends to response queue with correlation ID
- [ ] **Step 2: TDD for request_approval** — auto-approves (ServiceBus has no interactive user)
- [ ] **Step 3: Migrate ServiceBusProcessor logic** — poll prompt queue, emit `channel/message` notifications
- [ ] **Step 4: Run tests, commit**

```bash
git commit -m "feat: implement McpChannelServiceBus with queue processing and MCP tools"
```

---

## Phase 5: Wiring, Docker, and Cleanup

### Task 19: DI wiring in InjectorModule

**Files:**
- Modify: `Agent/Modules/InjectorModule.cs`

- [ ] **Step 1: Replace transport wiring with channel connections**

Replace all `AddWebClient()` / `AddTelegramClient()` / `AddCliClient()` etc. with:

```csharp
public IServiceCollection AddChannels(AgentSettings settings)
{
    var connections = settings.ChannelEndpoints
        .Select(ep =>
        {
            var conn = new McpChannelConnection(ep.ChannelId);
            // ConnectAsync will be called during hosted service startup
            return (IChannelConnection)conn;
        })
        .ToList();

    services.AddSingleton<IReadOnlyList<IChannelConnection>>(connections);
    return services;
}
```

Add a hosted service that calls `ConnectAsync` on each channel during startup.

- [ ] **Step 2: Remove IToolApprovalHandlerFactory registration**
- [ ] **Step 3: Update ChatMonitor and ScheduleExecutor DI registration**
- [ ] **Step 4: Build and verify**

Run: `dotnet build`
Expected: Build succeeded

- [ ] **Step 5: Commit**

```bash
git commit -m "refactor: wire channel connections in InjectorModule, remove transport registrations"
```

---

### Task 20: Docker Compose updates

**Files:**
- Modify: `DockerCompose/docker-compose.yml`
- Modify: `DockerCompose/.env`

- [ ] **Step 1: Add channel services to docker-compose.yml**

Follow the existing `mcp-*` service pattern:

```yaml
mcp-channel-signalr:
  image: mcp-channel-signalr:latest
  container_name: mcp-channel-signalr
  ports:
    - "6010:8080"
  build:
    context: ${REPOSITORY_PATH}
    dockerfile: McpChannelSignalR/Dockerfile
    cache_from:
      - mcp-channel-signalr:latest
    args:
      - BUILDKIT_INLINE_CACHE=1
  user: ${PUID:-1654}:${PGID:-1654}
  restart: unless-stopped
  env_file:
    - .env
  environment:
    - REDIS__CONNECTIONSTRING=redis:6379
  networks:
    - jackbot
  depends_on:
    - base-sdk
    - redis

mcp-channel-telegram:
  image: mcp-channel-telegram:latest
  container_name: mcp-channel-telegram
  ports:
    - "6011:8080"
  build:
    context: ${REPOSITORY_PATH}
    dockerfile: McpChannelTelegram/Dockerfile
    cache_from:
      - mcp-channel-telegram:latest
    args:
      - BUILDKIT_INLINE_CACHE=1
  user: ${PUID:-1654}:${PGID:-1654}
  restart: unless-stopped
  env_file:
    - .env
  environment:
    - REDIS__CONNECTIONSTRING=redis:6379
  networks:
    - jackbot
  depends_on:
    - base-sdk
    - redis

mcp-channel-servicebus:
  image: mcp-channel-servicebus:latest
  container_name: mcp-channel-servicebus
  ports:
    - "6012:8080"
  build:
    context: ${REPOSITORY_PATH}
    dockerfile: McpChannelServiceBus/Dockerfile
    cache_from:
      - mcp-channel-servicebus:latest
    args:
      - BUILDKIT_INLINE_CACHE=1
  user: ${PUID:-1654}:${PGID:-1654}
  restart: unless-stopped
  env_file:
    - .env
  environment:
    - REDIS__CONNECTIONSTRING=redis:6379
    - SERVICEBUS__CONNECTIONSTRING=${SERVICEBUS__CONNECTIONSTRING}
  networks:
    - jackbot
  depends_on:
    - base-sdk
    - redis
```

- [ ] **Step 2: Update agent service to depend on channel containers**

Add `depends_on: mcp-channel-signalr, mcp-channel-telegram, mcp-channel-servicebus` to the agent service. Add channel endpoint environment variables.

- [ ] **Step 3: Update Caddy configuration**

SignalR has moved from the Agent container to McpChannelSignalR. Update Caddy config to route `/hubs/*` to `mcp-channel-signalr:8080` instead of the agent container.

- [ ] **Step 4: Update .env with placeholders**

Add channel-specific env vars (Telegram bot tokens, etc.)

- [ ] **Step 5: Update `Agent/Settings/AgentSettings.cs` and `Agent/appsettings.json`**

Add `ChannelEndpoints` config section:
```json
{
  "ChannelEndpoints": [
    { "ChannelId": "signalr", "Endpoint": "http://mcp-channel-signalr:8080/sse" },
    { "ChannelId": "telegram", "Endpoint": "http://mcp-channel-telegram:8080/sse" },
    { "ChannelId": "servicebus", "Endpoint": "http://mcp-channel-servicebus:8080/sse" }
  ]
}
```

- [ ] **Step 6: Commit**

```bash
git commit -m "feat: add channel MCP servers to docker-compose and update routing"
```

---

### Task 21: Delete old transport code

**Files to delete:**
- `Domain/Contracts/IChatMessengerClient.cs`
- `Domain/Contracts/IToolApprovalHandlerFactory.cs`
- `Domain/DTOs/MessageSource.cs`
- `Domain/DTOs/ChatPrompt.cs`
- `Infrastructure/Clients/Messaging/CompositeChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/WebChat/WebChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/Telegram/TelegramChatClient.cs`
- `Infrastructure/Clients/Messaging/ServiceBus/ServiceBusChatMessengerClient.cs`
- `Infrastructure/Clients/Messaging/Cli/CliChatMessengerClient.cs`
- `Domain/Routers/MessageSourceRouter.cs`
- All `*ToolApprovalHandlerFactory` files in `Infrastructure/Clients/ToolApproval/`

- [ ] **Step 1: Delete files**
- [ ] **Step 2: Fix any remaining compilation errors**

Run: `dotnet build`

- [ ] **Step 3: Delete orphaned tests** for removed code
- [ ] **Step 4: Run full test suite**

Run: `dotnet test Tests/ -v minimal`
Expected: All remaining tests pass

- [ ] **Step 5: Commit**

```bash
git commit -m "chore: remove old transport code (IChatMessengerClient, MessageSource, etc.)"
```

---

### Task 22: Integration test — end-to-end channel flow

**Files:**
- Create: `Tests/Integration/Channels/ChannelIntegrationTests.cs`
- Create: `Tests/Integration/Fixtures/McpChannelFixture.cs`

- [ ] **Step 1: Create test fixture** that starts a minimal MCP channel server (using `McpChannelSignalR` or a test-specific server) on a dynamic port

- [ ] **Step 2: Write integration test**

```csharp
[Fact]
public async Task Channel_ReceivesMessage_AgentRespondsViaSendReply()
{
    // 1. Start channel MCP server
    // 2. Connect McpChannelConnection to it
    // 3. Simulate an external message (e.g., POST to SignalR hub)
    // 4. Verify McpChannelConnection.Messages yields a ChannelMessage
    // 5. Call SendReplyAsync
    // 6. Verify the channel server received the reply
}
```

- [ ] **Step 3: Run integration tests**

Run: `dotnet test Tests/ --filter "FullyQualifiedName~ChannelIntegrationTests" -v minimal`
Expected: PASS

- [ ] **Step 4: Commit**

```bash
git commit -m "test: add end-to-end channel integration test"
```

---

## Dependency Graph

```
Task 1 (ChannelMessage, ReplyContentType)
Task 2 (IChannelConnection) ── depends on Task 1
Task 3 (AgentKey change) ── independent
Task 4 (ChannelToolApprovalHandler) ── depends on Task 2
Task 5 (McpChannelConnection) ── depends on Task 2
Task 6 (ChatMonitor + approval wiring) ── depends on Tasks 2, 3, 4, 7
Task 7 (MultiAgentFactory refactor) ── depends on Task 3 — **execute before Task 6**
Task 8 (Redis migration) ── depends on Task 3
Task 9 (ScheduleExecutor) ── depends on Tasks 2, 3

Tasks 10-14 (McpChannelSignalR) ── depends on Phase 1
Tasks 15-16 (McpChannelTelegram) ── depends on Phase 1, independent of SignalR
Tasks 17-18 (McpChannelServiceBus) ── depends on Phase 1, independent of others

Task 19 (DI wiring) ── depends on all above
Task 20 (Docker) ── depends on Task 19
Task 21 (Cleanup) ── depends on Task 19
Task 22 (Integration test) ── depends on at least one channel server
```

Phase 2, 3, and 4 (channel servers) can be executed in parallel.
