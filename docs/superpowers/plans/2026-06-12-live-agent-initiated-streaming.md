# Live Agent-Initiated Streaming Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Agent-initiated replies into an existing WebChat conversation (library download alerts, schedule results) stream live to viewing browsers instead of requiring a refresh.

**Architecture:** ChatMonitor announces turn start for agent-initiated messages (`message.Origin` set) by calling each delivery target's `create_conversation` tool with the existing conversation id; the SignalR channel's `CreateConversationTool` grows an attach branch that sets up the in-memory stream (seeded with the originating prompt) and broadcasts `OnStreamChanged(Started)` before reply chunks arrive. The WebChat client already handles `OnStreamChanged` — zero client changes.

**Tech Stack:** .NET 10, xUnit + Shouldly + Moq, MCP channel protocol.

**Spec:** `docs/superpowers/specs/2026-06-12-live-agent-initiated-streaming-design.md`

**Deviation from spec:** the spec lists an E2E browser test. The channel server's MCP endpoint can't be driven from the E2E harness against a browser-minted conversation id without new plumbing (the id lives in WASM/Redis, not reachable from Playwright cheaply). Tasks 5 covers the same seam in-process (real `SessionService` + `StreamService` + both MCP tools end-to-end); Task 6 adds a manual smoke checklist. Flag this substitution to the user at review.

**Conventions that bite:**
- NO trailing newline in any `.cs` file (including tests).
- The pre-commit hook runs `dotnet format` and re-stages whole files — make the working tree match the commit.
- Test naming: `{Method}_{Scenario}_{ExpectedResult}`. Shouldly assertions.
- Commit after each task (triplet) completes green.

---

## File Map

| File | Action | Responsibility |
|------|--------|----------------|
| `Domain/Monitor/ChatMonitor.cs` | Modify | `DeliveryTarget.Minted` flag; `AnnounceTurnStartAsync`; wire announce into `ProcessChatThread` |
| `McpChannelSignalR/McpTools/CreateConversationTool.cs` | Modify | Attach branch: session lookup → seeded stream → `OnStreamChanged(Started)` broadcast |
| `Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs` | Modify | Minted-flag coverage |
| `Tests/Unit/Domain/Monitor/ChatMonitorAnnounceTests.cs` | Create | `AnnounceTurnStartAsync` unit coverage |
| `Tests/Unit/Domain/MonitorTests.cs` | Modify | Extend `FakeChannelConnection` capture; monitor-level wiring test |
| `Tests/Unit/McpChannelSignalR/CreateConversationToolTests.cs` | Create | Attach-branch unit coverage |
| `Tests/Unit/McpChannelSignalR/AgentInitiatedStreamingFlowTests.cs` | Create | In-proc flow: attach → send_reply → live subscriber |
| `CLAUDE.md` | Modify | One line documenting the turn-start announce in Channel Architecture |

All commands run from `/home/dethon/repos/agent`.

---

### Task 1: `DeliveryTarget` gains a `Minted` flag

`ResolveDeliveryTargetsAsync` mints a conversation when a `ReplyTo` target has a null conversation id. The announce step (Task 3) must skip just-minted targets — their `create_conversation` already set up the stream; a second call would double-increment the stream's pending count and wedge it open. Record minted-ness on the target.

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs` (lines 26, 81-85)
- Test: `Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs`

- [ ] **Step 1: Write the failing test**

Append to `ChatMonitorDeliveryTests` (inside the existing class):

```csharp
[Fact]
public async Task ResolveDeliveryTargets_MarksMintedTargetsAndPreservesPreExistingOnes()
{
    var origin = Channel("scheduling");
    var channels = new[] { origin, Channel("signalr"), Channel("telegram") };
    var msg = new ChannelMessage
    {
        ConversationId = "fire-1",
        Content = "x",
        Sender = "s",
        ChannelId = "scheduling",
        AgentId = "jonas",
        ReplyTo = [new ReplyTarget("signalr", null), new ReplyTarget("telegram", "t-9")]
    };

    var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

    targets.Count.ShouldBe(2);
    targets.Single(t => t.Channel.ChannelId == "signalr").Minted.ShouldBeTrue();
    targets.Single(t => t.Channel.ChannelId == "telegram").Minted.ShouldBeFalse();
}

[Fact]
public async Task ResolveDeliveryTargets_WithoutReplyTo_OriginTargetIsNotMinted()
{
    var origin = Channel("signalr");
    var msg = new ChannelMessage { ConversationId = "c1", Content = "x", Sender = "u", ChannelId = "signalr" };

    var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, [origin], CancellationToken.None);

    targets.ShouldHaveSingleItem().Minted.ShouldBeFalse();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitorDeliveryTests" 2>&1 | tail -20`
Expected: compile error — `'DeliveryTarget' does not contain a definition for 'Minted'`.

- [ ] **Step 3: Implement**

In `Domain/Monitor/ChatMonitor.cs` change line 26:

```csharp
public readonly record struct DeliveryTarget(IChannelConnection Channel, string ConversationId, bool Minted = false);
```

And in `ResolveDeliveryTargetsAsync`, change the `targets.Add` inside the `ReplyTo` loop (line 84):

```csharp
if (conversationId is not null)
{
    shared ??= conversationId;
    targets.Add(new DeliveryTarget(channel, conversationId, Minted: target.ConversationId is null));
}
```

The no-`ReplyTo` branch (line 37) stays as-is — the default `Minted = false` is correct there.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitorDeliveryTests" 2>&1 | tail -5`
Expected: all pass (existing tests construct `DeliveryTarget` positionally with two args, unaffected by the defaulted third).

- [ ] **Step 5: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs
git commit -m "feat: DeliveryTarget records whether its conversation was minted"
```

---

### Task 2: `ChatMonitor.AnnounceTurnStartAsync`

A public static helper (same testability pattern as `ResolveDeliveryTargetsAsync`) that announces an agent-initiated turn to delivery targets via `create_conversation` with `existingConversationId`. Skips voice (attach-only; a stray `VoiceDeliveryRegistry.Bind` can expire mid-turn and flush a live reply) and, when asked, minted targets. Failures are logged and swallowed — the reply delivery must never depend on the announce.

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs`
- Create: `Tests/Unit/Domain/Monitor/ChatMonitorAnnounceTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/Domain/Monitor/ChatMonitorAnnounceTests.cs` (no trailing newline):

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.Monitor;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Monitor;

public class ChatMonitorAnnounceTests
{
    private static (Mock<IChannelConnection> Mock, List<(string? InitialPrompt, string? ExistingConversationId)> Calls) Channel(string id)
    {
        var calls = new List<(string?, string?)>();
        var m = new Mock<IChannelConnection>();
        m.SetupGet(c => c.ChannelId).Returns(id);
        m.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? prompt, string? _, string? existing, CancellationToken _) => calls.Add((prompt, existing)))
            .ReturnsAsync((string _, string _, string _, string? _, string? _, string? existing, CancellationToken _) => existing);
        return (m, calls);
    }

    private static ChannelMessage DownloadMessage(string content = "[download-complete] film.mkv")
    {
        return new ChannelMessage
        {
            ConversationId = "7:42",
            Content = content,
            Sender = "fran",
            ChannelId = "library",
            AgentId = "jack",
            Origin = new MessageOrigin(MessageOriginKind.Download, null)
        };
    }

    [Fact]
    public async Task AnnounceTurnStart_PreExistingTarget_CallsCreateConversationWithExistingIdAndPrompt()
    {
        var (signalr, calls) = Channel("signalr");
        var targets = new[] { new ChatMonitor.DeliveryTarget(signalr.Object, "7:42") };

        await ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: true, CancellationToken.None);

        var call = calls.ShouldHaveSingleItem();
        call.ExistingConversationId.ShouldBe("7:42");
        call.InitialPrompt.ShouldBe("[download-complete] film.mkv");
    }

    [Fact]
    public async Task AnnounceTurnStart_MintedTarget_SkippedWhenSkipMintedIsTrue()
    {
        var (signalr, calls) = Channel("signalr");
        var targets = new[] { new ChatMonitor.DeliveryTarget(signalr.Object, "minted-1", Minted: true) };

        await ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: true, CancellationToken.None);

        calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnnounceTurnStart_MintedTarget_AnnouncedWhenSkipMintedIsFalse()
    {
        // A later message reusing the group-level targets sees conversations that were
        // minted by the FIRST message's resolution — for this turn they pre-exist.
        var (signalr, calls) = Channel("signalr");
        var targets = new[] { new ChatMonitor.DeliveryTarget(signalr.Object, "minted-1", Minted: true) };

        await ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: false, CancellationToken.None);

        calls.ShouldHaveSingleItem().ExistingConversationId.ShouldBe("minted-1");
    }

    [Fact]
    public async Task AnnounceTurnStart_VoiceTarget_AlwaysSkipped()
    {
        var (voice, calls) = Channel("voice");
        var targets = new[] { new ChatMonitor.DeliveryTarget(voice.Object, "7:42") };

        await ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: false, CancellationToken.None);

        calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task AnnounceTurnStart_AnnounceThrows_SwallowsAndContinuesToNextTarget()
    {
        var failing = new Mock<IChannelConnection>();
        failing.SetupGet(c => c.ChannelId).Returns("signalr");
        failing.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
        var (telegram, telegramCalls) = Channel("telegram");
        var targets = new[]
        {
            new ChatMonitor.DeliveryTarget(failing.Object, "7:42"),
            new ChatMonitor.DeliveryTarget(telegram.Object, "t-9")
        };

        await Should.NotThrowAsync(
            ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: true, CancellationToken.None));

        telegramCalls.ShouldHaveSingleItem();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitorAnnounceTests" 2>&1 | tail -20`
Expected: compile error — `'ChatMonitor' does not contain a definition for 'AnnounceTurnStartAsync'`.

- [ ] **Step 3: Implement**

Add to `Domain/Monitor/ChatMonitor.cs`, directly after `ResolveDeliveryTargetsAsync` (keeps the two static delivery helpers together):

```csharp
    public static async Task AnnounceTurnStartAsync(
        IReadOnlyList<DeliveryTarget> targets,
        ChannelMessage message,
        bool skipMinted,
        CancellationToken ct,
        ILogger? logger = null)
    {
        // Voice is attach-only: announcing would re-Bind its delivery registry, and a
        // stale binding expiring mid-turn flushes the shared reply accumulator. Its
        // send_reply path needs no turn-start. Channels without a create_conversation
        // tool no-op inside CreateConversationAsync.
        var announceable = targets.Where(t =>
            t.Channel.ChannelId != ChannelProtocol.VoiceChannelId && !(skipMinted && t.Minted));
        foreach (var target in announceable)
        {
            try
            {
                await target.Channel.CreateConversationAsync(
                    message.AgentId ?? "default",
                    topicName: string.Empty,
                    message.Sender,
                    initialPrompt: message.Content,
                    address: null,
                    existingConversationId: target.ConversationId,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The reply itself is persisted regardless; a failed announce only costs
                // live streaming, so it must never abort the turn.
                logger?.LogWarning(ex, "Turn-start announce to {ChannelId} failed; reply will not stream live",
                    target.Channel.ChannelId);
            }
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitorAnnounceTests" 2>&1 | tail -5`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/Monitor/ChatMonitorAnnounceTests.cs
git commit -m "feat: ChatMonitor turn-start announce for agent-initiated messages"
```

---

### Task 3: Wire the announce into `ProcessChatThread`

Announce every agent-initiated message (`Origin` set) to its delivery targets before the agent run starts, so the receiving channel's stream exists before the first reply chunk.

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs` (`ProcessChatThread`, default case of the command switch)
- Test: `Tests/Unit/Domain/MonitorTests.cs`

- [ ] **Step 1: Extend `FakeChannelConnection` capture**

In `Tests/Unit/Domain/MonitorTests.cs`, widen the `CreatedConversations` tuple (line 139) to also capture `ExistingConversationId`:

```csharp
    public List<(string AgentId, string TopicName, string Sender, string? InitialPrompt, string? ExistingConversationId)> CreatedConversations { get; } = [];
```

and update the recording method (line 164):

```csharp
    public Task<string?> CreateConversationAsync(string agentId, string topicName, string sender, string? initialPrompt, string? address, string? existingConversationId, CancellationToken ct)
    {
        CreatedConversations.Add((agentId, topicName, sender, initialPrompt, existingConversationId));
        return Task.FromResult(ConversationIdToReturn);
    }
```

Existing assertions only use `ShouldBeEmpty()` on this list, so they keep compiling.

- [ ] **Step 2: Write the failing wiring test**

Append to `ChatMonitorTests` in `Tests/Unit/Domain/MonitorTests.cs`:

```csharp
    [Fact]
    public async Task Monitor_DownloadOriginIntoExistingConversation_AnnouncesTurnStartBeforeFirstReply()
    {
        // A download alert delivers into an EXISTING WebChat conversation. The channel
        // must receive the turn-start announce (create_conversation with the existing id
        // and the alert text as initialPrompt) BEFORE the first reply chunk, so its
        // stream exists when chunks arrive.
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        FakeChannelConnection? signalrRef = null;
        bool? announcedAtFirstReply = null;
        var signalr = new FakeChannelConnection
        {
            ChannelId = "signalr",
            OnReply = _ => announcedAtFirstReply ??= signalrRef!.CreatedConversations.Count == 1
        };
        signalrRef = signalr;
        signalr.Complete();
        var library = MonitorTestMocks.CreateChannel("library", new ChannelMessage
        {
            ConversationId = "7:42",
            Content = "[download-complete] film.mkv",
            Sender = "fran",
            ChannelId = "library",
            AgentId = "jack",
            Origin = new MessageOrigin(MessageOriginKind.Download, null),
            ReplyTo = [new ReplyTarget("signalr", "7:42")]
        });
        var fakeAgent = MonitorTestMocks.CreateAgent();

        var monitor = new ChatMonitor(
            [library, signalr],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        var announce = signalr.CreatedConversations.ShouldHaveSingleItem();
        announce.ExistingConversationId.ShouldBe("7:42");
        announce.InitialPrompt.ShouldBe("[download-complete] film.mkv");
        announce.Sender.ShouldBe("fran");
        announcedAtFirstReply.ShouldBe(true);
        signalr.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete && r.IsComplete);
    }

    [Fact]
    public async Task Monitor_UserMessageWithoutOrigin_DoesNotAnnounce()
    {
        var threadResolver = MonitorTestMocks.CreateThreadResolver();
        var message = MonitorTestMocks.CreateChannelMessage();
        var channel = MonitorTestMocks.CreateChannel(messages: message);
        var fakeAgent = MonitorTestMocks.CreateAgent();

        var monitor = new ChatMonitor(
            [channel],
            MonitorTestMocks.CreateAgentFactory(fakeAgent),
            MonitorTestMocks.CreateApprovalHandlerFactory(),
            threadResolver,
            new Mock<IMetricsPublisher>().Object,
            null,
            Mock.Of<ILogger<ChatMonitor>>());

        await monitor.Monitor(CancellationToken.None);

        channel.CreatedConversations.ShouldBeEmpty();
        channel.SentReplies.ShouldContain(r => r.ContentType == ReplyContentType.StreamComplete);
    }
```

- [ ] **Step 3: Run tests to verify the new one fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitorTests" 2>&1 | tail -20`
Expected: `Monitor_DownloadOriginIntoExistingConversation_AnnouncesTurnStartBeforeFirstReply` FAILS (`CreatedConversations` empty — `ShouldHaveSingleItem` assertion); `Monitor_UserMessageWithoutOrigin_DoesNotAnnounce` passes (current behavior already never announces).

- [ ] **Step 4: Implement the wiring**

In `Domain/Monitor/ChatMonitor.cs`, `ProcessChatThread`, the default case computes `messageTargets` (lines 188-190). Insert the announce right after it, before `var userMessage = ...`:

```csharp
                        var messageTargets = index == 0 || x.Message.ReplyTo is { Count: > 0 }
                            ? targets
                            : await ResolveDeliveryTargetsAsync(x.Message, x.Channel, channels, linkedCt, logger);
                        // Agent-initiated turns (downloads, schedules) land in conversations
                        // with no live stream on the receiving channel; announce the turn so
                        // the channel can set one up before reply chunks arrive. Targets the
                        // group-opening message minted were announced by their own
                        // create_conversation; later messages reusing the group targets see
                        // those conversations as pre-existing.
                        if (x.Message.Origin is not null)
                        {
                            await AnnounceTurnStartAsync(messageTargets, x.Message, skipMinted: index == 0, linkedCt, logger);
                        }
```

- [ ] **Step 5: Run the monitor test suites**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChatMonitor" 2>&1 | tail -5`
Expected: all pass (delivery, announce, persistence-key, conversation-context, schedule-metrics, and monitor tests).

- [ ] **Step 6: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/MonitorTests.cs
git commit -m "feat: announce agent-initiated turns to delivery channels before streaming"
```

---

### Task 4: SignalR channel attach branch

`CreateConversationTool.McpRun` with `existingConversationId` set becomes the turn-start hook: resolve the session, set up the stream exactly like the interactive path does (seeded `currentPrompt` + buffered user bubble + pending increment), broadcast `OnStreamChanged(Started)` to the space group. Without a session it returns the id untouched (nobody is looking; today's persisted-only behavior).

**Files:**
- Modify: `McpChannelSignalR/McpTools/CreateConversationTool.cs`
- Create: `Tests/Unit/McpChannelSignalR/CreateConversationToolTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelSignalR/CreateConversationToolTests.cs` (no trailing newline):

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class CreateConversationToolTests
{
    private readonly SessionService _sessionService = new();
    private readonly StreamService _streamService;
    private readonly Mock<IHubNotificationSender> _hubSender = new();
    private readonly IServiceProvider _services;

    public CreateConversationToolTests()
    {
        _streamService = new StreamService(
            _sessionService,
            new Mock<IPushNotificationService>().Object,
            new Mock<ILogger<StreamService>>().Object);
        _services = new ServiceCollection()
            .AddSingleton(_sessionService)
            .AddSingleton(_streamService)
            .AddSingleton(_hubSender.Object)
            .BuildServiceProvider();
    }

    private Task<string> AttachAsync(string conversationId, string prompt = "[download-complete] film.mkv")
    {
        return CreateConversationTool.McpRun(
            "jack", string.Empty, "fran", _services,
            initialPrompt: prompt, address: null, existingConversationId: conversationId);
    }

    [Fact]
    public async Task McpRun_AttachWithSession_CreatesStreamSeededWithPromptAndSender()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, "default", "Downloads");

        var result = await AttachAsync("7:42");

        result.ShouldBe("7:42");
        _streamService.IsStreaming("topic-1").ShouldBeTrue();
        var state = _streamService.GetStreamState("topic-1");
        state.ShouldNotBeNull();
        state.CurrentPrompt.ShouldBe("[download-complete] film.mkv");
        state.CurrentSenderId.ShouldBe("fran");
        var userBubble = state.BufferedMessages.ShouldHaveSingleItem();
        userBubble.Content.ShouldBe("[download-complete] film.mkv");
        userBubble.UserMessage.ShouldNotBeNull();
        userBubble.UserMessage.SenderId.ShouldBe("fran");
    }

    [Fact]
    public async Task McpRun_AttachWithSession_BroadcastsStreamStartedToSpaceGroup()
    {
        _sessionService.StartSession("topic-1", "jack", 7, 42, "default", "Downloads");

        await AttachAsync("7:42");

        _hubSender.Verify(s => s.SendToGroupAsync(
            "space:default",
            "OnStreamChanged",
            It.Is<StreamChangedNotification>(n =>
                n.ChangeType == StreamChangeType.Started && n.TopicId == "topic-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_AttachWithoutSession_ReturnsIdWithoutSideEffects()
    {
        var result = await AttachAsync("9:99");

        result.ShouldBe("9:99");
        _streamService.IsStreaming("9:99").ShouldBeFalse();
        _hubSender.Verify(s => s.SendToGroupAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task McpRun_SingleAttachedTurn_TearsDownStreamOnStreamComplete()
    {
        // The attach increments the pending count exactly once, so the turn's single
        // StreamComplete must tear the stream down — a wedged-open stream would show a
        // perpetual typing indicator and suppress the push notification.
        _sessionService.StartSession("topic-1", "jack", 7, 42, "default", "Downloads");
        await AttachAsync("7:42");

        await _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = "7:42",
            Content = string.Empty,
            ContentType = ReplyContentType.StreamComplete,
            IsComplete = true
        });

        _streamService.IsStreaming("topic-1").ShouldBeFalse();
    }

    [Fact]
    public async Task McpRun_AttachDuringActiveUserTurn_JoinsStreamWithoutStealingTeardown()
    {
        // A download alert can land while the user's own turn is streaming. The attach
        // joins the existing stream (one more pending turn); the stream survives the
        // first StreamComplete and tears down after the second.
        _sessionService.StartSession("topic-1", "jack", 7, 42, "default", "Downloads");
        _streamService.GetOrCreateStream("topic-1", "user prompt", "fran", CancellationToken.None);
        _streamService.TryIncrementPending("topic-1");

        await AttachAsync("7:42");
        await _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = "7:42", Content = string.Empty,
            ContentType = ReplyContentType.StreamComplete, IsComplete = true
        });
        _streamService.IsStreaming("topic-1").ShouldBeTrue();

        await _streamService.WriteReplyAsync(new SendReplyParams
        {
            ConversationId = "7:42", Content = string.Empty,
            ContentType = ReplyContentType.StreamComplete, IsComplete = true
        });
        _streamService.IsStreaming("topic-1").ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpChannelSignalR.CreateConversationToolTests" 2>&1 | tail -20`
Expected: FAIL. The current tool ignores `existingConversationId` and runs the create path, which resolves `IConversationFactory` from the provider → `InvalidOperationException: No service for type 'Domain.Contracts.IConversationFactory'`.

- [ ] **Step 3: Implement the attach branch**

In `McpChannelSignalR/McpTools/CreateConversationTool.cs`:

1. Update the `existingConversationId` parameter description (it is no longer unused):

```csharp
        [Description("Existing conversation id: attaches this turn to it (turn-start announce) instead of creating a topic")] string? existingConversationId = null)
```

2. Insert at the top of `McpRun`, before `var p = new CreateConversationParams ...`:

```csharp
        if (existingConversationId is not null)
        {
            return await AttachTurnAsync(existingConversationId, sender, initialPrompt, services);
        }
```

3. Add the private method after `McpRun`:

```csharp
    private static async Task<string> AttachTurnAsync(
        string conversationId,
        string sender,
        string? initialPrompt,
        IServiceProvider services)
    {
        // Turn-start announce for an agent-initiated reply (download alert, schedule
        // result) into an existing conversation. No session means nobody has the topic
        // open on this server — degrade to persisted-only delivery (visible on refresh).
        var sessionService = services.GetRequiredService<SessionService>();
        var topicId = sessionService.GetTopicIdByConversationId(conversationId);
        if (topicId is null || !sessionService.TryGetSession(topicId, out var session) || session is null)
        {
            return conversationId;
        }

        // Mirror the interactive SendMessage idiom: seed the stream with the originating
        // prompt (rendered as the user bubble on resume), buffer the user-role message
        // for already-open browsers, and count this turn toward stream teardown.
        var streamService = services.GetRequiredService<StreamService>();
        streamService.GetOrCreateStream(topicId, initialPrompt ?? string.Empty, sender, CancellationToken.None);
        streamService.TryIncrementPending(topicId);
        if (!string.IsNullOrWhiteSpace(initialPrompt))
        {
            await streamService.WriteMessageAsync(topicId, new ChatStreamMessage
            {
                Content = initialPrompt,
                UserMessage = new UserMessageInfo(sender, DateTimeOffset.UtcNow)
            });
        }

        // Wake viewing clients: their OnStreamChanged(Started) handler resumes the
        // stream (buffered replay + live subscription) without any client changes.
        var spaceSlug = session.SpaceSlug ?? "default";
        var hubSender = services.GetRequiredService<IHubNotificationSender>();
        await hubSender.SendToGroupAsync(
            $"space:{spaceSlug}",
            "OnStreamChanged",
            new StreamChangedNotification(StreamChangeType.Started, topicId, spaceSlug));

        return conversationId;
    }
```

`Domain.DTOs.WebChat` is already imported; `UserMessageInfo` and `ChatStreamMessage` live there. `Domain.Contracts` (for nothing new) and `Domain.DTOs` are already present via existing usings — add `using Domain.DTOs;` only if the compiler asks (it shouldn't; no new types from there).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpChannelSignalR" 2>&1 | tail -5`
Expected: all McpChannelSignalR unit tests pass (new file + existing StreamService/Session/Approval suites).

- [ ] **Step 5: Commit**

```bash
git add McpChannelSignalR/McpTools/CreateConversationTool.cs Tests/Unit/McpChannelSignalR/CreateConversationToolTests.cs
git commit -m "feat: SignalR channel attach branch streams agent-initiated turns live"
```

---

### Task 5: In-proc flow test — attach → send_reply → live subscriber

Exercises the real channel-server seam end-to-end in process: the same `McpRun` entrypoints the agent calls over MCP, real `SessionService` + `StreamService`, a live subscriber standing in for the resumed browser. This is the spec's "E2E" substitute (see plan header).

**Files:**
- Create: `Tests/Unit/McpChannelSignalR/AgentInitiatedStreamingFlowTests.cs`

- [ ] **Step 1: Write the test**

Create `Tests/Unit/McpChannelSignalR/AgentInitiatedStreamingFlowTests.cs` (no trailing newline):

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.McpTools;
using McpChannelSignalR.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelSignalR;

public class AgentInitiatedStreamingFlowTests
{
    [Fact]
    public async Task AttachThenSendReply_DeliversChunksAndCompletionToLiveSubscriber()
    {
        // The full channel-server flow for a download alert, through the same MCP tool
        // entrypoints the agent invokes: turn-start attach, then send_reply chunks. The
        // subscriber plays the role of a browser that resumed after OnStreamChanged(Started).
        var sessionService = new SessionService();
        var streamService = new StreamService(
            sessionService,
            new Mock<IPushNotificationService>().Object,
            new Mock<ILogger<StreamService>>().Object);
        var hubSender = new Mock<IHubNotificationSender>();
        var services = new ServiceCollection()
            .AddSingleton(sessionService)
            .AddSingleton(streamService)
            .AddSingleton<IStreamService>(streamService)
            .AddSingleton(hubSender.Object)
            .BuildServiceProvider();
        sessionService.StartSession("topic-1", "jack", 7, 42, "default", "Downloads");

        await CreateConversationTool.McpRun(
            "jack", string.Empty, "fran", services,
            initialPrompt: "[download-complete] film.mkv", address: null, existingConversationId: "7:42");

        var subscription = streamService.SubscribeToStream("topic-1", CancellationToken.None);
        subscription.ShouldNotBeNull();

        await SendReplyTool.McpRun("7:42", "Your download finished: film.mkv", ReplyContentType.Text, false, "m1", services);
        await SendReplyTool.McpRun("7:42", string.Empty, ReplyContentType.StreamComplete, true, null, services);

        var received = new List<ChatStreamMessage>();
        await foreach (var msg in subscription)
        {
            received.Add(msg);
        }

        received.ShouldContain(m => m.Content == "Your download finished: film.mkv" && m.MessageId == "m1");
        received.ShouldContain(m => m.IsComplete);
        streamService.IsStreaming("topic-1").ShouldBeFalse();
        hubSender.Verify(s => s.SendToGroupAsync(
            "space:default",
            "OnStreamChanged",
            It.Is<StreamChangedNotification>(n => n.ChangeType == StreamChangeType.Started && n.TopicId == "topic-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run it**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AgentInitiatedStreamingFlowTests" 2>&1 | tail -5`
Expected: PASS (Task 4 implemented the behavior; this test pins the cross-component contract, including that `SendReplyTool` resolves the topic through the session the attach relies on). If it fails, debug before proceeding — do not weaken the assertions.

- [ ] **Step 3: Commit**

```bash
git add Tests/Unit/McpChannelSignalR/AgentInitiatedStreamingFlowTests.cs
git commit -m "test: pin attach-to-live-subscriber flow for agent-initiated turns"
```

---

### Task 6: Full suite, docs, manual smoke

**Files:**
- Modify: `CLAUDE.md` (Channel Architecture section)

- [ ] **Step 1: Run the full non-E2E test suite**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" 2>&1 | tail -10`
Expected: no NEW failures. Baseline note: ~148 pre-existing `DockerUnavailableException` failures exist in this WSL environment — compare failure names against a pre-change run if unsure; everything failing must be Docker-related, nothing in `ChatMonitor*`, `McpChannelSignalR`, or `Monitor*` suites.

- [ ] **Step 2: Document the turn-start announce in CLAUDE.md**

In `CLAUDE.md`, Channel Architecture section, the outbound protocol list describes `create_conversation`. Extend the sentence so the attach semantics are discoverable:

Replace:

```markdown
- **Outbound**: `send_reply` tool (agent response → user), `request_approval` tool (tool approval flow), `create_conversation` tool (agent-initiated conversations), `register_agents` tool (agent publishes its catalog to the channel)
```

with:

```markdown
- **Outbound**: `send_reply` tool (agent response → user), `request_approval` tool (tool approval flow), `create_conversation` tool (agent-initiated conversations; with `existingConversationId` it doubles as the turn-start announce — ChatMonitor calls it for agent-initiated messages (`Origin` set) into existing conversations so the SignalR channel can set up a live stream + `OnStreamChanged(Started)` before reply chunks arrive; voice targets are skipped), `register_agents` tool (agent publishes its catalog to the channel)
```

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: document turn-start announce in channel architecture"
```

- [ ] **Step 4: Manual smoke (requires the Docker stack; optional if unavailable)**

1. Rebuild and restart the changed services: `agent` and `mcp-channel-signalr` (see CLAUDE.md "Launching" for the compose command; both images must include this branch).
2. Open the WebChat through Caddy, pick a user identity, open a conversation, and ask the agent to download something small via the library tools.
3. Keep the conversation open (no refresh). When the download completes: the alert text should appear as a user-role bubble, the agent's reply should stream in live, and after it finishes a refresh should show the identical conversation.
4. Negative check: close the topic (navigate away), trigger another download, reopen after completion — the message must be there (persisted path intact).

---

## Self-Review Notes

- Spec coverage: Minted flag + announce (spec "Component Changes" rows 1-2) → Tasks 1-3; attach branch (row 3) → Task 4; "small seam" in StreamService proved unnecessary (attach composes existing public members — `GetOrCreateStream`/`TryIncrementPending`/`WriteMessageAsync`); no-session no-op, pending balance, concurrent-turn join (spec "Edge Cases") → Task 4 tests; voice skip + announce-failure tolerance → Task 2 tests; E2E → substituted (plan header) + Task 6 manual smoke.
- Type consistency: `DeliveryTarget(Channel, ConversationId, Minted)` used identically in Tasks 1-3; `AnnounceTurnStartAsync(targets, message, skipMinted, ct, logger)` signature matches between Task 2 impl and Task 3 call site; attach tests use only public members verified to exist (`IsStreaming`, `GetStreamState`, `TryIncrementPending`, `WriteReplyAsync`, `SubscribeToStream`).
