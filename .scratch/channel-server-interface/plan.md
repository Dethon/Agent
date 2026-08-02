# Channel Server Interface Implementation Plan

**Goal:** Make "being a channel server" a type instead of an unwritten checklist. One registration call replaces six hand-copied sets of receive tool, tool filter, emitter and drop policy.

**Why now:** The same stale-subscriber bug was fixed in three rounds across six servers (`6ae26bbc`, `13f7a658`, `faf95266`). All six now compute liveness identically, but by convention — nothing stops a seventh from computing it a seventh way. The 18-line doc comment at `ChannelProtocol.cs:31-46` is a warning label standing in for a missing module.

**Source:** architecture review 2026-08-02, candidate 2.

## Global Constraints

- TDD (Red-Green-Refactor) per task: write the failing test, watch it fail, implement.
- `dotnet test Tests/Unit --nologo -v q`, filtering with `--filter FullyQualifiedName~<TestClass>` while iterating.
- `.cs` files have **no trailing newline**; the pre-commit hook runs `dotnet format` and re-stages whole files.
- File-scoped namespaces, primary constructors, `record` DTOs, no XML doc comments (`.claude/rules/dotnet-style.md`).
- Domain never imports Infrastructure/Agent.
- Commit after each task.
- **Behaviour on all six servers must be identical before and after.** Any observable change is a bug in this plan, not a decision.

## Locked decisions

New project `Channels.Hosting`, referencing **Domain + `ModelContextProtocol.AspNetCore` 2.0.0 only**. Not Infrastructure: `McpChannelTelegram` and `McpChannelServiceBus` reference Domain alone today and must keep doing so, or they inherit Playwright, Redis, Terminal.Gui and the agent stack.

```csharp
namespace Channels.Hosting;

public enum DeliveryPolicy
{
    Broadcast,    // enqueue always; idle-but-unpruned subscribers still receive
    BufferAlways, // EnqueueFor(subscriberId); creates the queue on demand
    GateOnLive    // enqueue only when live; false means nothing was buffered
}

public static class ChannelServerExtensions
{
    public static IMcpServerBuilder AddChannelServer(
        this IMcpServerBuilder builder, DeliveryPolicy policy, string? subscriberId = null);
}

public sealed class ChannelNotificationEmitter(ChannelInbox inbox, DeliveryPolicy policy, string? subscriberId)
{
    public Task<bool> EmitAsync(ChannelMessageNotification payload, CancellationToken ct = default);
    public Task<bool> EmitCancelAsync(ChannelCancelNotification payload, CancellationToken ct = default);
}
```

`EmitAsync` returns whether a live subscriber was present. The liveness check is **inside** the operation, so it cannot be skipped or recomputed. `HasActiveSessions` disappears from every public surface.

`AddChannelServer` extends `IMcpServerBuilder`, not `IServiceCollection` — the receive tool and call-tool filter must join the MCP builder chain; the inbox and emitter go on `.Services` from there.

`subscriberId` is required only for `BufferAlways` (Telegram's `ChannelProtocol.ChannelClientNamePrefix + "telegram"`); validate at registration.

## Return-value semantics per policy

| policy | enqueues when no live subscriber | returns |
|---|---|---|
| `Broadcast` | yes | liveness |
| `BufferAlways` | yes, targeted | liveness |
| `GateOnLive` | **no** | liveness |

These three preserve today's behaviour exactly. `Broadcast` and `GateOnLive` differ only in the no-live-subscriber case, which is precisely the difference between the transports and the dual-role servers that was previously encoded by which enqueue call someone copied.

## Assignment

| server | policy | today |
|---|---|---|
| SignalR, Voice, ServiceBus | `Broadcast` | `inbox.Enqueue` |
| Telegram | `BufferAlways` | `inbox.EnqueueFor` |
| Library, Scheduling | `GateOnLive` | liveness check then `Enqueue` |

## Tasks

1. **`Channels.Hosting` project + `DeliveryPolicy`.** Tests pin all three policies against a real `ChannelInbox`: broadcast reaches an idle-but-unpruned subscriber, gate-on-live does not enqueue when nobody is live, buffer-always creates the queue on demand. These are the tests the three bug rounds never had.
2. **Shared `McpChannelReceiveTool`.** One `[McpServerToolType]` deriving from `Domain.Tools.Channels.ChannelReceiveTool`. Registration is always explicit `WithTools<T>()` — verified, no assembly scanning anywhere in the solution — so a type in another assembly registers fine. Delete the six copies.
3. **Shared call-tool filter.** The `OperationCanceledException` rethrow plus log-and-`IsError` block, currently duplicated verbatim in six `ConfigModule`s.
4. **Shared sealed emitter.** Payload in, `bool` out, gate inside.
5. **Migrate the six servers**, one commit each, verifying no behaviour change. Callers build `ChannelMessageNotification` with named properties — this is where SignalR's `ConfigPatch` and Voice's `Location`/`SatelliteId`/`DismissedAlert` land without widening any interface.
6. **Delete `IScheduleNotificationEmitter` and `IDownloadNotificationEmitter`.** One adapter each, no test doubles, and their consumers (`ScheduleDispatcherService`, `DownloadCompletionWatcher`) sit in the same project. Note: this is *not* in tension with ADR-0001, which covers `Domain/Contracts`; these two live in their server projects and have no Domain consumer.
7. **Restructure the `CapturingEmitter` sites.** ~14 across `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` (2,215 lines) and `WakeArbitrationHostTests.cs`. Tests construct a real `ChannelInbox` and drain it rather than subclassing the emitter. The four per-server `ChannelNotificationEmitterTests` files stay, narrowed to each transport's own payload shape — their duplicated `HasActiveSessions_FollowsInboxSubscribers` sections move to the policy seam. See `.scratch/channel-server-interface/spec.md` for the three-seam split.

## Sequencing

Task 7 touches the same integration tests as the voice-turn plan (`.scratch/voice-turn-module/plan.md`). Do whichever lands first, and rebase the other onto it — do not run both in parallel against those two files.

## Risks

- **`WakeArbitrationHostTests` uses a locked emitter variant** (`:266`) because two connections reach it concurrently. Draining a shared `ChannelInbox` from two connections must preserve that; `ChannelInbox` is already thread-safe, but the assertion helper needs to be.
- **Telegram's `subscriberId` is a well-known constant** that must match what `McpChannelConnection` derives for itself. Getting it wrong buffers into a queue nobody drains, silently. Pin it with a test that asserts the emitter's target equals the connection's derived id.
