# Voice Schedule Delivery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let a fired schedule speak its result on one or more voice satellites, opt-in only, by making voice a real `deliverTo` channel.

**Architecture:** A `deliverTo` entry may be sub-addressed (`voice`, `voice:all`, `voice:<satelliteId>`). `ScheduleFirePlanner` splits `channelId:address` into a new optional `ReplyTarget.Address`, which `ChatMonitor` threads into `create_conversation`. The voice channel gains a `create_conversation` tool that mints a (WebChat-visible) conversation and binds it to an `AnnounceTarget` in a new `VoiceDeliveryRegistry`; the agent's streamed reply then flows through the normal `send_reply` path, which on stream-complete speaks the accumulated text by reusing the existing `AnnouncementService` (target resolution, offline-drop, per-satellite playback, metrics).

**Tech Stack:** .NET 10, C#, ModelContextProtocol SDK, xUnit + Shouldly + Moq, `Microsoft.Extensions.Time.Testing.FakeTimeProvider`.

**Spec:** `docs/superpowers/specs/2026-06-05-voice-schedule-delivery-design.md`

## Conventions (read before starting)

- **No trailing newline** in any `.cs` file (including tests) — this repo enforces it.
- Run all commands from the repo root `/home/dethon/repos/agent`.
- The `.githooks/pre-commit` hook runs `dotnet format` and re-stages whole files; let it.
- Prefer real dependencies over mocks (see `.claude/rules/testing.md`). This plan constructs a **real** `AnnouncementService` in the `SendReplyTool` test rather than mocking it.
- Baseline: ~148 `Category!=E2E` tests already fail in this environment with `DockerUnavailableException` (pre-existing, not a regression). Verify the **specific** new/changed test classes pass; don't be alarmed by the Docker baseline.

---

### Task 1: `ReplyTarget.Address` + `ScheduleFirePlanner` sub-address split

**Files:**
- Modify: `Domain/DTOs/Channel/ReplyTarget.cs`
- Modify: `McpServerScheduling/Services/ScheduleFirePlanner.cs:13`
- Test: `Tests/Unit/McpServerScheduling/ScheduleFirePlannerTests.cs`

- [ ] **Step 1: Write the failing tests**

Append these three tests to `Tests/Unit/McpServerScheduling/ScheduleFirePlannerTests.cs` (inside the class, after the existing tests):

```csharp
    [Fact]
    public void Plan_VoiceDeliverToWithSatelliteId_SplitsChannelAndAddress()
    {
        var s = new Schedule { Id = "s", AgentId = "mycroft", Prompt = "p", RunAt = DateTime.UtcNow, DeliverTo = ["voice:fran-office-01"], CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null);

        var target = plan.Payload.ReplyTo!.ShouldHaveSingleItem();
        target.ChannelId.ShouldBe("voice");
        target.Address.ShouldBe("fran-office-01");
    }

    [Fact]
    public void Plan_BareVoiceDeliverTo_HasNullAddress()
    {
        var s = new Schedule { Id = "s", AgentId = "mycroft", Prompt = "p", RunAt = DateTime.UtcNow, DeliverTo = ["voice"], CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["signalr"], nextRun: null);

        var target = plan.Payload.ReplyTo!.ShouldHaveSingleItem();
        target.ChannelId.ShouldBe("voice");
        target.Address.ShouldBeNull();
    }

    [Fact]
    public void Plan_NonVoiceDeliverTo_HasNullAddress()
    {
        var s = new Schedule { Id = "s", AgentId = "jack", Prompt = "p", RunAt = DateTime.UtcNow, DeliverTo = ["signalr"], CreatedAt = DateTime.UtcNow };
        var plan = ScheduleFirePlanner.Plan(s, defaultDeliverTo: ["telegram"], nextRun: null);

        plan.Payload.ReplyTo![0].ChannelId.ShouldBe("signalr");
        plan.Payload.ReplyTo![0].Address.ShouldBeNull();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~ScheduleFirePlannerTests"`
Expected: FAIL to compile — `'ReplyTarget' does not contain a definition for 'Address'`.

- [ ] **Step 3: Add `Address` to `ReplyTarget`**

Replace the whole record in `Domain/DTOs/Channel/ReplyTarget.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record ReplyTarget(string ChannelId, string? ConversationId, string? Address = null);
```

- [ ] **Step 4: Split the sub-address in `ScheduleFirePlanner`**

In `McpServerScheduling/Services/ScheduleFirePlanner.cs`, replace line 13 (`var replyTo = channels.Select(c => new ReplyTarget(c, null)).ToList();`) with:

```csharp
        var replyTo = channels.Select(ParseTarget).ToList();
```

and add this private static method to the `ScheduleFirePlanner` class (after the `Plan` method):

```csharp
    private static ReplyTarget ParseTarget(string entry)
    {
        var separator = entry.IndexOf(':');
        if (separator < 0)
        {
            return new ReplyTarget(entry, null);
        }

        var channelId = entry[..separator];
        var address = entry[(separator + 1)..];
        return new ReplyTarget(channelId, null, string.IsNullOrWhiteSpace(address) ? null : address);
    }
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ScheduleFirePlannerTests"`
Expected: PASS (all 5 tests).

- [ ] **Step 6: Commit**

```bash
git add Domain/DTOs/Channel/ReplyTarget.cs McpServerScheduling/Services/ScheduleFirePlanner.cs Tests/Unit/McpServerScheduling/ScheduleFirePlannerTests.cs
git commit -m "feat(voice): carry satellite sub-address on ReplyTarget from schedule deliverTo"
```

---

### Task 2: Thread `address` through `create_conversation` to `ChatMonitor`

Adds `string? address` to `IChannelConnection.CreateConversationAsync`, threads it through `McpChannelConnection` (with an `IsError` guard so a rejected create returns `null`), and has `ChatMonitor` pass `target.Address`. Updates every implementer/mock so the solution compiles, and adds an ignored param to the SignalR tool for wire-compat.

**Files:**
- Modify: `Domain/Contracts/IChannelConnection.cs:31`
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs:196-233`
- Modify: `Domain/Monitor/ChatMonitor.cs:53`
- Modify: `McpChannelSignalR/McpTools/CreateConversationTool.cs:15-20`
- Modify: `Tests/Unit/Domain/MonitorTests.cs:150` (`FakeChannelConnection`)
- Modify: `Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs:392` (`StubChannelConnection`)
- Test: `Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs`

- [ ] **Step 1: Plumb the new parameter everywhere (compile-level), with `ChatMonitor` passing `null` for now**

(a) `Domain/Contracts/IChannelConnection.cs` — change the `CreateConversationAsync` signature:

```csharp
    Task<string?> CreateConversationAsync(
        string agentId,
        string topicName,
        string sender,
        string? initialPrompt,
        string? address,
        CancellationToken ct);
```

(b) `Infrastructure/Clients/Channels/McpChannelConnection.cs` — replace the entire `CreateConversationAsync` method (lines 196-233) with:

```csharp
    public async Task<string?> CreateConversationAsync(
        string agentId,
        string topicName,
        string sender,
        string? initialPrompt,
        string? address,
        CancellationToken ct)
    {
        if (_client is null)
        {
            return null;
        }

        try
        {
            var tools = await _client.ListToolsAsync(cancellationToken: ct);
            if (tools.All(t => t.Name != ChannelProtocol.CreateConversationTool))
            {
                return null;
            }

            var result = await _client.CallToolAsync(
                ChannelProtocol.CreateConversationTool,
                new Dictionary<string, object?>
                {
                    ["agentId"] = agentId,
                    ["topicName"] = topicName,
                    ["sender"] = sender,
                    ["initialPrompt"] = initialPrompt,
                    ["address"] = address
                },
                cancellationToken: ct);

            // A rejected create (e.g. unknown voice satellite) comes back as IsError with the
            // error text as content; treat it as "no conversation" rather than a conversation id.
            if (result.IsError == true)
            {
                return null;
            }

            return result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        }
        catch (McpException)
        {
            return null;
        }
    }
```

(c) `McpChannelSignalR/McpTools/CreateConversationTool.cs` — add an ignored `address` param to `McpRun` so the wire arg binds. Change the signature (lines 15-20) to:

```csharp
    public static async Task<string> McpRun(
        [Description("Agent identifier")] string agentId,
        [Description("Topic display name")] string topicName,
        [Description("User who initiated")] string sender,
        IServiceProvider services,
        [Description("Text of the originating prompt; rendered as the user-role bubble")] string? initialPrompt = null,
        [Description("Unused on this channel; voice uses it for satellite targeting")] string? address = null)
```

(The method body is unchanged — `address` is intentionally ignored here.)

(d) `Tests/Unit/Domain/MonitorTests.cs` — update `FakeChannelConnection.CreateConversationAsync` (line 150) to:

```csharp
    public Task<string?> CreateConversationAsync(string agentId, string topicName, string sender, string? initialPrompt, string? address, CancellationToken ct)
    {
        CreatedConversations.Add((agentId, topicName, sender, initialPrompt));
        return Task.FromResult(ConversationIdToReturn);
    }
```

(e) `Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs` — update `StubChannelConnection.CreateConversationAsync` (lines 392-394) to:

```csharp
    public Task<string?> CreateConversationAsync(
        string agentId, string topicName, string sender, string? initialPrompt, string? address, CancellationToken ct)
        => Task.FromResult<string?>(null);
```

(f) `Domain/Monitor/ChatMonitor.cs` — at the mint call (lines 53-54), add `null` as the address argument **for now** (this is the temporary RED state):

```csharp
                    conversationId = await channel.CreateConversationAsync(
                        message.AgentId ?? "default", "Scheduled task", message.Sender, message.Content, null, ct);
```

- [ ] **Step 2: Update existing mocks + add the threading test**

In `Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs`:

Update the helper mock setup (lines 17-18) to the 6-arg signature:

```csharp
        m.Setup(c => c.CreateConversationAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, string _, string _, string? _, string? _, CancellationToken _) => $"minted-{id}");
```

Update the captor setup in `ResolveDeliveryTargets_WhenMintingConversation_PassesMessageContentAsInitialPrompt` (lines 85-88) to:

```csharp
        captor.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? p, string? _, CancellationToken _) => capturedPrompt = p)
            .ReturnsAsync("minted-signalr");
```

Update the failing-mint setup in `ResolveDeliveryTargets_WhenMintingThrows_SkipsTargetInsteadOfThrowing` (lines 111-113) to:

```csharp
        failing.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection reset"));
```

Add this new test to the class:

```csharp
    [Fact]
    public async Task ResolveDeliveryTargets_ThreadsReplyTargetAddressIntoCreateConversation()
    {
        var origin = Channel("scheduling");
        var captor = new Mock<IChannelConnection>();
        captor.SetupGet(c => c.ChannelId).Returns("voice");
        string? capturedAddress = null;
        captor.Setup(c => c.CreateConversationAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Callback((string _, string _, string _, string? _, string? a, CancellationToken _) => capturedAddress = a)
            .ReturnsAsync("minted-voice");
        var channels = new[] { origin, captor.Object };
        var msg = new ChannelMessage
        {
            ConversationId = "fire-1",
            Content = "The AC is on",
            Sender = "scheduler",
            ChannelId = "scheduling",
            AgentId = "mycroft",
            ReplyTo = [new ReplyTarget("voice", null, "fran-office-01")]
        };

        await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);

        capturedAddress.ShouldBe("fran-office-01");
    }
```

- [ ] **Step 3: Run tests to verify the new test fails (RED)**

Run: `dotnet test --filter "FullyQualifiedName~ChatMonitorDeliveryTests"`
Expected: the 4 existing tests PASS; `ResolveDeliveryTargets_ThreadsReplyTargetAddressIntoCreateConversation` FAILS with `capturedAddress should be "fran-office-01" but was null` (ChatMonitor still passes `null`).

- [ ] **Step 4: Pass the real address in `ChatMonitor` (GREEN)**

In `Domain/Monitor/ChatMonitor.cs`, change the mint call to pass `target.Address` instead of `null`:

```csharp
                    conversationId = await channel.CreateConversationAsync(
                        message.AgentId ?? "default", "Scheduled task", message.Sender, message.Content, target.Address, ct);
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~ChatMonitorDeliveryTests|FullyQualifiedName~MonitorTests"`
Expected: PASS.

- [ ] **Step 6: Build the solution to confirm all implementers compile**

Run: `dotnet build`
Expected: Build succeeded (0 errors).

- [ ] **Step 7: Commit**

```bash
git add Domain/Contracts/IChannelConnection.cs Infrastructure/Clients/Channels/McpChannelConnection.cs Domain/Monitor/ChatMonitor.cs McpChannelSignalR/McpTools/CreateConversationTool.cs Tests/Unit/Domain/MonitorTests.cs Tests/Integration/Benchmarks/JonasInstantiationBenchmark.cs Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs
git commit -m "feat(voice): thread create_conversation address through ChatMonitor"
```

---

### Task 3: `VoiceDeliveryRegistry`

A singleton mapping a minted conversation id to the `AnnounceTarget` it should be spoken on, with idle expiry to backstop leaks.

**Files:**
- Create: `McpChannelVoice/Services/VoiceDeliveryRegistry.cs`
- Test: `Tests/Unit/McpChannelVoice/VoiceDeliveryRegistryTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/VoiceDeliveryRegistryTests.cs`:

```csharp
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceDeliveryRegistryTests
{
    private static VoiceDeliveryRegistry Build(FakeTimeProvider clock, TimeSpan? lifetime = null) =>
        new(clock, lifetime ?? TimeSpan.FromMinutes(5), NullLogger<VoiceDeliveryRegistry>.Instance);

    [Fact]
    public void Bind_ThenResolve_ReturnsTarget()
    {
        var sut = Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var target = new AnnounceTarget { SatelliteId = "office-01" };

        sut.Bind("c1", target);

        sut.Resolve("c1").ShouldBe(target);
    }

    [Fact]
    public void Resolve_UnknownConversation_ReturnsNull()
    {
        var sut = Build(new FakeTimeProvider(DateTimeOffset.UtcNow));

        sut.Resolve("nope").ShouldBeNull();
    }

    [Fact]
    public void Remove_DropsBinding()
    {
        var sut = Build(new FakeTimeProvider(DateTimeOffset.UtcNow));
        sut.Bind("c1", new AnnounceTarget { All = true });

        sut.Remove("c1");

        sut.Resolve("c1").ShouldBeNull();
    }

    [Fact]
    public void Binding_ExpiresAfterIdleLifetime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = Build(clock, TimeSpan.FromMinutes(5));
        sut.Bind("c1", new AnnounceTarget { SatelliteId = "office-01" });

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        sut.Resolve("c1").ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~VoiceDeliveryRegistryTests"`
Expected: FAIL to compile — `VoiceDeliveryRegistry` does not exist.

- [ ] **Step 3: Implement `VoiceDeliveryRegistry`**

Create `McpChannelVoice/Services/VoiceDeliveryRegistry.cs` (other types — `Lock`, `ITimer`, `TimeProvider`, `ILogger`, `Timeout` — come from this project's global usings, same as `VoiceConversationManager.cs`):

```csharp
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

// Maps a schedule-minted conversation id to the satellite selector its reply should be
// spoken on. Populated by the voice create_conversation tool; resolved by send_reply on
// stream-complete. Bindings are removed when the reply is delivered; the idle timer is a
// backstop that drops a binding if the agent run dies before completing.
public sealed class VoiceDeliveryRegistry(
    TimeProvider time,
    TimeSpan lifetime,
    ILogger<VoiceDeliveryRegistry> logger)
{
    private sealed record Entry(AnnounceTarget Target, ITimer Timer);

    private readonly Dictionary<string, Entry> _byConversation = new();
    private readonly Lock _gate = new();

    public void Bind(string conversationId, AnnounceTarget target)
    {
        lock (_gate)
        {
            if (_byConversation.Remove(conversationId, out var existing))
            {
                existing.Timer.Dispose();
            }

            var timer = time.CreateTimer(_ => Expire(conversationId), null, lifetime, Timeout.InfiniteTimeSpan);
            _byConversation[conversationId] = new Entry(target, timer);
        }
    }

    public AnnounceTarget? Resolve(string conversationId)
    {
        lock (_gate)
        {
            return _byConversation.TryGetValue(conversationId, out var entry) ? entry.Target : null;
        }
    }

    public void Remove(string conversationId)
    {
        lock (_gate)
        {
            if (_byConversation.Remove(conversationId, out var entry))
            {
                entry.Timer.Dispose();
            }
        }
    }

    private void Expire(string conversationId)
    {
        lock (_gate)
        {
            if (_byConversation.Remove(conversationId, out var entry))
            {
                entry.Timer.Dispose();
                logger.LogInformation("Voice delivery binding {ConversationId} expired", conversationId);
            }
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~VoiceDeliveryRegistryTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/VoiceDeliveryRegistry.cs Tests/Unit/McpChannelVoice/VoiceDeliveryRegistryTests.cs
git commit -m "feat(voice): add VoiceDeliveryRegistry for scheduled satellite bindings"
```

---

### Task 4: Voice `create_conversation` tool

Mints a WebChat-visible conversation, validates the satellite, binds it, and registers the tool in DI.

**Files:**
- Create: `McpChannelVoice/McpTools/CreateConversationTool.cs`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (register `VoiceDeliveryRegistry` + `.WithTools<CreateConversationTool>()`)
- Test: `Tests/Unit/McpChannelVoice/CreateConversationToolTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/CreateConversationToolTests.cs`:

```csharp
using Domain.Contracts;
using Domain.Conversations;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Domain.DTOs.WebChat;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class CreateConversationToolTests
{
    private readonly VoiceDeliveryRegistry _delivery;
    private readonly IServiceProvider _services;

    public CreateConversationToolTests()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["office-01"] = new() { Identity = "household", Room = "Office" }
        });
        _delivery = new VoiceDeliveryRegistry(
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            NullLogger<VoiceDeliveryRegistry>.Instance);

        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-sched");
                var topic = new TopicMetadata("topic-sched", identity.ChatId, identity.ThreadId, "mycroft",
                    "Scheduled task", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        _services = new ServiceCollection()
            .AddSingleton(registry)
            .AddSingleton(_delivery)
            .AddSingleton<IConversationFactory>(factory.Object)
            .BuildServiceProvider();
    }

    [Fact]
    public async Task McpRun_KnownSatellite_MintsConversationAndBindsSatelliteTarget()
    {
        var convId = await CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "AC reminder", "office-01");

        convId.ShouldNotBeNullOrWhiteSpace();
        var target = _delivery.Resolve(convId);
        target.ShouldNotBeNull();
        target!.SatelliteId.ShouldBe("office-01");
        target.All.ShouldBeNull();
    }

    [Fact]
    public async Task McpRun_AllAddress_BindsAllTarget()
    {
        var convId = await CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "broadcast", "all");

        _delivery.Resolve(convId)!.All.ShouldBe(true);
    }

    [Fact]
    public async Task McpRun_NullAddress_BindsAllTarget()
    {
        var convId = await CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "broadcast", null);

        _delivery.Resolve(convId)!.All.ShouldBe(true);
    }

    [Fact]
    public async Task McpRun_UnknownSatellite_Throws()
    {
        await Should.ThrowAsync<McpException>(() => CreateConversationTool.McpRun(
            "mycroft", "Scheduled task", "scheduler", _services, "hi", "ghost-99"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~CreateConversationToolTests"`
Expected: FAIL to compile — `McpChannelVoice.McpTools.CreateConversationTool` does not exist.

- [ ] **Step 3: Implement the tool**

Create `McpChannelVoice/McpTools/CreateConversationTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class CreateConversationTool
{
    [McpServerTool(Name = ChannelProtocol.CreateConversationTool)]
    [Description("Create a conversation that speaks a scheduled reply on voice satellites")]
    public static async Task<string> McpRun(
        [Description("Agent identifier")] string agentId,
        [Description("Topic display name")] string topicName,
        [Description("User who initiated")] string sender,
        IServiceProvider services,
        [Description("Text of the originating prompt")] string? initialPrompt = null,
        [Description("Satellite selector: a satellite id, or 'all'/empty for every satellite")] string? address = null)
    {
        var registry = services.GetRequiredService<SatelliteRegistry>();
        var delivery = services.GetRequiredService<VoiceDeliveryRegistry>();
        var factory = services.GetRequiredService<IConversationFactory>();

        var target = ParseTarget(address);
        if (target.SatelliteId is not null && registry.GetById(target.SatelliteId) is null)
        {
            throw new McpException($"Unknown voice satellite '{target.SatelliteId}'");
        }

        var creation = await factory.CreateAsync(new CreateConversationParams
        {
            AgentId = agentId,
            TopicName = topicName,
            Sender = sender,
            InitialPrompt = initialPrompt
        });

        delivery.Bind(creation.Identity.ConversationId, target);
        return creation.Identity.ConversationId;
    }

    private static AnnounceTarget ParseTarget(string? address) =>
        string.IsNullOrWhiteSpace(address) || address.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? new AnnounceTarget { All = true }
            : new AnnounceTarget { SatelliteId = address };
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~CreateConversationToolTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Register the registry and tool in DI**

In `McpChannelVoice/Modules/ConfigModule.cs`:

Add the registry registration immediately after the `VoiceConversationManager` registration (after line 67, the closing `));` of the `AddSingleton(sp => new VoiceConversationManager(...))`):

```csharp
        services.AddSingleton(sp => new VoiceDeliveryRegistry(
            sp.GetRequiredService<TimeProvider>(),
            settings.ConversationLifetime,
            sp.GetRequiredService<ILogger<VoiceDeliveryRegistry>>()));
```

Add the tool registration right after `.WithTools<RegisterAgentsTool>()` (line 116):

```csharp
            .WithTools<CreateConversationTool>()
```

- [ ] **Step 6: Build to confirm DI wiring compiles**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/McpTools/CreateConversationTool.cs McpChannelVoice/Modules/ConfigModule.cs Tests/Unit/McpChannelVoice/CreateConversationToolTests.cs
git commit -m "feat(voice): add create_conversation tool binding scheduled deliveries to satellites"
```

---

### Task 5: `send_reply` scheduled-delivery branch

When `send_reply` targets a delivery-bound conversation (not an utterance), accumulate the reply and, on stream-complete, speak it via `AnnouncementService`. Errors are dropped (silence preferred).

**Files:**
- Modify: `McpChannelVoice/McpTools/SendReplyTool.cs`
- Test: `Tests/Unit/McpChannelVoice/SendReplyToolScheduledDeliveryTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/SendReplyToolScheduledDeliveryTests.cs`:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Voice;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SendReplyToolScheduledDeliveryTests
{
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly VoiceDeliveryRegistry _delivery;
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly SatelliteConfig _config = new() { Identity = "household", Room = "Office" };
    private readonly IServiceProvider _services;

    public SendReplyToolScheduledDeliveryTests()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig> { ["office-01"] = _config });
        _delivery = new VoiceDeliveryRegistry(
            new FakeTimeProvider(DateTimeOffset.UtcNow), TimeSpan.FromMinutes(5),
            NullLogger<VoiceDeliveryRegistry>.Instance);

        var factory = new Mock<IConversationFactory>();
        var manager = new VoiceConversationManager(
            factory.Object, _accumulator, new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        _tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => EmptyAudio(text));

        var settings = new VoiceSettings();
        var metrics = Mock.Of<IMetricsPublisher>();
        var announcer = new AnnouncementService(
            registry, _sessions, _tts.Object, settings, metrics, NullLogger<AnnouncementService>.Instance);

        _services = new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(registry)
            .AddSingleton(_delivery)
            .AddSingleton(_accumulator)
            .AddSingleton(manager)
            .AddSingleton(_tts.Object)
            .AddSingleton(metrics)
            .AddSingleton(settings)
            .AddSingleton(announcer)
            .AddSingleton<ILogger<SendReplyTool>>(NullLogger<SendReplyTool>.Instance)
            .BuildServiceProvider();
    }

    private static async IAsyncEnumerable<AudioChunk> EmptyAudio(string label)
    {
        yield return new AudioChunk
        {
            Data = System.Text.Encoding.UTF8.GetBytes(label),
            Format = AudioFormat.WyomingStandard
        };
        await Task.Yield();
    }

    private void RegisterLiveSession() => _sessions.Register(new SatelliteSession("office-01", _config));

    [Fact]
    public async Task ScheduledDelivery_OnStreamComplete_AnnouncesAccumulatedTextToSatellite()
    {
        RegisterLiveSession();
        _delivery.Bind("sched-conv", new AnnounceTarget { SatelliteId = "office-01" });

        await SendReplyTool.McpRun("sched-conv", "The AC ", ReplyContentType.Text, false, null, _services);
        await SendReplyTool.McpRun("sched-conv", "is on.", ReplyContentType.Text, false, null, _services);
        await SendReplyTool.McpRun("sched-conv", "", ReplyContentType.StreamComplete, true, null, _services);

        _tts.Verify(t => t.SynthesizeAsync("The AC is on.", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
        _delivery.Resolve("sched-conv").ShouldBeNull();
    }

    [Fact]
    public async Task ScheduledDelivery_Error_DoesNotSpeakAndUnbinds()
    {
        RegisterLiveSession();
        _delivery.Bind("sched-conv", new AnnounceTarget { SatelliteId = "office-01" });

        await SendReplyTool.McpRun("sched-conv", "partial", ReplyContentType.Text, false, null, _services);
        var result = await SendReplyTool.McpRun("sched-conv", "boom", ReplyContentType.Error, false, null, _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
        _delivery.Resolve("sched-conv").ShouldBeNull();
    }

    [Fact]
    public async Task ScheduledDelivery_OfflineSatellite_DoesNotThrowOrSpeak()
    {
        // Configured satellite but no live session registered -> AnnouncementService records "offline".
        _delivery.Bind("sched-conv", new AnnounceTarget { SatelliteId = "office-01" });

        await SendReplyTool.McpRun("sched-conv", "anyone home?", ReplyContentType.Text, false, null, _services);
        var result = await SendReplyTool.McpRun("sched-conv", "", ReplyContentType.StreamComplete, true, null, _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnboundConversation_ReturnsOkWithoutSpeaking()
    {
        var result = await SendReplyTool.McpRun("never-seen", "hi", ReplyContentType.Text, true, null, _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail (RED)**

Run: `dotnet test --filter "FullyQualifiedName~SendReplyToolScheduledDeliveryTests"`
Expected: `ScheduledDelivery_OnStreamComplete_AnnouncesAccumulatedTextToSatellite` FAILS — `SynthesizeAsync("The AC is on.")` was called `0` times (current `McpRun` returns `"ok"` for an unresolved satellite without speaking). The other three may pass incidentally; the first must fail.

- [ ] **Step 3: Refactor `SendReplyTool.McpRun` to add the scheduled branch**

In `McpChannelVoice/McpTools/SendReplyTool.cs`, add `using Microsoft.Extensions.Logging;` to the usings. Replace the `McpRun` method (lines 19-76) with the following (the existing utterance switch is moved verbatim into `HandleUtteranceReplyAsync`):

```csharp
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Speak a response chunk on the originating voice satellite")]
    public static async Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services)
    {
        var p = new SendReplyParams
        {
            ConversationId = conversationId,
            Content = content,
            ContentType = contentType,
            IsComplete = isComplete,
            MessageId = messageId
        };

        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var manager = services.GetRequiredService<VoiceConversationManager>();
        var accumulator = services.GetRequiredService<ReplyTextAccumulator>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var settings = services.GetRequiredService<VoiceSettings>();
        var metrics = services.GetRequiredService<IMetricsPublisher>();

        var satelliteId = manager.ResolveSatelliteId(p.ConversationId);
        var session = satelliteId is null ? null : sessions.Get(satelliteId);
        if (session is not null)
        {
            return await HandleUtteranceReplyAsync(session, p, accumulator, tts, settings, metrics);
        }

        var delivery = services.GetRequiredService<VoiceDeliveryRegistry>();
        var target = delivery.Resolve(p.ConversationId);
        if (target is not null)
        {
            var announcer = services.GetRequiredService<AnnouncementService>();
            var logger = services.GetRequiredService<ILogger<SendReplyTool>>();
            return await HandleScheduledDeliveryAsync(p, target, delivery, accumulator, announcer, logger);
        }

        return "ok";
    }

    private static async Task<string> HandleUtteranceReplyAsync(
        SatelliteSession session,
        SendReplyParams p,
        ReplyTextAccumulator accumulator,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics)
    {
        switch (p.ContentType)
        {
            case ReplyContentType.Reasoning:
            case ReplyContentType.ToolCall:
                return "ok";

            case ReplyContentType.Error:
                await SpeakAsync(session, $"Hubo un error: {p.Content}", p.ConversationId, tts, settings, metrics, default);
                return "ok";

            // Completion arrives as a dedicated StreamComplete event (empty content, no
            // messageId). Text chunks are never flagged complete, so this is where we
            // speak the accumulated reply.
            case ReplyContentType.StreamComplete:
                await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics);
                return "ok";

            default:
                accumulator.Append(p.ConversationId, p.Content);
                // Defensive: honor an explicitly-completed text chunk if a transport ever sends one.
                if (p.IsComplete)
                {
                    await FlushAndSpeakAsync(session, accumulator, p.ConversationId, tts, settings, metrics);
                }
                return "ok";
        }
    }

    private static async Task<string> HandleScheduledDeliveryAsync(
        SendReplyParams p,
        AnnounceTarget target,
        VoiceDeliveryRegistry delivery,
        ReplyTextAccumulator accumulator,
        AnnouncementService announcer,
        ILogger<SendReplyTool> logger)
    {
        switch (p.ContentType)
        {
            case ReplyContentType.Reasoning:
            case ReplyContentType.ToolCall:
                return "ok";

            // An unsolicited scheduled delivery prefers silence over announcing a failure
            // (e.g. at night). Drop the buffer and the binding without speaking.
            case ReplyContentType.Error:
                accumulator.Flush(p.ConversationId);
                delivery.Remove(p.ConversationId);
                logger.LogWarning("Scheduled voice delivery {ConversationId} errored; not speaking", p.ConversationId);
                return "ok";

            case ReplyContentType.StreamComplete:
                await AnnounceAccumulatedAsync(p.ConversationId, target, delivery, accumulator, announcer, logger);
                return "ok";

            default:
                accumulator.Append(p.ConversationId, p.Content);
                if (p.IsComplete)
                {
                    await AnnounceAccumulatedAsync(p.ConversationId, target, delivery, accumulator, announcer, logger);
                }
                return "ok";
        }
    }

    private static async Task AnnounceAccumulatedAsync(
        string conversationId,
        AnnounceTarget target,
        VoiceDeliveryRegistry delivery,
        ReplyTextAccumulator accumulator,
        AnnouncementService announcer,
        ILogger<SendReplyTool> logger)
    {
        var text = accumulator.Flush(conversationId);
        delivery.Remove(conversationId);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        try
        {
            await announcer.AnnounceAsync(new AnnounceRequest { Target = target, Text = text }, default);
        }
        catch (AnnounceTargetNotFoundException ex)
        {
            logger.LogWarning(ex, "Scheduled voice delivery {ConversationId} had no matching satellites", conversationId);
        }
    }
```

(Leave the existing `FlushAndSpeakAsync` and `SpeakAsync` private methods unchanged.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~SendReplyToolScheduledDeliveryTests|FullyQualifiedName~SendReplyToolTests"`
Expected: PASS (both the new scheduled-delivery tests and the existing `SendReplyToolTests` utterance tests).

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/McpTools/SendReplyTool.cs Tests/Unit/McpChannelVoice/SendReplyToolScheduledDeliveryTests.cs
git commit -m "feat(voice): speak scheduled deliveries via AnnouncementService on stream-complete"
```

---

### Task 6: Scheduling prompt — voice delivery guidance

Teach the agent the voice sub-address syntax and the opt-in-only rule. This is prompt content (no brittle string-assertion test, per the repo's no-low-signal-test convention); verified by build.

**Files:**
- Modify: `Domain/Prompts/SchedulingPrompt.cs:34`

- [ ] **Step 1: Update the `deliverTo` documentation**

In `Domain/Prompts/SchedulingPrompt.cs`, replace the `deliverTo` bullet (line 34) with:

```
        - `deliverTo` (optional) — a list of channel ids that should receive the result (e.g. `["signalr", "telegram"]`). Omit to use the configured default.

          **Voice delivery (speak the result aloud).** A `deliverTo` entry may target the voice channel:
          - `"voice"` or `"voice:all"` — speak on every voice satellite.
          - `"voice:<satelliteId>"` — speak on one specific satellite (e.g. `"voice:fran-office-01"`).

          Add a voice target **only when the user explicitly asked to be notified by voice** (spoken aloud / announced). Otherwise omit voice — **silence is the default**. For example, a schedule that starts the air conditioning at night must NOT announce. Offline satellites are skipped silently. To keep tool-approval prompts answerable, list a non-voice channel first, e.g. `["signalr", "voice:fran-office-01"]`.
```

- [ ] **Step 2: Build to confirm the string literal is valid**

Run: `dotnet build Domain/Domain.csproj`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Domain/Prompts/SchedulingPrompt.cs
git commit -m "docs(voice): teach scheduling prompt the voice deliverTo opt-in syntax"
```

---

### Task 7: Full-solution verification

**Files:** none (verification only)

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Run all touched unit-test classes together**

Run:
```bash
dotnet test --filter "FullyQualifiedName~ScheduleFirePlannerTests|FullyQualifiedName~ChatMonitorDeliveryTests|FullyQualifiedName~MonitorTests|FullyQualifiedName~VoiceDeliveryRegistryTests|FullyQualifiedName~CreateConversationToolTests|FullyQualifiedName~SendReplyTool"
```
Expected: PASS (all). If any fail, fix before proceeding — these are all non-Docker unit tests, so failures here are real regressions, not the Docker baseline.

- [ ] **Step 3: Confirm no stray uncommitted changes**

Run: `git status`
Expected: clean working tree (all six task commits in place).

---

## Self-Review

**Spec coverage:**
- Sub-addressed `deliverTo` (`voice` / `voice:all` / `voice:<id>`) → Task 1 (split) + Task 6 (prompt).
- `Address` on `ReplyTarget` + `create_conversation` → Tasks 1–2.
- Voice `create_conversation` minting + binding → Task 4.
- `VoiceDeliveryRegistry` with idle expiry → Task 3.
- `send_reply` scheduled branch reusing `AnnouncementService`; errors dropped → Task 5.
- Offline = silent (logged) → Task 5 (`ScheduledDelivery_OfflineSatellite_DoesNotThrowOrSpeak`).
- Unknown satellite = dropped, nothing persisted → Task 2 (`IsError` guard) + Task 4 (throws `McpException`).
- Opt-in-only rule → Task 6 (prompt).
- Approval mitigation (non-voice channel first) → documented in Task 6 prompt.

**Type consistency:** `CreateConversationAsync(... string? address, CancellationToken ct)` is consistent across interface, impl, both fakes, and all Moq setups (6-arg). `AnnounceTarget` / `AnnounceRequest` shapes match `Domain/DTOs/Voice`. `VoiceDeliveryRegistry.Bind/Resolve/Remove` names match across Tasks 3–5.

**Out of scope (by spec):** room targeting, offline queue/replay, code-level opt-in guard, satellite-catalog discovery for the agent.
