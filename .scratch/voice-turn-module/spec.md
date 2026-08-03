# Spec — Concentrate the Voice Turn

Status: done

## Problem Statement

A voice turn is one simple idea: the user speaks, the agent answers, and the mic reopens once the whole answer has been heard. The state that tracks it is not simple. It lives as seven private fields on the per-connection session object, is read and written from the read loop, the playback loop and the MCP reply tool, and the rule that decides when a turn is over sits in a private method with a comment explaining it.

The rule cannot be enforced from where it lives. The session has to publish its raw segment counter so that the reply tool can answer three unrelated questions from it: has the turn produced any audio at all, is this the first segment (which selects a different minimum length), and is this the segment allowed to publish the time-to-first-audio metric. Publishing the counter also publishes the option of settling a turn some other way, so the settle rule is one caller away from being bypassed.

The epoch that protects against stale playback callbacks is passed by hand. A segment registers, gets a number back, and must hand that same number to its completion and failure callbacks. A comment marks the one line where reading the epoch separately instead would silently register a segment on one turn and release it against another. Nothing in the type system says so.

The turn-taking loop is worse. It takes fourteen injected members, eleven of them mandatory, each carrying an ordering contract in prose, and the whole thing is wired by an eighty-four line object initialiser inside the connection handler. Four of those members are one idea — the microphone capture: open it, close it and read its stats at exactly that moment, tell the satellite the user stopped talking, tell the satellite the mic is live again.

Two of those members reach the same live capture from different sides, and that is where a user-visible bug lives. The gate that decides when the user has stopped speaking is built twice with different rules. The wake and follow-up capture caps its noise floor with the quietest recent reading from the room; the approval capture does not. So a satellite that asks the user to confirm something endpoints them against a different noise floor than the wake turn it was answering seconds earlier, in the same room, with the same background. An inflated floor is not cosmetic: it arms the adaptive regime whose peak-drop backstop reads normal syllable dynamics as background and cuts the user off mid-sentence. The approval prompt is exactly where being cut off costs the most.

The three files holding this are the three highest-churn files in the repository. The state itself is careful work — every hazard comment records a bug that actually happened. The problem is placement.

## Solution

Give the turn a module. One object owns every field the turn resets, and its interface offers no way to reach the settle rule from outside. The reply tool stops asking a counter three questions and asks three named things instead: whether the next segment is the first, whether this segment token is the first, and — in one call — that the agent's stream has ended. The module decides silent-versus-spoken internally, because it is the only thing that knows both halves of the rule.

The epoch stops being a number the caller carries. Beginning a segment returns a token that closes over its own epoch and knows whether it is the turn's first. Completing or failing a segment is a method on that token. Registering on one turn and releasing against another stops being a comment and becomes unrepresentable.

Give the capture a module too. Opening, closing, and the two satellite indicator events become one object, and the turn-taking loop drops from fourteen injected members to about ten, five of them mandatory. The eighty-four line initialiser shrinks to wiring two modules and the handful of callbacks that genuinely belong to the loop.

Gate construction moves out of both call sites into a single per-satellite factory that owns the room-noise memory and the per-satellite resolution. After the move the two capture sites build an identical gate, so the approval capture inherits the room-noise floor and the two can no longer drift apart — there is nothing left at either call site to drift.

## User Stories

1. As a voice user, I want to be asked to confirm something under the same noise floor as the question I just asked, so that the confirmation mic behaves like the mic I already spoke into.
2. As a voice user, I want an approval answer not to be cut off mid-sentence in a room with background noise, so that saying "sí, la de las tres" works as well as saying "sí".
3. As a voice user in a noisy room, I want every capture on my satellite to use the calibration the operator set for that room, so that one part of a conversation is not tuned and another untuned.
4. As a voice user, I want the mic to reopen only once the entire answer has been spoken, so that a three-sentence reply is not interrupted by the follow-up chime after its first sentence.
5. As a voice user, I want a reply whose segments all failed to synthesize to end the conversation rather than hold the mic open, so that a broken answer costs me a re-wake and not a wedged satellite.
6. As a voice user, I want a reply where only some segments played to still give me the follow-up window, so that half an answer is still an answer.
7. As a voice user, I want an empty answer from the agent to end my turn promptly, so that I am not left waiting on a reply timeout for something that was never coming.
8. As a voice user, I want a slow playback callback from my previous turn not to disturb my current one, so that speaking again quickly does not wedge the mic.
9. As a voice user, I want the follow-up chime never to preempt a sentence that is still playing, so that the earcon does not eat the end of the answer.
10. As a voice user, I want the first sentence of a reply to be spoken as soon as it is short but complete, so that the answer starts quickly.
11. As a voice user, I want later sentences held to the longer minimum, so that the rest of the answer is not chopped into fragments.
12. As an operator, I want the time-to-first-audio metric published once per turn by the first segment, so that a three-sentence answer does not report three samples of a metric that means "how long until the user heard anything".
13. As an operator, I want the agent round-trip stamp consumed exactly once per turn, so that a schedule firing into a live session never reports an earlier turn's age as its own.
14. As an operator, I want a turn that produced no audio to release the round-trip stamp too, so that the stamp cannot outlive the turn that set it.
15. As an operator, I want per-satellite endpointing overrides honoured by every live capture, so that calibrating a satellite calibrates it everywhere it listens.
16. As an operator, I want room-noise readings to keep accumulating across a reconnect, so that a satellite that just dropped its TCP link is not also the least calibrated.
17. As a developer, I want the turn's fields unreachable from outside the module that owns them, so that a new call site cannot settle a turn by another route.
18. As a developer, I want a segment's completion tied to the turn it registered on by the type system, so that the epoch rule needs no comment to survive.
19. As a developer, I want the settle rule stated once in one place, so that changing it does not mean finding every caller that reasons about the counter.
20. As a developer, I want the turn's invariants covered by unit tests with no TCP, so that a race is a fast red test rather than a flake in a two-thousand-line integration file.
21. As a developer, I want the capture lifecycle behind one interface, so that opening a capture without recording its room sample is not something a new call site can do by omission.
22. As a developer, I want gate construction to exist in one place, so that a new capture site cannot invent a fourth resolution rule.
23. As a developer, I want the turn-taking loop's injected surface small enough to read at once, so that its ordering contracts are visible rather than distributed across a page of prose.
24. As a developer adding a new capture site, I want to ask a factory for a gate rather than assemble a tracker by hand, so that I cannot forget the room cap.
25. As a developer, I want the existing hazard comments carried to their new home, so that the bugs they record are not reintroduced.

## Implementation Decisions

### The turn module

One class owns the turn. Its interface, which was settled in the plan and is repeated here because it encodes the decisions:

```csharp
public sealed class VoiceTurn
{
    public SegmentToken BeginSegment();       // token carries epoch + IsFirst
    public void EndStream();                  // silent-vs-complete decided inside
    public Task<bool> AwaitSpoken();
    public void Reset();
    public bool NextSegmentIsFirst { get; }   // for the min-chars split decision
    public void MarkDispatched(long timestamp);
    public long? TryConsumeDispatchedAt();
    public bool TryClaimPreamble();
}

public readonly struct SegmentToken
{
    public bool IsFirst { get; }
    public void Complete();
    public void Fail();
}
```

Private to the module: the started, outstanding, stream-complete and audio-played counters, the turn epoch, the preamble claim, the dispatch stamp, the turn gate lock, and the settle rule.

`MarkDispatched` is on this list and was missing from the plan's version of the interface. The dispatch stamp is cleared by the turn reset, so it has to move with everything else the reset touches; the connection handler is its only producer today and will call it through the turn.

There are no public signal-spoken or signal-silent methods. The only production caller today is the reply tool's stream-complete branch, which becomes a single `EndStream()`. Tests that need a settled turn drive the real path: begin a segment, complete it, end the stream.

The module absorbs everything the turn reset touches. A partial reset left behind on the session would recreate exactly the split invariant this removes.

### Exposure

The turn hangs off the session as a property. The session does not forward methods to it; forwarding would be a pass-through layer with the same surface as before.

The two latency anchors the playback loop reads — turn start and speech end — stay on the session. They are stamped by the connection handler with the same time provider the playback loop reads back, and they are not part of the turn reset.

### The reply tool

Three reads of the public segment counter become three different things. The stream-complete branch becomes one call to `EndStream()`. The minimum-length choice reads `NextSegmentIsFirst`. The metrics-publishing decision reads the token's `IsFirst`. After this the counter is private.

### The gate factory

A DI-registered factory, one instance per process, holding the per-satellite room-noise memory that lives on the connection host today. The memory is keyed by satellite and deliberately outlives a connection; the factory is the right lifetime for it, and neither the per-connection session nor the per-conversation capture module is.

It exposes one build method taking the satellite. It resolves the per-satellite overrides against the global settings, applies the room-noise cap, and returns the gate. It also takes the room sample recorded at each capture close, which is how the memory keeps filling.

There is no gate-purpose parameter. The plan specified one because three sites diverged; with the segmenting decorator out of scope, the two remaining sites build an identical gate, and a parameter that only ever takes one value is a place for them to diverge again. If a real difference appears later, the parameter comes back with a test that names the difference.

The connection host and the approval tool both go through the factory. The approval tool already receives a service provider, so reaching it costs no restructuring.

This is a behaviour change on the approval site: its capture now inherits the room-noise floor. It is the only intended behaviour change in this work.

### The capture module

One class owns the capture lifecycle for a connection: open (returning the capture, since the turn-taking loop needs it), close (returning the frozen gate statistics and recording the room sample and the speech-end anchor at that instant), and the two satellite indicator writes for speech-stopped and listening-started. It asks the factory for its gates.

The close must freeze the statistics at the close. The endpointing tail is what anchors speech end and it must not be re-read later.

### The turn-taking loop

Its injected surface goes from fourteen members to about ten, and from eleven mandatory to five. The capture module replaces four members; the turn replaces two. The three metric side-effects and the two early-verification members stay: folding them would drag the metrics publisher into the loop, which is a separate piece of work.

The plan's estimate of roughly six members assumed the metric callbacks folded too. They do not, for that reason.

The eighty-four line initialiser in the connection handler is rewritten against the two modules.

### Sequencing

The turn extraction, the segment token and the reply-tool collapse form one landing group. Splitting them leaves the session holding a turn module and the old methods at the same time, which is the split invariant this work removes, temporarily reintroduced.

## Testing Decisions

A good test here names a behaviour a user or an operator can observe: the mic reopened, the answer was not interrupted, the stamp was consumed once, the approval capture used the room floor. It does not assert on which private field holds a count. The invariants below are worth pinning precisely because each of them currently rests on a comment.

**The turn's invariants.** The existing reply-segment unit file already tests exactly this state through the session, with no TCP, and covers most of what the new module must guarantee. Retarget it at the module and rename it accordingly. Four invariants must survive or arrive: a stream that ends with no segments started settles silent; a stream that ends with one segment drained and another outstanding does not settle; a callback carrying a previous turn's epoch is ignored; and a reset landing between a segment beginning and its completion does not drive the new turn's counter negative. Prior art is the file itself.

The playback unit file has two tests that settle a turn directly through the signal methods. They are testing the handshake, not playback, and move to the turn's file.

**The segment token and the reply tool.** The reply tool's own unit file is the seam. It is large and already drives the whole streaming path with fakes, including the preemption and synthesis-failure branches that reach the failure callback. The token change is visible there as the epoch parameters disappearing from the call sites; the behaviour it protects is already covered.

**Gate resolution.** A new unit file for the factory. It asserts that a gate built for a capture and a gate built for an approval resolve identically, including the room-noise cap, and that per-satellite overrides beat the globals. Prior art: the silence-gate, adaptive-tracker, room-noise-memory and satellite-config unit files all exist and drive these pieces directly.

**The approval behaviour change.** The approval tool's unit file asserts the capture is built through the factory. That file already builds a real service collection and calls the tool's entry point, so the factory is reachable by registration. Together with the factory test this pins the change at both ends: the factory applies the cap, and the tool goes through the factory.

The accepted trade: neither test drives real audio through an approval capture and observes the endpointing decision change. Doing that means the two-thousand-line host integration file, which another plan is also rewriting. The pair of tests pins the wiring and the resolution; it does not pin the acoustic outcome. That is knowingly accepted.

**The capture module and the loop.** The turn-taking loop's unit file is the seam. It fakes every injected member today, so narrowing the surface is visible there directly, and its existing coverage of the loop's exits — abandoned, no-speech, undispatched, reply timeout, max turns — is the regression net for the rewrite.

**The integration restructure.** The host and wake-arbitration integration files move onto the narrower surface. The three places that settle a turn by calling the signal method directly become the real path: begin a segment, complete it, end the stream. The room-noise coverage in the host file must keep passing unchanged through the memory's move to the factory; that is what says the move changed nothing.

## Out of Scope

The segmenting speech-to-text decorator's gate. It is built once at startup as a process-wide decorator, has no satellite session and no satellite configuration, uses a different settings group entirely — its own silence threshold, segment silence and minimum segment length, an unbounded maximum utterance, and no no-speech timeout — and reuses one gate across phrase segments with a deliberately un-reset tracker. It genuinely ignores every per-satellite calibration, and that stays true after this work. Feeding it a room-noise cap would be wrong: it splits phrases inside an already-captured utterance rather than deciding when a user stopped talking. Giving it per-satellite margin knobs would mean threading satellite configuration down through the speech-to-text interface, which is its own change. There is no follow-up ticket for it in this spec.

The session's control-writer delegate, a public mutable field bound and nulled around a connection whose teardown comment records a past bug. Adjacent, tempting, and not this work.

The approval tool's static entry point and service-locator shape. It gains one resolved service and keeps its shape.

Folding the loop's three metric callbacks into one observer.

The turn-start and speech-end latency anchors, which stay on the session.

## Further Notes

**Test-file collision.** The host and wake-arbitration integration files are also rewritten by the channel-server plan's ticket seven. The host file is 2,215 lines. Whichever lands second rebases onto the first. Do not run the two in parallel.

**The hazard comments are load-bearing.** Every comment in the extracted code explaining a race describes a bug that happened. They move with the code they describe. Deleting one is a regression, not a cleanup.

**Naming proximity.** A unit file already exists whose name begins with the same words as the new module but which tests turn latency decomposition, not turn state. Pick the new file's name so the two are told apart at a glance.

**Corrections to the plan's factual claims, verified against the code.** The turn-taking loop has fourteen injected members of which eleven are mandatory, not twelve. The reply tool's stream-complete branch is a little longer than the plan's line range suggests but is otherwise exactly as described. The gate divergence table is accurate for all three sites. The eighty-four line initialiser, the three-file churn ranking and the integration file's size are all as stated.
