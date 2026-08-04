# Spec — Voice and Channel Lifecycle

Status: ready-for-agent

Grilled 2026-08-04 from the "Noted, not carded" section of
`.scratch/architecture-audit-2026-08-03/candidates.md`, which holds the file and line
evidence for every claim below. Three of that section's seven items went in; a fourth
issue appeared during the grilling and was never a noted item. The four items that
stayed noted were re-verified and rewritten there with today's facts.

Vocabulary follows `CONTEXT.md`, which now carries the terms this spec pins down:
**capture**, **satellite identity**, **reply speaker**, **connection generation** and
**not connected**. Decisions are recorded in ADRs 0011, 0012 and 0013.

## Problem Statement

Three things in this system have a lifecycle that nothing states, and the cost lands on
whoever works on them next.

Someone changing how the hub listens to a satellite finds two different answers to
"who owns the microphone". A module was extracted to own it, and the rules say so, but
the field it was extracted from is still public on the session, so two callers reach past
the module. One of them re-implements, correctly and by hand, the rule that closing a
capture and telling the room-noise memory what it measured are a single act. The next
person to add a listening path has an even chance of copying the wrong one.

Someone changing how the agent talks to a channel finds a connection whose interfaces
describe none of its own lifecycle. Being not connected behaves five different ways with
no signature mentioning any of them, and the order the connection must be driven in
lives somewhere else entirely, in two near-identical retry loops. The same connection
asks the far end what tools it has before every call, and the delivery path makes that
call once per target per turn.

Someone changing how a spoken reply works finds 470 lines of policy reachable only
through a static entry point that resolves nine services from a container and threads
them through seven private methods, nine and ten parameters at a time. To assert that a
first spoken segment waits for enough characters, a test builds a nine-registration
service provider.

Underneath all three, every report the hub makes about a satellite names it by writing
the same three fields by hand, at twenty places.

## Solution

Each of the three gets an owner, and the smaller duplication underneath them gets one
place to live.

The microphone becomes a type that owns being open and closed, and knows nothing about
turns. The module that owns a turn sits on top of it. A caller that is opening a turn
holds the turn module; a caller that is only asking a question mid-turn holds the
microphone. Which type a caller holds is now the statement of what it is doing, and the
wake arbiter stops carrying an entire satellite session to reach six facts about one.

The channel connection runs itself. One call owns connect, register, watch, reconnect and
re-register, so the sequence lives in the thing the sequence is about. The five
not-connected behaviours are unchanged and written down, because at least one of them is
load-bearing. The far end's tool set is asked for once per connection generation instead
of once per call.

The reply policy becomes a reply speaker holding its collaborators, and the MCP tool
becomes the thin entry point the convention intends.

And one place stamps a satellite's identity onto anything the hub reports about it.

## User Stories

1. As a developer adding a new listening path to the voice hub, I want the microphone to
   be a type I hold, so that I cannot open a capture without also getting the rule about
   closing it.
2. As a developer adding a new listening path, I want opening a turn and opening a
   one-shot capture to be different types, so that I choose deliberately rather than by
   copying whichever neighbour I found first.
3. As a developer reading the approval prompt code, I want its difference from a wake
   turn to be visible in what it holds, so that I do not read the duplication as an
   oversight and "fix" it.
4. As a person answering a spoken confirmation, I want the approval microphone to keep
   paying back into the room-noise memory, so that a satellite I mostly use for
   confirmations still learns what its room sounds like.
5. As a person talking to a satellite, I want an approval prompt never to mark a turn
   start, so that the latency reported for the turn actually in flight stays true.
6. As a developer working on wake arbitration, I want the arbiter's handle to carry only
   what the arbiter reads, so that I can tell from the type what arbitration depends on.
7. As a developer changing the satellite session, I want to know that the arbiter does
   not depend on it wholesale, so that I can change it without reading the arbiter.
8. As a developer running the voice tests, I want the connection to expose its
   microphone, so that a test can still wait for a capture to open.
9. As a developer working on the agent's channel connections, I want one call that runs a
   connection for its lifetime, so that I do not have to read a background service to
   learn what order the connection must be driven in.
10. As a developer fixing a reconnect bug, I want one retry loop rather than two
    near-identical ones, so that a fix cannot land in one and miss the other.
11. As a developer calling a channel connection, I want each member to say what it does
    when there is no connection, so that I do not have to read the implementation to find
    out whether it throws or returns nothing.
12. As a developer tempted to unify the five not-connected behaviours, I want the reason
    they differ written down, so that I do not break the delivery path by tidying them.
13. As a person receiving a scheduled announcement, I want the agent not to pay a round
    trip per target per turn asking what tools the channel has, so that the announcement
    starts sooner.
14. As an operator redeploying a channel server with a new tool, I want the agent to see
    the new tool once it reconnects, so that a cached answer never outlives the process
    that gave it.
15. As a developer adding a new channel transport, I want the connection lifecycle to be
    something I get rather than something I write, so that a new transport stays "one new
    channel server".
16. As a developer changing how replies are spoken, I want the reply policy in a module
    with its collaborators as fields, so that I can read one method without following ten
    parameters.
17. As a developer writing a test for a reply rule, I want to construct the reply speaker
    directly, so that I do not build a nine-registration service provider to call a static
    method.
18. As a developer maintaining the MCP tools, I want the static-plus-service-provider
    entry point to stay, so that this tool still looks like every other tool in the repo.
19. As a person hearing a streamed reply, I want segment timing, prefetch and voice
    selection to behave exactly as they do today, so that this change is inaudible.
20. As a person whose satellite was offline when an answer was written, I want scheduled
    delivery to keep working, so that both reply paths survive the move.
21. As a developer reading a voice metric, I want every report about a satellite to name
    it the same way, so that a dashboard query cannot miss events that named only two of
    the three fields.
22. As a developer adding a new voice metric, I want to say which satellite it is about
    once, so that I cannot forget the room or the identity.
23. As a developer reading the dashboard, I want the emitted payloads to be unchanged, so
    that no existing chart or breakdown shifts.
24. As a developer picking up this work later, I want each ticket to name what it verified
    and what it corrected, so that I do not re-derive facts that already drifted once.
25. As a developer of any of the four issues, I want the tests to run without Docker where
    the subject is a unit, so that the loop stays fast.

## Implementation Decisions

**The identity stamp lands first.** The three-field triple is stamped by one extension in
the voice server, not in Domain: the voice metric DTO is a Domain type and must not learn
what a satellite session is. Sequencing it first shrinks the reply-speaker work and
settles what the narrowed arbitration handle carries — after the stamp, four separate
identity reads in the arbiter collapse to one value.

**The microphone and the turn are separate types (ADR 0013).** The microphone owns the
capture field, open, close, feed, force-end, abort, read-activity, and the pairing of
closing with recording gate statistics into the room-noise memory. The turn module keeps
the playback anchors, the wake announcement and its room-level payment, the wake and
follow-up openers, and the two indicator events. The satellite session ends up with no
capture surface at all, matching how it already exposes its turn and its playback queue
as owned sub-objects.

**The approval capture holds the microphone directly.** It is not a turn, and it must not
mark a turn start or a speech end. Today that is a comment; afterwards it is which type
the caller holds.

**The observation point moves rather than dying.** The "is a capture open" read keeps
having no production caller and moves onto the microphone. It is an observation point,
and it belongs on the thing being observed. The connection must expose its microphone so
the connection tests still have something to wait on.

**The arbitration handle carries what the arbiter reads and nothing else:** the satellite
identity, the RMS offset, whether the satellite supports pause, its capture activity, and
the verbs to abort, pause and re-arm. The noted entry's proposal of three members was
written from an incomplete read and is not followed.

**The channel connection gains one run call** owning connect with retry, register the
catalog, watch health, reconnect with retry, re-register. The host keeps reading the
endpoint map and starting one run per endpoint. The connect, reconnect, health and
register verbs stop being part of the interface a caller drives, because driving them in
order is what the run call now does.

**The five not-connected behaviours do not change (ADR 0011).** They are stated on the
interface. The create-conversation null is load-bearing: the delivery resolver reads it as
"this channel minted nothing", which is also what an attach-only channel and a channel
without the tool return, and the resolver's job is to try the next target.

**The tool set is cached per connection generation (ADR 0012)**, discarded on reconnect.
Every server in the repo registers its tools before its transport starts, so the answer
cannot change inside one generation. A process-lifetime cache and a time-based expiry were
both rejected.

**Domain's channel interface does not change.** Domain sees a channel it can send on. The
run call belongs on the infrastructure interface.

**The reply speaker holds the accumulator, the speech synthesiser, the voice settings, the
metrics publisher, the time provider and the logger as fields**, and offers two entry
points: the live utterance reply and the scheduled delivery. Both branches go in one
module because they share the accumulator; splitting by branch would put one collaborator
in two places. The MCP tool keeps its static-plus-service-provider signature, resolves the
speaker and the session, and picks the branch.

**Two deliberate details in the reply path survive the move**: the time provider is
resolved only on the live branch, and the send path's argument dictionary is built by hand
to keep reflection off a per-chunk hot path. Both carry comments today and keep them.

**Behaviour does not change anywhere in this spec.** Every issue is a structural move.

## Testing Decisions

A good test here asserts what a caller can observe: what was spoken, what was recorded,
what was emitted, what order things happened in, how many round trips were paid. None of
these issues should produce a test that names a private method or asserts that a
particular type was constructed. The seams below were chosen to make that possible, and
confirmed with the developer before writing.

**Prefer the seams that already exist.** Three of the four issues need no new seam.

- *The identity stamp* is asserted through the tests that already assert voice metric
  payloads — the transcript dispatcher, the announcement service, the insistent
  announcement controller, the wake arbiter, the reply tool and the satellite connection.
  If those payloads still assert the same three fields, the stamp is correct. No new test
  file.
- *The microphone* is asserted through its two callers, which is where the rules matter:
  the turn module's tests for turn semantics, and the approval tool's tests for the
  distinguishing rule — an approval capture pays back into the room-noise memory and marks
  no turn anchor. That test is the red one to start from, because it fails against today's
  shape.
- *The arbitration handle* is asserted where it is already constructed. The wake arbiter
  tests build a handle directly, so the narrowed handle is proven by that file compiling
  and passing.

**One new seam: the reply speaker's two entry points.** It replaces the current seam,
which is a service provider plus a static call. It is the highest point that can reach a
segment rule without a satellite socket — the connection tests and the voice end-to-end
tests sit above it and are too high to assert a first-segment character minimum. Prior art
for driving a voice module directly: the turn module, the playback queue and the capture
module all have unit tests that construct them with fakes and a fake time provider.

**One extended test helper: the in-memory MCP server.** The integration tests already boot
a real MCP server over loopback and hand back a client; exposing its endpoint lets the
real channel connection's run call be pointed at a real server. This covers the
connect-then-register ordering, the probe count across two calls and across a reconnect,
and the not-connected behaviours, with no test-only hook added to production code. That
last point is deliberate: the agent-spec work removed a factory delegate that existed only
so tests could cut through a seam, and this must not reintroduce the pattern.

The existing fake channel connection stays for what the host still owns — which endpoints
get a run — and shrinks as the retry loops leave it.

**Tests that should get simpler, and it is worth checking that they did.** The reply tests
stop building a nine-registration service provider. The approval tests construct a
microphone rather than a session with a running playback pump. If either still needs its
old scaffolding afterwards, the module boundary is in the wrong place.

## Out of Scope

**The four items that stayed noted.** The dashboard subscription helper, the alert-routing
prose fragment, the HTTP adapter boilerplate and the conversation-context scope remain in
`candidates.md` with corrected facts and no ticket.

**Unifying the five not-connected behaviours.** Deliberately rejected, ADR 0011.

**Making not-connected unrepresentable.** Considered and rejected in ADR 0011: the monitor
and the delivery resolver hold their connection for the process lifetime and would each
need a new way to say "nothing right now", which is the same question moved one layer up.

**Re-cutting the split between Domain's channel interface and infrastructure's.** Domain's
stays exactly as it is.

**The reply tool's disposal duty and the per-satellite voice fallback.** Both were claimed
by the playback-outcome candidate and have shipped; the voice fallback is now a method on
the satellite session.

**Any behaviour change.** If an issue cannot be done without one, stop and raise it.

## Further Notes

**These entries had aged, and the corrections are part of the work.** The noted items were
written before the twelve audit candidates shipped. At grilling time:

- The reply tool is 470 lines, not 518 — the playback-outcome candidate took the voice
  fallback and the disposal duty. Seven unconditional service lookups plus two conditional,
  not eight; seven private statics; four test files reach the tool, not three.
- The capture observation point has sixteen test call sites, not twelve.
- The noted fix for the arbitration handle named three members and missed six.
- The per-turn per-target create-conversation calls moved out of the chat monitor when the
  conversation-group candidate landed; they are in the delivery target resolver now.

**Sequencing.** Six tickets, two of which can start immediately.

- `01` identity stamp — no blockers
- `02` reply speaker — blocked by `01`
- `03` microphone type — blocked by `01`
- `04` arbitration handle — blocked by `03`, and `01` through it
- `05` tool set cached per connection generation — no blockers
- `06` connection runs itself — blocked by `05`

`02` and `03` touch different files — the reply tool reads the session's turn, playback,
voice resolution and config, never the capture surface — so once `01` lands they can run at
the same time. The `05` → `06` edge exists because both edit the same connection type and
`05` lands the test seam `06` drives.

**One documentation duty is inside a ticket, not this spec.** The voice rules file
describes the capture module's ownership, and that description will be wrong between the
start and the end of issue 03. Updating it is an acceptance item on that issue rather than
something done up front.
