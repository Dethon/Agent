# Voice Turn Module Implementation Plan

**Goal:** Turn lifecycle state is spread across `SatelliteSession`, `SendReplyTool` and `FollowUpConversation`, mutated from three threads. Concentrate it in one module whose interface makes the settle rule unreachable from outside.

**Why now:** `WyomingSatelliteHost` (58 commits), `SatelliteSession` (25) and `SendReplyTool` (24) are the three highest-churn files in the repo. The state itself is carefully guarded — `_turnGate`, epoch-carrying callbacks, comments explaining each hazard — the problem is placement, not quality. `ReplySegmentsStarted` is public solely so an MCP tool can answer three different questions from it, and the invariant "the turn is over only once the stream ended AND every started segment drained" lives in a private method plus a comment.

**Source:** architecture review 2026-08-02, candidates 1 and 7 (merged).

## Global Constraints

- TDD per task. `dotnet test Tests/Unit --nologo -v q`.
- `.cs` files have no trailing newline; pre-commit runs `dotnet format` and re-stages whole files.
- File-scoped namespaces, primary constructors, no XML doc comments.
- `.claude/rules/voice.md` governs the subsystem; read it before task 1.
- Commit after each task.
- **The existing hazard comments are load-bearing documentation of real past bugs.** Move them with the code they describe; do not drop them.

## Locked decisions

```csharp
namespace McpChannelVoice.Services;

public sealed class VoiceTurn
{
    public SegmentToken BeginSegment();       // token carries epoch + IsFirst
    public void EndStream();                  // silent-vs-complete decided inside
    public Task<bool> AwaitSpoken();
    public void Reset();
    public bool NextSegmentIsFirst { get; }   // for the min-chars split decision
    public bool TryClaimPreamble();
    public long? TryConsumeDispatchedAt();
    // private: _replySegmentsStarted, _replySegmentsOutstanding,
    //          _replyStreamComplete, _replyAudioPlayed, _turnEpoch,
    //          _preambleClaimed, _dispatchedAt, _turnGate, SettleIfComplete
}

public readonly struct SegmentToken
{
    public bool IsFirst { get; }
    public void Complete();   // was CompleteReplySegment(epoch)
    public void Fail();       // was FailReplySegment(epoch)
}
```

**`VoiceTurn` absorbs everything `ResetTurn` touches** — including `_preambleClaimed` and `_dispatchedAt`. A partial `ResetTurn` left on `SatelliteSession` would recreate exactly the split invariant this removes.

**Exposed as `session.Turn`, not forwarded.** Forwarding methods would be a pass-through layer.

**The epoch stops being passed by hand.** `BeginSegment` returns a token that closes over its own epoch, so the "read the epoch from registration, not separately" rule at `SendReplyTool.cs:291-292` becomes structural instead of a comment.

## Call-site collapse

`SendReplyTool.cs:119-125` becomes `session.Turn.EndStream()`:

```csharp
// before — the tool decides whether the turn is over
if (session.ReplySegmentsStarted == 0) { _ = session.TryConsumeDispatchedAt(); session.SignalTurnSilent(); }
else { session.MarkReplyStreamComplete(); }
```

The other two reads of `ReplySegmentsStarted` are different questions: `:257` picks `FirstSegmentMinChars` vs `MinChars` before a segment begins (`NextSegmentIsFirst`), and `:289` decides which segment publishes latency (`token.IsFirst`). After this, `ReplySegmentsStarted` leaves the public surface.

## CaptureSession (absorbs candidate 7)

`FollowUpConversation` has 14 injected members, 12 of them `required`, each carrying an ordering contract in prose, wired by an 84-line lambda in `WyomingSatelliteHost.BuildCoordinator`. The capture-side members fold into one module:

```csharp
public sealed class CaptureSession
{
    public Task OpenAsync(CancellationToken ct);
    public Task<CaptureResult> CloseAsync(CancellationToken ct);   // freezes gate stats at the close
    public Task SpeechStoppedAsync(); public Task ListeningStartedAsync();
    public SilenceGate BuildGate(GatePurpose purpose);             // was 3 divergent sites
}
```

`BuildGate` is candidate 7. The gate is constructed three times today with three different resolution rules:

| site | room-noise cap | per-satellite overrides | noSpeechTimeout |
|---|---|---|---|
| `WyomingSatelliteHost.cs:333-347` | yes | yes | `followUp.WindowMs` |
| `RequestApprovalTool.cs:175-186` | **no** | yes | `followUp.WindowMs` |
| `SegmentedSpeechToText.cs:45-53` | **no** | **no** (raw settings) | none |

So an approval capture endpoints under a different noise floor than the wake capture seconds before it on the same satellite, and the segmenting gate ignores every per-satellite calibration the operator set. `BuildGate` owns resolution and the room cap so the three sites can differ only where they mean to. **This is a behaviour change on two of the three sites** — give each its own failing test.

`FollowUpConversation` ends at roughly 6 members: `Turn`, `Capture`, `TranscribeAndDispatch`, chime, and the timeout knobs.

## Tasks

1. **`VoiceTurn`, extracted from `SatelliteSession`.** Move the seven fields, `_turnGate`, `SettleIfComplete` and their comments. Unit tests, no TCP: stream-ends-with-no-segments settles silent; stream ends with one drained and one outstanding does not settle; a stale callback from a previous epoch is ignored; `Reset` between `BeginSegment` and `Complete` does not drive the new turn's counter negative. These are the invariants that currently rely on comments and a 2,215-line integration file.
2. **`SegmentToken`.** Replace the four `epoch` parameters.
3. **`session.Turn` + `SendReplyTool` collapse.** `:119-125` to one call; `:257` and `:289` to `NextSegmentIsFirst` / `token.IsFirst`.
4. **`CaptureSession` with `BuildGate`.** Failing test per divergent site first: approval capture inherits the room-noise floor; the segmenting gate honours per-satellite overrides.
5. **Rewrite `BuildCoordinator`** against the two modules; `FollowUpConversation` down to ~6 members.
6. **Restructure the integration tests** onto the narrower surface.

## Sequencing

Task 6 touches `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` and `WakeArbitrationHostTests.cs`, the same files as the channel-server plan's task 7. Land one, rebase the other. Do not run both in parallel.

## Risks

- **`SatelliteSession.ControlWriter` is a public mutable delegate** bound at `WyomingSatelliteHost.cs:143` and nulled at `:306`, inside a `finally` whose comment records a past bug where a throw in setup left a session registered with a writer over a disposed client. This plan does not fix that; it is adjacent and will be tempting to touch. Resist, or make it a separate task with its own test.
- **`RequestApprovalTool.McpRun` is `static` with an `IServiceProvider`** and constructs its own gate inline, so task 4 cannot vary its endpointing from a test without also addressing the service-locator shape.
- Task 1 moves concurrency-sensitive code. Every existing comment explaining a race describes a bug that actually happened; treat deleting one as a regression.
