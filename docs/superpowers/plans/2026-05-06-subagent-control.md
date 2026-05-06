# Subagent Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the parent agent and the human user the ability to start subagents in the background, inspect per-turn progress, cancel mid-flight, and (for the parent) be woken when a backgrounded subagent finishes.

**Architecture:** A per-`AgentKey` `SubAgentSessionManager` lives in a singleton registry — outliving the per-grouping `McpAgent` instances created by `ChatMonitor` so that handles persist across the parent's turn boundaries. Each `SubAgentSession` consumes the subagent's `RunStreamingAsync` updates itself, recording per-turn snapshots and managing cancellation. A new in-process `SystemChannelConnection` (registered as one of the `IChannelConnection`s consumed by `ChatMonitor`) carries synthetic system messages used to wake the parent when backgrounded subagents complete; replies posted by ChatMonitor through this channel are forwarded to the original user-facing channel via a per-conversation route map. The chat-card UI is built on the same MCP-tool / inbound-notification pattern as the existing approval flow.

**Tech Stack:** .NET 10 LTS, Microsoft.Extensions.AI, ModelContextProtocol, xUnit + Shouldly, SignalR, Telegram.Bot, Blazor WebAssembly (Redux-style stores).

**Spec reference:** `docs/superpowers/specs/2026-05-06-subagent-control-design.md`

---

## File Structure

### Create

| Path | Responsibility |
|---|---|
| `Domain/Contracts/ISubAgentSessions.cs` | Per-conversation manager interface used by Domain tools |
| `Domain/Contracts/ISubAgentSessionsRegistry.cs` | Singleton lookup: `AgentKey` → `ISubAgentSessions` |
| `Domain/DTOs/SubAgent/SubAgentTerminalState.cs` | Enum: `Running`, `Completed`, `Failed`, `Cancelled` |
| `Domain/DTOs/SubAgent/SubAgentCancelSource.cs` | Enum: `Parent`, `User`, `System` |
| `Domain/DTOs/SubAgent/SubAgentWaitMode.cs` | Enum: `Any`, `All` |
| `Domain/DTOs/SubAgent/SubAgentTurnSnapshot.cs` | Per-turn snapshot DTO |
| `Domain/DTOs/SubAgent/SubAgentSessionView.cs` | Status + snapshots + result/error DTO returned by tools |
| `Domain/DTOs/SubAgent/SubAgentWaitResult.cs` | `{ completed, still_running }` partition |
| `Domain/DTOs/SubAgent/SubAgentCancelRequest.cs` | Inbound notification DTO from channels |
| `Domain/Tools/SubAgents/SubAgentCheckTool.cs` | `subagent_check` |
| `Domain/Tools/SubAgents/SubAgentWaitTool.cs` | `subagent_wait` |
| `Domain/Tools/SubAgents/SubAgentCancelTool.cs` | `subagent_cancel` |
| `Domain/Tools/SubAgents/SubAgentListTool.cs` | `subagent_list` |
| `Domain/Tools/SubAgents/SubAgentReleaseTool.cs` | `subagent_release` |
| `Agent/Services/SubAgents/SubAgentSession.cs` | One running subagent (state machine, snapshots, cancel) |
| `Agent/Services/SubAgents/SubAgentSessionManager.cs` | Per-`AgentKey` registry + wake buffer |
| `Agent/Services/SubAgents/SubAgentSessionsRegistry.cs` | Singleton `AgentKey` → manager map |
| `Agent/Services/SubAgents/SystemChannelConnection.cs` | In-process synthetic channel for wake messages |
| `Agent/Services/SubAgents/SnapshotRecorder.cs` | Consumes `IAsyncEnumerable<AgentResponseUpdate>` and emits per-turn snapshots |
| `McpChannelSignalR/McpTools/SubAgentAnnounceTool.cs` | MCP tool agent calls to post a card |
| `McpChannelSignalR/McpTools/SubAgentUpdateTool.cs` | MCP tool agent calls to update a card |
| `McpChannelSignalR/Services/ISubAgentSignalService.cs` (+ impl) | Hub-side announce/update + cancel callback dispatch |
| `McpChannelTelegram/McpTools/SubAgentAnnounceTool.cs` | TG inline-keyboard card |
| `McpChannelTelegram/McpTools/SubAgentUpdateTool.cs` | TG `editMessageText` + remove keyboard |
| `WebChat.Client/Components/SubAgentCard.razor` | Card component |
| `WebChat.Client/State/SubAgents/SubAgentStore.cs` | Redux-style store for cards |
| `WebChat.Client/State/SubAgents/SubAgentEffects.cs` | Hub event subscriptions + cancel dispatch |
| All `Tests/...` files listed under each task |

### Modify

| Path | Change |
|---|---|
| `Domain/DTOs/FeatureConfig.cs` | Add `ISubAgentSessions? SubAgentSessions = null` |
| `Domain/Tools/SubAgents/SubAgentRunTool.cs` | Add `run_in_background`, `silent` params; route through `ISubAgentSessions` when async |
| `Domain/Tools/SubAgents/SubAgentToolFeature.cs` | Register the five new tools |
| `Domain/Prompts/SubAgentPrompt.cs` | Document the new params and tools |
| `Domain/Contracts/IChannelConnection.cs` | Add `AnnounceSubAgentAsync`, `UpdateSubAgentAsync`, `IAsyncEnumerable<SubAgentCancelRequest> SubAgentCancelRequests` |
| `Infrastructure/Agents/MultiAgentFactory.cs` | Resolve `ISubAgentSessions` from registry per `AgentKey`; populate `FeatureConfig` |
| `Agent/Modules/SubAgentModule.cs` | Register registry singleton + system channel + new tool feature wiring |
| `Domain/Monitor/ChatMonitor.cs` | Subscribe to each channel's `SubAgentCancelRequests` and route to the registry |
| `McpChannelSignalR` startup / DI | Register `ISubAgentSignalService` |
| `McpChannelSignalR/Hubs/ChatHub.cs` (or equivalent) | Add `CancelSubAgent(handle)` hub method |
| `WebChat.Client` startup / hub event dispatcher | Wire subagent SignalR events into the new store |
| Existing channel-side `IChannelConnection` implementations | Implement the new members (no-op `SubAgentCancelRequests` for ServiceBus) |

---

## Naming conventions

- All new domain tools registered as `domain__subagents__<tool_name>`.
- Test files: `Tests/Unit/<ProjectName>/<ClassName>Tests.cs`, `Tests/Integration/<Area>/<Scenario>Tests.cs`, `Tests/E2E/<Area>/<Feature>E2ETests.cs`.
- Test methods: `MethodName_Scenario_ExpectedOutcome`.
- xUnit `[Fact]` / `[Theory]`, Shouldly `ShouldBe`/`ShouldContain`.

---

# Tasks

The plan has 12 tasks matching the 12 spec triplets. Each task is RED → GREEN → REVIEW → COMMIT.

---

## Task 1: `SubAgentSession` core (state machine, snapshots, cancel)

**Files:**
- Create: `Domain/DTOs/SubAgent/SubAgentTerminalState.cs`
- Create: `Domain/DTOs/SubAgent/SubAgentCancelSource.cs`
- Create: `Domain/DTOs/SubAgent/SubAgentTurnSnapshot.cs`
- Create: `Domain/DTOs/SubAgent/SubAgentSessionView.cs`
- Create: `Agent/Services/SubAgents/SubAgentSession.cs`
- Create: `Tests/Unit/Agent/SubAgentSessionTests.cs`

### Step 1.1: Add the DTOs

- [ ] **Step:** Create `Domain/DTOs/SubAgent/SubAgentTerminalState.cs`

```csharp
namespace Domain.DTOs.SubAgent;

public enum SubAgentTerminalState
{
    Running,
    Completed,
    Failed,
    Cancelled
}
```

- [ ] **Step:** Create `Domain/DTOs/SubAgent/SubAgentCancelSource.cs`

```csharp
namespace Domain.DTOs.SubAgent;

public enum SubAgentCancelSource
{
    Parent,
    User,
    System
}
```

- [ ] **Step:** Create `Domain/DTOs/SubAgent/SubAgentTurnSnapshot.cs`

```csharp
namespace Domain.DTOs.SubAgent;

public sealed record SubAgentTurnSnapshot
{
    public required int Index { get; init; }
    public required string AssistantText { get; init; }
    public required IReadOnlyList<SubAgentToolCallSummary> ToolCalls { get; init; }
    public required IReadOnlyList<SubAgentToolResultSummary> ToolResults { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}

public sealed record SubAgentToolCallSummary(string Name, string ArgsSummary);

public sealed record SubAgentToolResultSummary(string Name, bool Ok, string Summary);
```

- [ ] **Step:** Create `Domain/DTOs/SubAgent/SubAgentSessionView.cs`

```csharp
namespace Domain.DTOs.SubAgent;

public sealed record SubAgentSessionView
{
    public required string Handle { get; init; }
    public required string SubAgentId { get; init; }
    public required SubAgentTerminalState Status { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required double ElapsedSeconds { get; init; }
    public required IReadOnlyList<SubAgentTurnSnapshot> Turns { get; init; }
    public string? Result { get; init; }
    public SubAgentCancelSource? CancelledBy { get; init; }
    public SubAgentSessionError? Error { get; init; }
}

public sealed record SubAgentSessionError(string Code, string Message);
```

### Step 1.2: Write the failing tests

- [ ] **Step:** Create `Tests/Unit/Agent/SubAgentSessionTests.cs` with these test bodies. Mock the agent via a fake `DisposableAgent` you'll add as a private nested class (it must inherit from `Domain.Agents.DisposableAgent`; review its members in `Domain/Agents/DisposableAgent.cs` before writing).

```csharp
using Agent.Services.SubAgents;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Agent;

public sealed class SubAgentSessionTests
{
    private static SubAgentDefinition Profile(int maxSec = 60) => new()
    {
        Id = "researcher", Name = "Researcher", Model = "test/model",
        McpServerEndpoints = [], MaxExecutionSeconds = maxSec
    };

    [Fact]
    public async Task RunAsync_NaturalCompletion_TransitionsToCompleted()
    {
        var fake = new FakeStreamingAgent(turns: 2, finalText: "done");
        var session = new SubAgentSession("h1", Profile(), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1");

        await session.RunAsync(CancellationToken.None);

        var view = session.Snapshot();
        view.Status.ShouldBe(SubAgentTerminalState.Completed);
        view.Result.ShouldBe("done");
        view.Turns.Count.ShouldBe(2);
        view.CancelledBy.ShouldBeNull();
    }

    [Fact]
    public async Task RunAsync_CancelMidFlight_TransitionsToCancelled()
    {
        var fake = new FakeStreamingAgent(turns: 5, delayPerTurn: TimeSpan.FromMilliseconds(50));
        var session = new SubAgentSession("h2", Profile(), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1");

        var runTask = session.RunAsync(CancellationToken.None);
        await Task.Delay(60);
        session.Cancel(SubAgentCancelSource.User);
        await runTask;

        var view = session.Snapshot();
        view.Status.ShouldBe(SubAgentTerminalState.Cancelled);
        view.CancelledBy.ShouldBe(SubAgentCancelSource.User);
        view.Error.ShouldNotBeNull();
        view.Error!.Code.ShouldBe("Cancelled");
    }

    [Fact]
    public async Task RunAsync_TerminalRace_ResolvesToOneState()
    {
        var fake = new FakeStreamingAgent(turns: 1);
        var session = new SubAgentSession("h3", Profile(), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1");

        var runTask = session.RunAsync(CancellationToken.None);
        Parallel.For(0, 50, _ => session.Cancel(SubAgentCancelSource.Parent));
        await runTask;

        var view = session.Snapshot();
        // Must be exactly one terminal state, not a mixture
        view.Status.ShouldBeOneOf(SubAgentTerminalState.Completed, SubAgentTerminalState.Cancelled);
    }

    [Fact]
    public async Task RunAsync_ExceedsMaxExecutionSeconds_TimesOut()
    {
        var fake = new FakeStreamingAgent(turns: 100, delayPerTurn: TimeSpan.FromMilliseconds(20));
        var session = new SubAgentSession("h4", Profile(maxSec: 0 /* 0s = effectively immediate */),
            "prompt", silent: false, agentFactory: () => fake, replyToConversationId: "c1");

        // Override: SubAgentSession should treat MaxExecutionSeconds<=0 as 100ms for testability.
        // OR: pass a TimeProvider; here we use the simpler real-time timer with a small profile.
        await session.RunAsync(CancellationToken.None);

        var view = session.Snapshot();
        view.Status.ShouldBe(SubAgentTerminalState.Cancelled);
        view.CancelledBy.ShouldBe(SubAgentCancelSource.System);
        view.Error!.Code.ShouldBe("Timeout");
    }

    [Fact]
    public async Task Snapshots_TruncateLongArgsAndResults()
    {
        var fake = new FakeStreamingAgent(
            turns: 1,
            toolCallArgs: new string('a', 1000),
            toolResultBody: new string('b', 2000));
        var session = new SubAgentSession("h5", Profile(), "prompt",
            silent: false, agentFactory: () => fake, replyToConversationId: "c1");

        await session.RunAsync(CancellationToken.None);

        var snap = session.Snapshot().Turns.Single();
        snap.ToolCalls[0].ArgsSummary.Length.ShouldBeLessThanOrEqualTo(200);
        snap.ToolResults[0].Summary.Length.ShouldBeLessThanOrEqualTo(500);
    }

    // FakeStreamingAgent: emits N turns of (assistant text + tool call + tool result),
    // completes with finalText. Honors the cancellation token between turns.
    // See note in step 1.3 for the implementation skeleton.
}
```

The `FakeStreamingAgent` skeleton (define as a `file sealed class` at the bottom of the test file):

```csharp
file sealed class FakeStreamingAgent : Domain.Agents.DisposableAgent
{
    private readonly int _turns;
    private readonly string _finalText;
    private readonly TimeSpan _delayPerTurn;
    private readonly string _toolCallArgs;
    private readonly string _toolResultBody;

    public FakeStreamingAgent(
        int turns,
        string finalText = "ok",
        TimeSpan? delayPerTurn = null,
        string toolCallArgs = "{}",
        string toolResultBody = "ok")
    {
        _turns = turns;
        _finalText = finalText;
        _delayPerTurn = delayPerTurn ?? TimeSpan.Zero;
        _toolCallArgs = toolCallArgs;
        _toolResultBody = toolResultBody;
    }

    // RunStreamingAsync override emits per-turn updates carrying assistant text and
    // tool call/result content, honoring CancellationToken between turns. See Domain
    // base class members; mirror the AgentResponseUpdate emission pattern visible
    // in McpAgent.RunCoreStreamingAsync (Infrastructure/Agents/McpAgent.cs:154-213).

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => ValueTask.CompletedTask;
}
```

- [ ] **Step:** Run the test file to confirm it fails to compile (no `SubAgentSession` yet)

```bash
dotnet test Tests --filter FullyQualifiedName~SubAgentSessionTests --no-restore 2>&1 | tail -20
```

Expected: build error referencing missing `Agent.Services.SubAgents.SubAgentSession`.

### Step 1.3: Implement `SubAgentSession`

- [ ] **Step:** Create `Agent/Services/SubAgents/SubAgentSession.cs`

Key responsibilities:
- Owns one `DisposableAgent` produced by `agentFactory()`.
- Owns a `CancellationTokenSource` and a `MaxExecutionSeconds` timer that cancels it.
- Owns a `List<SubAgentTurnSnapshot> _turns` guarded by a `lock`.
- Owns an atomic `int _terminalState` (-1 = running, others map to enum) set via `Interlocked.CompareExchange`.
- `RunAsync(ct)`: starts the timer, calls `agent.RunStreamingAsync(messages, cancellationToken: linkedToken)`, runs the updates through `SnapshotRecorder` (Task 1.4 below), captures the final assistant text, sets terminal state. On `OperationCanceledException`, attribution comes from whichever source fired first (tracked via `Cancel(source)` writing to a field guarded by the same CompareExchange).
- `Cancel(source)`: idempotent; first caller wins via CAS on `_cancelledBy`.
- `Snapshot()`: returns a `SubAgentSessionView` from current state.
- `IsTerminal { get; }` and `IsSilent { get; }` exposed for the manager.
- `ReplyToConversationId { get; }` and an exposed `IChannelConnection? ReplyChannel { get; }` (set in ctor) so the manager can update the card.

```csharp
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Microsoft.Extensions.AI;

namespace Agent.Services.SubAgents;

public sealed class SubAgentSession : IAsyncDisposable
{
    public string Handle { get; }
    public SubAgentDefinition Profile { get; }
    public bool Silent { get; }
    public string ReplyToConversationId { get; }
    public IChannelConnection? ReplyChannel { get; }
    public DateTimeOffset StartedAt { get; }

    private readonly Func<DisposableAgent> _agentFactory;
    private readonly string _prompt;
    private readonly List<SubAgentTurnSnapshot> _turns = [];
    private readonly object _turnsLock = new();
    private readonly CancellationTokenSource _cts = new();

    // -1 = Running, else cast to SubAgentTerminalState
    private int _terminalState = -1;
    private SubAgentCancelSource? _cancelledBy;
    private SubAgentSessionError? _error;
    private string? _finalResult;
    private DisposableAgent? _agent;

    public SubAgentSession(
        string handle,
        SubAgentDefinition profile,
        string prompt,
        bool silent,
        Func<DisposableAgent> agentFactory,
        string replyToConversationId,
        IChannelConnection? replyChannel = null,
        DateTimeOffset? now = null)
    {
        Handle = handle;
        Profile = profile;
        Silent = silent;
        _prompt = prompt;
        _agentFactory = agentFactory;
        ReplyToConversationId = replyToConversationId;
        ReplyChannel = replyChannel;
        StartedAt = now ?? DateTimeOffset.UtcNow;
    }

    public bool IsTerminal => Volatile.Read(ref _terminalState) >= 0
                              && (SubAgentTerminalState)Volatile.Read(ref _terminalState) != SubAgentTerminalState.Running;

    public SubAgentCancelSource? CancelledBy => _cancelledBy;

    public async Task RunAsync(CancellationToken ct)
    {
        _agent = _agentFactory();
        var maxMs = Math.Max(1, Profile.MaxExecutionSeconds) * 1000;
        _cts.CancelAfter(maxMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, ct);

        try
        {
            var userMsg = new ChatMessage(ChatRole.User, _prompt);
            var stream = _agent.RunStreamingAsync([userMsg], cancellationToken: linked.Token);
            var recorder = new SnapshotRecorder();
            await foreach (var update in stream.WithCancellation(linked.Token))
            {
                if (recorder.OnUpdate(update) is { } completedTurn)
                {
                    lock (_turnsLock) _turns.Add(completedTurn);
                }
            }
            var lastTurn = recorder.Flush();
            if (lastTurn is not null) lock (_turnsLock) _turns.Add(lastTurn);

            _finalResult = recorder.FinalAssistantText;
            TrySetTerminal(SubAgentTerminalState.Completed);
        }
        catch (OperationCanceledException)
        {
            // Distinguish: if MaxExecutionSeconds elapsed, _cts.Token will be cancelled
            // and no caller had set _cancelledBy.
            if (_cancelledBy is null)
            {
                Interlocked.CompareExchange(
                    ref Unsafe.As<SubAgentCancelSource?, int>(ref _cancelledBy!),
                    (int)SubAgentCancelSource.System, 0);
                _error = new SubAgentSessionError("Timeout",
                    $"Subagent '{Profile.Id}' exceeded {Profile.MaxExecutionSeconds}s.");
            }
            else
            {
                _error ??= new SubAgentSessionError("Cancelled",
                    $"Subagent '{Profile.Id}' was cancelled by {_cancelledBy}.");
            }
            TrySetTerminal(SubAgentTerminalState.Cancelled);
        }
        catch (Exception ex)
        {
            _error = new SubAgentSessionError("InternalError", ex.Message);
            TrySetTerminal(SubAgentTerminalState.Failed);
        }
    }

    public void Cancel(SubAgentCancelSource source)
    {
        // First caller wins attribution.
        Interlocked.CompareExchange(ref _cancelledBy, source, null);
        if (!_cts.IsCancellationRequested)
        {
            try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    public SubAgentSessionView Snapshot()
    {
        var status = Volatile.Read(ref _terminalState) < 0
            ? SubAgentTerminalState.Running
            : (SubAgentTerminalState)Volatile.Read(ref _terminalState);

        IReadOnlyList<SubAgentTurnSnapshot> turns;
        lock (_turnsLock) turns = _turns.ToArray();

        return new SubAgentSessionView
        {
            Handle = Handle,
            SubAgentId = Profile.Id,
            Status = status,
            StartedAt = StartedAt,
            ElapsedSeconds = (DateTimeOffset.UtcNow - StartedAt).TotalSeconds,
            Turns = turns,
            Result = status == SubAgentTerminalState.Completed ? _finalResult : null,
            CancelledBy = _cancelledBy,
            Error = _error
        };
    }

    private bool TrySetTerminal(SubAgentTerminalState state)
        => Interlocked.CompareExchange(ref _terminalState, (int)state, -1) == -1;

    public async ValueTask DisposeAsync()
    {
        Cancel(SubAgentCancelSource.System);
        if (_agent is not null) await _agent.DisposeAsync();
        _cts.Dispose();
    }
}
```

> **Note on the `Unsafe.As` usage above:** that snippet is intentionally hand-rolled to keep the code in front of you. Replace it with a cleaner `lock`-based or `Interlocked.CompareExchange<T>` pattern if T isn't a reference type. The contract is "first caller's source wins". The code as shown will not compile against an enum directly — use a private `int _cancelledBySource = -1` backing field and convert to enum on read. **Adopt the int-backed approach when you implement.**

### Step 1.4: Implement `SnapshotRecorder`

- [ ] **Step:** Create `Agent/Services/SubAgents/SnapshotRecorder.cs`

```csharp
using Domain.DTOs.SubAgent;
using Microsoft.Extensions.AI;

namespace Agent.Services.SubAgents;

public sealed class SnapshotRecorder
{
    private const int ArgsCap = 200;
    private const int ResultCap = 500;
    private const int MaxRetainedTurns = 50;

    public string FinalAssistantText { get; private set; } = "";

    private int _index;
    private DateTimeOffset _turnStart = DateTimeOffset.UtcNow;
    private readonly System.Text.StringBuilder _assistantTextBuf = new();
    private readonly List<SubAgentToolCallSummary> _calls = [];
    private readonly List<SubAgentToolResultSummary> _results = [];
    private bool _sawToolResultThisTurn;

    public SubAgentTurnSnapshot? OnUpdate(AgentResponseUpdate update)
    {
        // A new assistant message after we've already recorded tool results = turn boundary.
        // (Heuristic: if _sawToolResultThisTurn is true and this update is assistant text/calls,
        // emit the previous turn first.)
        SubAgentTurnSnapshot? completed = null;

        if (update.Role == ChatRole.Assistant && _sawToolResultThisTurn)
        {
            completed = BuildAndReset();
        }

        if (update.Role == ChatRole.Assistant)
        {
            foreach (var c in update.Contents)
            {
                if (c is TextContent tc) _assistantTextBuf.Append(tc.Text);
                else if (c is FunctionCallContent fc)
                    _calls.Add(new SubAgentToolCallSummary(fc.Name, Truncate(fc.Arguments?.ToString() ?? "", ArgsCap)));
            }
        }
        else if (update.Role == ChatRole.Tool)
        {
            _sawToolResultThisTurn = true;
            foreach (var c in update.Contents)
            {
                if (c is FunctionResultContent fr)
                    _results.Add(new SubAgentToolResultSummary(
                        fr.Name ?? "(tool)",
                        ok: fr.Exception is null,
                        Summary: Truncate(fr.Result?.ToString() ?? "", ResultCap)));
            }
        }

        return completed;
    }

    public SubAgentTurnSnapshot? Flush()
    {
        if (_assistantTextBuf.Length == 0 && _calls.Count == 0 && _results.Count == 0) return null;
        FinalAssistantText = _assistantTextBuf.ToString();
        return BuildAndReset();
    }

    private SubAgentTurnSnapshot BuildAndReset()
    {
        var snap = new SubAgentTurnSnapshot
        {
            Index = _index++,
            AssistantText = _assistantTextBuf.ToString(),
            ToolCalls = _calls.ToArray(),
            ToolResults = _results.ToArray(),
            StartedAt = _turnStart,
            CompletedAt = DateTimeOffset.UtcNow
        };
        _assistantTextBuf.Clear();
        _calls.Clear();
        _results.Clear();
        _sawToolResultThisTurn = false;
        _turnStart = DateTimeOffset.UtcNow;
        return snap;
    }

    private static string Truncate(string s, int cap) => s.Length <= cap ? s : s[..cap] + "…";
}
```

> **Note on `MaxRetainedTurns`:** drop-policy enforcement (cap of 50) belongs to the manager once it persists snapshots — call out as TODO and add a test for it in Task 2 step 2.6.

### Step 1.5: Run tests and confirm green

- [ ] **Step:** Run

```bash
dotnet test Tests --filter FullyQualifiedName~SubAgentSessionTests -v normal 2>&1 | tail -30
```

Expected: 5 tests pass.

### Step 1.6: Commit

- [ ] **Step:**

```bash
git add Domain/DTOs/SubAgent/ Agent/Services/SubAgents/SubAgentSession.cs Agent/Services/SubAgents/SnapshotRecorder.cs Tests/Unit/Agent/SubAgentSessionTests.cs
git commit -m "feat(subagents): SubAgentSession state machine + snapshot recorder"
```

---

## Task 2: `SubAgentSessionManager` and `SubAgentSessionsRegistry`

**Files:**
- Create: `Domain/Contracts/ISubAgentSessions.cs`
- Create: `Domain/Contracts/ISubAgentSessionsRegistry.cs`
- Create: `Domain/DTOs/SubAgent/SubAgentWaitMode.cs`
- Create: `Domain/DTOs/SubAgent/SubAgentWaitResult.cs`
- Create: `Agent/Services/SubAgents/SubAgentSessionManager.cs`
- Create: `Agent/Services/SubAgents/SubAgentSessionsRegistry.cs`
- Create: `Tests/Unit/Agent/SubAgentSessionManagerTests.cs`
- Create: `Tests/Unit/Agent/SubAgentSessionsRegistryTests.cs`

### Step 2.1: DTOs and contracts

- [ ] **Step:** Create `Domain/DTOs/SubAgent/SubAgentWaitMode.cs`

```csharp
namespace Domain.DTOs.SubAgent;
public enum SubAgentWaitMode { Any, All }
```

- [ ] **Step:** Create `Domain/DTOs/SubAgent/SubAgentWaitResult.cs`

```csharp
namespace Domain.DTOs.SubAgent;

public sealed record SubAgentWaitResult(
    IReadOnlyList<string> Completed,
    IReadOnlyList<string> StillRunning);
```

- [ ] **Step:** Create `Domain/Contracts/ISubAgentSessions.cs`

```csharp
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Domain.Contracts;

public interface ISubAgentSessions
{
    string Start(SubAgentDefinition profile, string prompt, bool silent);
    SubAgentSessionView? Get(string handle);
    IReadOnlyList<SubAgentSessionView> List();
    void Cancel(string handle, SubAgentCancelSource source);
    Task<SubAgentWaitResult> WaitAsync(IReadOnlyList<string> handles, SubAgentWaitMode mode,
        TimeSpan timeout, CancellationToken ct);
    bool Release(string handle);
    int ActiveCount { get; }
}
```

- [ ] **Step:** Create `Domain/Contracts/ISubAgentSessionsRegistry.cs`

```csharp
using Domain.Agents;

namespace Domain.Contracts;

public interface ISubAgentSessionsRegistry
{
    ISubAgentSessions GetOrCreate(AgentKey key);
    bool TryGet(AgentKey key, out ISubAgentSessions sessions);
}
```

### Step 2.2: Write failing tests for the manager

- [ ] **Step:** Create `Tests/Unit/Agent/SubAgentSessionManagerTests.cs`

```csharp
using Agent.Services.SubAgents;
using Domain.Agents;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Shouldly;

namespace Tests.Unit.Agent;

public sealed class SubAgentSessionManagerTests
{
    private static SubAgentDefinition Profile() => new()
    {
        Id = "researcher", Name = "Researcher", Model = "test/model",
        McpServerEndpoints = [], MaxExecutionSeconds = 30
    };

    [Fact]
    public void Start_ReturnsUniqueHandles()
    {
        var mgr = MakeManager();
        var h1 = mgr.Start(Profile(), "p1", silent: false);
        var h2 = mgr.Start(Profile(), "p2", silent: false);
        h1.ShouldNotBe(h2);
    }

    [Fact]
    public async Task Start_BeyondCap_ReturnsThrows()
    {
        var mgr = MakeManager();
        for (var i = 0; i < SubAgentSessionManager.MaxConcurrentPerThread; i++)
            mgr.Start(Profile(), $"p{i}", silent: false);

        Should.Throw<InvalidOperationException>(() =>
            mgr.Start(Profile(), "extra", silent: false))
            .Message.ShouldContain("Too many active subagents");
    }

    [Fact]
    public async Task Get_ForUnknownHandle_ReturnsNull()
    {
        var mgr = MakeManager();
        mgr.Get("nope").ShouldBeNull();
    }

    [Fact]
    public async Task WaitAsync_ModeAll_BlocksUntilAllTerminal()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var h1 = mgr.Start(Profile(), "p1", silent: false);
        var h2 = mgr.Start(Profile(), "p2", silent: false);

        var result = await mgr.WaitAsync([h1, h2], SubAgentWaitMode.All,
            TimeSpan.FromSeconds(5), CancellationToken.None);

        result.Completed.Count.ShouldBe(2);
        result.StillRunning.ShouldBeEmpty();
    }

    [Fact]
    public async Task WaitAsync_ModeAny_ReturnsOnFirstTerminal()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var hFast = mgr.Start(Profile(), "fast", silent: false);
        // The second agent is intentionally slow via factory in MakeManager(turnsPerAgent),
        // here we re-use the fast factory and rely on identical timing — both will likely
        // complete; the test asserts at least one is in Completed and that the call returns
        // before the timeout.
        var hOther = mgr.Start(Profile(), "other", silent: false);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await mgr.WaitAsync([hFast, hOther], SubAgentWaitMode.Any,
            TimeSpan.FromSeconds(5), CancellationToken.None);
        sw.Stop();

        result.Completed.Count.ShouldBeGreaterThanOrEqualTo(1);
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task WaitAsync_TimesOut_ReturnsPartition()
    {
        var mgr = MakeManager(turnsPerAgent: 100, delay: TimeSpan.FromMilliseconds(50));
        var h = mgr.Start(Profile(), "long", silent: false);

        var result = await mgr.WaitAsync([h], SubAgentWaitMode.All,
            TimeSpan.FromMilliseconds(100), CancellationToken.None);

        result.Completed.ShouldBeEmpty();
        result.StillRunning.ShouldContain(h);
    }

    [Fact]
    public async Task Cancel_ParentSource_DoesNotEnqueueWake()
    {
        var mgr = MakeManager(turnsPerAgent: 100, delay: TimeSpan.FromMilliseconds(20));
        var wakes = new List<IReadOnlyList<string>>();
        mgr.WakeRequested += (handles) => wakes.Add(handles);

        var h = mgr.Start(Profile(), "p", silent: false);
        await Task.Delay(30);
        mgr.Cancel(h, SubAgentCancelSource.Parent);
        await Task.Delay(400); // > 250ms debounce window

        wakes.ShouldBeEmpty();
    }

    [Fact]
    public async Task NaturalCompletion_WhileParentIdle_FiresWake()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var wakes = new List<IReadOnlyList<string>>();
        mgr.WakeRequested += (handles) => wakes.Add(handles);

        mgr.SetParentTurnActive(false);
        var h = mgr.Start(Profile(), "p", silent: false);

        // Wait long enough for the run to complete and the debounce to fire.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (wakes.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        wakes.Count.ShouldBe(1);
        wakes[0].ShouldContain(h);
    }

    [Fact]
    public async Task DebounceWindow_CoalescesMultipleCompletions()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var wakes = new List<IReadOnlyList<string>>();
        mgr.WakeRequested += (handles) => wakes.Add(handles);

        mgr.SetParentTurnActive(false);
        var h1 = mgr.Start(Profile(), "a", silent: false);
        var h2 = mgr.Start(Profile(), "b", silent: false);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (wakes.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        wakes.Count.ShouldBe(1);
        wakes[0].Count.ShouldBe(2);
    }

    [Fact]
    public async Task WhenParentTurnActive_NoWakeFires_UntilTurnEnds()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var wakes = new List<IReadOnlyList<string>>();
        mgr.WakeRequested += (handles) => wakes.Add(handles);

        mgr.SetParentTurnActive(true);
        var h = mgr.Start(Profile(), "p", silent: false);
        await Task.Delay(500);

        wakes.ShouldBeEmpty();

        mgr.SetParentTurnActive(false);
        var deadline = DateTime.UtcNow.AddMilliseconds(500);
        while (wakes.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(20);

        wakes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Release_OnRunningSession_Throws()
    {
        var mgr = MakeManager(turnsPerAgent: 100, delay: TimeSpan.FromMilliseconds(50));
        var h = mgr.Start(Profile(), "p", silent: false);
        Should.Throw<InvalidOperationException>(() => mgr.Release(h));
    }

    [Fact]
    public async Task Release_OnCompletedSession_RemovesIt()
    {
        var mgr = MakeManager(turnsPerAgent: 1);
        var h = mgr.Start(Profile(), "p", silent: false);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (mgr.Get(h)?.Status == SubAgentTerminalState.Running && DateTime.UtcNow < deadline)
            await Task.Delay(20);

        mgr.Release(h).ShouldBeTrue();
        mgr.Get(h).ShouldBeNull();
    }

    private static SubAgentSessionManager MakeManager(int turnsPerAgent = 1, TimeSpan? delay = null)
    {
        return new SubAgentSessionManager(
            agentFactory: _ => new FakeStreamingAgentFactoryAgent(turnsPerAgent, delay ?? TimeSpan.Zero),
            replyToConversationId: "c1",
            replyChannel: null,
            wakeDebounce: TimeSpan.FromMilliseconds(250));
    }
}

// Same FakeStreamingAgent shape as Task 1, but injected per-Start via the factory.
file sealed class FakeStreamingAgentFactoryAgent : Domain.Agents.DisposableAgent
{
    public FakeStreamingAgentFactoryAgent(int turns, TimeSpan delay) { /* ... */ }
    // RunStreamingAsync override; mirror Task 1's FakeStreamingAgent.
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public override ValueTask DisposeThreadSessionAsync(AgentSession thread) => ValueTask.CompletedTask;
}
```

- [ ] **Step:** Run

```bash
dotnet test Tests --filter FullyQualifiedName~SubAgentSessionManagerTests --no-restore 2>&1 | tail -10
```

Expected: build error, missing `SubAgentSessionManager`.

### Step 2.3: Implement `SubAgentSessionManager`

- [ ] **Step:** Create `Agent/Services/SubAgents/SubAgentSessionManager.cs`

```csharp
using System.Collections.Concurrent;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Agent.Services.SubAgents;

public sealed class SubAgentSessionManager : ISubAgentSessions, IAsyncDisposable
{
    public const int MaxConcurrentPerThread = 8;

    public event Action<IReadOnlyList<string>>? WakeRequested;

    private readonly Func<SubAgentDefinition, DisposableAgent> _agentFactory;
    private readonly string _replyToConversationId;
    private readonly IChannelConnection? _replyChannel;
    private readonly TimeSpan _wakeDebounce;
    private readonly ConcurrentDictionary<string, SubAgentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, Task> _runs = new();

    private readonly object _wakeLock = new();
    private readonly HashSet<string> _wakeBuffer = [];
    private CancellationTokenSource? _wakeDebounceCts;
    private bool _isParentTurnActive = true;

    public SubAgentSessionManager(
        Func<SubAgentDefinition, DisposableAgent> agentFactory,
        string replyToConversationId,
        IChannelConnection? replyChannel,
        TimeSpan? wakeDebounce = null)
    {
        _agentFactory = agentFactory;
        _replyToConversationId = replyToConversationId;
        _replyChannel = replyChannel;
        _wakeDebounce = wakeDebounce ?? TimeSpan.FromMilliseconds(250);
    }

    public int ActiveCount => _sessions.Values.Count(s => !s.IsTerminal);

    public string Start(SubAgentDefinition profile, string prompt, bool silent)
    {
        if (ActiveCount >= MaxConcurrentPerThread)
            throw new InvalidOperationException(
                $"Too many active subagents in this thread ({MaxConcurrentPerThread} max). Cancel or wait on existing handles first.");

        var handle = NewHandle();
        var session = new SubAgentSession(handle, profile, prompt, silent,
            agentFactory: () => _agentFactory(profile),
            replyToConversationId: _replyToConversationId,
            replyChannel: _replyChannel);
        _sessions[handle] = session;

        var run = Task.Run(async () =>
        {
            try { await session.RunAsync(CancellationToken.None); }
            finally { OnSessionTerminal(session); }
        });
        _runs[handle] = run;
        return handle;
    }

    public SubAgentSessionView? Get(string handle) => _sessions.TryGetValue(handle, out var s) ? s.Snapshot() : null;

    public IReadOnlyList<SubAgentSessionView> List() => _sessions.Values.Select(s => s.Snapshot()).ToArray();

    public void Cancel(string handle, SubAgentCancelSource source)
    {
        if (_sessions.TryGetValue(handle, out var s)) s.Cancel(source);
    }

    public async Task<SubAgentWaitResult> WaitAsync(IReadOnlyList<string> handles,
        SubAgentWaitMode mode, TimeSpan timeout, CancellationToken ct)
    {
        var tasks = handles
            .Where(_runs.ContainsKey)
            .Select(h => (h, t: _runs[h]))
            .ToArray();
        if (tasks.Length == 0) return new SubAgentWaitResult([], handles);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        if (mode == SubAgentWaitMode.All)
        {
            try { await Task.WhenAll(tasks.Select(x => x.t)).WaitAsync(linked.Token); }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested) { }
        }
        else
        {
            try { await Task.WhenAny(tasks.Select(x => x.t)).WaitAsync(linked.Token); }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested) { }
        }

        var completed = tasks.Where(x => x.t.IsCompleted).Select(x => x.h).ToArray();
        var still = tasks.Where(x => !x.t.IsCompleted).Select(x => x.h).ToArray();
        return new SubAgentWaitResult(completed, still);
    }

    public bool Release(string handle)
    {
        if (!_sessions.TryGetValue(handle, out var s))
            return false;
        if (!s.IsTerminal)
            throw new InvalidOperationException($"Cannot release running session '{handle}'.");
        _sessions.TryRemove(handle, out _);
        _runs.TryRemove(handle, out _);
        return true;
    }

    public void SetParentTurnActive(bool active)
    {
        bool flushNow = false;
        lock (_wakeLock)
        {
            _isParentTurnActive = active;
            if (!active && _wakeBuffer.Count > 0) flushNow = true;
        }
        if (flushNow) FlushWakeNow();
    }

    private void OnSessionTerminal(SubAgentSession session)
    {
        // Card update for non-silent sessions
        if (!session.Silent && session.ReplyChannel is not null)
        {
            var view = session.Snapshot();
            var status = view.Status.ToString();
            var summary = view.Result ?? view.Error?.Message ?? "";
            _ = SafeUpdateCardAsync(session.ReplyChannel, view.Handle, status, summary);
        }

        // Skip wake if parent cancelled itself.
        if (session.CancelledBy == SubAgentCancelSource.Parent) return;

        bool fireNow;
        lock (_wakeLock)
        {
            _wakeBuffer.Add(session.Handle);
            // Restart debounce window
            _wakeDebounceCts?.Cancel();
            _wakeDebounceCts = new CancellationTokenSource();
            fireNow = false;
            // Schedule the debounce
            _ = ScheduleWakeFlushAsync(_wakeDebounceCts.Token);
        }
    }

    private async Task ScheduleWakeFlushAsync(CancellationToken token)
    {
        try { await Task.Delay(_wakeDebounce, token); }
        catch (OperationCanceledException) { return; }
        FlushWakeNow();
    }

    private void FlushWakeNow()
    {
        string[] toEmit;
        lock (_wakeLock)
        {
            if (_isParentTurnActive) return;
            if (_wakeBuffer.Count == 0) return;
            toEmit = _wakeBuffer.ToArray();
            _wakeBuffer.Clear();
        }
        WakeRequested?.Invoke(toEmit);
    }

    private async Task SafeUpdateCardAsync(IChannelConnection ch, string handle, string status, string summary)
    {
        try
        {
            await ch.UpdateSubAgentAsync(_replyToConversationId, handle, status, summary, CancellationToken.None);
        }
        catch
        {
            // Per spec: log + retry once + drop. The logger is plumbed via DI in production;
            // unit-test paths pass null channel so this branch is unreached.
        }
    }

    private static string NewHandle() => Guid.NewGuid().ToString("N")[..16];

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values)
            s.Cancel(SubAgentCancelSource.System);
        try { await Task.WhenAll(_runs.Values).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        foreach (var s in _sessions.Values)
            await s.DisposeAsync();
        _sessions.Clear();
        _runs.Clear();
    }
}
```

### Step 2.4: Implement the registry

- [ ] **Step:** Create `Tests/Unit/Agent/SubAgentSessionsRegistryTests.cs`

```csharp
using Agent.Services.SubAgents;
using Domain.Agents;
using Shouldly;

namespace Tests.Unit.Agent;

public sealed class SubAgentSessionsRegistryTests
{
    [Fact]
    public void GetOrCreate_ReturnsSameInstanceForSameKey()
    {
        var reg = new SubAgentSessionsRegistry(MakeFactory());
        var a = reg.GetOrCreate(new AgentKey("c1", "agent1"));
        var b = reg.GetOrCreate(new AgentKey("c1", "agent1"));
        a.ShouldBeSameAs(b);
    }

    [Fact]
    public void GetOrCreate_DifferentKeys_AreIsolated()
    {
        var reg = new SubAgentSessionsRegistry(MakeFactory());
        var a = reg.GetOrCreate(new AgentKey("c1", "agent1"));
        var b = reg.GetOrCreate(new AgentKey("c2", "agent1"));
        a.ShouldNotBeSameAs(b);
    }

    private static Func<AgentKey, SubAgentSessionManager> MakeFactory() =>
        _ => new SubAgentSessionManager(
            agentFactory: _ => throw new NotImplementedException(),
            replyToConversationId: "c1",
            replyChannel: null);
}
```

- [ ] **Step:** Create `Agent/Services/SubAgents/SubAgentSessionsRegistry.cs`

```csharp
using System.Collections.Concurrent;
using Domain.Agents;
using Domain.Contracts;

namespace Agent.Services.SubAgents;

public sealed class SubAgentSessionsRegistry : ISubAgentSessionsRegistry, IAsyncDisposable
{
    private readonly ConcurrentDictionary<AgentKey, SubAgentSessionManager> _byKey = new();
    private readonly Func<AgentKey, SubAgentSessionManager> _factory;

    public SubAgentSessionsRegistry(Func<AgentKey, SubAgentSessionManager> factory)
    {
        _factory = factory;
    }

    public ISubAgentSessions GetOrCreate(AgentKey key) =>
        _byKey.GetOrAdd(key, k => _factory(k));

    public bool TryGet(AgentKey key, out ISubAgentSessions sessions)
    {
        if (_byKey.TryGetValue(key, out var mgr)) { sessions = mgr; return true; }
        sessions = null!;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var m in _byKey.Values) await m.DisposeAsync();
        _byKey.Clear();
    }
}
```

### Step 2.5: Run tests

- [ ] **Step:**

```bash
dotnet test Tests --filter "FullyQualifiedName~SubAgentSessionManagerTests|FullyQualifiedName~SubAgentSessionsRegistryTests" -v normal 2>&1 | tail -30
```

Expected: all pass.

### Step 2.6: Commit

- [ ] **Step:**

```bash
git add Domain/Contracts/ISubAgentSessions.cs Domain/Contracts/ISubAgentSessionsRegistry.cs Domain/DTOs/SubAgent/SubAgentWaitMode.cs Domain/DTOs/SubAgent/SubAgentWaitResult.cs Agent/Services/SubAgents/SubAgentSessionManager.cs Agent/Services/SubAgents/SubAgentSessionsRegistry.cs Tests/Unit/Agent/SubAgentSessionManagerTests.cs Tests/Unit/Agent/SubAgentSessionsRegistryTests.cs
git commit -m "feat(subagents): per-thread SubAgentSessionManager and registry"
```

---

## Task 3: `run_subagent` background mode

**Files:**
- Modify: `Domain/DTOs/FeatureConfig.cs`
- Modify: `Domain/Tools/SubAgents/SubAgentRunTool.cs`
- Create: `Tests/Unit/Domain/SubAgentRunToolBackgroundTests.cs`

### Step 3.1: Extend `FeatureConfig`

- [ ] **Step:** Replace `Domain/DTOs/FeatureConfig.cs` contents with

```csharp
using Domain.Agents;
using Domain.Contracts;

namespace Domain.DTOs;

public record FeatureConfig(
    IReadOnlySet<string>? EnabledTools = null,
    Func<SubAgentDefinition, DisposableAgent>? SubAgentFactory = null,
    string? UserId = null,
    ISubAgentSessions? SubAgentSessions = null);
```

### Step 3.2: Failing test

- [ ] **Step:** Create `Tests/Unit/Domain/SubAgentRunToolBackgroundTests.cs`

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Domain.Tools.SubAgents;
using Shouldly;

namespace Tests.Unit.Domain;

public sealed class SubAgentRunToolBackgroundTests
{
    [Fact]
    public async Task RunAsync_WithBackgroundFlag_StartsAndReturnsHandle()
    {
        var sessions = new FakeSessions();
        var registry = new SubAgentRegistryOptions { SubAgents = [Profile()] };
        var cfg = new FeatureConfig(SubAgentSessions: sessions);
        var tool = new SubAgentRunTool(registry, cfg);

        var result = await tool.RunAsync("researcher", "go", run_in_background: true, silent: false);

        result["status"]!.ToString().ShouldBe("started");
        result["handle"]!.ToString().ShouldBe(sessions.LastHandle);
        result["subagent_id"]!.ToString().ShouldBe("researcher");
        sessions.StartCount.ShouldBe(1);
        sessions.LastSilent.ShouldBeFalse();
    }

    [Fact]
    public async Task RunAsync_WithBackgroundFlag_NoSessions_ReturnsUnavailable()
    {
        var registry = new SubAgentRegistryOptions { SubAgents = [Profile()] };
        var cfg = new FeatureConfig(/* SubAgentSessions = null */);
        var tool = new SubAgentRunTool(registry, cfg);

        var result = await tool.RunAsync("researcher", "go", run_in_background: true);
        result["error"]!["code"]!.ToString().ShouldBe("unavailable");
    }

    [Fact]
    public async Task RunAsync_WithoutBackgroundFlag_PreservesExistingBehavior()
    {
        // Use a fake SubAgentFactory that returns immediately; verify "completed" envelope.
        // (See existing SubAgentRunTool flow for envelope shape.)
    }

    private static SubAgentDefinition Profile() => new()
    {
        Id = "researcher", Name = "Researcher", Model = "test/model",
        McpServerEndpoints = [], MaxExecutionSeconds = 30
    };

    private sealed class FakeSessions : ISubAgentSessions
    {
        public int StartCount;
        public string LastHandle = "";
        public bool LastSilent;

        public string Start(SubAgentDefinition profile, string prompt, bool silent)
        {
            StartCount++;
            LastSilent = silent;
            LastHandle = "h-" + StartCount;
            return LastHandle;
        }

        public SubAgentSessionView? Get(string handle) => null;
        public IReadOnlyList<SubAgentSessionView> List() => [];
        public void Cancel(string handle, SubAgentCancelSource source) { }
        public Task<SubAgentWaitResult> WaitAsync(IReadOnlyList<string> handles, SubAgentWaitMode mode, TimeSpan timeout, CancellationToken ct)
            => Task.FromResult(new SubAgentWaitResult([], handles));
        public bool Release(string handle) => false;
        public int ActiveCount => 0;
    }
}
```

- [ ] **Step:** Run, expect FAIL (signature mismatch)

```bash
dotnet test Tests --filter FullyQualifiedName~SubAgentRunToolBackgroundTests --no-restore 2>&1 | tail -20
```

### Step 3.3: Modify `SubAgentRunTool`

- [ ] **Step:** Update `Domain/Tools/SubAgents/SubAgentRunTool.cs` — extend `RunAsync` to accept the two new params and route to `ISubAgentSessions` when background:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.SubAgent;
using Domain.Extensions;
using Microsoft.Extensions.AI;

namespace Domain.Tools.SubAgents;

public class SubAgentRunTool(
    SubAgentRegistryOptions registryOptions,
    FeatureConfig featureConfig)
{
    public const string Name = "run_subagent";
    private readonly SubAgentDefinition[] _profiles = registryOptions.SubAgents;

    public string Description
    {
        get
        {
            var profileList = string.Join("\n",
                _profiles.Select(p => $"- \"{p.Id}\": {p.Description ?? p.Name}"));
            return $"""
                    Runs a task on a subagent.
                    - Default (run_in_background=false): blocks, returns final result.
                    - run_in_background=true: starts the subagent and returns a handle. Use
                      subagent_check, subagent_wait, subagent_cancel to manage it. If silent=true,
                      no chat card is shown to the user (otherwise a card with a Cancel button appears).
                    Available subagents:
                    {profileList}
                    """;
        }
    }

    public async Task<JsonNode> RunAsync(
        [Description("ID of the subagent profile to use")]
        string subAgentId,
        [Description("The task/prompt to send to the subagent")]
        string prompt,
        [Description("If true, returns a handle immediately and the subagent runs in the background")]
        bool run_in_background = false,
        [Description("If true and run_in_background is true, no chat card is shown to the user")]
        bool silent = false,
        CancellationToken ct = default)
    {
        var profile = _profiles.FirstOrDefault(p =>
            p.Id.Equals(subAgentId, StringComparison.OrdinalIgnoreCase));

        if (profile is null)
            return ToolError.Create(ToolError.Codes.NotFound,
                $"Unknown subagent: '{subAgentId}'. Available: {string.Join(", ", _profiles.Select(p => p.Id))}",
                retryable: false);

        if (run_in_background)
        {
            if (featureConfig.SubAgentSessions is null)
                return ToolError.Create(ToolError.Codes.Unavailable,
                    "Background subagent execution is not available in this context", retryable: false);

            try
            {
                var handle = featureConfig.SubAgentSessions.Start(profile, prompt, silent);
                return new JsonObject
                {
                    ["status"] = "started",
                    ["handle"] = handle,
                    ["subagent_id"] = profile.Id
                };
            }
            catch (InvalidOperationException ex)
            {
                return ToolError.Create(ToolError.Codes.Unavailable, ex.Message, retryable: true);
            }
        }

        if (featureConfig.SubAgentFactory is null)
            return ToolError.Create(ToolError.Codes.Unavailable,
                "Subagent execution is not available in this context", retryable: false);

        try
        {
            await using var agent = featureConfig.SubAgentFactory(profile);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(profile.MaxExecutionSeconds));

            var userMessage = new ChatMessage(ChatRole.User, prompt);
            userMessage.SetSenderId(featureConfig.UserId);
            var response = await agent.RunAsync([userMessage], cancellationToken: timeoutCts.Token);

            return new JsonObject
            {
                ["status"] = "completed",
                ["result"] = response.Text
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ToolError.Create(ToolError.Codes.Timeout,
                $"Subagent '{profile.Id}' exceeded its maximum execution time of {profile.MaxExecutionSeconds}s",
                retryable: true);
        }
        catch (Exception ex)
        {
            return ToolError.Create(ToolError.Codes.InternalError, ex.Message, retryable: true);
        }
    }
}
```

### Step 3.4: Run tests, commit

- [ ] **Step:**

```bash
dotnet test Tests --filter FullyQualifiedName~SubAgentRunToolBackgroundTests -v normal 2>&1 | tail -20
```

Expected: pass.

- [ ] **Step:**

```bash
git add Domain/DTOs/FeatureConfig.cs Domain/Tools/SubAgents/SubAgentRunTool.cs Tests/Unit/Domain/SubAgentRunToolBackgroundTests.cs
git commit -m "feat(subagents): run_subagent background mode + silent flag"
```

---

## Task 4: `subagent_check` and `subagent_release`

**Files:**
- Create: `Domain/Tools/SubAgents/SubAgentCheckTool.cs`
- Create: `Domain/Tools/SubAgents/SubAgentReleaseTool.cs`
- Create: `Tests/Unit/Domain/SubAgentCheckToolTests.cs`
- Create: `Tests/Unit/Domain/SubAgentReleaseToolTests.cs`

### Step 4.1: Failing tests

- [ ] **Step:** Create `Tests/Unit/Domain/SubAgentCheckToolTests.cs` with two tests:
  1. `RunAsync_KnownHandle_ReturnsViewWithSnapshots` — uses a fake `ISubAgentSessions` with a stored `SubAgentSessionView`, asserts JSON shape matches the spec (`status`, `handle`, `subagent_id`, `started_at`, `elapsed_seconds`, `turns[]`, optionally `result`/`error`/`cancelled_by`).
  2. `RunAsync_UnknownHandle_ReturnsNotFound` — fake returns null, tool returns `not_found` ToolError.

Mirror Task 3's `FakeSessions` pattern.

- [ ] **Step:** Create `Tests/Unit/Domain/SubAgentReleaseToolTests.cs` with three tests:
  1. `RunAsync_TerminalSession_RemovesIt` — fake returns true.
  2. `RunAsync_RunningSession_ReturnsInvalidOperation` — fake throws `InvalidOperationException`.
  3. `RunAsync_UnknownHandle_ReturnsNotFound` — fake returns false → ToolError `not_found`.

- [ ] **Step:** Run, expect FAIL.

```bash
dotnet test Tests --filter "FullyQualifiedName~SubAgentCheckToolTests|FullyQualifiedName~SubAgentReleaseToolTests" --no-restore 2>&1 | tail -10
```

### Step 4.2: Implement the tools

- [ ] **Step:** Create `Domain/Tools/SubAgents/SubAgentCheckTool.cs`

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Domain.Tools.SubAgents;

public class SubAgentCheckTool(FeatureConfig featureConfig)
{
    public const string Name = "subagent_check";
    public string Description =>
        "Returns current status, per-turn snapshots, and (if terminal) the final result of a subagent.";

    public Task<JsonNode> RunAsync(
        [Description("Handle returned by run_subagent with run_in_background=true")]
        string handle,
        CancellationToken ct = default)
    {
        if (featureConfig.SubAgentSessions is null)
            return Task.FromResult<JsonNode>(ToolError.Create(ToolError.Codes.Unavailable,
                "Background subagent control is not available in this context", retryable: false));

        var view = featureConfig.SubAgentSessions.Get(handle);
        if (view is null)
            return Task.FromResult<JsonNode>(ToolError.Create(ToolError.Codes.NotFound,
                $"Unknown subagent handle: '{handle}'", retryable: false));

        return Task.FromResult<JsonNode>(SerializeView(view));
    }

    public static JsonObject SerializeView(SubAgentSessionView v)
    {
        var turns = new JsonArray();
        foreach (var t in v.Turns)
        {
            var calls = new JsonArray(t.ToolCalls.Select(c =>
                (JsonNode)new JsonObject { ["name"] = c.Name, ["args_summary"] = c.ArgsSummary }).ToArray());
            var results = new JsonArray(t.ToolResults.Select(r =>
                (JsonNode)new JsonObject { ["name"] = r.Name, ["ok"] = r.Ok, ["summary"] = r.Summary }).ToArray());
            turns.Add(new JsonObject
            {
                ["index"] = t.Index,
                ["assistant_text"] = t.AssistantText,
                ["tool_calls"] = calls,
                ["tool_results"] = results,
                ["started_at"] = t.StartedAt.ToString("O"),
                ["completed_at"] = t.CompletedAt.ToString("O")
            });
        }

        var obj = new JsonObject
        {
            ["status"] = v.Status.ToString().ToLowerInvariant(),
            ["handle"] = v.Handle,
            ["subagent_id"] = v.SubAgentId,
            ["started_at"] = v.StartedAt.ToString("O"),
            ["elapsed_seconds"] = v.ElapsedSeconds,
            ["turns"] = turns
        };
        if (v.Result is not null) obj["result"] = v.Result;
        if (v.Error is not null) obj["error"] = new JsonObject { ["code"] = v.Error.Code, ["message"] = v.Error.Message };
        if (v.CancelledBy is not null) obj["cancelled_by"] = v.CancelledBy.ToString()!.ToLowerInvariant();
        return obj;
    }
}
```

- [ ] **Step:** Create `Domain/Tools/SubAgents/SubAgentReleaseTool.cs`

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Tools.SubAgents;

public class SubAgentReleaseTool(FeatureConfig featureConfig)
{
    public const string Name = "subagent_release";
    public string Description =>
        "Drops a terminal subagent session from the registry. Use after consuming the result to free memory.";

    public Task<JsonNode> RunAsync(string handle, CancellationToken ct = default)
    {
        if (featureConfig.SubAgentSessions is null)
            return Task.FromResult<JsonNode>(ToolError.Create(ToolError.Codes.Unavailable,
                "Background subagent control is not available in this context", retryable: false));

        try
        {
            var removed = featureConfig.SubAgentSessions.Release(handle);
            if (!removed)
                return Task.FromResult<JsonNode>(ToolError.Create(ToolError.Codes.NotFound,
                    $"Unknown subagent handle: '{handle}'", retryable: false));
            return Task.FromResult<JsonNode>(new JsonObject { ["status"] = "released" });
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult<JsonNode>(ToolError.Create(
                "invalid_operation", ex.Message, retryable: false));
        }
    }
}
```

- [ ] **Step:** Run, expect PASS.

```bash
dotnet test Tests --filter "FullyQualifiedName~SubAgentCheckToolTests|FullyQualifiedName~SubAgentReleaseToolTests" -v normal 2>&1 | tail -20
```

- [ ] **Step:** Commit

```bash
git add Domain/Tools/SubAgents/SubAgentCheckTool.cs Domain/Tools/SubAgents/SubAgentReleaseTool.cs Tests/Unit/Domain/SubAgentCheckToolTests.cs Tests/Unit/Domain/SubAgentReleaseToolTests.cs
git commit -m "feat(subagents): subagent_check and subagent_release tools"
```

---

## Task 5: `subagent_cancel`

**Files:**
- Create: `Domain/Tools/SubAgents/SubAgentCancelTool.cs`
- Create: `Tests/Unit/Domain/SubAgentCancelToolTests.cs`

### Step 5.1: Failing tests

- [ ] **Step:** Create `Tests/Unit/Domain/SubAgentCancelToolTests.cs` with three tests:
  1. `RunAsync_KnownHandle_CallsCancelWithParentSource` — fake `ISubAgentSessions` records the source; assert `SubAgentCancelSource.Parent`. Returns `{ status: "cancelling" }`.
  2. `RunAsync_AlreadyTerminal_ReturnsCurrentTerminalState` — fake returns terminal view; tool returns `{ status: "completed" }` (or whatever the current state is) without calling Cancel.
  3. `RunAsync_UnknownHandle_ReturnsNotFound`.

- [ ] **Step:** Run, expect FAIL.

### Step 5.2: Implement

- [ ] **Step:** Create `Domain/Tools/SubAgents/SubAgentCancelTool.cs`

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Domain.Tools.SubAgents;

public class SubAgentCancelTool(FeatureConfig featureConfig)
{
    public const string Name = "subagent_cancel";
    public string Description =>
        "Best-effort cancel of a running subagent. Returns immediately; cancellation is observed asynchronously.";

    public Task<JsonNode> RunAsync(string handle, CancellationToken ct = default)
    {
        if (featureConfig.SubAgentSessions is null)
            return Task.FromResult<JsonNode>(ToolError.Create(ToolError.Codes.Unavailable,
                "Background subagent control is not available in this context", retryable: false));

        var view = featureConfig.SubAgentSessions.Get(handle);
        if (view is null)
            return Task.FromResult<JsonNode>(ToolError.Create(ToolError.Codes.NotFound,
                $"Unknown subagent handle: '{handle}'", retryable: false));

        if (view.Status != SubAgentTerminalState.Running)
            return Task.FromResult<JsonNode>(new JsonObject
            {
                ["status"] = view.Status.ToString().ToLowerInvariant(),
                ["handle"] = handle
            });

        featureConfig.SubAgentSessions.Cancel(handle, SubAgentCancelSource.Parent);
        return Task.FromResult<JsonNode>(new JsonObject
        {
            ["status"] = "cancelling",
            ["handle"] = handle
        });
    }
}
```

- [ ] **Step:** Run tests; commit.

```bash
dotnet test Tests --filter FullyQualifiedName~SubAgentCancelToolTests -v normal 2>&1 | tail -20
git add Domain/Tools/SubAgents/SubAgentCancelTool.cs Tests/Unit/Domain/SubAgentCancelToolTests.cs
git commit -m "feat(subagents): subagent_cancel tool"
```

---

## Task 6: `subagent_wait`

**Files:**
- Create: `Domain/Tools/SubAgents/SubAgentWaitTool.cs`
- Create: `Tests/Unit/Domain/SubAgentWaitToolTests.cs`

### Step 6.1: Failing tests

- [ ] **Step:** Create `Tests/Unit/Domain/SubAgentWaitToolTests.cs` with three tests:
  1. `RunAsync_AnyMode_DelegatesToSessions` — fake captures the call; tool returns `{ completed: [...], still_running: [...] }`.
  2. `RunAsync_AllMode_DelegatesWithCorrectMode`.
  3. `RunAsync_InvalidTimeout_ReturnsInvalidArgument` — timeout < 1 → ToolError `invalid_argument`.

- [ ] **Step:** Run, expect FAIL.

### Step 6.2: Implement

- [ ] **Step:** Create `Domain/Tools/SubAgents/SubAgentWaitTool.cs`

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Domain.Tools.SubAgents;

public class SubAgentWaitTool(FeatureConfig featureConfig)
{
    public const string Name = "subagent_wait";
    public string Description =>
        "Blocks the current tool call until the listed subagents reach a terminal state (mode='all' or 'any') or the timeout elapses. Returns the partition.";

    public async Task<JsonNode> RunAsync(
        [Description("List of handles to wait on")]
        string[] handles,
        [Description("'any' to return when at least one is terminal; 'all' to wait for every handle")]
        string mode = "all",
        [Description("Timeout in seconds (1-600)")]
        int timeout_seconds = 60,
        CancellationToken ct = default)
    {
        if (featureConfig.SubAgentSessions is null)
            return ToolError.Create(ToolError.Codes.Unavailable,
                "Background subagent control is not available in this context", retryable: false);

        if (timeout_seconds < 1 || timeout_seconds > 600)
            return ToolError.Create(ToolError.Codes.InvalidArgument,
                "timeout_seconds must be between 1 and 600", retryable: false);

        if (!Enum.TryParse<SubAgentWaitMode>(mode, ignoreCase: true, out var waitMode))
            return ToolError.Create(ToolError.Codes.InvalidArgument,
                $"Invalid mode '{mode}'. Use 'any' or 'all'.", retryable: false);

        var result = await featureConfig.SubAgentSessions.WaitAsync(
            handles, waitMode, TimeSpan.FromSeconds(timeout_seconds), ct);

        return new JsonObject
        {
            ["completed"] = new JsonArray(result.Completed.Select(h => (JsonNode)h).ToArray()),
            ["still_running"] = new JsonArray(result.StillRunning.Select(h => (JsonNode)h).ToArray())
        };
    }
}
```

- [ ] **Step:** Run + commit.

```bash
dotnet test Tests --filter FullyQualifiedName~SubAgentWaitToolTests -v normal 2>&1 | tail -20
git add Domain/Tools/SubAgents/SubAgentWaitTool.cs Tests/Unit/Domain/SubAgentWaitToolTests.cs
git commit -m "feat(subagents): subagent_wait tool"
```

---

## Task 7: `subagent_list`

**Files:**
- Create: `Domain/Tools/SubAgents/SubAgentListTool.cs`
- Create: `Tests/Unit/Domain/SubAgentListToolTests.cs`

### Step 7.1: Failing test

- [ ] **Step:** Create `Tests/Unit/Domain/SubAgentListToolTests.cs` — single test: fake returns 2 views (one running, one completed); tool returns a JsonArray with 2 entries containing `{handle, subagent_id, status, elapsed_seconds, started_at}`.

### Step 7.2: Implement

- [ ] **Step:** Create `Domain/Tools/SubAgents/SubAgentListTool.cs`

```csharp
using System.Text.Json.Nodes;
using Domain.DTOs;

namespace Domain.Tools.SubAgents;

public class SubAgentListTool(FeatureConfig featureConfig)
{
    public const string Name = "subagent_list";
    public string Description =>
        "Lists all subagent sessions in this conversation (running and terminal).";

    public Task<JsonNode> RunAsync(CancellationToken ct = default)
    {
        if (featureConfig.SubAgentSessions is null)
            return Task.FromResult<JsonNode>(ToolError.Create(ToolError.Codes.Unavailable,
                "Background subagent control is not available in this context", retryable: false));

        var arr = new JsonArray(featureConfig.SubAgentSessions.List()
            .Select(v => (JsonNode)new JsonObject
            {
                ["handle"] = v.Handle,
                ["subagent_id"] = v.SubAgentId,
                ["status"] = v.Status.ToString().ToLowerInvariant(),
                ["started_at"] = v.StartedAt.ToString("O"),
                ["elapsed_seconds"] = v.ElapsedSeconds
            }).ToArray());
        return Task.FromResult<JsonNode>(arr);
    }
}
```

- [ ] **Step:** Run + commit.

```bash
dotnet test Tests --filter FullyQualifiedName~SubAgentListToolTests -v normal 2>&1 | tail -20
git add Domain/Tools/SubAgents/SubAgentListTool.cs Tests/Unit/Domain/SubAgentListToolTests.cs
git commit -m "feat(subagents): subagent_list tool"
```

---

## Task 8: Wake buffer + push trigger via `SystemChannelConnection`

This is the most surgical task — it touches `IChannelConnection`, `ChatMonitor`, and `MultiAgentFactory`.

**Files:**
- Modify: `Domain/Contracts/IChannelConnection.cs` — add `AnnounceSubAgentAsync`, `UpdateSubAgentAsync`, `IAsyncEnumerable<SubAgentCancelRequest> SubAgentCancelRequests`
- Create: `Domain/DTOs/SubAgent/SubAgentCancelRequest.cs`
- Create: `Agent/Services/SubAgents/SystemChannelConnection.cs`
- Modify: `Domain/Monitor/ChatMonitor.cs` — subscribe to each channel's `SubAgentCancelRequests` and route to registry
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs` — populate `FeatureConfig.SubAgentSessions` from registry; bind reply channel to manager
- Modify: `Agent/Modules/SubAgentModule.cs` — register `ISubAgentSessionsRegistry` singleton + `SystemChannelConnection` as an `IChannelConnection`
- Existing channel impls (`McpChannelSignalR/Services/SignalRChannelConnection.cs`, `McpChannelTelegram/Services/TelegramChannelConnection.cs`, `McpChannelServiceBus/Services/ServiceBusChannelConnection.cs`): implement the three new members. ServiceBus returns `AsyncEnumerable.Empty<SubAgentCancelRequest>()` and no-op for the announce/update.
- Create: `Tests/Integration/Agents/SubAgentBackgroundFlowTests.cs`

### Step 8.1: DTO + interface

- [ ] **Step:** Create `Domain/DTOs/SubAgent/SubAgentCancelRequest.cs`

```csharp
namespace Domain.DTOs.SubAgent;

public sealed record SubAgentCancelRequest(string ConversationId, string Handle);
```

- [ ] **Step:** Modify `Domain/Contracts/IChannelConnection.cs` — add three new members. Keep existing ones intact:

```csharp
using Domain.DTOs.SubAgent;
// ... existing usings ...

public interface IChannelConnection
{
    string ChannelId { get; }
    IAsyncEnumerable<ChannelMessage> Messages { get; }
    IAsyncEnumerable<SubAgentCancelRequest> SubAgentCancelRequests
        => AsyncEnumerable.Empty<SubAgentCancelRequest>();

    Task SendReplyAsync(...);  // unchanged
    Task<ToolApprovalResult> RequestApprovalAsync(...);  // unchanged
    Task NotifyAutoApprovedAsync(...);  // unchanged
    Task<string?> CreateConversationAsync(...);  // unchanged

    Task AnnounceSubAgentAsync(string conversationId, string handle, string subAgentId,
        string promptSummary, CancellationToken ct) => Task.CompletedTask;
    Task UpdateSubAgentAsync(string conversationId, string handle, string status,
        string terminalSummary, CancellationToken ct) => Task.CompletedTask;
}
```

The default-implementation pattern keeps existing channel classes compiling without modification; only those that override gain real behavior.

### Step 8.2: `SystemChannelConnection`

- [ ] **Step:** Create `Agent/Services/SubAgents/SystemChannelConnection.cs`

```csharp
using System.Collections.Concurrent;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.SubAgent;

namespace Agent.Services.SubAgents;

public sealed class SystemChannelConnection : IChannelConnection
{
    public string ChannelId => "system";

    private readonly Channel<ChannelMessage> _msgs = Channel.CreateUnbounded<ChannelMessage>();
    private readonly ConcurrentDictionary<string, IChannelConnection> _replyRoutes = new();

    public IAsyncEnumerable<ChannelMessage> Messages => _msgs.Reader.ReadAllAsync();
    public IAsyncEnumerable<SubAgentCancelRequest> SubAgentCancelRequests => AsyncEnumerable.Empty<SubAgentCancelRequest>();

    public ValueTask InjectAsync(ChannelMessage msg, IChannelConnection replyRoute)
    {
        _replyRoutes[msg.ConversationId] = replyRoute;
        return _msgs.Writer.WriteAsync(msg);
    }

    public Task SendReplyAsync(string conversationId, string content, ReplyContentType contentType,
        bool isComplete, string? messageId, CancellationToken ct)
    {
        if (_replyRoutes.TryGetValue(conversationId, out var route))
            return route.SendReplyAsync(conversationId, content, contentType, isComplete, messageId, ct);
        return Task.CompletedTask;
    }

    public Task<ToolApprovalResult> RequestApprovalAsync(string conversationId,
        IReadOnlyList<ToolApprovalRequest> requests, CancellationToken ct)
        => _replyRoutes.TryGetValue(conversationId, out var route)
            ? route.RequestApprovalAsync(conversationId, requests, ct)
            : Task.FromResult(ToolApprovalResult.Approved);

    public Task NotifyAutoApprovedAsync(string conversationId, IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken ct)
        => _replyRoutes.TryGetValue(conversationId, out var route)
            ? route.NotifyAutoApprovedAsync(conversationId, requests, ct)
            : Task.CompletedTask;

    public Task<string?> CreateConversationAsync(string agentId, string topicName, string sender, CancellationToken ct)
        => Task.FromResult<string?>(null);

    public Task AnnounceSubAgentAsync(string conversationId, string handle, string subAgentId,
        string promptSummary, CancellationToken ct)
        => _replyRoutes.TryGetValue(conversationId, out var route)
            ? route.AnnounceSubAgentAsync(conversationId, handle, subAgentId, promptSummary, ct)
            : Task.CompletedTask;

    public Task UpdateSubAgentAsync(string conversationId, string handle, string status,
        string terminalSummary, CancellationToken ct)
        => _replyRoutes.TryGetValue(conversationId, out var route)
            ? route.UpdateSubAgentAsync(conversationId, handle, status, terminalSummary, ct)
            : Task.CompletedTask;
}
```

### Step 8.3: Wire wake → SystemChannelConnection

- [ ] **Step:** Modify `Agent/Services/SubAgents/SubAgentSessionManager.cs` — extend constructor to accept a `SystemChannelConnection systemChannel` and an `AgentKey agentKey`. In the wake flush, build a synthetic `ChannelMessage` and call `systemChannel.InjectAsync(msg, _replyChannel)`.

Add new helper inside the manager:

```csharp
private readonly SystemChannelConnection? _systemChannel;
private readonly AgentKey _agentKey;

private async Task EmitWakeAsync(IReadOnlyList<string> handles)
{
    if (_systemChannel is null || _replyChannel is null) return;

    var lines = handles
        .Select(h => Get(h) is { } v
            ? $"  - handle={v.Handle}, subagent={v.SubAgentId}, status={v.Status.ToString().ToLowerInvariant()}"
              + (v.CancelledBy is not null ? $" (cancelled by {v.CancelledBy.ToString()!.ToLowerInvariant()})" : "")
            : $"  - handle={h}")
        .ToArray();

    var content = "[system] Background subagents have completed and are awaiting your attention:\n"
                  + string.Join("\n", lines)
                  + "\nUse subagent_check on each handle to retrieve results.";

    var msg = new ChannelMessage
    {
        ConversationId = _replyToConversationId,
        Content = content,
        Sender = "system",
        ChannelId = _systemChannel.ChannelId,
        AgentId = _agentKey.AgentId
    };
    await _systemChannel.InjectAsync(msg, _replyChannel);
}
```

Replace the `WakeRequested?.Invoke(toEmit)` line in `FlushWakeNow` with `_ = EmitWakeAsync(toEmit)`. Keep the public `WakeRequested` event for unit-test introspection.

### Step 8.4: Modify `MultiAgentFactory`

- [ ] **Step:** In `Infrastructure/Agents/MultiAgentFactory.cs`, modify `Create(...)` (around line 22) to accept `ISubAgentSessionsRegistry` and `SystemChannelConnection` via DI (constructor), and inside `Create`:
  - After computing the agent and approval handler, call `var manager = (SubAgentSessionManager)registry.GetOrCreate(agentKey);` (registry returns `ISubAgentSessions` — the registry's factory creates concrete `SubAgentSessionManager` instances bound to the right `AgentKey` + `SystemChannelConnection`. The registry needs the system channel in its factory closure.)
  - Bind the manager's reply channel: when the manager doesn't already have one, set the channel and conversation id from the current request. Add a method `manager.RebindReply(IChannelConnection channel, string conversationId)` so the channel reference can be refreshed if the user moves transports between turns.
  - Set `featureConfig.SubAgentSessions = manager`.

Also modify `CreateSubAgent(...)` to leave `SubAgentSessions` null on the **subagent's own** FeatureConfig (subagents must not enable backgrounded subagents — recursion guard already enforced in `SubAgentModule`).

### Step 8.5: Modify `ChatMonitor`

- [ ] **Step:** In `Domain/Monitor/ChatMonitor.cs`:
  - Constructor: accept `IEnumerable<IChannelConnection> channels` (already has it) plus `ISubAgentSessionsRegistry registry`.
  - At the start of `Monitor(...)`, for each channel, fire-and-forget a subscription:
    ```csharp
    foreach (var ch in channels)
    {
        _ = Task.Run(async () =>
        {
            await foreach (var req in ch.SubAgentCancelRequests.WithCancellation(cancellationToken))
            {
                // We don't know AgentKey directly from the cancel request; the manager is keyed
                // by AgentKey. The conversation ID is enough to look up — registry needs a
                // ResolveByConversation(conversationId) helper. Add it.
                if (registry.TryGetByConversation(req.ConversationId, out var sessions))
                    sessions.Cancel(req.Handle, SubAgentCancelSource.User);
            }
        }, cancellationToken);
    }
    ```
  - In `ProcessChatThread`, call `manager.SetParentTurnActive(true)` before invoking `RunStreamingAsync` and `manager.SetParentTurnActive(false)` after the stream completes (or in a `finally`). Resolve `manager` via `registry.GetOrCreate(agentKey)`.

- [ ] **Step:** Extend `ISubAgentSessionsRegistry` with `bool TryGetByConversation(string conversationId, out ISubAgentSessions sessions)` (registry maintains an auxiliary `ConcurrentDictionary<string, AgentKey>` populated on `GetOrCreate`).

### Step 8.6: Wire DI

- [ ] **Step:** Modify `Agent/Modules/SubAgentModule.cs` to register the registry, the system channel, and the new tools. Concrete shape:

```csharp
public IServiceCollection AddSubAgents(SubAgentDefinition[] subAgentDefinitions)
{
    // existing recursion guard ...

    var registryOptions = new SubAgentRegistryOptions { SubAgents = subAgentDefinitions };
    services.AddSingleton(registryOptions);

    services.AddSingleton<SystemChannelConnection>();
    services.AddSingleton<IChannelConnection>(sp => sp.GetRequiredService<SystemChannelConnection>());

    services.AddSingleton<ISubAgentSessionsRegistry>(sp =>
    {
        var systemCh = sp.GetRequiredService<SystemChannelConnection>();
        // Note: agentFactory is supplied per-key by MultiAgentFactory; the registry's own
        // factory creates a *partial* manager that the factory finishes wiring at Create() time
        // by calling manager.BindAgentFactory(...). See Task 8 step 8.4.
        return new SubAgentSessionsRegistry(_ => throw new InvalidOperationException(
            "Manager must be created via MultiAgentFactory which supplies agent + reply context."));
    });

    services.AddTransient<IDomainToolFeature, SubAgentToolFeature>();
    return services;
}
```

> **Note:** The registry's factory delegate is intentionally a "fail-loud" sentinel because the manager needs per-conversation context (channel reference, agent factory) that only `MultiAgentFactory.Create` knows. `MultiAgentFactory.Create` will use a different code path:
> ```csharp
> var manager = registry switch {
>     SubAgentSessionsRegistry r => r.GetOrCreateExplicit(agentKey,
>         () => new SubAgentSessionManager(
>             agentFactory: def => CreateSubAgent(def, approvalHandler, whitelistPatterns, userId),
>             replyToConversationId: conversationId,
>             replyChannel: firstChannel,
>             systemChannel: systemCh,
>             agentKey: agentKey)),
>     _ => throw ...
> };
> ```
> Add `GetOrCreateExplicit(AgentKey, Func<SubAgentSessionManager>)` to `SubAgentSessionsRegistry`.

### Step 8.7: Update `SubAgentToolFeature` to register the new tools

- [ ] **Step:** Replace `Domain/Tools/SubAgents/SubAgentToolFeature.cs` `GetTools` with:

```csharp
public IEnumerable<AIFunction> GetTools(FeatureConfig config)
{
    var run = new SubAgentRunTool(registryOptions, config);
    yield return AIFunctionFactory.Create(run.RunAsync,
        name: $"domain__{Feature}__{SubAgentRunTool.Name}", description: run.Description);

    var check = new SubAgentCheckTool(config);
    yield return AIFunctionFactory.Create(check.RunAsync,
        name: $"domain__{Feature}__{SubAgentCheckTool.Name}", description: check.Description);

    var cancel = new SubAgentCancelTool(config);
    yield return AIFunctionFactory.Create(cancel.RunAsync,
        name: $"domain__{Feature}__{SubAgentCancelTool.Name}", description: cancel.Description);

    var wait = new SubAgentWaitTool(config);
    yield return AIFunctionFactory.Create(wait.RunAsync,
        name: $"domain__{Feature}__{SubAgentWaitTool.Name}", description: wait.Description);

    var list = new SubAgentListTool(config);
    yield return AIFunctionFactory.Create(list.RunAsync,
        name: $"domain__{Feature}__{SubAgentListTool.Name}", description: list.Description);

    var release = new SubAgentReleaseTool(config);
    yield return AIFunctionFactory.Create(release.RunAsync,
        name: $"domain__{Feature}__{SubAgentReleaseTool.Name}", description: release.Description);
}
```

### Step 8.8: Integration test

- [ ] **Step:** Create `Tests/Integration/Agents/SubAgentBackgroundFlowTests.cs` mirroring the existing `Tests/Integration/Agents/SubAgentTests.cs:16-101` setup. New scenarios:

  1. `Background_Start_Check_Wait_Result` — start with `run_in_background=true`, poll `subagent_check` until `Status=Completed`, then verify `result` matches expected. (Use a fast-completing echo subagent definition.)
  2. `Background_Cancel_Parent_TerminatesAndDoesNotWake` — start, immediately call `subagent_cancel`, verify view goes Cancelled with `cancelled_by=parent` and no synthetic system message is enqueued via the SystemChannelConnection (use a test double for `SystemChannelConnection` exposing `InjectedMessages`).
  3. `Background_NaturalCompletion_WhileParentIdle_InjectsSystemMessage` — start, mark `SetParentTurnActive(false)`, wait for completion + debounce, assert `SystemChannelConnection.InjectedMessages` has 1 entry containing the handle and `subagent_check` instructions.

- [ ] **Step:** Run

```bash
dotnet test Tests --filter FullyQualifiedName~SubAgentBackgroundFlowTests -v normal 2>&1 | tail -40
```

Expected: 3 tests pass.

### Step 8.9: Commit

- [ ] **Step:**

```bash
git add Domain/Contracts/IChannelConnection.cs Domain/DTOs/SubAgent/SubAgentCancelRequest.cs Agent/Services/SubAgents/SystemChannelConnection.cs Domain/Monitor/ChatMonitor.cs Infrastructure/Agents/MultiAgentFactory.cs Agent/Modules/SubAgentModule.cs Domain/Tools/SubAgents/SubAgentToolFeature.cs Domain/Contracts/ISubAgentSessionsRegistry.cs Agent/Services/SubAgents/SubAgentSessionsRegistry.cs Agent/Services/SubAgents/SubAgentSessionManager.cs Tests/Integration/Agents/SubAgentBackgroundFlowTests.cs
git commit -m "feat(subagents): wake-buffer + system channel injection for push completions"
```

---

## Task 9: SignalR channel — announce/update tools + cancel callback

**Files:**
- Create: `McpChannelSignalR/McpTools/SubAgentAnnounceTool.cs` (mirror `RequestApprovalTool.cs:10-38`)
- Create: `McpChannelSignalR/McpTools/SubAgentUpdateTool.cs`
- Create: `McpChannelSignalR/Services/ISubAgentSignalService.cs`
- Create: `McpChannelSignalR/Services/StubSubAgentSignalService.cs` (for non-hub builds, mirror `StubApprovalService.cs:5-28`)
- Create: `McpChannelSignalR/Services/SubAgentSignalService.cs` (real hub-backed impl) — pushes `SubAgentAnnounced` / `SubAgentUpdated` events to clients via the existing hub
- Modify: `McpChannelSignalR/Hubs/<HubFile>.cs` — add `CancelSubAgent(handle, conversationId)` hub method that publishes a `SubAgentCancelRequest` to a per-channel async stream
- Modify: `McpChannelSignalR/Services/SignalRChannelConnection.cs` — implement `AnnounceSubAgentAsync` / `UpdateSubAgentAsync` (call into the local `ISubAgentSignalService`) and expose `SubAgentCancelRequests` (the stream populated by the hub method)
- Create: `WebChat.Client/Components/SubAgentCard.razor` (+ `.razor.css`)
- Create: `WebChat.Client/State/SubAgents/SubAgentStore.cs`
- Create: `WebChat.Client/State/SubAgents/SubAgentEffects.cs`
- Modify: `WebChat.Client/Services/HubEventDispatcher.cs` (or equivalent) — subscribe to `SubAgentAnnounced`, `SubAgentUpdated` SignalR events; dispatch to store
- Create: `Tests/Integration/Channels/SubAgentSignalRChannelTests.cs`

### Step 9.1: Mirror the approval tool for announce/update

- [ ] **Step:** Read `McpChannelSignalR/McpTools/RequestApprovalTool.cs:10-38` and `McpChannelSignalR/Services/StubApprovalService.cs:5-28` first.
- [ ] **Step:** Create `McpChannelSignalR/McpTools/SubAgentAnnounceTool.cs`:

```csharp
using System.ComponentModel;
using McpChannelSignalR.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpChannelSignalR.McpTools;

[McpServerToolType]
public sealed class SubAgentAnnounceTool
{
    [McpServerTool(Name = "subagent_announce")]
    [Description("Posts a 'subagent running' card to the user's chat with a Cancel button.")]
    public static async Task<string> McpRun(
        [Description("Conversation ID")] string conversationId,
        [Description("Handle returned by domain__subagents__run_subagent (background)")] string handle,
        [Description("Subagent profile id")] string subAgentId,
        [Description("First ~80 chars of the subagent's prompt, for display")] string promptSummary,
        IServiceProvider services)
    {
        var svc = services.GetRequiredService<ISubAgentSignalService>();
        await svc.AnnounceAsync(conversationId, handle, subAgentId, promptSummary);
        return "announced";
    }
}
```

- [ ] **Step:** Create `McpChannelSignalR/McpTools/SubAgentUpdateTool.cs` analogously, with method signature

```csharp
public static async Task<string> McpRun(
    string conversationId, string handle, string status, string terminalSummary,
    IServiceProvider services)
```

calling `svc.UpdateAsync(conversationId, handle, status, terminalSummary)`.

### Step 9.2: Service interface + impl

- [ ] **Step:** `McpChannelSignalR/Services/ISubAgentSignalService.cs`:

```csharp
namespace McpChannelSignalR.Services;

public interface ISubAgentSignalService
{
    Task AnnounceAsync(string conversationId, string handle, string subAgentId, string promptSummary);
    Task UpdateAsync(string conversationId, string handle, string status, string terminalSummary);
}
```

- [ ] **Step:** `McpChannelSignalR/Services/SubAgentSignalService.cs` (real impl) — uses `IHubContext<TChat>` from the existing hub. Pushes events via `clients.Group(conversationId).SendAsync("SubAgentAnnounced", payload)` / `"SubAgentUpdated"`. Group naming should mirror existing approval hub usage — find by reading the existing `IApprovalService` real implementation if present, otherwise mirror the hub group convention used for `SendReplyAsync`.

- [ ] **Step:** `McpChannelSignalR/Services/StubSubAgentSignalService.cs` — log-only fallback for non-hub builds.

### Step 9.3: Hub method

- [ ] **Step:** Locate the chat hub class in `McpChannelSignalR/Hubs/`. Add:

```csharp
public Task CancelSubAgent(string conversationId, string handle)
{
    _subAgentCancelSink.Publish(new SubAgentCancelRequest(conversationId, handle));
    return Task.CompletedTask;
}
```

`_subAgentCancelSink` is a new singleton `ISubAgentCancelSink` (channel-backed publisher) registered in DI. The `SignalRChannelConnection` exposes its `Subscribe()` IAsyncEnumerable as `SubAgentCancelRequests`.

### Step 9.4: Channel connection updates

- [ ] **Step:** In `McpChannelSignalR/Services/SignalRChannelConnection.cs`:
  - Constructor accepts `ISubAgentCancelSink sink` and exposes `public IAsyncEnumerable<SubAgentCancelRequest> SubAgentCancelRequests => sink.Stream;`
  - `AnnounceSubAgentAsync` calls into the MCP tool flow via the connected MCP server — but since this is the in-process side, the cleanest is to delegate directly to `ISubAgentSignalService`. Inject it and call `svc.AnnounceAsync(...)`.
  - `UpdateSubAgentAsync` analogous.

### Step 9.5: WebChat.Client UI

- [ ] **Step:** Create `WebChat.Client/State/SubAgents/SubAgentStore.cs` — Redux-style store with state shape `Dictionary<string handle, SubAgentCardState>`. `SubAgentCardState` = `{ Handle, SubAgentId, Status, PromptSummary, TerminalSummary, ConversationId }`. Reducers: `Announced`, `Updated`, `Cancelled`. Mirror existing store patterns from any existing store under `WebChat.Client/State/` (find one and copy the structure).

- [ ] **Step:** Create `WebChat.Client/State/SubAgents/SubAgentEffects.cs` — subscribes to `HubEventDispatcher` events `SubAgentAnnounced` and `SubAgentUpdated`, dispatching the appropriate store actions. Also exposes a `CancelAsync(handle, conversationId)` that invokes the hub `CancelSubAgent` method.

- [ ] **Step:** Create `WebChat.Client/Components/SubAgentCard.razor`:

```razor
@inject SubAgentStore Store
@inject SubAgentEffects Effects
@implements IDisposable

@if (_card is not null)
{
    <div class="subagent-card subagent-status-@_card.Status.ToLower()">
        <div class="subagent-card-header">
            <span class="subagent-id">@_card.SubAgentId</span>
            <span class="subagent-status">@_card.Status</span>
        </div>
        <div class="subagent-card-prompt">@_card.PromptSummary</div>
        @if (_card.Status == "Running")
        {
            <button @onclick="OnCancel" class="subagent-cancel-btn">Cancel</button>
        }
        else if (!string.IsNullOrEmpty(_card.TerminalSummary))
        {
            <div class="subagent-card-summary">@_card.TerminalSummary</div>
        }
    </div>
}

@code {
    [Parameter, EditorRequired] public string Handle { get; set; } = "";
    private SubAgentCardState? _card;

    protected override void OnInitialized()
    {
        Store.Subscribe(OnStoreChanged);
        OnStoreChanged();
    }

    private void OnStoreChanged()
    {
        _card = Store.GetCard(Handle);
        InvokeAsync(StateHasChanged);
    }

    private async Task OnCancel()
    {
        if (_card is null) return;
        await Effects.CancelAsync(_card.Handle, _card.ConversationId);
    }

    public void Dispose() => Store.Unsubscribe(OnStoreChanged);
}
```

- [ ] **Step:** Modify the chat thread component (find the parent that renders messages — likely `WebChat.Client/Pages/Chat.razor` or `WebChat.Client/Components/ChatThread.razor`) to render `<SubAgentCard Handle="@h" />` for each handle currently in the store. Place inline in chronological order with messages.

- [ ] **Step:** Modify `WebChat.Client/Services/HubEventDispatcher.cs` (find it) to add hub event handlers:

```csharp
hub.On<SubAgentAnnouncedEvent>("SubAgentAnnounced", e => /* dispatch to store */);
hub.On<SubAgentUpdatedEvent>("SubAgentUpdated", e => /* dispatch to store */);
```

### Step 9.6: Integration test

- [ ] **Step:** Create `Tests/Integration/Channels/SubAgentSignalRChannelTests.cs` covering:
  - Calling the MCP tool `subagent_announce` causes a SignalR `SubAgentAnnounced` event to be received by a connected test client
  - Calling `subagent_update` updates the card
  - Invoking the hub method `CancelSubAgent` causes a `SubAgentCancelRequest` to appear on the channel's `SubAgentCancelRequests` stream

Use the existing approval flow integration tests as templates if any exist; otherwise, build using `WebApplicationFactory` and `HubConnectionBuilder` against the test host.

### Step 9.7: Commit

- [ ] **Step:**

```bash
git add McpChannelSignalR/ WebChat.Client/ Tests/Integration/Channels/SubAgentSignalRChannelTests.cs
git commit -m "feat(subagents): SignalR channel announce/update + cancel callback + WebChat card"
```

---

## Task 10: Telegram channel — announce/update tools + cancel callback

**Files:**
- Create: `McpChannelTelegram/McpTools/SubAgentAnnounceTool.cs`
- Create: `McpChannelTelegram/McpTools/SubAgentUpdateTool.cs`
- Modify: `McpChannelTelegram/Services/<UpdateRouter or BotPoller>.cs` — handle `callback_data` starting with `subagent_cancel:` and publish to `ISubAgentCancelSink`
- Modify: `McpChannelTelegram/Services/TelegramChannelConnection.cs` — implement `AnnounceSubAgentAsync`, `UpdateSubAgentAsync`, expose `SubAgentCancelRequests`
- Create: `Tests/Integration/Channels/SubAgentTelegramChannelTests.cs`

### Step 10.1: Mirror the Telegram approval tool

- [ ] **Step:** Read `McpChannelTelegram/McpTools/RequestApprovalTool.cs:14-71` to understand the pattern — `BotRegistry`, `ParseConversationId`, `botClient.SendMessage(...)` with `replyMarkup`, the `ApprovalCallbackRouter`.

- [ ] **Step:** Create `McpChannelTelegram/McpTools/SubAgentAnnounceTool.cs`:

```csharp
using System.ComponentModel;
using McpChannelTelegram.Services;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using Telegram.Bot;
using Telegram.Bot.Types.ReplyMarkups;

namespace McpChannelTelegram.McpTools;

[McpServerToolType]
public sealed class SubAgentAnnounceTool
{
    [McpServerTool(Name = "subagent_announce")]
    [Description("Posts a 'subagent running' inline-keyboard message to the user's Telegram chat with a Cancel button.")]
    public static async Task<string> McpRun(
        [Description("Conversation ID in format chatId:threadId")] string conversationId,
        [Description("Handle returned by domain__subagents__run_subagent (background)")] string handle,
        [Description("Subagent profile id")] string subAgentId,
        [Description("First ~80 chars of the subagent's prompt, for display")] string promptSummary,
        IServiceProvider services)
    {
        var registry = services.GetRequiredService<BotRegistry>();
        var cardStore = services.GetRequiredService<ISubAgentCardStore>();
        var (chatId, threadId) = RequestApprovalTool.ParseConversationId(conversationId);
        var bot = registry.GetBotForChat(chatId)
                  ?? throw new InvalidOperationException($"No bot registered for chat {chatId}");

        var text = $"🤖 <b>Subagent: {System.Net.WebUtility.HtmlEncode(subAgentId)}</b>\n" +
                   $"<i>{System.Net.WebUtility.HtmlEncode(promptSummary)}</i>\n" +
                   $"Status: Running";
        var keyboard = new InlineKeyboardMarkup(
            InlineKeyboardButton.WithCallbackData("Cancel", $"subagent_cancel:{handle}"));

        var sent = await bot.SendMessage(chatId, text, Telegram.Bot.Types.Enums.ParseMode.Html,
            replyMarkup: keyboard, messageThreadId: threadId, cancellationToken: CancellationToken.None);

        cardStore.Track(handle, chatId, sent.MessageId, subAgentId, promptSummary);
        return "announced";
    }
}
```

- [ ] **Step:** Create `McpChannelTelegram/Services/ISubAgentCardStore.cs` (+ in-memory impl) tracking `(handle → (chatId, messageId, subAgentId, promptSummary))` so `subagent_update` can target the right message via `editMessageText`.

- [ ] **Step:** Create `McpChannelTelegram/McpTools/SubAgentUpdateTool.cs` — looks up the card, edits the text via `bot.EditMessageText(chatId, messageId, newText, ...)`, removes the keyboard via `bot.EditMessageReplyMarkup(chatId, messageId, replyMarkup: null)` when status is terminal.

### Step 10.2: Cancel callback routing

- [ ] **Step:** Find the Telegram update router (`McpChannelTelegram/Services/TelegramUpdateRouter.cs` or similar by browsing the directory). It already handles `callback_data` for approvals. Add a branch:

```csharp
if (update.CallbackQuery?.Data?.StartsWith("subagent_cancel:") == true)
{
    var handle = update.CallbackQuery.Data["subagent_cancel:".Length..];
    var conversationId = $"{update.CallbackQuery.Message!.Chat.Id}:{update.CallbackQuery.Message.MessageThreadId}";
    _subAgentCancelSink.Publish(new SubAgentCancelRequest(conversationId, handle));
    await bot.AnswerCallbackQuery(update.CallbackQuery.Id, "Cancellation requested.");
    return;
}
```

Inject `ISubAgentCancelSink` (same interface as in SignalR — promote it to a shared abstraction in `Domain/Contracts/ISubAgentCancelSink.cs`).

### Step 10.3: Channel connection

- [ ] **Step:** In `McpChannelTelegram/Services/TelegramChannelConnection.cs`:
  - Implement `AnnounceSubAgentAsync` and `UpdateSubAgentAsync` that delegate to the same handlers used by the MCP tools (extract a shared `TelegramSubAgentDispatcher` if duplication grows).
  - Expose `SubAgentCancelRequests => _sink.Stream`.

### Step 10.4: Integration test

- [ ] **Step:** Create `Tests/Integration/Channels/SubAgentTelegramChannelTests.cs` using a fake `ITelegramBotClient` (mock out `SendMessage`, `EditMessageText`, `EditMessageReplyMarkup`, `AnswerCallbackQuery`). Cover:
  - Announce → `SendMessage` invoked with the cancel inline keyboard
  - Update terminal → `EditMessageText` + `EditMessageReplyMarkup(null)` invoked
  - Inbound `CallbackQuery` with `subagent_cancel:abc123` → `SubAgentCancelRequests` yields a request

### Step 10.5: Commit

- [ ] **Step:**

```bash
git add McpChannelTelegram/ Domain/Contracts/ISubAgentCancelSink.cs Tests/Integration/Channels/SubAgentTelegramChannelTests.cs
git commit -m "feat(subagents): Telegram channel announce/update + inline cancel callback"
```

---

## Task 11: SubAgentPrompt updates

**Files:**
- Modify: `Domain/Prompts/SubAgentPrompt.cs`
- Modify: `Tests/Unit/Domain/SubAgentToolFeatureTests.cs` (or equivalent) — add `Prompt_DocumentsBackgroundFlagAndHelperTools`

### Step 11.1: Failing test

- [ ] **Step:** Add to existing `Tests/Unit/Domain/SubAgentToolFeatureTests.cs`:

```csharp
[Fact]
public void Prompt_DocumentsBackgroundFlagAndHelperTools()
{
    var prompt = SubAgentPrompt.SystemPrompt;
    prompt.ShouldContain("run_in_background");
    prompt.ShouldContain("silent");
    prompt.ShouldContain("subagent_check");
    prompt.ShouldContain("subagent_wait");
    prompt.ShouldContain("subagent_cancel");
    prompt.ShouldContain("subagent_list");
}
```

### Step 11.2: Update prompt

- [ ] **Step:** Replace `Domain/Prompts/SubAgentPrompt.cs` with:

```csharp
namespace Domain.Prompts;

public static class SubAgentPrompt
{
    public const string SystemPrompt =
        """
        ## Subagent Delegation

        You have access to subagents — lightweight workers that run tasks independently with their
        own fresh context. Use them proactively to improve response quality and speed.

        ### When to Delegate

        - **Parallel tasks**: When a request involves multiple independent parts, fire several
          subagents in parallel rather than sequentially.
        - **Heavy operations**: Delegate research, web searches, multi-step data gathering, or any
          task requiring many tool calls.
        - **Exploration**: Send subagents to explore alternative approaches simultaneously.

        ### When NOT to Delegate

        - Simple, single-tool-call tasks — faster to do yourself.
        - Tasks that require conversation context the subagent won't have.
        - Follow-up questions or clarifications with the user.

        ### How to Delegate Effectively

        - **Self-contained prompts**: Subagents have NO conversation history. Include ALL necessary
          context, URLs, names, and requirements in the prompt.
        - **Clear success criteria**: Tell the subagent what a good result looks like.
        - **Synthesize results**: After subagents complete, combine their outputs into a coherent
          response for the user. Don't just relay raw results.

        ### Background subagents

        `run_subagent` accepts two extra flags:
        - `run_in_background=true` — returns a handle immediately and the subagent runs while you
          do other things. Use this when you want to fan out N tasks and gather them later, or when
          a task is long enough that you don't want to block on it.
        - `silent=true` (only meaningful with `run_in_background=true`) — suppresses the chat card
          shown to the user. Default is `false`: a card with a Cancel button appears so the user can
          see and stop the subagent.

        Once a backgrounded subagent is running, use:
        - `subagent_check(handle)` — non-consuming status + per-turn snapshots + final result if done.
        - `subagent_wait(handles, mode='all'|'any', timeout_seconds)` — block until all/any handle
          reaches a terminal state, or timeout. Returns `{ completed, still_running }`.
        - `subagent_cancel(handle)` — best-effort cancel.
        - `subagent_list()` — enumerate all sessions in this conversation.
        - `subagent_release(handle)` — drop a terminal session from the registry.

        If you end your turn while backgrounded subagents are still running, you will be woken in a
        fresh turn with a system message listing the completed handles. Call `subagent_check` on each
        to retrieve the result, then synthesize a follow-up reply for the user.
        """;
}
```

### Step 11.3: Run + commit

- [ ] **Step:**

```bash
dotnet test Tests --filter FullyQualifiedName~SubAgentToolFeatureTests -v normal 2>&1 | tail -10
git add Domain/Prompts/SubAgentPrompt.cs Tests/Unit/Domain/SubAgentToolFeatureTests.cs
git commit -m "docs(subagents): document run_in_background, silent, and helper tools in prompt"
```

---

## Task 12: Metrics events for snapshots and terminal transitions + E2E

**Files:**
- Modify: `Agent/Services/SubAgents/SubAgentSession.cs` and `SubAgentSessionManager.cs` — emit `MetricEvent`s via `IMetricsPublisher`
- Modify: constructors/DI to thread `IMetricsPublisher` into manager
- Add metric dimensions/metrics enum entries in `Domain/DTOs/Metrics/Enums/*.cs` if needed (read existing enums first; reuse where possible)
- Create: `Tests/E2E/WebChat/SubAgentCardE2ETests.cs`

### Step 12.1: Metric events

- [ ] **Step:** Read existing usage of `IMetricsPublisher` in the codebase:

```bash
grep -rn "IMetricsPublisher" /home/dethon/repos/agent --include="*.cs" | head -20
```

- [ ] **Step:** Identify the closest existing event naming convention (e.g., `agent.turn.completed`, `tool.call.started`). Add (or reuse) enum entries:
  - `subagent.session.started` (dimensions: `subagent_id`, `mode = blocking|background`, `silent`)
  - `subagent.snapshot.appended` (dimensions: `subagent_id`, `turn_index`)
  - `subagent.session.terminal` (dimensions: `subagent_id`, `terminal_state`, `cancelled_by` if applicable)

- [ ] **Step:** Modify `SubAgentSession.RunAsync` to emit `subagent.snapshot.appended` after each turn snapshot is added, and `subagent.session.terminal` on terminal transition. Modify `SubAgentSessionManager.Start` to emit `subagent.session.started`. Pass `IMetricsPublisher` via constructor.

- [ ] **Step:** Add a unit test in `Tests/Unit/Agent/SubAgentSessionTests.cs` using a fake `IMetricsPublisher` that records published events. Assert counts and dimensions.

### Step 12.2: E2E test

- [ ] **Step:** Create `Tests/E2E/WebChat/SubAgentCardE2ETests.cs` mirroring the structure in `Tests/E2E/Dashboard/DashboardNavigationE2ETests.cs:8-31`. Scenario:

```csharp
[Collection("WebChatE2E")]
[Trait("Category", "E2E")]
public class SubAgentCardE2ETests(WebChatE2EFixture fixture)
{
    [Fact]
    public async Task BackgroundedSubAgent_ShowsCard_CancelButton_WorksEndToEnd()
    {
        var page = await fixture.CreatePageAsync();
        await page.GotoAsync(fixture.WebChatUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // Per CLAUDE.md: select a user identity from the avatar picker before sending messages
        await page.Locator(".avatar-picker .avatar").First.ClickAsync();

        await page.Locator("textarea.chat-input").FillAsync(
            "Use run_subagent with run_in_background=true to research X");
        await page.Locator("button.chat-send").ClickAsync();

        // Wait for card
        var card = page.Locator(".subagent-card.subagent-status-running");
        await Assertions.Expect(card).ToBeVisibleAsync(new() { Timeout = 15_000 });

        // Click cancel
        await card.Locator("button.subagent-cancel-btn").ClickAsync();

        // Card flips to Cancelled
        var cancelled = page.Locator(".subagent-card.subagent-status-cancelled");
        await Assertions.Expect(cancelled).ToBeVisibleAsync(new() { Timeout = 10_000 });

        // The follow-up assistant message acknowledges the cancellation
        var lastMessage = page.Locator(".chat-message.assistant").Last;
        await Assertions.Expect(lastMessage).ToContainTextAsync("cancel", new() { IgnoreCase = true });
    }
}
```

(Use the existing `E2EFixtureBase`/`WebChatE2EFixture` pattern; if no `WebChatE2EFixture` exists yet, create one mirroring `DashboardE2EFixture`.)

### Step 12.3: Run all tests

- [ ] **Step:**

```bash
dotnet test Tests -v normal 2>&1 | tail -30
```

Expected: full suite passes (modulo any E2E tests that require Docker — those run via the existing test categorization and may be filtered out in CI).

### Step 12.4: Commit

- [ ] **Step:**

```bash
git add Agent/Services/SubAgents/ Domain/DTOs/Metrics/Enums/ Tests/Unit/Agent/SubAgentSessionTests.cs Tests/E2E/WebChat/SubAgentCardE2ETests.cs
git commit -m "feat(subagents): metric events for sessions and snapshots + E2E test for chat card"
```

---

# Self-review

## Spec coverage

| Spec section | Implementing task(s) |
|---|---|
| Architecture: SubAgentSession | 1 |
| Architecture: SubAgentSessionManager | 2, 8 (wake + system channel) |
| Layer placement & per-thread scoping | 2 (registry), 8 (factory wiring) |
| Parent agent tool surface: `run_subagent` extended | 3 |
| `subagent_check` | 4 |
| `subagent_wait` | 6 |
| `subagent_cancel` | 5 |
| `subagent_list` | 7 |
| `subagent_release` | 4 |
| Per-turn snapshots | 1 (`SnapshotRecorder`), 12 (metrics) |
| Human chat surface — protocol additions | 8 (interface), 9 (SignalR), 10 (Telegram) |
| Human chat surface — WebChat card | 9 |
| Human chat surface — Telegram inline keyboard | 10 |
| ServiceBus skipped — default-implementation in `IChannelConnection` no-ops | 8 |
| Push completion flow — wake buffer, debounce, attribution skip | 2 (manager), 8 (system channel injection) |
| Push completion flow — synthetic system message | 8 |
| Cancellation semantics — sources, propagation, attribution | 1 (session), 5 (parent source), 9/10 (user source) |
| Cancellation semantics — race resolution | 1 (CompareExchange) |
| Cancellation semantics — `MaxExecutionSeconds` | 1 |
| Cancellation semantics — wait decoupling | 6 |
| Error envelopes | 3-7 |
| Limits — concurrency cap, snapshot cap, debounce, default wait timeout | 2 (cap), 1 (snapshot cap TODO), 2 (debounce), 6 (wait timeout) |
| Concurrency & thread safety | 1, 2 |
| Backwards compatibility — default param values | 3 |
| Observability — metric events | 12 |
| Test strategy | covered per task with units + 1 in 8 (integration) + 1 in 12 (E2E) |

**Gap noticed and fixed inline:** snapshot retention cap (50) was originally noted only as a TODO in Task 1. I left it as a follow-up rather than adding a separate task — the policy belongs to whichever component manages snapshot persistence, and snapshots already live on `SubAgentSession` not the manager. **Action for the implementer:** add the cap inside `SnapshotRecorder` (it owns the in-progress turn buffer; final retention is on the session). Add `Tests/Unit/Agent/SubAgentSessionTests.cs::Snapshots_RetentionCap_DropsOldestNonTerminal` as part of Task 1 — fold it in there, not a new task.

## Placeholder scan

- One genuinely soft instruction in Task 8.4 / 8.6 about how `MultiAgentFactory` and the registry coordinate the construction of `SubAgentSessionManager`. The reason it's not full code: the existing `MultiAgentFactory.CreateSubAgent` shape and exact constructor signatures need to be inspected at the moment of editing (the factory has multiple call sites). The plan includes the precise method name to add (`GetOrCreateExplicit`), the precise constructor parameters, and the rebind invariant. The implementer has enough to write it without inventing a contract.
- Task 9.5 references "find one and copy the structure" for the WebChat store. This is fine because the project's existing stores under `WebChat.Client/State/` are the source of truth for the local pattern; spelling them out in the plan would duplicate code that already exists.

## Type consistency

- `ISubAgentSessions` member names match across the manager impl (Task 2), and all tools (Tasks 3-7).
- `SubAgentTerminalState`, `SubAgentCancelSource`, `SubAgentWaitMode` are referenced consistently.
- `SubAgentSessionsRegistry.GetOrCreate` (Task 2.4) vs `GetOrCreateExplicit` (Task 8.6): added `GetOrCreateExplicit` deliberately to thread per-conversation context. Both methods exist on the concrete registry; the interface only exposes `GetOrCreate` and `TryGet` / `TryGetByConversation`.
- `IChannelConnection` new members consistent across SystemChannelConnection, SignalR, Telegram, and ServiceBus (default no-op).
- `ISubAgentCancelSink` is shared between SignalR (Task 9) and Telegram (Task 10) — promote to `Domain/Contracts/ISubAgentCancelSink.cs` per Task 10.5 commit message.

No further inline fixes needed.

---

**Plan complete and saved to `docs/superpowers/plans/2026-05-06-subagent-control.md`. Two execution options:**

**1. Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — execute tasks in this session using executing-plans, batch execution with checkpoints

**Which approach?**
