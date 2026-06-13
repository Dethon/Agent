# ChatMonitor Readability Refactor Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split `Domain/Monitor/ChatMonitor.cs` (414 lines, 4 mixed concerns) into focused collaborators with zero behavior change.

**Architecture:** Extract `DeliveryTarget` (top-level record), `DeliveryTargetResolver` (fan-out/minting + turn announce + conversation context), `ReplyDispatcher` (update mapping + per-target delivery), and a `ScheduleExecutionEvent.FromMessage` DTO factory. ChatMonitor keeps its constructor (no DI churn), builds collaborators as fields, and `ProcessChatThread`'s 137-line body decomposes into named private methods.

**Tech Stack:** .NET 10, xUnit + Shouldly + Moq. Spec: `docs/superpowers/specs/2026-06-13-chatmonitor-readability-refactor-design.md`.

**This is a behavior-preserving refactor.** No new tests are written (RED steps don't apply — this is the Refactor leg of Red-Green-Refactor). The existing suite pins behavior; it must be green after every task. Every "why" comment in ChatMonitor.cs moves verbatim with its code — do not drop or reword them.

**Project conventions that WILL bite you if ignored:**
- **NO trailing newline in any `.cs` file** (project-wide convention, enforced across all 226 files). Every file you create or rewrite must end on the last character of the last line.
- The pre-commit hook runs `dotnet format` and re-stages whole files. `git add` explicit paths only.
- Commit on the current branch (`subscription-refactor`). Never switch branches.
- File-scoped namespaces, primary constructors, no XML doc comments.
- Test command: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Monitor"` covers all monitor tests — 46 at baseline (note: `MonitorTests.cs` contains class `ChatMonitorTests` in namespace `Tests.Unit.Domain`, so the broad `~Monitor` substring is required to catch it).

---

### Task 1: Green baseline

**Files:** none modified.

- [ ] **Step 1.1: Run the monitor test suite and record the baseline**

Run:
```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Monitor"
```

Expected: all tests pass (failed: 0). Note the passed count — every later task must end with the same count passing. If anything fails here, STOP and report; do not start the refactor on a red baseline.

---

### Task 2: Extract `DeliveryTarget` to its own file

**Files:**
- Create: `Domain/Monitor/DeliveryTarget.cs`
- Modify: `Domain/Monitor/ChatMonitor.cs:26` (remove nested record)
- Modify: `Tests/Unit/Domain/Monitor/ChatMonitorAnnounceTests.cs` (7 occurrences)
- Modify: `Tests/Unit/Domain/Monitor/ChatMonitorConversationContextTests.cs` (1 occurrence)

- [ ] **Step 2.1: Create `Domain/Monitor/DeliveryTarget.cs`**

Exact content (no trailing newline):

```csharp
using Domain.Contracts;

namespace Domain.Monitor;

public readonly record struct DeliveryTarget(IChannelConnection Channel, string ConversationId, bool Minted = false, string? Address = null);
```

- [ ] **Step 2.2: Remove the nested record from ChatMonitor**

In `Domain/Monitor/ChatMonitor.cs`, delete line 26:

```csharp
    public readonly record struct DeliveryTarget(IChannelConnection Channel, string ConversationId, bool Minted = false, string? Address = null);
```

All `DeliveryTarget` references inside ChatMonitor resolve to the new top-level type (same namespace) — no other edits needed in this file.

- [ ] **Step 2.3: Re-point test references**

Replace every `ChatMonitor.DeliveryTarget` with `DeliveryTarget`:
- `Tests/Unit/Domain/Monitor/ChatMonitorAnnounceTests.cs` — 7 occurrences (lines 40, 53, 66, 80, 93, 110, 111).
- `Tests/Unit/Domain/Monitor/ChatMonitorConversationContextTests.cs` — 1 occurrence (line 49).

Example (`ChatMonitorAnnounceTests.cs:40`):

```csharp
// before
var targets = new[] { new ChatMonitor.DeliveryTarget(signalr.Object, "7:42") };
// after
var targets = new[] { new DeliveryTarget(signalr.Object, "7:42") };
```

- [ ] **Step 2.4: Build and run the monitor tests**

Run:
```bash
dotnet build Domain && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Monitor"
```

Expected: build clean, same passed count as Task 1, failed: 0.

- [ ] **Step 2.5: Commit**

```bash
git add Domain/Monitor/DeliveryTarget.cs Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/Monitor/ChatMonitorAnnounceTests.cs Tests/Unit/Domain/Monitor/ChatMonitorConversationContextTests.cs
git commit -m "refactor: promote DeliveryTarget to top-level record in Domain.Monitor"
```

---

### Task 3: Extract `DeliveryTargetResolver`

**Files:**
- Create: `Domain/Monitor/DeliveryTargetResolver.cs`
- Modify: `Domain/Monitor/ChatMonitor.cs` (remove `ResolveDeliveryTargetsAsync`, `AnnounceTurnStartAsync`, `BuildConversationContext`; add field; update 4 call sites)
- Rename: `Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs` → `DeliveryTargetResolverTests.cs`
- Rename: `Tests/Unit/Domain/Monitor/ChatMonitorAnnounceTests.cs` → `DeliveryTargetResolverAnnounceTests.cs`
- Modify: `Tests/Unit/Domain/Monitor/ChatMonitorConversationContextTests.cs` (2 call sites)

- [ ] **Step 3.1: Create `Domain/Monitor/DeliveryTargetResolver.cs`**

The three method bodies are MOVED VERBATIM from `ChatMonitor.cs` (including every comment). The only changes: `channels` and `logger` come from the primary constructor instead of parameters, so `logger?.LogWarning` becomes `logger.LogWarning`, and the methods lose their `static` modifier (except `BuildConversationContext`, which stays static). Exact content (no trailing newline):

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class DeliveryTargetResolver(IReadOnlyList<IChannelConnection> channels, ILogger logger)
{
    public async Task<IReadOnlyList<DeliveryTarget>> ResolveAsync(
        ChannelMessage message,
        IChannelConnection originChannel,
        CancellationToken ct)
    {
        if (message.ReplyTo is not { Count: > 0 })
        {
            return [new DeliveryTarget(originChannel, message.ConversationId)];
        }

        var targets = new List<DeliveryTarget>();
        // The first resolved conversation anchors a shared id for the whole fan-out. A
        // schedule delivering to both a WebChat channel and voice should surface as ONE
        // conversation — displayed by WebChat and spoken by voice — not a populated thread
        // plus an empty duplicate. Later targets that need minting attach to this id (the
        // voice channel binds its satellite to it instead of persisting its own topic).
        //
        // Attach-only channels (a config-declared capability, e.g. voice) return the id they
        // are handed instead of persisting a topic, so a topic-owning channel must anchor:
        // order attach-only targets last regardless of how the schedule listed them. This
        // also makes targets[0] — the chat-history persistence + approval-routing anchor — a
        // channel that actually displays the conversation. OrderBy is stable, so the
        // author's ordering is otherwise preserved.
        var replyTo = message.ReplyTo
            .OrderBy(t => channels.FirstOrDefault(c => c.ChannelId == t.ChannelId)?.AttachOnly == true ? 1 : 0)
            .ToList();
        string? shared = null;
        foreach (var target in replyTo)
        {
            var channel = channels.FirstOrDefault(c => c.ChannelId == target.ChannelId);
            if (channel is null)
            {
                continue;
            }

            var conversationId = target.ConversationId;
            var wasMinted = false;
            if (conversationId is null)
            {
                try
                {
                    conversationId = await channel.CreateConversationAsync(
                        message.AgentId ?? "default", "Scheduled task", message.Sender, message.Content, target.Address, shared, ct);
                    wasMinted = true;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A target whose conversation can't be minted is skipped rather than
                    // aborting the whole fan-out (and the agent run that depends on it).
                    logger.LogWarning(ex, "Failed to mint conversation on {ChannelId}; skipping target", target.ChannelId);
                    continue;
                }
            }

            if (conversationId is not null)
            {
                shared ??= conversationId;
                targets.Add(new DeliveryTarget(channel, conversationId, Minted: wasMinted, Address: target.Address));
            }
        }

        return targets;
    }

    public async Task AnnounceTurnStartAsync(
        IReadOnlyList<DeliveryTarget> targets,
        ChannelMessage message,
        bool skipMinted,
        CancellationToken ct)
    {
        // The announce is channel-agnostic: every target gets the same create_conversation
        // call and applies its own turn-start semantics (SignalR sets up a live stream,
        // voice binds an announcement unless the satellite session is live). Channels
        // without a create_conversation tool no-op inside CreateConversationAsync.
        var announceable = targets.Where(t => !(skipMinted && t.Minted));
        foreach (var target in announceable)
        {
            try
            {
                await target.Channel.CreateConversationAsync(
                    message.AgentId ?? "default",
                    topicName: string.Empty,
                    message.Sender,
                    initialPrompt: message.Content,
                    address: target.Address,
                    existingConversationId: target.ConversationId,
                    ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The reply itself is persisted regardless; a failed announce only costs
                // live streaming, so it must never abort the turn.
                logger.LogWarning(ex, "Turn-start announce to {ChannelId} failed; reply will not stream live",
                    target.Channel.ChannelId);
            }
        }
    }

    public static ConversationContext BuildConversationContext(
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
}
```

- [ ] **Step 3.2: Re-point ChatMonitor to the resolver**

In `Domain/Monitor/ChatMonitor.cs`:

a) Add the field as the first member of the class body:

```csharp
    private readonly DeliveryTargetResolver targetResolver = new(channels, logger);
```

b) Delete the three methods now living in the resolver: `ResolveDeliveryTargetsAsync` (lines 28–92 pre-Task-2 numbering), `AnnounceTurnStartAsync` (lines 94–127), `BuildConversationContext` (lines 350–362).

c) Update the 4 call sites:

```csharp
// in ProcessChatThread — group anchors:
// before
var targets = await ResolveDeliveryTargetsAsync(first.Message, first.Channel, channels, ct, logger);
// after
var targets = await targetResolver.ResolveAsync(first.Message, first.Channel, ct);

// in the per-message lambda — later-message target resolution:
// before
var messageTargets = index == 0 || x.Message.ReplyTo is { Count: > 0 }
    ? targets
    : await ResolveDeliveryTargetsAsync(x.Message, x.Channel, channels, linkedCt, logger);
// after
var messageTargets = index == 0 || x.Message.ReplyTo is { Count: > 0 }
    ? targets
    : await targetResolver.ResolveAsync(x.Message, x.Channel, linkedCt);

// in the per-message lambda — announce:
// before
await AnnounceTurnStartAsync(messageTargets, x.Message, skipMinted: index == 0, linkedCt, logger);
// after
await targetResolver.AnnounceTurnStartAsync(messageTargets, x.Message, skipMinted: index == 0, linkedCt);

// in the per-message lambda — conversation context:
// before
userMessage.SetConversationContext(BuildConversationContext(x.Message, messageTargets));
// after
userMessage.SetConversationContext(DeliveryTargetResolver.BuildConversationContext(x.Message, messageTargets));
```

- [ ] **Step 3.3: Migrate the delivery tests**

```bash
git mv Tests/Unit/Domain/Monitor/ChatMonitorDeliveryTests.cs Tests/Unit/Domain/Monitor/DeliveryTargetResolverTests.cs
```

In the renamed file:

a) Rename the class and add the NullLogger using:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
...
public class DeliveryTargetResolverTests
```

b) Add a factory helper right after the existing `Channel(string id)` helper:

```csharp
    private static DeliveryTargetResolver Resolver(params IChannelConnection[] channels)
    {
        return new DeliveryTargetResolver(channels, NullLogger.Instance);
    }
```

c) Convert all 13 call sites from the static to the instance form. The channels array that was the third argument moves into `Resolver(...)`; message and origin stay. Examples covering every shape in the file:

```csharp
// before
var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, channels, CancellationToken.None);
// after
var targets = await Resolver(channels).ResolveAsync(msg, origin, CancellationToken.None);

// before (inline array)
var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(msg, origin, [origin, signalr], CancellationToken.None);
// after
var targets = await Resolver(origin, signalr).ResolveAsync(msg, origin, CancellationToken.None);

// before (multi-line, in ResolveDeliveryTargets_AttachOnlyOrderingIsCapabilityDriven_NotChannelIdDriven)
var targets = await ChatMonitor.ResolveDeliveryTargetsAsync(
    msg, origin, [origin, signalr, notify.Object], CancellationToken.None);
// after
var targets = await Resolver(origin, signalr, notify.Object).ResolveAsync(msg, origin, CancellationToken.None);
```

Where `channels` is a `new[] { ... }` local of `IChannelConnection`, `Resolver(channels)` compiles as-is (`params` accepts the array). Test method names keep their `ResolveDeliveryTargets_` prefix — they still describe the behavior.

- [ ] **Step 3.4: Migrate the announce tests**

```bash
git mv Tests/Unit/Domain/Monitor/ChatMonitorAnnounceTests.cs Tests/Unit/Domain/Monitor/DeliveryTargetResolverAnnounceTests.cs
```

In the renamed file:

a) Rename the class and add usings/helper:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
...
public class DeliveryTargetResolverAnnounceTests
{
    private static readonly DeliveryTargetResolver Resolver = new([], NullLogger.Instance);
```

(The empty channel list is correct — announce only iterates the already-resolved targets.)

b) Convert all 6 `ChatMonitor.AnnounceTurnStartAsync(...)` call sites; the argument list is unchanged apart from dropping nothing (logger was already omitted in tests):

```csharp
// before
await ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: true, CancellationToken.None);
// after
await Resolver.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: true, CancellationToken.None);

// before (inside Should.NotThrowAsync)
await Should.NotThrowAsync(
    ChatMonitor.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: true, CancellationToken.None));
// after
await Should.NotThrowAsync(
    Resolver.AnnounceTurnStartAsync(targets, DownloadMessage(), skipMinted: true, CancellationToken.None));
```

- [ ] **Step 3.5: Re-point the conversation-context tests**

In `Tests/Unit/Domain/Monitor/ChatMonitorConversationContextTests.cs` (file name unchanged — it primarily pins ChatMonitor pipeline behavior), update the 2 static call sites:

```csharp
// before
var context = ChatMonitor.BuildConversationContext(message, targets);
var context = ChatMonitor.BuildConversationContext(message, []);
// after
var context = DeliveryTargetResolver.BuildConversationContext(message, targets);
var context = DeliveryTargetResolver.BuildConversationContext(message, []);
```

- [ ] **Step 3.6: Build and run the monitor tests**

Run:
```bash
dotnet build Domain && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Monitor"
```

Expected: build clean, same passed count as Task 1, failed: 0.

- [ ] **Step 3.7: Commit**

```bash
git add Domain/Monitor/DeliveryTargetResolver.cs Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/Monitor/DeliveryTargetResolverTests.cs Tests/Unit/Domain/Monitor/DeliveryTargetResolverAnnounceTests.cs Tests/Unit/Domain/Monitor/ChatMonitorConversationContextTests.cs
git commit -m "refactor: extract DeliveryTargetResolver from ChatMonitor"
```

(`git mv` already staged the renames; `git add` the new paths anyway — it is idempotent.)

---

### Task 4: Extract `ReplyDispatcher`

**Files:**
- Create: `Domain/Monitor/ReplyDispatcher.cs`
- Modify: `Domain/Monitor/ChatMonitor.cs` (remove `DeliverUpdateAsync`, `DeliverToTargetAsync`, `MapResponseUpdate`; add field; update 1 call site)
- Modify: `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs:153` (stale comment)

- [ ] **Step 4.1: Create `Domain/Monitor/ReplyDispatcher.cs`**

Bodies moved verbatim from ChatMonitor (comments included); `metricsPublisher`/`logger` come from the primary constructor. Exact content (no trailing newline):

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Domain.Monitor;

public class ReplyDispatcher(IMetricsPublisher metricsPublisher, ILogger logger)
{
    public async Task<bool> DeliverUpdateAsync(
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

    private async Task<bool> DeliverToTargetAsync(
        DeliveryTarget target,
        (string Content, ReplyContentType ContentType, bool IsComplete) mapped,
        string? messageId,
        CancellationToken ct)
    {
        try
        {
            await target.Channel.SendReplyAsync(
                target.ConversationId, mapped.Content, mapped.ContentType, mapped.IsComplete, messageId, ct);
            return true;
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
            return false;
        }
    }

    private static IEnumerable<(string Content, ReplyContentType ContentType, bool IsComplete)> MapResponseUpdate(
        AgentResponseUpdate update)
    {
        foreach (var aiContent in update.Contents)
        {
            (string, ReplyContentType, bool)? mapped = aiContent switch
            {
                TextContent text when !string.IsNullOrEmpty(text.Text)
                    => (text.Text, ReplyContentType.Text, false),
                TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text)
                    => (reasoning.Text, ReplyContentType.Reasoning, false),
                // FunctionCallContent is intentionally skipped — tool calls are displayed
                // by the approval flow (request_approval tool with mode=request or mode=notify)
                ErrorContent error
                    => (error.Message, ReplyContentType.Error, false),
                StreamCompleteContent
                    => (string.Empty, ReplyContentType.StreamComplete, true),
                _ => null
            };

            if (mapped is { } value)
            {
                yield return value;
            }
        }
    }
}
```

If the compiler reports a missing type, check the original `ChatMonitor.cs` using list (`Domain.DTOs.Channel`, `Domain.Extensions`) and add only what is needed — do not blanket-copy unused usings.

- [ ] **Step 4.2: Re-point ChatMonitor to the dispatcher**

In `Domain/Monitor/ChatMonitor.cs`:

a) Add the field next to `targetResolver`:

```csharp
    private readonly ReplyDispatcher replyDispatcher = new(metricsPublisher, logger);
```

b) Delete `DeliverUpdateAsync`, `DeliverToTargetAsync`, and `MapResponseUpdate`.

c) Update the single call site in the delivery loop:

```csharp
// before
var deliveredContent = await DeliverUpdateAsync(update, replyTargets, ct);
// after
var deliveredContent = await replyDispatcher.DeliverUpdateAsync(update, replyTargets, ct);
```

- [ ] **Step 4.3: Fix the stale comment in SendReplyToolTests**

`Tests/Unit/McpChannelVoice/SendReplyToolTests.cs:153`:

```csharp
// before
        // Real agent streaming (see ChatMonitor.MapResponseUpdate): Text chunks are
// after
        // Real agent streaming (see ReplyDispatcher.MapResponseUpdate): Text chunks are
```

- [ ] **Step 4.4: Build and run the monitor tests**

Run:
```bash
dotnet build Domain && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Monitor"
```

Expected: build clean, same passed count as Task 1, failed: 0. (Delivery isolation stays covered by `ChatMonitorScheduleMetricsTests.Monitor_ScheduledMessage_OneDeliveryTargetFails_DeliversToOthersAndEmitsMetricOnce`.)

- [ ] **Step 4.5: Commit**

```bash
git add Domain/Monitor/ReplyDispatcher.cs Domain/Monitor/ChatMonitor.cs Tests/Unit/McpChannelVoice/SendReplyToolTests.cs
git commit -m "refactor: extract ReplyDispatcher from ChatMonitor"
```

---

### Task 5: Move `BuildScheduleEvent` to `ScheduleExecutionEvent.FromMessage`

**Files:**
- Modify: `Domain/DTOs/Metrics/ScheduleExecutionEvent.cs`
- Modify: `Domain/Monitor/ChatMonitor.cs` (remove `BuildScheduleEvent`; update 1 call site)
- Modify: `Tests/Unit/Domain/Monitor/ChatMonitorScheduleMetricsTests.cs` (2 tests)
- Modify: `Tests/Unit/Domain/MonitorTests.cs` (1 test, around line 558)

- [ ] **Step 5.1: Add the factory to the DTO**

Rewrite `Domain/DTOs/Metrics/ScheduleExecutionEvent.cs` as (no trailing newline; `ChannelMessage` lives in the enclosing `Domain.DTOs` namespace, so only the `Channel` using is added for `MessageOriginKind`):

```csharp
using Domain.DTOs.Channel;

namespace Domain.DTOs.Metrics;

public record ScheduleExecutionEvent : MetricEvent
{
    public required string ScheduleId { get; init; }
    public required string Prompt { get; init; }
    public required long DurationMs { get; init; }
    public required bool Success { get; init; }
    public string? Error { get; init; }

    public static ScheduleExecutionEvent? FromMessage(
        ChannelMessage message, long durationMs, bool success, string? error)
    {
        if (message.Origin is not { Kind: MessageOriginKind.Schedule, ScheduleId: { } scheduleId })
        {
            return null;
        }

        return new ScheduleExecutionEvent
        {
            ScheduleId = scheduleId,
            AgentId = message.AgentId ?? "default",
            Prompt = message.Content,
            DurationMs = durationMs,
            Success = success,
            Error = error
        };
    }
}
```

Note: `AgentId` is set in the current `BuildScheduleEvent` body, so it is inherited from `MetricEvent` — keep the assignment exactly as shown.

- [ ] **Step 5.2: Re-point ChatMonitor and delete the old method**

In `Domain/Monitor/ChatMonitor.cs`:

a) Update the call site inside the `OnCompletion` fold:

```csharp
// before
var evt = BuildScheduleEvent(x.Message, stopwatch.ElapsedMilliseconds, !faulted, error);
// after
var evt = ScheduleExecutionEvent.FromMessage(x.Message, stopwatch.ElapsedMilliseconds, !faulted, error);
```

b) Delete the `BuildScheduleEvent` method entirely.

- [ ] **Step 5.3: Re-point the tests**

In `Tests/Unit/Domain/Monitor/ChatMonitorScheduleMetricsTests.cs`, rename the two unit-level tests and update their calls (pipeline tests in the file are untouched):

```csharp
// before
    public void BuildScheduleEvent_WithScheduleOrigin_ReturnsEvent()
    ...
        var evt = ChatMonitor.BuildScheduleEvent(msg, durationMs: 1234, success: true, error: null);
// after
    public void FromMessage_WithScheduleOrigin_ReturnsEvent()
    ...
        var evt = ScheduleExecutionEvent.FromMessage(msg, durationMs: 1234, success: true, error: null);

// before
    public void BuildScheduleEvent_WithNonScheduleMessage_ReturnsNull()
    ...
        ChatMonitor.BuildScheduleEvent(msg, 1, true, null).ShouldBeNull();
// after
    public void FromMessage_WithNonScheduleMessage_ReturnsNull()
    ...
        ScheduleExecutionEvent.FromMessage(msg, 1, true, null).ShouldBeNull();
```

In `Tests/Unit/Domain/MonitorTests.cs` (around line 558):

```csharp
// before
    public void BuildScheduleEvent_DownloadOrigin_ReturnsNull()
    ...
        ChatMonitor.BuildScheduleEvent(msg, 100, true, null).ShouldBeNull();
// after
    public void FromMessage_DownloadOrigin_ReturnsNull()
    ...
        ScheduleExecutionEvent.FromMessage(msg, 100, true, null).ShouldBeNull();
```

- [ ] **Step 5.4: Build and run the monitor tests**

Run:
```bash
dotnet build Domain && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Monitor"
```

Expected: build clean, same passed count as Task 1, failed: 0.

- [ ] **Step 5.5: Commit**

```bash
git add Domain/DTOs/Metrics/ScheduleExecutionEvent.cs Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/Monitor/ChatMonitorScheduleMetricsTests.cs Tests/Unit/Domain/MonitorTests.cs
git commit -m "refactor: move schedule-event building onto ScheduleExecutionEvent.FromMessage"
```

---

### Task 6: Decompose `ProcessChatThread`

**Files:**
- Modify: `Domain/Monitor/ChatMonitor.cs` (restructure only — no test changes; the suite pins behavior)

After Tasks 2–5 ChatMonitor contains: the two collaborator fields, `Monitor`, `ProcessChatThread` (still with the 75-line per-message lambda), and `GetOrRestoreThread`. This task replaces the class body with the decomposed form below. `Monitor` and `GetOrRestoreThread` are NOT shown because they are unchanged — leave them exactly as they are. Everything between them is replaced as follows.

- [ ] **Step 6.1: Restructure ChatMonitor**

Add two private records and replace `ProcessChatThread` with the decomposed methods. The complete new section (bodies moved verbatim from the current lambda — comments included):

```csharp
    private readonly DeliveryTargetResolver targetResolver = new(channels, logger);
    private readonly ReplyDispatcher replyDispatcher = new(metricsPublisher, logger);

    private sealed record TurnUpdate(
        AgentResponseUpdate Update, IReadOnlyList<DeliveryTarget> Targets, FirstReplyTracker? Tracker);

    private sealed record GroupAnchors(
        IReadOnlyList<DeliveryTarget> Targets, IToolApprovalHandler ApprovalHandler, AgentKey PersistenceKey);

    // public async Task Monitor(...) — UNCHANGED, keep as-is

    private async IAsyncEnumerable<bool> ProcessChatThread(
        AgentKey agentKey,
        IAsyncGrouping<AgentKey, (IChannelConnection Channel, ChannelMessage Message)> group,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var first = await group.FirstAsync(ct);
        var anchors = await ResolveGroupAnchorsAsync(first, agentKey, ct);
        await using var agent = agentFactory.Create(agentKey, first.Message.Sender, first.Message.AgentId, anchors.ApprovalHandler);
        var context = threadResolver.Resolve(agentKey);
        var thread = await GetOrRestoreThread(agent, anchors.PersistenceKey, ct);

        context.RegisterCompletionCallback(group.Complete);

        using var linkedCts = context.GetLinkedTokenSource(ct);
        var linkedCt = linkedCts.Token;

        // Start session warmup (MCP connections + tool discovery) without awaiting it
        // yet, so it overlaps with command parsing and memory recall. It is awaited
        // deterministically just before the first RunStreamingAsync below, so it never
        // outlives the agent and the order of operations is well-defined.
        var warmup = agent.WarmupSessionAsync(thread, linkedCt);

        var aiResponses = group.Prepend(first)
            .Select((x, index, _) => RunTurnAsync(x, index, agentKey, anchors.Targets, agent, thread, warmup, linkedCt))
            .Merge(linkedCt);

        await foreach (var turn in aiResponses.WithCancellation(ct))
        {
            var deliveredContent = await replyDispatcher.DeliverUpdateAsync(turn.Update, turn.Targets, ct);
            if (deliveredContent && turn.Tracker?.TryComplete() is { } firstReplyMs)
            {
                await PublishFirstReplyLatencyAsync(firstReplyMs, turn.Targets, agentKey, ct);
            }

            yield return true;
        }
    }

    // Resolve delivery targets BEFORE binding the approval handler and restoring
    // the thread. Two reasons:
    // 1) The persistence key for chat-history must match the first delivery
    //    target's conversation id — otherwise, when a target is minted (e.g. a
    //    schedule fire with a null ReplyTo conversationId), history persists
    //    under the synthetic group id while the receiving channel (WebChat)
    //    reads history keyed on the minted id and sees an empty conversation.
    // 2) The approval handler must route to the delivery target's channel, not
    //    the origin. Schedule/ServiceBus channels auto-approve silently, so
    //    binding to the origin would hide tool calls from the user in WebChat.
    // These first-message targets anchor the group-level persistence key and
    // approval handler; per-message reply delivery is resolved separately in
    // ResolveTurnTargetsAsync.
    private async Task<GroupAnchors> ResolveGroupAnchorsAsync(
        (IChannelConnection Channel, ChannelMessage Message) first, AgentKey agentKey, CancellationToken ct)
    {
        var targets = await targetResolver.ResolveAsync(first.Message, first.Channel, ct);
        var (approvalChannel, approvalConversationId) = targets.Count > 0
            ? (targets[0].Channel, targets[0].ConversationId)
            : (first.Channel, first.Message.ConversationId);
        var approvalHandler = approvalHandlerFactory(approvalChannel, approvalConversationId);
        var persistenceKey = targets.Count > 0
            ? new AgentKey(targets[0].ConversationId, first.Message.AgentId)
            : agentKey;
        return new GroupAnchors(targets, approvalHandler, persistenceKey);
    }

    private async Task<IAsyncEnumerable<TurnUpdate>> RunTurnAsync(
        (IChannelConnection Channel, ChannelMessage Message) x,
        int index,
        AgentKey agentKey,
        IReadOnlyList<DeliveryTarget> groupTargets,
        DisposableAgent agent,
        AgentSession thread,
        Task warmup,
        CancellationToken ct)
    {
        switch (ChatCommandParser.Parse(x.Message.Content))
        {
            case ChatCommand.Clear:
                await threadResolver.ClearAsync(agentKey);
                return AsyncEnumerable.Empty<TurnUpdate>();
            case ChatCommand.Cancel:
                threadResolver.Cancel(agentKey);
                return AsyncEnumerable.Empty<TurnUpdate>();
        }

        // FirstReply times "message arrival → first delivered reply chunk":
        // started before target resolution, memory recall, session warmup, and
        // the turn-start announce for agent-initiated messages, so the
        // measurement includes every stage the user actually waits on.
        var tracker = new FirstReplyTracker();
        var targets = await ResolveTurnTargetsAsync(x, index, groupTargets, ct);
        // Agent-initiated turns (downloads, schedules) land in conversations
        // with no live stream on the receiving channel; announce the turn so
        // the channel can set one up before reply chunks arrive. Targets the
        // group-opening message minted were announced by their own
        // create_conversation; later messages reusing the group targets see
        // those conversations as pre-existing.
        if (x.Message.Origin is not null)
        {
            await targetResolver.AnnounceTurnStartAsync(targets, x.Message, skipMinted: index == 0, ct);
        }
        var userMessage = await BuildUserMessageAsync(x.Message, targets, thread, ct);

        await warmup;
        return StreamAgentTurn(agent, thread, userMessage, x.Message, targets, tracker, ct);
    }

    // Deliver each message's reply to the channel that actually sent it. The
    // group is keyed only by (ConversationId, AgentId), so a later message from
    // a different channel — e.g. the user typing in WebChat inside a
    // voice-started conversation — joins this same group. The group-level
    // targets cover the first/initiating message and any ReplyTo fan-out
    // (re-resolving the latter would re-mint conversations); a subsequent plain
    // interactive message is routed back to its own origin instead of the
    // opening channel.
    private async Task<IReadOnlyList<DeliveryTarget>> ResolveTurnTargetsAsync(
        (IChannelConnection Channel, ChannelMessage Message) x,
        int index,
        IReadOnlyList<DeliveryTarget> groupTargets,
        CancellationToken ct)
    {
        return index == 0 || x.Message.ReplyTo is { Count: > 0 }
            ? groupTargets
            : await targetResolver.ResolveAsync(x.Message, x.Channel, ct);
    }

    private async Task<ChatMessage> BuildUserMessageAsync(
        ChannelMessage message, IReadOnlyList<DeliveryTarget> targets, AgentSession thread, CancellationToken ct)
    {
        var userMessage = new ChatMessage(ChatRole.User, message.Content);
        userMessage.SetSenderId(message.Sender);
        userMessage.SetLocation(message.Location);
        userMessage.SetSatelliteId(message.SatelliteId);
        userMessage.SetTimestamp(DateTimeOffset.UtcNow);
        userMessage.SetConversationContext(DeliveryTargetResolver.BuildConversationContext(message, targets));
        if (memoryRecallHook is not null)
        {
            await memoryRecallHook.EnrichAsync(userMessage, message.Sender, message.ConversationId, message.AgentId, thread, ct);
        }

        return userMessage;
    }

    private IAsyncEnumerable<TurnUpdate> StreamAgentTurn(
        DisposableAgent agent,
        AgentSession thread,
        ChatMessage userMessage,
        ChannelMessage message,
        IReadOnlyList<DeliveryTarget> targets,
        FirstReplyTracker tracker,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        // ReSharper disable once AccessToDisposedClosure
        return agent
            .RunStreamingAsync([userMessage], thread, cancellationToken: ct)
            .WithErrorHandling(ct)
            .ToUpdateAiResponsePairs()
            .Append((new AgentResponseUpdate { Contents = [new StreamCompleteContent()] }, null))
            .OnCompletion(
                seed: false,
                fold: (faulted, pair) => faulted || pair.Item1.Contents.OfType<ErrorContent>().Any(),
                onCompletion: async (faulted, completionCt) =>
                {
                    var error = faulted ? "Agent run reported an error" : null;
                    var evt = ScheduleExecutionEvent.FromMessage(message, stopwatch.ElapsedMilliseconds, !faulted, error);
                    if (evt is not null)
                    {
                        await metricsPublisher.PublishAsync(evt, completionCt);
                    }
                },
                ct)
            .Select(pair => new TurnUpdate(pair.Item1, targets, tracker));
    }

    private async Task PublishFirstReplyLatencyAsync(
        long firstReplyMs, IReadOnlyList<DeliveryTarget> targets, AgentKey agentKey, CancellationToken ct)
    {
        await metricsPublisher.PublishAsync(new LatencyEvent
        {
            Stage = LatencyStage.FirstReply,
            DurationMs = firstReplyMs,
            // Attribute the event to where the reply actually landed (same idiom as
            // the persistence key in ResolveGroupAnchorsAsync): a scheduled fire
            // delivers to minted target conversations, not the scheduling channel's
            // own conversation id.
            ConversationId = targets.Count > 0 ? targets[0].ConversationId : agentKey.ConversationId
        }, ct);
    }

    // private static ValueTask<AgentSession> GetOrRestoreThread(...) — UNCHANGED, keep as-is
```

Behavior-preservation notes for this step (verify each while editing):
- The original `.Select(async (x, index, _) => { ... })` lambda returned `Task<IAsyncEnumerable<...>>`; the replacement `(x, index, _) => RunTurnAsync(...)` has the identical shape — the third lambda parameter stays ignored and `linkedCt` is threaded explicitly, exactly as before.
- Operation ORDER inside a turn is unchanged: command parse → tracker → per-message target resolution → announce (only when `Origin` is set) → user-message build + memory recall → `await warmup` → streaming run.
- The original `switch` had Clear/Cancel cases plus a `default` containing the turn body; the rewrite exits early on Clear/Cancel and falls through to the same body — identical semantics.
- `Stopwatch.StartNew()` still happens before `RunStreamingAsync` is composed (it moved from before `return agent...` into `StreamAgentTurn`, which is invoked at the same point in the sequence — after `await warmup`).
- The delivery loop still enumerates with `ct` (not `linkedCt`) and yields `true` per update, and unused usings (`System.Text.Json` stays for `GetOrRestoreThread`) must be pruned only if the compiler/format hook flags them.

- [ ] **Step 6.2: Build and run the monitor tests**

Run:
```bash
dotnet build Domain && dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Monitor"
```

Expected: build clean, same passed count as Task 1, failed: 0. The pipeline tests (`MonitorTests`, `ChatMonitorPersistenceKeyTests`, `ChatMonitorScheduleMetricsTests`, `ChatMonitorConversationContextTests`) exercise the full decomposed flow — Clear/Cancel commands, approval binding, persistence keys, announce ordering, schedule metrics, delivery isolation.

- [ ] **Step 6.3: Commit**

```bash
git add Domain/Monitor/ChatMonitor.cs
git commit -m "refactor: decompose ProcessChatThread into named turn-stage methods"
```

---

### Task 7: Full-suite verification

**Files:** none modified.

- [ ] **Step 7.1: Build the whole solution**

Run:
```bash
dotnet build
```

Expected: clean build, no warnings introduced by the refactor (compare against pre-existing warnings if any).

- [ ] **Step 7.2: Run the full non-E2E suite**

Run:
```bash
dotnet test Tests/Tests.csproj --filter "Category!=E2E"
```

Expected: no NEW failures versus the known baseline. In this WSL environment ~148 failures are pre-existing `DockerUnavailableException` integration tests — those are the baseline, not regressions. Every failure must be a `DockerUnavailableException`; any other failure is a regression introduced by this refactor and must be fixed before finishing.

- [ ] **Step 7.3: Confirm the final shape**

Run:
```bash
wc -l Domain/Monitor/*.cs
```

Expected: `ChatMonitor.cs` ≈ 170–220 lines (orchestration only), `DeliveryTargetResolver.cs` ≈ 120, `ReplyDispatcher.cs` ≈ 95, `DeliveryTarget.cs` ≈ 5.

No commit (nothing changed); this task is the verification gate for declaring the refactor done.
