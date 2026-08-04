# Spec — Satellite Connection Module

Status: done

Grilled from candidate 3 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the exact file and line evidence for every claim below. No ADR: every
decision here is cheap to reverse, so all three of the ADR tests fail. Vocabulary
follows the "Voice satellite" section of `CONTEXT.md` — **satellite connection**,
**satellite session**, **wake announcement**, **wake turn**, **follow-up turn**.

## Problem Statement

A developer changing anything about how the hub talks to a voice satellite has to
work inside a single 174-line method that does six unrelated jobs at once: dial the
satellite, hand the session a control writer, register with two registries and the
wake arbiter, launch two background tasks, decode and route five Wyoming frame
types, and unwind all of it in a precisely ordered `finally`.

The ordering that makes that method correct exists only as prose. Nothing in any
signature says that on a wake the hub must record the room level, then stash the
wake metadata, then claim with the arbiter, then open the turn, then drop whatever
was left stashed — in that order. Nothing says that on teardown the arbiter
registration must be released before anything that can await, because until it is,
a satellite whose TCP link just died is still a candidate to win arbitration
against a live one and silently suppress a real wake. Both rules are comments. A
future edit that reorders them compiles, passes review and breaks a user's wake
word.

The wake metadata makes this worse by travelling through a stash rather than as an
argument. The read loop writes it onto the satellite session; a callback two files
away reads it back single-use when the microphone opens. Because the two ends are
not connected by anything a compiler or a reader can follow, a third rule had to be
invented to cover the case where nobody reads it — the read loop drops the stash
itself, guarded by a nine-line comment explaining that a stale value would
misattribute one turn's loudness to the next and skew the gate calibration that
consumes it.

The cost lands hardest on tests. The only way into any of this is `StartAsync` over
a TCP socket, so every test stands up a real `TcpListener` and hand-writes a fake
satellite that speaks the Wyoming framing. That is roughly 130 lines of plumbing
before a single assertion. The result is a 2,233-line integration file holding 15
test methods for behaviour that is almost entirely about routing frames and
ordering calls — no sockets required. Wanting to assert something new about the
wake path means writing another fake satellite first, so in practice people do not.

## Solution

The connection becomes a thing rather than a method. `SatelliteConnection` owns one
run of the link to a satellite: it registers, launches the playback and conversation
tasks, routes the frames, and unwinds in order. `WyomingSatelliteHost` keeps what is
genuinely its own — discovering which satellites have an address, dialling them, and
reconnecting forever.

The wake announcement stops being stashed and starts being passed. The satellite's
report of its own wake — how loud, how confident, what triggered it, how loud the
room was — travels as an argument from the frame that carried it down to the
microphone opening on the strength of it. The stash, both of its consumers and the
drop-the-leftovers rule all disappear, because a value passed as an argument cannot
be left behind for the next turn to misread.

The teardown rule becomes structure. Unwinding splits into a synchronous phase that
cannot await and an asynchronous phase that drains, so "the arbiter goes first,
before anything unbounded" is expressed by which method the call is in.

For a developer, the payoff is that the behaviour becomes reachable without a
socket. A test constructs the host with fakes, asks it for a connection, and pushes
Wyoming events into it through a channel while recording what comes back out. 14 of
the 15 socket-backed tests become unit tests with their assertions unchanged. One
stays, over a real socket, to prove that dialling and framing still work.

## User Stories

1. As a household member, I want a wake word to open a turn with the loudness the
   satellite reported for that wake, so that the gate calibration that reads it is
   fed the right number.
2. As a household member, I want a wake that opens no turn to leave nothing behind,
   so that my next wake is not reported with a previous one's loudness.
3. As a household member speaking to a satellite whose link has just died, I want a
   different satellite in the same room to win the wake, so that a dead device does
   not silently swallow my command.
4. As a household member, I want the room level the satellite measured just before
   my wake to still cap the endpointing floor, so that I am not cut off mid-sentence
   in a noisy room.
5. As a household member on an older satellite that announces with audio-start and
   sends no wake metadata, I want my turn to open exactly as it does today, so that
   the device keeps working.
6. As a household member on an older satellite, I want the absence of a room reading
   to be treated as "unknown" rather than as "silent", so that the floor is not
   pinned at silence.
7. As a household member, I want a follow-up turn never to be reported as if it had
   its own wake, so that follow-up telemetry does not pollute wake calibration.
8. As a household member, I want a reply that is still playing when the link drops
   to stop cleanly, so that the satellite is usable again as soon as it reconnects.
9. As a household member, I want an alarm ringing on a satellite to be acknowledged
   when I wake that satellite, so that saying the wake word dismisses it.
10. As a household member, I want a satellite that dropped mid-utterance to
    reconnect and behave as a fresh connection, so that I do not have to power-cycle
    it.
11. As a household member, I want an unknown voice to be rejected before speech
    recognition runs, so that a television does not command my house.
12. As a household member, I want an unknown voice caught at the early mark to close
    the microphone immediately, so that the satellite re-arms instead of holding the
    mic open to background noise.
13. As a household member, I want my own voice never to be truncated by that early
    check, so that a short command still reaches the agent whole.
14. As a household member, I want the telemetry backbone being down to cost me
    nothing, so that a Redis outage does not wedge my satellite.
15. As a household member, I want a follow-up turn to open without a wake word after
    the agent replies, so that a conversation flows.
16. As a household member, I want a follow-up window that hears nothing to re-arm the
    satellite, so that the microphone does not stay open indefinitely.
17. As a household member, I want a wake followed by silence to re-arm quickly rather
    than waiting out the maximum utterance length, so that a false trigger costs me
    seconds, not a minute.
18. As a developer, I want one type to own a satellite connection's whole lifetime,
    so that I can read what happens on connect and on teardown in one place.
19. As a developer, I want the host to be about dialling and reconnecting only, so
    that its size reflects one job.
20. As a developer, I want the wake announcement to arrive as an argument, so that I
    can follow it from the frame that carried it to the code that uses it.
21. As a developer, I want the wake stash deleted rather than moved, so that there is
    no leftover value for a future turn to misread.
22. As a developer, I want the drop-the-unconsumed-stash rule to stop existing, so
    that there is one less invariant to preserve by hand.
23. As a developer, I want opening a wake turn and opening a follow-up turn to be two
    named operations, so that a boolean argument cannot be passed the wrong way
    round.
24. As a developer, I want the satellite's reported room level recorded where the
    gate is built, so that "record before the gate reads it back" is not an ordering
    rule I have to remember.
25. As a developer, I want the arbiter released in a synchronous phase that cannot
    await, so that the ordering rule protecting a live satellite is carried by
    structure rather than by a comment.
26. As a developer, I want the drain phase to skip what was never started, so that a
    connection that failed partway through setup still unwinds cleanly.
27. As a developer, I want a test proving the arbiter is already released while
    playback is still draining, so that the rule has a regression test rather than a
    comment.
28. As a developer, I want to drive a connection by pushing Wyoming events into it,
    so that I can test frame routing without a socket.
29. As a developer, I want to assert on the events a connection wrote back, so that I
    can test what the satellite would have received.
30. As a developer, I want the host to hand me a fully wired connection, so that my
    test exercises the real transcription, verification and telemetry code rather
    than my own stand-ins for it.
31. As a developer, I want the four tests that assert on real metric publishes to
    keep asserting on real metric publishes, so that the telemetry code does not lose
    its coverage in the move.
32. As a developer, I want one end-to-end test over a real socket to survive, so that
    dialling, framing and the hosted service stay proved together.
33. As a developer, I want the multi-satellite arbitration tests left alone, so that
    cross-connection behaviour keeps its end-to-end proof.
34. As a developer, I want the arbitration tests to compile and pass unchanged after
    the extraction, so that I have a cheap check that the refactor changed nothing.
35. As a developer, I want the wake announcement parser to live with the other
    Wyoming wire types, so that I look for peer-supplied parsing in one place.
36. As a developer, I want that parser to keep surviving absent, null and wrong-typed
    values, so that a misbehaving peer cannot tear down a connection mid-utterance.
37. As a developer, I want the dismissed-alert formatting to live next to the stash it
    feeds, so that it is not copied across the new seam.
38. As a developer, I want the satellite connection and the satellite session to be
    distinguishable by name, so that I know which one to reach for.
39. As a developer, I want the vocabulary written down, so that "session" and
    "connection" do not drift back into meaning the same thing.
40. As a developer, I want the wake stash removal to land as its own change, so that
    the integration suite proves the behaviour held before anything moves.
41. As a developer, I want the module to land already covered by ported tests, so that
    coverage never dips and a reviewer can line each ported test up against the
    original it replaces.
42. As a developer working on metrics publishing, I want the voice telemetry spans
    reachable without a hosted service, so that the voice half of the metrics work is
    unblocked.

## Implementation Decisions

### The satellite connection module

A new `SatelliteConnection` in the voice channel's services owns one run of the link
to a satellite. It is the thing that runs: nothing outside it holds a reference, and
a drop ends it for good. It takes the process-wide collaborators — the session
registry, the wake arbiter, the voice settings, the time provider and a logger — as
constructor arguments, and its per-connection collaborators as required init
properties, matching the idiom `FollowUpConversation` already uses one level down.
No factory type: the gate factory exists because gates have several call sites, and
this has one.

`WyomingSatelliteHost` keeps starting and stopping, discovering dialable
satellites, parsing addresses, the reconnect loop, and the turn helpers —
transcription and dispatch, the early speaker check, the listening chime, and every
metric publish. It stays an `IHostedService` and its registration is unchanged.

### The wire seam

The write side is a delegate supplied at construction, not a client object. It has
to be available before the read loop starts, because the conversation coordinator's
end-of-turn write and the arbiter's re-arm handle both close over it.

The read side is an `IAsyncEnumerable` of Wyoming events passed to the run
operation. The host keeps ownership of the client and disposes it when the run
returns, so dialling and disposal stay together. No new interface is introduced over
the client: the Wyoming client is already a thin duplex, and this file already
passes the wire around as delegates everywhere else.

The run operation still throws when the connection drops, so the host's existing
reconnect loop catches and retries exactly as it does today.

### Assembly

The host exposes an internal operation that takes a satellite id, its configuration
and a writer delegate, and returns a fully wired connection — building the satellite
session, the capture session, the follow-up conversation and the connection itself,
with the real transcription, verification and telemetry helpers bound. This is the
existing coordinator-building method grown one level. The per-connection method
becomes: dial, create, run.

The voice channel project already exposes its internals to the test project, so
tests reach this operation directly.

### The wake announcement travels as an argument

The wake announcement type and its defensive parser move to the Wyoming protocol
folder, alongside the other wire types, as a type with a static read operation. Its
tolerance is unchanged and load-bearing: every field is optional because it comes
from a schema-less peer, and an exception on the read loop tears down a connection
mid-utterance.

The wake path collapses from five ordered steps to two. The connection claims with
the arbiter, then announces the wake to the conversation coordinator, passing the
announcement. The coordinator passes it to the capture session, which opens the
turn.

The capture session's single open operation splits into two named ones — opening a
**wake turn**, which takes the announcement, and opening a **follow-up turn**, which
takes nothing and never will. This is not a widening: the existing boolean was
already fully determined by which of the two call sites made the call.

Recording the satellite's reported room level moves into the wake-turn opening,
immediately before the gate is built, beside the capture-close recording that
already lives in the same type. This makes "record before the gate reads the memory
back" a fact about one method rather than an ordering rule spread across two files.
It also means the room-noise memory is paid into and read from exactly one place.

The satellite session loses its wake stash entirely — both the note and the
single-use consume operation. The drop-the-unconsumed-stash step disappears with
them, because the announce operation's early return when a turn is already open
discards its argument by itself.

The legacy path, where a satellite announces a turn with an audio-start frame and no
wake metadata, announces the wake with no announcement. It records a zero room
level, which the room-noise memory already discards as an absent measurement rather
than a silent room, so its behaviour is unchanged.

### The unwind

The run operation keeps one `try`/`finally`. The finally calls a synchronous
operation that releases the arbiter registration, then awaits a second operation
that drains everything else — dispose the coordinator, complete the playback
channel, await both background tasks swallowing their faults, clear the session's
control writer, unregister the session.

The split is the point. The first phase cannot await because it is not asynchronous,
so the rule that a dropped connection stops being an arbitration candidate before
anything unbounded runs is enforced by which method the call sits in. The teardown
order is deliberately not the reverse of construction, which is why an unwind stack
would be wrong here and is not used.

The three fields holding the coordinator and the two background tasks stay nullable
and stay null-checked in the drain. Setup can genuinely throw partway — this was a
real defect once, where a throw during setup left a session registered with a writer
closing over a disposed client — and the null checks read as "drain only what was
started".

### Small relocations

The audio-start payload builder, the playback frame writer, the audio format reader
and the chunk conversion move onto the connection with the playback wiring they
serve. The subsystem architecture rules reference the audio-start builder by its old
owner and need updating.

The dismissed-alert helper has call sites on both sides of the new seam. Rather than
copying it, its formatting folds into a new operation on the satellite session that
takes the dismissed alerts and the current time, placing it next to the stash it
feeds.

### Vocabulary

Five terms were written into the project glossary during grilling, under a new
"Voice satellite" section: **satellite connection** (one run of the link, the thing
that runs), **satellite session** (the satellite as something the rest of the hub can
address by id), **wake announcement**, **wake turn** and **follow-up turn**. Use
them in code, comments and tickets. In particular, do not use "satellite session"
for the connection: they have the same lifetime but opposite directions of use, and
conflating them is what made a 174-line method look like one thing.

### Sequencing

Four tickets, in `issues/`.

Two prefactors run in parallel and block nothing but each other's absence. One moves
the dismissed-alert formatting onto the satellite session, because it has call sites
that end up on opposite sides of the new seam. The other deletes the wake stash on
its own: the announcement type moves, the capture session gains its two named
openings, the coordinator's wake announcement takes the announcement, the satellite
session loses the stash. Through both, the host stays one file and its integration
suite stays green, which is the proof that behaviour held.

The extraction then lands with the four wake and routing tests ported and the new
unwind test written, so the module is covered the moment it exists. A final ticket
ports the remaining ten. The port is split because reading a 2,233-line suite and
rewriting fourteen methods does not fit one context window comfortably, and the
worst place to stop is halfway through a port.

## Testing Decisions

A good test here asserts what a satellite would have observed: which events came
back over the wire, which metrics were published, whether the microphone re-armed.
It does not assert that a particular method was called in a particular order. The
current suite is already behavioural in that sense; what it pays for that is a fake
satellite per test. The move keeps the assertions and drops the socket.

### Seams

One new, everything else existing.

The new seam is the connection itself, reached through the host's internal assembly
operation. A test builds the host with fakes, asks for a connection, and runs it
against an unbounded channel of Wyoming events while recording what the writer
delegate receives. This is the highest seam that removes the listener: one level
below the hosted service, directly above the socket.

Everything beneath stays real — the satellite session, both registries, the wake
arbiter, the follow-up conversation, the capture session, the silence gate and its
factory, the transcript dispatcher. The existing fakes are reused unchanged: the
recording metrics publisher, the stub speech-to-text, the speaker verifier doubles
that drive accept, reject and skip by peak sample, the conversation factory mock and
the channel inbox probe.

Because the connection is assembled by the host rather than by the test, the four
tests that assert on real metric publishes keep exercising the real publishing code.
That is what makes this seam, rather than a delegate-per-helper seam, the right one —
and it is what unblocks the voice half of the metrics-publishing work.

### What gets tested

The first change is covered by the existing follow-up conversation unit tests,
updated for the two named openings, plus the existing wake-announcement parser tests
under their new name. The integration suite is not touched and must stay green — it
is the evidence that removing the stash changed nothing.

The extraction and the ticket following it move 14 of the 15 socket-backed test
methods to a new unit suite for the connection, assertions verbatim. The extraction
takes the four wake and routing tests so the module is never uncovered; the last
ticket takes the other ten. They cover: a command running straight on
from the wake word using the room level
the satellite measured; wake metadata attributed to the turn that opened it in both
frame orders; the dispatch stamp taken before the dispatch; a conclusive speaker
emitted as the sender; an unknown speaker rejected before recognition with its
metric; the early mark keeping the microphone open when no speech has landed yet;
the early mark rejecting a continuous unknown voice; both telemetry-down paths still
rejecting and re-arming; a follow-up turn dispatched without a second wake; a
follow-up silence re-arming with a closing transcript; a wake followed by silence
re-arming without waiting out the maximum utterance; and alert acknowledgement both
with and without an utterance.

One new test comes with the unwind split: with the playback task still draining, the
arbiter registration is already released.

One test method stays in the integration suite, over a real socket, proving dial,
framing, a full turn and the hosted service still work together.

The multi-satellite arbitration integration tests are not touched. They are short,
already behind a shared fake-satellite fixture, and they are the only end-to-end
proof that two real connections arbitrate correctly. That they compile and pass
unchanged is the cheapest available check that the extraction did not change what
starting the hosted service does.

### Prior art

The existing satellite host integration tests are the model for the ported suite,
including their PCM helpers, their speaker verifier doubles and their
one-reply-segment helper; those move with them. The existing follow-up conversation
tests are the model for the capture-opening changes. The Wyoming reader, writer and
client already have unit tests over plain streams, which is why dropping the socket
from 14 tests costs no framing coverage.

Follow red-green-refactor throughout, per the project rules.

## Out of Scope

Candidate 4, playback having no outcome. The playback job's settle-a-completion-
source-from-three-of-five callbacks idiom is untouched, including in the listening
chime.

Candidate 1's metrics-publishing module. This spec makes the voice telemetry
reachable in a unit test; it does not change how any metric is published, does not
touch the publisher registration in the voice host, and does not fix the unwrapped
publisher defect that candidate records.

Splitting the turn helpers — transcription and dispatch, the early speaker check,
the chime, the metric publishes — into their own module. That was considered and
rejected as a second extraction inside one candidate. It remains available later,
and this spec's assembly operation is where it would be cut.

Renaming the satellite session or its registry. The names stay; the glossary carries
the distinction instead.

Any change to the Wyoming protocol, to what any frame carries, to the reconnect
delay, to the arbitration window or rules, to the silence gate, to speaker
verification, or to what any voice metric contains.

The dashboard, the satellite firmware, and both sides of the wire format.

## Further Notes

The candidate document proposed folding the five wake steps into one operation on
the new module, leaving the stash in place as an internal invariant. Grilling
rejected that: the stash's two consumers both sit inside this work's blast radius,
nothing else in the codebase touches them, and the follow-up-turn boolean that
guards one of them is fully determined by its call site. Passing the announcement
deletes the stash, both consumers, the drop rule and the boolean together, which is
strictly more than relocating the order.

The candidate also proposed keeping two real-socket tests, for framing and for
reconnect. Reconnect has no test in that file today, so that would be new coverage
rather than a migration, and framing is already covered by the Wyoming reader and
writer unit tests. One end-to-end test is kept instead.

The candidate did not mention the multi-satellite arbitration integration tests,
which also drive the host over sockets and sit in the blast radius. They are
deliberately left alone; see the testing section.
