# Voice Dynamic Threads Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give each voice satellite a dynamic conversation that resets after 5 minutes of inactivity, and make voice-initiated threads appear in WebChat — by routing both channels' conversation creation through one shared component.

**Architecture:** Centralize conversation-id generation (FNV-1a) and topic creation/persistence into `Domain` (`ConversationIdGenerator`, `IConversationFactory`) + `Infrastructure` (`ConversationFactory`). The SignalR channel and a new voice `VoiceConversationManager` both call the shared factory. The voice manager keeps one active conversation per satellite, keyed in memory, with a `TimeProvider`-based idle timer that mints a fresh conversation after expiry while leaving persisted history intact.

**Tech Stack:** .NET 10, xUnit + Shouldly + Moq, `Microsoft.Extensions.Time.Testing.FakeTimeProvider`, StackExchange.Redis, ModelContextProtocol.

---

## Background facts (verified in code)

- `conversationId` format is `"{chatId}:{threadId}"`, where `chatId`/`threadId` are FNV-1a hashes of a GUID `topicId` (`McpChannelSignalR/Services/SessionService.cs:13-26,69-81`).
- The agent persists chat history to `agent-key:{agentId}:{chatId}:{threadId}` and WebChat reads the **same** key (`Infrastructure/StateManagers/RedisThreadStateStore.cs`, `McpChannelSignalR/Services/RedisStateService.cs:GetHistoryAsync`). So a voice `conversationId` in this format makes voice turns visible in WebChat history automatically.
- A thread shows in the WebChat sidebar only if a `TopicMetadata` exists at `topic:{agentId}:{chatId}:{topicId}` (written by `IThreadStateStore.SaveTopicAsync`, identical format in `RedisThreadStateStore` and `RedisStateService`).
- Today `SatelliteSession.ConversationId => SatelliteId` (`McpChannelVoice/Services/SatelliteSession.cs:28`) — one permanent thread per satellite.
- The voice reply path tools receive `conversationId` and currently do `sessions.Get(conversationId)` assuming `conversationId == satelliteId` (`SendReplyTool.cs:33`, `RequestApprovalTool.cs:34`).
- `Tests/Tests.csproj` already references `Microsoft.Extensions.TimeProvider.Testing` (10.6.0). `McpChannelVoice` references both `Domain` and `Infrastructure`.

## File structure

**Create:**
- `Domain/Conversations/ConversationIdentity.cs` — record `(TopicId, ChatId, ThreadId, ConversationId)`.
- `Domain/Conversations/ConversationIdGenerator.cs` — pure FNV-1a id generation.
- `Domain/Conversations/ConversationCreation.cs` — record `(Identity, Topic)`.
- `Domain/Contracts/IConversationFactory.cs` — `CreateAsync`.
- `Infrastructure/Conversations/ConversationFactory.cs` — builds + persists `TopicMetadata`.
- `McpChannelVoice/Services/VoiceConversationManager.cs` — per-satellite active conversation + idle timer.
- Tests: `Tests/Unit/Domain/Conversations/ConversationIdGeneratorTests.cs`, `Tests/Unit/Infrastructure/Conversations/ConversationFactoryTests.cs`, `Tests/Unit/McpChannelVoice/VoiceConversationManagerTests.cs`, `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`.

**Modify:**
- `McpChannelSignalR/Services/SessionService.cs` (use generator)
- `McpChannelSignalR/McpTools/CreateConversationTool.cs` (use factory)
- `McpChannelSignalR/Modules/ConfigModule.cs` (register factory deps)
- `McpChannelVoice/Settings/VoiceSettings.cs` (`ConversationLifetime`)
- `McpChannelVoice/appsettings.json` (`Voice:ConversationLifetime`)
- `McpChannelVoice/Modules/ConfigModule.cs` (register manager + factory deps)
- `McpChannelVoice/Services/SatelliteSession.cs` (remove `ConversationId` alias)
- `McpChannelVoice/Services/TranscriptDispatcher.cs` (resolve via manager)
- `McpChannelVoice/Services/WyomingSatelliteHost.cs` (metric conversation ids)
- `McpChannelVoice/McpTools/SendReplyTool.cs`, `RequestApprovalTool.cs` (resolve satellite)
- Existing tests for `SendReplyTool`/`RequestApproval` get the manager registered in their DI.

> **Conventions:** `.cs` files in this repo have **no trailing newline**; single-line `if` bodies use braces (a pre-commit format hook enforces this and re-stages whole files). Run `dotnet build` before committing each task.

---

### Task 1: ConversationIdGenerator (Domain)

**Files:**
- Create: `Domain/Conversations/ConversationIdentity.cs`
- Create: `Domain/Conversations/ConversationIdGenerator.cs`
- Test: `Tests/Unit/Domain/Conversations/ConversationIdGeneratorTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Conversations;
using Shouldly;

namespace Tests.Unit.Domain.Conversations;

public class ConversationIdGeneratorTests
{
    [Fact]
    public void CreateFor_IsDeterministicForSameTopicId()
    {
        var a = ConversationIdGenerator.CreateFor("topic-abc");
        var b = ConversationIdGenerator.CreateFor("topic-abc");

        a.ChatId.ShouldBe(b.ChatId);
        a.ThreadId.ShouldBe(b.ThreadId);
        a.ConversationId.ShouldBe(b.ConversationId);
    }

    [Fact]
    public void CreateFor_FormatsConversationIdAsChatColonThread()
    {
        var id = ConversationIdGenerator.CreateFor("topic-abc");

        id.ConversationId.ShouldBe($"{id.ChatId}:{id.ThreadId}");
        id.TopicId.ShouldBe("topic-abc");
    }

    [Fact]
    public void CreateFor_ProducesNonNegativeIds()
    {
        var id = ConversationIdGenerator.CreateFor("topic-abc");

        id.ChatId.ShouldBeGreaterThanOrEqualTo(0);
        id.ThreadId.ShouldBeGreaterThanOrEqualTo(0);
        id.ThreadId.ShouldBeLessThanOrEqualTo(0x7FFFFFFF);
    }

    [Fact]
    public void Create_GeneratesDistinctConversations()
    {
        var a = ConversationIdGenerator.Create();
        var b = ConversationIdGenerator.Create();

        a.ConversationId.ShouldNotBe(b.ConversationId);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConversationIdGeneratorTests"`
Expected: FAIL — `ConversationIdGenerator`/`ConversationIdentity` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

`Domain/Conversations/ConversationIdentity.cs`:

```csharp
namespace Domain.Conversations;

public record ConversationIdentity(string TopicId, long ChatId, long ThreadId, string ConversationId);
```

`Domain/Conversations/ConversationIdGenerator.cs` (FNV-1a copied verbatim from `SessionService.GetDeterministicHash` so ids stay byte-for-byte compatible with existing WebChat/agent keys):

```csharp
namespace Domain.Conversations;

public static class ConversationIdGenerator
{
    public static ConversationIdentity Create() => CreateFor(Guid.NewGuid().ToString("N"));

    public static ConversationIdentity CreateFor(string topicId)
    {
        var chatId = GetDeterministicHash(topicId, seed: 0x1234);
        var threadId = GetDeterministicHash(topicId, seed: 0x5678) & 0x7FFFFFFF;
        return new ConversationIdentity(topicId, chatId, threadId, $"{chatId}:{threadId}");
    }

    private static long GetDeterministicHash(string input, long seed)
    {
        const long fnvPrime = 0x100000001b3;
        var hash = unchecked((long)0xcbf29ce484222325) ^ seed;

        foreach (var c in input)
        {
            hash ^= c;
            hash = unchecked(hash * fnvPrime);
        }

        return hash & 0x7FFFFFFFFFFFFFFF;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConversationIdGeneratorTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add Domain/Conversations/ConversationIdentity.cs Domain/Conversations/ConversationIdGenerator.cs Tests/Unit/Domain/Conversations/ConversationIdGeneratorTests.cs
git commit -m "feat(conversations): shared conversation-id generator in Domain"
```

---

### Task 2: ConversationFactory (Domain contract + Infrastructure impl)

**Files:**
- Create: `Domain/Conversations/ConversationCreation.cs`
- Create: `Domain/Contracts/IConversationFactory.cs`
- Create: `Infrastructure/Conversations/ConversationFactory.cs`
- Test: `Tests/Unit/Infrastructure/Conversations/ConversationFactoryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using Infrastructure.Conversations;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Conversations;

public class ConversationFactoryTests
{
    [Fact]
    public async Task CreateAsync_PersistsTopicAndReturnsMatchingIdentity()
    {
        var now = new DateTimeOffset(2026, 6, 3, 10, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(now);
        var store = new Mock<IThreadStateStore>();
        TopicMetadata? saved = null;
        store.Setup(s => s.SaveTopicAsync(It.IsAny<TopicMetadata>()))
            .Callback<TopicMetadata>(t => saved = t)
            .Returns(Task.CompletedTask);

        var sut = new ConversationFactory(store.Object, clock);

        var creation = await sut.CreateAsync(new CreateConversationParams
        {
            AgentId = "agent-1",
            TopicName = "household @ Kitchen",
            Sender = "household",
            InitialPrompt = "what time is it"
        });

        saved.ShouldNotBeNull();
        saved.AgentId.ShouldBe("agent-1");
        saved.Name.ShouldBe("household @ Kitchen");
        saved.SpaceSlug.ShouldBe("default");
        saved.CreatedAt.ShouldBe(now);
        saved.LastMessageAt.ShouldBeNull();

        creation.Topic.ShouldBe(saved);
        creation.Identity.TopicId.ShouldBe(saved.TopicId);
        creation.Identity.ChatId.ShouldBe(saved.ChatId);
        creation.Identity.ThreadId.ShouldBe(saved.ThreadId);
        creation.Identity.ConversationId.ShouldBe($"{saved.ChatId}:{saved.ThreadId}");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConversationFactoryTests"`
Expected: FAIL — `IConversationFactory`/`ConversationCreation`/`ConversationFactory` do not exist.

- [ ] **Step 3: Write minimal implementation**

`Domain/Conversations/ConversationCreation.cs`:

```csharp
using Domain.DTOs.WebChat;

namespace Domain.Conversations;

public record ConversationCreation(ConversationIdentity Identity, TopicMetadata Topic);
```

`Domain/Contracts/IConversationFactory.cs`:

```csharp
using Domain.Conversations;
using Domain.DTOs.Channel;

namespace Domain.Contracts;

public interface IConversationFactory
{
    Task<ConversationCreation> CreateAsync(CreateConversationParams p, CancellationToken ct = default);
}
```

`Infrastructure/Conversations/ConversationFactory.cs`:

```csharp
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;

namespace Infrastructure.Conversations;

public sealed class ConversationFactory(IThreadStateStore store, TimeProvider time) : IConversationFactory
{
    public async Task<ConversationCreation> CreateAsync(CreateConversationParams p, CancellationToken ct = default)
    {
        var identity = ConversationIdGenerator.Create();
        var topic = new TopicMetadata(
            identity.TopicId,
            identity.ChatId,
            identity.ThreadId,
            p.AgentId,
            p.TopicName,
            time.GetUtcNow(),
            LastMessageAt: null,
            SpaceSlug: "default");

        await store.SaveTopicAsync(topic);
        return new ConversationCreation(identity, topic);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConversationFactoryTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Conversations/ConversationCreation.cs Domain/Contracts/IConversationFactory.cs Infrastructure/Conversations/ConversationFactory.cs Tests/Unit/Infrastructure/Conversations/ConversationFactoryTests.cs
git commit -m "feat(conversations): shared ConversationFactory persisting topic metadata"
```

---

### Task 3: SignalR SessionService uses the shared generator

**Files:**
- Modify: `McpChannelSignalR/Services/SessionService.cs:13-26,69-81`

This is a no-behavior-change refactor: replace the inline FNV with `ConversationIdGenerator`. Existing `SessionServiceTests` must stay green.

- [ ] **Step 1: Run the existing tests to confirm they pass first**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SessionServiceTests"`
Expected: PASS (baseline before refactor).

- [ ] **Step 2: Refactor `CreateConversationAsync` and delete the private hash helper**

Replace lines 13-26 (`CreateConversationAsync`) with:

```csharp
    public Task<string> CreateConversationAsync(CreateConversationParams p)
    {
        var id = ConversationIdGenerator.Create();
        StartSession(id.TopicId, p.AgentId, id.ChatId, id.ThreadId, spaceSlug: "default", topicName: p.TopicName);
        return Task.FromResult(id.ConversationId);
    }
```

Delete the entire `GetDeterministicHash` method (lines 69-81). Add `using Domain.Conversations;` to the top of the file.

- [ ] **Step 3: Build**

Run: `dotnet build McpChannelSignalR/McpChannelSignalR.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Run the existing tests to verify they still pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SessionServiceTests"`
Expected: PASS (same set as Step 1).

- [ ] **Step 5: Commit**

```bash
git add McpChannelSignalR/Services/SessionService.cs
git commit -m "refactor(signalr): SessionService uses shared ConversationIdGenerator"
```

---

### Task 4: SignalR CreateConversationTool uses the shared factory

**Files:**
- Modify: `McpChannelSignalR/McpTools/CreateConversationTool.cs`
- Modify: `McpChannelSignalR/Modules/ConfigModule.cs:33-48`

Route topic creation/persistence through `IConversationFactory` so SignalR and voice share one path. No behavior change (same Redis key/format/TTL, same notification, same stream seed).

- [ ] **Step 1: Register the factory and its dependencies in SignalR DI**

In `McpChannelSignalR/Modules/ConfigModule.cs`, inside the `services` chain starting at line 33 (after `.AddSingleton<RedisStateService>()`), add:

```csharp
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IThreadStateStore>(sp =>
                new RedisThreadStateStore(sp.GetRequiredService<IConnectionMultiplexer>(), TimeSpan.FromDays(30)))
            .AddSingleton<IConversationFactory, ConversationFactory>()
```

Add `using Infrastructure.Conversations;` to the file (note `Infrastructure.StateManagers` is already imported on line 4, and `Domain.Contracts` on line 2).

- [ ] **Step 2: Rewrite the tool body to call the factory**

Replace `McpChannelSignalR/McpTools/CreateConversationTool.cs` entirely with:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelSignalR.Services;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class CreateConversationTool
{
    [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
    [Description("Create a new conversation for agent-initiated messages")]
    public static async Task<string> McpRun(
        [Description("Agent identifier")] string agentId,
        [Description("Topic display name")] string topicName,
        [Description("User who initiated")] string sender,
        IServiceProvider services,
        [Description("Text of the originating prompt; rendered as the user-role bubble")] string? initialPrompt = null)
    {
        var p = new CreateConversationParams
        {
            AgentId = agentId,
            TopicName = topicName,
            Sender = sender,
            InitialPrompt = initialPrompt
        };

        // Shared factory generates the conversation identity and persists the topic
        // to Redis (single source of truth shared with the voice channel).
        var factory = services.GetRequiredService<IConversationFactory>();
        var creation = await factory.CreateAsync(p);

        // Register the in-memory session so send_reply/request_approval can resolve it.
        var sessionService = services.GetRequiredService<SessionService>();
        sessionService.StartSession(
            creation.Identity.TopicId, agentId, creation.Identity.ChatId, creation.Identity.ThreadId,
            spaceSlug: "default", topicName: topicName);

        // Notify WebChat clients so the topic appears without refresh.
        var hubSender = services.GetRequiredService<IHubNotificationSender>();
        var notification = new TopicChangedNotification(
            TopicChangeType.Created, creation.Identity.TopicId, creation.Topic, SpaceSlug: "default");
        await hubSender.SendToGroupAsync("space:default", "OnTopicChanged", notification);

        // Create a stream so send_reply chunks have somewhere to go. The stream's
        // currentPrompt seeds the user-role bubble on WebChat, so it must be the
        // originating prompt — falling back to topicName for legacy callers.
        var streamService = services.GetRequiredService<StreamService>();
        streamService.GetOrCreateStream(creation.Identity.TopicId, initialPrompt ?? topicName, sender, CancellationToken.None);

        return creation.Identity.ConversationId;
    }
}
```

(`IServiceProvider` extension methods come from `Microsoft.Extensions.DependencyInjection`, already in scope via the project's global usings; if the build complains, add `using Microsoft.Extensions.DependencyInjection;`.)

- [ ] **Step 3: Build**

Run: `dotnet build McpChannelSignalR/McpChannelSignalR.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Run SignalR-related tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelSignalR"`
Expected: PASS (no regressions; any pre-existing Docker-dependent failures are unrelated baseline).

- [ ] **Step 5: Commit**

```bash
git add McpChannelSignalR/McpTools/CreateConversationTool.cs McpChannelSignalR/Modules/ConfigModule.cs
git commit -m "refactor(signalr): create conversations via shared ConversationFactory"
```

---

### Task 5: Voice config — ConversationLifetime setting

**Files:**
- Modify: `McpChannelVoice/Settings/VoiceSettings.cs`
- Modify: `McpChannelVoice/appsettings.json`
- Test: `Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs` (existing — add one case)

- [ ] **Step 1: Write the failing test**

Open `Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs` and add this test method inside the class (match the file's existing binding style — it builds configuration from an in-memory dictionary; mirror the existing tests there):

```csharp
    [Fact]
    public void ConversationLifetime_DefaultsToFiveMinutes()
    {
        var settings = new VoiceSettings();

        settings.ConversationLifetime.ShouldBe(TimeSpan.FromMinutes(5));
    }
```

Add `using Shouldly;` and `using McpChannelVoice.Settings;` if not already present (they are used by the existing tests in this file).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSettingsBindingTests.ConversationLifetime_DefaultsToFiveMinutes"`
Expected: FAIL — `VoiceSettings` has no `ConversationLifetime`.

- [ ] **Step 3: Add the property**

In `McpChannelVoice/Settings/VoiceSettings.cs`, add to the `VoiceSettings` class:

```csharp
    public TimeSpan ConversationLifetime { get; set; } = TimeSpan.FromMinutes(5);
```

(Use the same property style — `get; set;` vs `get; init;` — as the other top-level properties already on `VoiceSettings`.)

In `McpChannelVoice/appsettings.json`, inside the `"Voice"` object, add:

```json
    "ConversationLifetime": "00:05:00",
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSettingsBindingTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Settings/VoiceSettings.cs McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs
git commit -m "feat(voice): add ConversationLifetime setting (default 5 min)"
```

---

### Task 6: VoiceConversationManager (per-satellite conversation + idle timer)

**Files:**
- Create: `McpChannelVoice/Services/VoiceConversationManager.cs`
- Test: `Tests/Unit/McpChannelVoice/VoiceConversationManagerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.WebChat;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceConversationManagerTests
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static (VoiceConversationManager Sut, Mock<IConversationFactory> Factory) Build(FakeTimeProvider clock)
    {
        var factory = new Mock<IConversationFactory>();
        var counter = 0;
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                counter++;
                var topicId = $"topic-{counter}";
                var identity = ConversationIdGenerator.CreateFor(topicId);
                var topic = new TopicMetadata(topicId, identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", clock.GetUtcNow(), null);
                return new ConversationCreation(identity, topic);
            });

        var sut = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), clock, Lifetime,
            NullLogger<VoiceConversationManager>.Instance);
        return (sut, factory);
    }

    [Fact]
    public async Task FirstUtterance_MintsConversation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, factory) = Build(clock);

        var id = await sut.GetOrCreateAsync(Session(), "agent-1", "hello", default);

        id.ShouldNotBeNullOrWhiteSpace();
        sut.GetActiveConversationId("kitchen-01").ShouldBe(id);
        sut.ResolveSatelliteId(id).ShouldBe("kitchen-01");
        factory.Verify(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SecondUtteranceWithinWindow_ReusesAndRenews()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, factory) = Build(clock);

        var first = await sut.GetOrCreateAsync(Session(), "agent-1", "hello", default);
        clock.Advance(TimeSpan.FromMinutes(4));
        var second = await sut.GetOrCreateAsync(Session(), "agent-1", "again", default);

        second.ShouldBe(first);
        factory.Verify(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()), Times.Once);

        // The renewal reset the timer: 4 more minutes (8 total) must NOT expire it.
        clock.Advance(TimeSpan.FromMinutes(4));
        sut.GetActiveConversationId("kitchen-01").ShouldBe(first);
    }

    [Fact]
    public async Task AfterIdleExpiry_NextUtteranceMintsNewConversation()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, _) = Build(clock);

        var first = await sut.GetOrCreateAsync(Session(), "agent-1", "hello", default);
        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        sut.GetActiveConversationId("kitchen-01").ShouldBeNull();
        sut.ResolveSatelliteId(first).ShouldBeNull();

        var second = await sut.GetOrCreateAsync(Session(), "agent-1", "fresh", default);
        second.ShouldNotBe(first);
        sut.ResolveSatelliteId(second).ShouldBe("kitchen-01");
    }

    [Fact]
    public async Task BuildsTopicNameFromIdentityAndRoom()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var (sut, factory) = Build(clock);

        await sut.GetOrCreateAsync(Session(), "agent-1", "hello", default);

        factory.Verify(f => f.CreateAsync(
            It.Is<CreateConversationParams>(p =>
                p.AgentId == "agent-1" &&
                p.TopicName == "household @ Kitchen" &&
                p.Sender == "household" &&
                p.InitialPrompt == "hello"),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceConversationManagerTests"`
Expected: FAIL — `VoiceConversationManager` does not exist.

- [ ] **Step 3: Write the implementation**

`McpChannelVoice/Services/VoiceConversationManager.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs.Channel;

namespace McpChannelVoice.Services;

// One active conversation per satellite. Each utterance renews a TimeProvider-based
// idle timer; when it fires, the in-memory mapping is dropped so the next utterance
// mints a fresh conversation. Persisted Redis history and the WebChat topic are left
// intact (the timer only clears local routing state).
public sealed class VoiceConversationManager(
    IConversationFactory factory,
    ReplyTextAccumulator accumulator,
    TimeProvider time,
    TimeSpan lifetime,
    ILogger<VoiceConversationManager> logger)
{
    private sealed record Entry(string ConversationId, ITimer Timer);

    private readonly Dictionary<string, Entry> _bySatellite = new();
    private readonly Dictionary<string, string> _conversationToSatellite = new();
    private readonly Lock _gate = new();

    public async Task<string> GetOrCreateAsync(
        SatelliteSession session, string agentId, string firstUtterance, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_bySatellite.TryGetValue(session.SatelliteId, out var existing))
            {
                existing.Timer.Change(lifetime, Timeout.InfiniteTimeSpan);
                return existing.ConversationId;
            }
        }

        var creation = await factory.CreateAsync(
            new CreateConversationParams
            {
                AgentId = agentId,
                TopicName = $"{session.Config.Identity} @ {session.Config.Room}",
                Sender = session.Config.Identity,
                InitialPrompt = firstUtterance
            },
            ct);

        lock (_gate)
        {
            // A concurrent utterance may have created one first; reuse it and drop ours.
            if (_bySatellite.TryGetValue(session.SatelliteId, out var existing))
            {
                existing.Timer.Change(lifetime, Timeout.InfiniteTimeSpan);
                return existing.ConversationId;
            }

            var satelliteId = session.SatelliteId;
            var timer = time.CreateTimer(_ => Expire(satelliteId), null, lifetime, Timeout.InfiniteTimeSpan);
            _bySatellite[satelliteId] = new Entry(creation.Identity.ConversationId, timer);
            _conversationToSatellite[creation.Identity.ConversationId] = satelliteId;
            logger.LogInformation(
                "Voice conversation {ConversationId} opened for satellite {Satellite}",
                creation.Identity.ConversationId, satelliteId);
            return creation.Identity.ConversationId;
        }
    }

    public string? GetActiveConversationId(string satelliteId)
    {
        lock (_gate)
        {
            return _bySatellite.TryGetValue(satelliteId, out var entry) ? entry.ConversationId : null;
        }
    }

    public string? ResolveSatelliteId(string conversationId)
    {
        lock (_gate)
        {
            return _conversationToSatellite.GetValueOrDefault(conversationId);
        }
    }

    private void Expire(string satelliteId)
    {
        lock (_gate)
        {
            if (!_bySatellite.Remove(satelliteId, out var entry))
            {
                return;
            }

            _conversationToSatellite.Remove(entry.ConversationId);
            entry.Timer.Dispose();
            accumulator.Flush(entry.ConversationId);
            logger.LogInformation(
                "Voice conversation {ConversationId} expired for satellite {Satellite}",
                entry.ConversationId, satelliteId);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceConversationManagerTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/VoiceConversationManager.cs Tests/Unit/McpChannelVoice/VoiceConversationManagerTests.cs
git commit -m "feat(voice): per-satellite conversation manager with idle lifetime"
```

---

### Task 7: Register manager + factory in the voice host

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs:37-57`

- [ ] **Step 1: Register dependencies**

In `McpChannelVoice/Modules/ConfigModule.cs`, in the first `services` chain (lines 37-47, the one that registers `IConnectionMultiplexer` and `IMetricsPublisher`), add these registrations:

```csharp
            .AddSingleton(TimeProvider.System)
            .AddSingleton<Domain.Contracts.IThreadStateStore>(sp =>
                new Infrastructure.StateManagers.RedisThreadStateStore(
                    sp.GetRequiredService<IConnectionMultiplexer>(), TimeSpan.FromDays(30)))
            .AddSingleton<Domain.Contracts.IConversationFactory, Infrastructure.Conversations.ConversationFactory>()
```

Then, in the second `services` chain (line 49, which registers `SatelliteSessionRegistry`, `ApprovalCaptureBroker`, `TranscriptDispatcher`), register the manager (it depends on `ReplyTextAccumulator`, which is registered at line 94 — singletons resolve lazily, so order does not matter):

```csharp
            .AddSingleton(sp => new VoiceConversationManager(
                sp.GetRequiredService<Domain.Contracts.IConversationFactory>(),
                sp.GetRequiredService<ReplyTextAccumulator>(),
                sp.GetRequiredService<TimeProvider>(),
                settings.ConversationLifetime,
                sp.GetRequiredService<ILogger<VoiceConversationManager>>()))
```

- [ ] **Step 2: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: Build succeeded. (`TranscriptDispatcher`, `SatelliteSession`, the tools, and the host still reference `session.ConversationId` — those compile until Task 9; this task only adds registrations, no removals, so the build stays green.)

- [ ] **Step 3: Commit**

```bash
git add McpChannelVoice/Modules/ConfigModule.cs
git commit -m "chore(voice): register conversation factory and manager in DI"
```

---

### Task 8: TranscriptDispatcher resolves the conversation via the manager

**Files:**
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs`
- Test: `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class TranscriptDispatcherTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static (TranscriptDispatcher Sut, VoiceConversationManager Manager) Build()
    {
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-x");
                var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        var manager = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        var emitter = new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance);
        var sut = new TranscriptDispatcher(
            emitter, Mock.Of<IMetricsPublisher>(), new ApprovalCaptureBroker(), manager,
            confidenceThreshold: 0.5, NullLogger<TranscriptDispatcher>.Instance);
        return (sut, manager);
    }

    [Fact]
    public async Task DispatchAsync_GoodTranscript_OpensConversationViaManager()
    {
        var (sut, manager) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "what time is it", Confidence = 0.9 }, "agent-1", default);

        ok.ShouldBeTrue();
        var convo = manager.GetActiveConversationId("kitchen-01");
        convo.ShouldNotBeNull();
        manager.ResolveSatelliteId(convo).ShouldBe("kitchen-01");
    }

    [Fact]
    public async Task DispatchAsync_LowConfidence_DoesNotOpenConversation()
    {
        var (sut, manager) = Build();

        var ok = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "mumble", Confidence = 0.1 }, "agent-1", default);

        ok.ShouldBeFalse();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
    }
}
```

> Note: `TranscriptionResult` is constructed via object initializer above. If the real `TranscriptionResult` (in `Domain/DTOs/Voice`) is a positional record, adjust the construction to match its actual shape — read the type before writing the test.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests"`
Expected: FAIL — `TranscriptDispatcher` has no constructor accepting `VoiceConversationManager`.

- [ ] **Step 3: Update the dispatcher**

In `McpChannelVoice/Services/TranscriptDispatcher.cs`, add `VoiceConversationManager manager` to the primary constructor parameter list (place it before `double confidenceThreshold` to keep value-type config last):

```csharp
public sealed class TranscriptDispatcher(
    ChannelNotificationEmitter emitter,
    IMetricsPublisher publisher,
    ApprovalCaptureBroker broker,
    VoiceConversationManager manager,
    double confidenceThreshold,
    ILogger<TranscriptDispatcher> logger)
{
```

In the dropped/low-confidence branch, replace `ConversationId = session.ConversationId` (line 48) with:

```csharp
                    ConversationId = manager.GetActiveConversationId(session.SatelliteId)
```

In the dispatch branch, before the `emitter.EmitMessageNotificationAsync(...)` call, resolve the conversation:

```csharp
        var conversationId = await manager.GetOrCreateAsync(session, agentId ?? string.Empty, transcript.Text, ct);

        await emitter.EmitMessageNotificationAsync(
            new ChannelMessageNotification
            {
                ConversationId = conversationId,
                Sender = session.Config.Identity,
                Content = transcript.Text,
                AgentId = agentId,
                Timestamp = DateTimeOffset.UtcNow
            },
            ct);
```

Replace `ConversationId = session.ConversationId` in the final "dispatched" metric (line 75) with `ConversationId = conversationId`.

Update the `TranscriptDispatcher` DI registration in `McpChannelVoice/Modules/ConfigModule.cs:52-57` to pass the manager:

```csharp
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                sp.GetRequiredService<ApprovalCaptureBroker>(),
                sp.GetRequiredService<VoiceConversationManager>(),
                settings.ConfidenceThreshold,
                sp.GetRequiredService<ILogger<TranscriptDispatcher>>()));
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/TranscriptDispatcher.cs McpChannelVoice/Modules/ConfigModule.cs Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs
git commit -m "feat(voice): dispatch resolves dynamic conversation per utterance"
```

---

### Task 9: SendReplyTool resolves the satellite by conversationId

**Files:**
- Modify: `McpChannelVoice/McpTools/SendReplyTool.cs`
- Modify: `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs` (register manager in DI; add resolution test)

- [ ] **Step 1: Write the failing test**

In `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs`, the constructor builds a service provider without a `VoiceConversationManager`. Update it so the satellite is reached via the manager. First, register a real manager seeded with a known conversation. Add a helper and registration in the test class constructor:

```csharp
    private readonly VoiceConversationManager _manager;
    private string _conversationId = null!;
```

Replace the `_services = new ServiceCollection()...BuildServiceProvider();` block with one that also registers the manager, and add manager construction before it:

```csharp
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-kitchen");
                var topic = new TopicMetadata("topic-kitchen", identity.ChatId, identity.ThreadId,
                    "agent-1", "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });
        _manager = new VoiceConversationManager(
            factory.Object, _accumulator, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), Microsoft.Extensions.Logging.Abstractions.NullLogger<VoiceConversationManager>.Instance);
        _conversationId = _manager.GetOrCreateAsync(_session, "agent-1", "hello", default).GetAwaiter().GetResult();

        _services = new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_manager)
            .AddSingleton(_accumulator)
            .AddSingleton(_tts.Object)
            .AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>())
            .AddSingleton(new VoiceSettings())
            .AddSingleton<ILogger<SendReplyTool>>(NullLogger<SendReplyTool>.Instance)
            .BuildServiceProvider();
```

Add the needed usings: `using Domain.Contracts;`, `using Domain.Conversations;`, `using Domain.DTOs.Channel;`, `using Domain.DTOs.WebChat;`.

Then update existing tests in this file that call `SendReplyTool.McpRun(..., conversationId: "kitchen-01", ...)` (or pass `_session.SatelliteId`) to pass `_conversationId` instead, since the composite id — not the satellite id — is now what the agent sends. Add one explicit test:

```csharp
    [Fact]
    public async Task McpRun_ResolvesSatelliteFromCompositeConversationId()
    {
        var result = await SendReplyTool.McpRun(
            _conversationId, "hola", ReplyContentType.StreamComplete, true, null, _services);

        result.ShouldBe("ok");
        _tts.Verify(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }
```

(Before `StreamComplete`, append text so there is something to flush — mirror how the existing tests in this file drive the accumulator; if they append via a prior `McpRun` call with a text chunk, do the same here.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SendReplyToolTests"`
Expected: FAIL — `SendReplyTool` still does `sessions.Get(conversationId)`, which now treats the composite id as a satellite id, finds nothing, and returns "ok" without speaking. (The `SatelliteSession.ConversationId` alias still exists at this point, so the file compiles; the failure is purely behavioral.)

- [ ] **Step 3: Update the tool**

In `McpChannelVoice/McpTools/SendReplyTool.cs`:

Resolve the satellite via the manager. Replace the registry lookup block (lines 27-37) with:

```csharp
        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var manager = services.GetRequiredService<VoiceConversationManager>();
        var accumulator = services.GetRequiredService<ReplyTextAccumulator>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var settings = services.GetRequiredService<VoiceSettings>();
        var metrics = services.GetRequiredService<IMetricsPublisher>();

        var satelliteId = manager.ResolveSatelliteId(conversationId);
        var session = satelliteId is null ? null : sessions.Get(satelliteId);
        if (session is null)
        {
            return "ok";
        }
```

Change `FlushAndSpeakAsync` to flush by the conversation id rather than `session.ConversationId`. Update its signature to take `string conversationId` and update both call sites:

```csharp
            case ReplyContentType.StreamComplete:
                await FlushAndSpeakAsync(session, conversationId, accumulator, tts, settings, metrics);
                return "ok";

            default:
                accumulator.Append(conversationId, content);
                if (isComplete)
                {
                    await FlushAndSpeakAsync(session, conversationId, accumulator, tts, settings, metrics);
                }
                return "ok";
```

```csharp
    private static async Task FlushAndSpeakAsync(
        SatelliteSession session,
        string conversationId,
        ReplyTextAccumulator accumulator,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics)
    {
        var text = accumulator.Flush(conversationId);
        if (!string.IsNullOrWhiteSpace(text))
        {
            await SpeakAsync(session, text, tts, settings, metrics, default);
        }
    }
```

In `SpeakAsync`, the three `ConversationId = session.ConversationId` metric fields (lines 106, 115, 128) must use a real conversation id. Pass `conversationId` into `SpeakAsync` and use it. Update `SpeakAsync`'s signature to accept `string conversationId` and update both callers (the `Error` case at line 46 passes `conversationId`; `FlushAndSpeakAsync` passes its `conversationId`). Replace each `ConversationId = session.ConversationId` in `SpeakAsync` with `ConversationId = conversationId`.

The `Error` case becomes:

```csharp
            case ReplyContentType.Error:
                await SpeakAsync(session, $"Hubo un error: {content}", conversationId, tts, settings, metrics, default);
                return "ok";
```

And `SpeakAsync` signature:

```csharp
    private static async Task SpeakAsync(
        SatelliteSession session,
        string text,
        string conversationId,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics,
        CancellationToken ct)
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SendReplyToolTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/McpTools/SendReplyTool.cs Tests/Unit/McpChannelVoice/SendReplyToolTests.cs
git commit -m "feat(voice): SendReplyTool resolves satellite by composite conversation id"
```

---

### Task 10: RequestApprovalTool resolves the satellite by conversationId

**Files:**
- Modify: `McpChannelVoice/McpTools/RequestApprovalTool.cs`
- Modify: `Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs` (register manager; pass composite id)

- [ ] **Step 1: Write the failing test**

In `Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs`, register a `VoiceConversationManager` in the test's service provider exactly as in Task 10 (mock `IConversationFactory` returning a `ConversationIdGenerator.CreateFor("topic-kitchen")` identity; seed `_conversationId = manager.GetOrCreateAsync(session, "agent-1", "hi", default).GetAwaiter().GetResult()`), add `.AddSingleton(manager)` to the provider, and change calls that pass the satellite id as `conversationId` to pass the seeded `_conversationId`. Add:

```csharp
    [Fact]
    public async Task McpRun_Notify_ResolvesSatelliteFromCompositeConversationId()
    {
        var result = await RequestApprovalTool.McpRun(
            _conversationId, ApprovalMode.Notify, new List<ToolApprovalRequest>(), _services);

        result.ShouldBe("notified");
    }
```

Add usings: `using Domain.Contracts;`, `using Domain.Conversations;`, `using Domain.DTOs.Channel;`, `using Domain.DTOs.WebChat;`.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RequestApprovalToolTests"`
Expected: FAIL — the tool resolves `sessions.Get(conversationId)` with the composite id, finds nothing, and returns the no-session branch. (The `SatelliteSession.ConversationId` alias still exists at this point, so the file compiles; the failure is behavioral.)

- [ ] **Step 3: Update the tool**

In `McpChannelVoice/McpTools/RequestApprovalTool.cs`, replace the registry lookup (lines 27-38) so it resolves via the manager:

```csharp
        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var manager = services.GetRequiredService<VoiceConversationManager>();
        var broker = services.GetRequiredService<ApprovalCaptureBroker>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var settings = services.GetRequiredService<VoiceSettings>();
        var metrics = services.GetRequiredService<IMetricsPublisher>();
        var accumulator = services.GetRequiredService<ReplyTextAccumulator>();

        var satelliteId = manager.ResolveSatelliteId(conversationId);
        var session = satelliteId is null ? null : sessions.Get(satelliteId);
        if (session is null)
        {
            return mode == ApprovalMode.Notify ? "notified" : "declined";
        }
```

Replace `ConversationId = session.ConversationId` (line 70) in the `ApprovalResolved` metric with `ConversationId = conversationId`. (`accumulator.Flush(conversationId)` at line 46 already uses the parameter — leave it.)

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RequestApprovalToolTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/McpTools/RequestApprovalTool.cs Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs
git commit -m "feat(voice): RequestApprovalTool resolves satellite by composite conversation id"
```

---

### Task 11: Remove the SatelliteSession.ConversationId alias and fix host metrics

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs:28`
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs:19-27,227,251,272`

By now Tasks 8–10 have removed every `session.ConversationId` reference except those in `SatelliteSession` itself and `WyomingSatelliteHost`. This task removes the alias and fixes the last references, so the build returns to green.

- [ ] **Step 1: Remove the alias**

Delete line 28 of `McpChannelVoice/Services/SatelliteSession.cs`:

```csharp
    public string ConversationId => SatelliteId;
```

- [ ] **Step 2: Inject the manager into the host and fix metric references**

In `McpChannelVoice/Services/WyomingSatelliteHost.cs`, add `VoiceConversationManager conversationManager` to the primary constructor (e.g. after `SatelliteSessionRegistry sessionRegistry,`):

```csharp
    SatelliteSessionRegistry sessionRegistry,
    VoiceConversationManager conversationManager,
    ISpeechToText speechToText,
```

Replace each `ConversationId = session.ConversationId` (lines 227, 251, 272) with:

```csharp
            ConversationId = conversationManager.GetActiveConversationId(session.SatelliteId)
```

(`WyomingSatelliteHost` is registered with `AddHostedService<WyomingSatelliteHost>()` at line 91 of ConfigModule, so DI resolves the new parameter automatically — no registration change needed.)

- [ ] **Step 3: Build the voice project**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: Build succeeded. Confirm no stragglers: `grep -rn "session\.ConversationId" McpChannelVoice/ --include=*.cs` returns nothing.

- [ ] **Step 4: Run all voice tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelVoice"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSession.cs McpChannelVoice/Services/WyomingSatelliteHost.cs
git commit -m "refactor(voice): drop static ConversationId alias; host uses active id"
```

---

### Task 12: Full build + verification + final commit

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeded with no errors. (Verify no remaining references to `session.ConversationId` anywhere: `grep -rn "\.ConversationId" McpChannelVoice/ --include=*.cs` should only show local `conversationId` variables / `ChannelMessageNotification.ConversationId` assignments, not `SatelliteSession.ConversationId`.)

- [ ] **Step 2: Run the full unit test suite for the touched areas**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E&(FullyQualifiedName~Conversation|FullyQualifiedName~McpChannelVoice|FullyQualifiedName~McpChannelSignalR)"`
Expected: All listed tests PASS. (Note: the repo has ~148 pre-existing Docker-dependent failures across the broader `Category!=E2E` suite in this WSL environment — those are baseline, not regressions. Confirm none of the new/voice/signalr/conversation tests are among any failures.)

- [ ] **Step 3: Sanity-check the docker-compose wiring (no change expected)**

`ConversationLifetime` is non-secret configuration living in `appsettings.json`, not an environment variable, so no `docker-compose.yml`/`.env` changes are required. Confirm there is nothing to add.

- [ ] **Step 4: Final no-op commit if the format hook adjusted anything**

```bash
git status --short
# If the pre-commit format hook left staged changes from earlier tasks, they are already committed.
# Nothing to do if the tree is clean.
```

---

## Self-review notes

- **Spec coverage:** dynamic per-satellite thread (Tasks 6, 8); 5-min idle with renew (Task 6); cancel + clean on expiry, history kept (Task 6 `Expire`, which only touches in-memory state); WebChat visibility (Tasks 2, 4, 6 — topic persisted via shared factory; history shows via the shared `{chatId}:{threadId}` key); shared/de-drifted creation (Tasks 1–4); config (Task 5); reply-path resolution (Tasks 9, 10); alias removal + host metrics (Task 11).
- **Out of scope (per spec):** live `OnTopicChanged` push for voice and live streaming of voice turns into an open WebChat view — not implemented here.
- **Type consistency:** `ConversationIdentity(TopicId, ChatId, ThreadId, ConversationId)`, `ConversationCreation(Identity, Topic)`, `IConversationFactory.CreateAsync`, `VoiceConversationManager.GetOrCreateAsync/GetActiveConversationId/ResolveSatelliteId/Expire` are used consistently across tasks.
- **Known benign edge:** approval responses go through `ApprovalCaptureBroker` (not the dispatch path), so they do not renew the idle timer; harmless because the approval capture window is 10s, far inside the 5-min lifetime.
