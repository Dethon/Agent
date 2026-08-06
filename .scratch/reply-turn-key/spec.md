# A reply says which turn it answers

Status: ready-for-agent

## Problem Statement

Talking to a satellite goes wrong in three ways that look unrelated and are not.

The first is a satellite that stops answering. The user asks something, the agent starts
writing, and the agent's link to the voice server drops mid-answer. The satellite redials a
few seconds later and looks healthy — its LED returns to idle, it hears the wake word — but
the next turn never completes. The microphone stays shut for about two minutes, and only
then does the hub give up and re-arm. From the user's side the satellite simply ignored them.

The second is an answer spoken twice over. The agent takes longer than the reply timeout, the
hub gives up on that turn, and the user asks something else. The abandoned answer arrives
while the new one is being written, and the two are spoken as one: the tail of a question the
user has already moved on from, glued to the front of the answer they are waiting for.

The third is a timer or a scheduled message cutting a conversation short. An announcement
delivered into a satellite that is mid-conversation is treated as the answer to the turn in
flight. The turn settles on it, the follow-up window opens early, and the real answer arrives
after the hub has already stopped listening.

All three have the same cause. A reply says nothing about which turn it answers. The hub has
to infer it, and it infers it from the conversation — but a conversation outlives a turn, it
outlives a satellite connection, and it outlives the agent run that was writing into it. Every
one of the three symptoms is that inference being wrong in a different way.

## Solution

A turn is dispatched under a **turn key**, and every reply belonging to that turn carries it
back. The hub stops inferring and starts comparing.

When a reply arrives for a satellite that is mid-turn, there are four possibilities and the
key decides between them:

- The key matches the turn's. This is the answer the user is waiting for. It is spoken and it
  settles the turn, exactly as today.
- The key does not match and the reply belongs to an agent-initiated turn. This is a timer, an
  alarm or a scheduled message that happens to land while the user is talking. It is spoken,
  and it never touches the turn — the user keeps the follow-up window they were owed.
- The key does not match and the reply belongs to a user turn. This is an abandoned answer
  arriving late. It is discarded, and the discard is logged.
- There is no key at all. That can only happen if the echo itself is broken, so the reply is
  treated as the current turn's — the old behaviour — and an error is published so the
  breakage is visible as itself rather than as satellites that stopped answering.

Whatever text an abandoned turn left buffered is dropped when the next turn is dispatched, so
a stale sentence can no longer reach the front of a fresh answer.

The machinery the hub used to infer with — a per-conversation map of stream handles, the
handle type, its reference check and its epoch guard — has nothing left to answer and is
deleted.

## User Stories

1. As someone talking to a satellite, I want it to answer my next question after it has
   reconnected, so that a dropped link costs me one answer rather than the next two minutes.
2. As someone talking to a satellite, I want an answer the hub gave up on to stay unsaid, so
   that I do not hear the tail of a question I have already moved on from.
3. As someone talking to a satellite, I want a fresh answer to start at its own beginning, so
   that I can follow it without editing out a stale sentence in my head.
4. As someone talking to a satellite, I want a timer going off mid-conversation to interrupt
   me and nothing more, so that the answer I asked for still arrives.
5. As someone talking to a satellite, I want the follow-up window I was promised, so that an
   announcement landing at the wrong moment does not cost me the chance to say one more thing.
6. As someone talking to a satellite, I want it to keep listening for as long as the answer
   takes, so that a slow agent does not read as a broken device.
7. As someone whose satellite lost power mid-answer, I want the next thing I say to be heard,
   so that a power blip costs me nothing beyond that one answer.
8. As someone using a satellite in a room with several of them, I want a handoff mid-conversation
   to keep answering me on the satellite I moved to, so that the conversation follows me.
9. As the operator of this hub, I want a broken reply echo to show up as an error on the
   dashboard, so that I learn about it from a metric rather than from someone telling me the
   house stopped listening.
10. As the operator of this hub, I want a discarded late answer written to the log with the
    turn it belonged to, so that I can tell "the agent was slow" from "the agent never
    answered".
11. As the operator of this hub, I want a schedule firing into a live conversation to be
    visible as its own delivery, so that reply latency figures are not polluted by
    announcements the user never asked for.
12. As the agent, I want the turn I am answering to be part of what I send, so that my answer
    cannot be misfiled by a hub that has moved on.
13. As a developer of the voice channel, I want one place that decides which turn a reply
    answers, so that a new reply path cannot invent a fourth way of guessing.
14. As a developer of the voice channel, I want the reply speaker to hold no cross-turn state,
    so that reading it does not require knowing what a previous turn left behind.
15. As a developer of the voice channel, I want the reply-latency metrics gate to fall out of
    the same rule that decides whether a reply is this turn's, so that the two cannot disagree.
16. As a developer of any channel, I want `send_reply` to take the record that is already the
    wire shape, so that adding a field is one edit rather than five positional argument lists.
17. As a developer adding a new channel, I want the turn key to be present on every message
    without my channel doing anything, so that I can start reading it whenever I need it.
18. As a developer of a channel that does not care about turns, I want the new fields to be
    ignorable, so that this change costs my channel nothing but a parameter.
19. As a developer of the voice channel, I want the playback queue's public surface to contain
    only verbs production uses, so that reading it tells me how a connection actually ends.
20. As a developer reading the codebase later, I want the glossary to define what a turn key
    is, so that I do not have to reconstruct the rule from four guard clauses.
21. As a test author, I want the minting and the echo proved through the real monitor, so that
    a test passing means the key actually survives the round trip.
22. As a test author, I want each of the four reply cases to be one test against the reply
    speaker, so that a regression names the case it broke.
23. As a test author, I want every channel's `send_reply` pinned against the same contract, so
    that a channel that silently drops the key fails the build rather than production.
24. As a maintainer, I want this change to land as two commits, so that the domain change can
    be reverted without taking the voice fix with it.

## Implementation Decisions

**The turn key exists on the message, not on the reply alone.** `ChannelMessage` carries a
turn key. The channel that has one to mint mints it — voice mints it as it dispatches a
transcript, because voice is the side that needs to know the value in advance. When an inbound
message carries none, the conversation group mints one as it builds the turn, so everything
downstream of the group sees a key on every turn regardless of which channel the message came
from.

**The turn carries it, and every reply echoes it.** The turn object the monitor already builds
per message is where the key travels; every `SendReplyParams` produced for that turn carries
it, including the synthesized stream-complete event. That last part is the whole point — the
terminal event is exactly where today's message id goes null.

**The reply also says whether its turn was agent-initiated.** Derived from the message origin
the conversation group already tests when it announces a turn start. Without it, a schedule
fire and an abandoned answer are indistinguishable at the hub: both carry a key that does not
match the live turn's, and the two must be treated oppositely.

**`SendReplyAsync` takes the params record.** The reply-sending member of the channel
connection contract currently takes six positional parameters and each implementer rebuilds
the record by hand on the far side. It takes the record instead. This is what keeps the two
new fields from becoming positions seven and eight.

**All five channel servers' `send_reply` tools gain the two parameters, both nullable.** The
narrower alternative — voice only — would require the connection to know which channels accept
which parameters, which is the per-channel branching the shared channel protocol exists to
prevent. Telegram, ServiceBus, WebChat and Scheduling accept both and ignore both.

**The voice turn is stamped at dispatch.** The transcript dispatcher already marks the turn
with the dispatch timestamp; it stamps the turn key in the same act, and drops whatever reply
text the previous turn left buffered for that conversation at the same moment. Flushing at
dispatch rather than on a mismatched chunk means the buffer is cleared even when the abandoned
run never sends anything else.

**The four cases live in the reply speaker's live path, before anything is buffered or
queued.** Matching key: unchanged behaviour. Mismatched key with an agent-initiated origin:
spoken, no stream opened, no segment registered, no turn settled. Mismatched key with a user
origin: discarded and logged, nothing appended. Absent key: treated as the current turn's, and
an error event published.

**Three things delete.** The per-conversation map of stream handles, the stream handle type
with its reference check, and the epoch guard on stream ends. The segment token and its epoch
stay — playback callbacks genuinely outlive the turn that queued them, which is a different
question. The hand-written gate that currently suppresses turn-anchored latency metrics when
no dispatch stamp was consumed also deletes: a reply that is not this turn's no longer reaches
the metrics at all.

**The playback queue's graceful close becomes private.** Production ends a connection with the
link-drop close, the unplayed sweep and disposal, in that order, from one place. The graceful
"play what is queued, then stop" verb has no production caller and exists on the public surface
for tests alone.

**The glossary gains one term.** `Turn key`, beside `Turn` in the conversation section — what a
turn is dispatched under, so a reply can say which turn it answers. No ADR: the rule is small,
follows from the existing turn definition, and is reversible.

**Two changes, landed in order.** First the turn key on the wire, proved through the monitor
and the channel contract. Then the voice classification, the deletions and the queue cleanup on
top.

## Testing Decisions

A good test here asserts what a caller can observe: what reaches `send_reply`, what the
satellite is asked to play, and whether the turn settles. None of them should reach for the
stream map, the epoch counters or the buffered text — those are the things this change deletes,
and a test that names them would have to be rewritten by the very change it is meant to guard.

Three seams, all of which exist today. No new ones.

**The monitor's delivery identity tests** are the seam for minting and echo. They already drive
a real chat monitor with fake channel connections and assert that everything a turn produces
names the right conversation; the same shape asserts that every update of a turn carries one
key, that the stream-complete event carries it too, that two turns carry different ones, and
that a message arriving without a key leaves with one. Prior art: the scheduled-message test
that asserts the whole turn is built from the minted conversation id.

**The per-channel contract pin** is the seam for the wire. Every channel server's `send_reply`
accepts both new parameters and hands them back unchanged. Prior art: the channel receive
contract tests, which drive the channel-capable rows of the one server table and assert each
declared delivery policy; and the server contract tests, which drive every server's real config
module.

**The reply speaker's unit tests** are the seam for the four cases, one test each: matching key
settles the turn; mismatched agent-initiated key is spoken and leaves the turn outstanding;
mismatched user key is discarded and appends nothing; absent key settles the turn and publishes
an error. Prior art: the existing reply speaker tests, which drive a real satellite session,
turn and playback queue and assert on the turn's settled result and the synthesizer's calls.

The queue's graceful close going private rewrites its 25 test call sites: tests close by
cancelling the token they already hand to the queue's run loop, and the few that want the
link-drop semantics call the verb production calls.

## Out of Scope

- **Keying WebChat's topic stream on the turn key.** Its per-topic ownership was closed out
  three commits ago and has its own decision record; rewiring it on the back of this is a
  separate argument.
- **Any channel other than voice reading the key.** Telegram, ServiceBus, WebChat and
  Scheduling accept both fields and ignore them.
- **Collapsing the playback queue's closing verbs into one terminal verb with a reason.** The
  ordering rule they encode has exactly one production call site and one order; naming it as an
  enum buys nothing once the test-only verb is gone.
- **A separate type owning a satellite connection's generation.** The glossary already defines
  a satellite connection as one run from dial to finished unwind, and the connection type
  already owns it.
- **An end-of-generation notification with adapters.** The adoption path covers everything that
  is genuinely generation-scoped, and a drain-side hook would need the connection to acquire a
  collaborator purely to reach a conversation id.
- **The two idle-expiry registries.** Their generation counters guard timer renewal, not
  connection lifetime, and both are meant to outlive a connection.
- **An end-to-end test.** The contract pin covers the echo across all five channels; a compose
  run adds fidelity this change does not need.

## Further Notes

The architecture review that produced this work described it as "a module that owns the
satellite connection's generation", citing five commits as one bug class and three stores that
had each invented their own expiry. Reading the code did not support that: two of the five
commits are the class, the two registries' counters answer a timer question rather than a
connection one, and the connection's own type already owns the generation. Candidate 4 of
`.scratch/architecture-review/2026-08-05-deepening-candidates.md` should be rewritten to match
this spec so a later session does not act on the original framing.

The absent-key case is the one that deserves care in review. It is unreachable if the echo
works, and if the echo breaks it is the difference between a visible error and every satellite
in the house going quiet for two minutes at a time with nothing in the logs.
