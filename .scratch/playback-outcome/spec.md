# Spec — Playback Outcome

Status: ready-for-agent

Grilled from candidate 4 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the exact file and line evidence for every claim below. The settle
contract is recorded in `docs/adr/0003-playback-settles-by-outcome.md` — read it
before changing any of it. Vocabulary follows the "Satellite playback" section of
`CONTEXT.md` — **playback queue**, **playback job**, **playback outcome**,
**refusal**, **preemption**, **drain** — plus the "Voice satellite" section's
**satellite connection** and **satellite session**.

Sequenced after `.scratch/satellite-connection-module/spec.md` (candidate 3). That
work creates the satellite connection and its drain phase, which is where a discarded
job is settled, and the connection seam its tests introduce is what makes the
listening chime reachable in a unit test at all.

## Problem Statement

Everything the user hears on a satellite — a reply, an announcement, an alarm, a
confirmation prompt, the listening earcon — is queued as a playback job, and a job's
whole contract is five callbacks. Three of them are terminal and mutually exclusive.
Nothing in the type says so.

So each of the six producers works the rule out again, and each one can get it wrong
in a way the compiler cannot see:

- Two producers that need to wait for their audio hand-roll the same idiom: settle one
  completion source from three of the five callbacks, then await it. Both were written
  independently and both are correct by inspection only.
- The reply tool must release its segment on three separate paths, and dispose the
  in-flight speech synthesis on the same three. Missing one release wedges the
  microphone for the full reply timeout, roughly two minutes, and the comments there
  say so because that is how it was found.
- A refusal is not a callback at all. The queue answers "no" with a `false`, and every
  caller then invents the terminal result itself.
- Connection teardown settles nothing whatsoever. When a satellite drops, neither the
  job being played nor any job queued behind it produces a callback of any kind. That
  is why every waiting producer has to carry a cancellation token — not because it has
  a reason to stop waiting, but to avoid waiting forever.

The two terminal callbacks are also awaited inside the queue's own loop, so one
producer's metrics publish sits in the gap between two sentences of a single answer,
and the loop needs a swallow-everything guard around every callback it invokes in case
a producer throws.

The cost to the user is a mic that stays shut after an answer, an answer with a hole in
the middle, or a confirmation prompt that never returns. The cost to a developer is
that "did this job finish?" cannot be answered by reading one type, and the queue's
own test file proves preemption by counting label strings because there is nothing else
to assert on.

## Solution

The playback queue becomes a module with one promise: **every job gets exactly one
outcome.** Not usually one — always one, including a job the queue turned away and
including every job still waiting when the satellite disappears.

Queueing a job hands back a ticket. The ticket says immediately whether the job was
refused and why, and carries the one outcome that will end it: it was heard to the
end, it was cut short, it broke, it was refused, or the connection died first. A
producer that needs to wait awaits the outcome. A producer with nothing to wait for
reads the refusal and moves on. Nobody settles anything by hand, and nobody has three
paths to keep in step.

The queue also takes over the two duties producers were carrying for it. It owns the
audio source's lifetime, so no producer disposes anything. And it reads the job's kind
to decide how much of that kind it will hold at once, whether that kind gets its speech
synthesis started early, and whether that kind plays on the satellite's alert route —
so a producer states what it is queueing rather than restating three policies.

## User Stories

1. As someone talking to a satellite, I want the microphone to reopen when my answer
   finishes, so that I can follow up without waiting out a two-minute timeout.
2. As someone talking to a satellite, I want an alarm to cut into a reply and be heard
   immediately, so that a timer is not delayed by the rest of an answer.
3. As someone talking to a satellite, I want the rest of an answer to keep playing
   after one sentence fails, so that a single synthesis error does not silently end
   the reply.
4. As someone talking to a satellite, I want the microphone not to stay shut when my
   satellite reconnects mid-answer, so that a dropped connection costs me one turn
   rather than the conversation.
5. As someone talking to a satellite, I want a confirmation prompt to always come back
   with an answer or a clean abandon, so that an approval request cannot hang on audio
   that will never play.
6. As someone talking to a satellite, I want the earcon that tells me the mic is open
   to be reliably followed by the mic actually opening, so that I do not speak into a
   closed microphone.
7. As someone talking to a satellite, I want each sentence of an answer to follow the
   last without a gap, so that a metrics write is never audible as a pause.
8. As someone asking for an announcement, I want to be told the speaker was offline
   when it was, rather than that my announcement was dropped, so that I can tell a
   missing satellite from a busy one.
9. As someone reading voice metrics, I want an announcement to count as played only
   when audio actually reached the satellite, so that the played count is not inflated
   by announcements whose speech synthesis failed.
10. As someone reading voice metrics, I want the time-to-first-audio spans to stay
    anchored to the turn's first reply segment, so that the turn decomposition keeps
    summing.
11. As a developer, I want one type to tell me every way a playback job can end, so
    that I do not have to read six producers to learn the rule.
12. As a developer, I want the terminal outcomes to be one value rather than three
    callbacks, so that mutual exclusion is a property of the type instead of a
    convention.
13. As a developer, I want a refused job to produce an outcome like any other, so that
    I do not have to write a second settle path for it.
14. As a developer, I want to know why a job was refused, so that "the queue is full"
    and "the satellite is gone" are distinguishable at the call site and in logs.
15. As a developer, I want every job to settle when the connection dies, so that
    waiting on an outcome cannot hang.
16. As a developer, I want to stop passing a cancellation token purely to avoid
    hanging, so that a token I do pass means I have a real reason to stop waiting.
17. As a developer, I want to await one thing instead of settling a completion source
    from three callbacks, so that a new waiting producer cannot get the set wrong.
18. As a developer, I want the queue to dispose the audio it was given, so that
    releasing an in-flight speech synthesis is not a duty spread across producer
    paths.
19. As a developer, I want a refused job to have nothing to dispose, so that the
    trickiest disposal path stops existing instead of being handled.
20. As a developer, I want to declare what kind of job I am queueing, so that the depth
    limit, the early synthesis and the alert route follow from that one fact.
21. As a developer, I want the alert route to be chosen by the job's kind, so that the
    rule "only timers and alarms are alert-routed" is expressed in a type rather than
    a boolean that any producer may set.
22. As a developer, I want to ask the queue whether it can accept a kind, so that I can
    decide not to consume reply text I am about to have refused.
23. As a developer, I want the queue not to run my code on its own path, so that a slow
    or throwing reaction of mine cannot delay someone else's audio.
24. As a developer, I want the queue to stop guarding every callback against my
    mistakes, so that its loop reads as playback rather than as defence.
25. As a developer, I want the queue's own owner hooks — the frame writer, the audio
    envelope, the per-job error metric — to stay awaited, so that the connection's work
    is still ordered with respect to the audio it frames.
26. As a developer, I want the queue extracted from the satellite session, so that the
    session stops being the place where playback, the microphone, wake metadata and
    alert dismissal all live together.
27. As a developer, I want the queue to be a plain collaborator rather than an
    interface with one implementation, so that producer tests exercise the real
    playback rules.
28. As a developer, I want the per-satellite voice fallback written once, so that a
    satellite with its own configured voice cannot be honoured in three places and
    missed in a fourth.
29. As a developer, I want to assert that a job produced exactly one outcome, so that
    the guarantee has a test rather than a comment.
30. As a developer, I want to assert that a preempted job never spoke a single chunk,
    so that preemption is proved by what was heard instead of by counting label
    strings.
31. As a developer, I want to await an outcome in a test instead of spin-waiting on a
    flag, so that the approval tests stop being timing-sensitive.
32. As a developer, I want the queue's tests named after the queue, so that the file to
    open is obvious.
33. As a developer, I want the subsystem rules to name the playback queue alongside the
    turn, capture and gate modules, so that the next person finds four modules rather
    than three and a loop.

## Implementation Decisions

### The module

`PlaybackQueue` is a new sealed class in the voice channel's services, one per
satellite connection. The satellite session exposes it as a property, exactly as it
exposes the voice turn today — no forwarding methods, because a pass-through layer
would rebuild the surface the extraction removes. The satellite connection constructs
it, launches its loop, and settles its unplayed jobs from the drain phase.

It moves off the satellite session: the job channel, the enqueue sequence, the
high-water preemption mark, the current job's cancellation source, the enqueue
operation, the depth reader, the preempt-current operation, the channel-completion
operation and the playback loop. The session keeps the microphone, the wake stash, the
turn, the control writer and the alert dismissal stash.

No interface is extracted. The queue has one implementation and every producer test
runs the real thing.

### The contract

This shape is fixed by ADR 0003 and is the one thing in this spec that other work
depends on, so it is stated exactly:

```csharp
readonly record struct PlaybackTicket(RefusalReason? Refused, Task<PlaybackOutcome> Completed);
sealed record PlaybackOutcome(PlaybackOutcomeKind Kind, int ChunksWritten = 0, Exception? Error = null);
enum PlaybackOutcomeKind { Drained, Preempted, Failed, Refused, Discarded }
enum RefusalReason { QueueClosed, QueueFull, LowPriorityBehindQueue }
enum PlaybackKind { Reply, Preamble, Announce, Alarm, Chime, Approval }
```

A refused ticket's outcome task is already completed as `Refused`, so a caller has one
settle path. `ChunksWritten` is what the loop already counts. `Error` is set for
`Failed` only.

Queueing is synchronous — every branch of today's enqueue already returns a completed
value — so it returns a ticket rather than a task.

The queue signals an outcome and advances. It never awaits a producer's reaction, and
the interface states that no ordering exists between an outcome being signalled and the
next job starting. Continuations run asynchronously so a producer cannot capture the
loop's thread.

### What the job carries

The job keeps its label as free text for logs, gains a kind, and loses `OnStarted`,
both terminal callbacks and the alert boolean. It keeps the priority, the audio, the
enqueue stamp and the first-audio callback — the only genuinely non-terminal
observation, which stays awaited inside the loop and stays guarded, because it is
invoked between two audio writes.

`OnStarted` has two real users, both publishing the announcement-played metric. That
publish moves to the first-audio callback, where "played" is true: it stops being
published for an announcement whose synthesis then failed, and it stops sitting in
front of the first audio pull. Every producer ignores the label argument the callbacks
receive today, so callbacks take no label.

### Kind decides policy

The queue is constructed with the reply depth limit, the limit for everything else, and
the prefetch buffer size (absent when prefetching is switched off). It reads the kind
to pick which limit applies, whether to prefetch, and whether the audio is
alert-routed. `Alarm` is the alert marker, which is what the subsystem rules already
say in prose — priority is deliberately not the marker, because confirmation prompts
share the high priority.

No producer passes a depth. The reply tool's pre-check becomes a question to the queue
— can you accept this kind — because the tool no longer holds the limit to compare
against.

### Audio ownership

The queue wraps a job's audio in the prefetch buffer itself, after it has decided to
accept the job, and disposes it on every terminal outcome. A refused job therefore has
nothing to dispose, so that path disappears rather than being handled. The reply tool
stops referencing the prefetch type; the buffer size reaches the queue through its
construction, not through the job.

### Behaviour that changes

- Teardown settles. The in-flight job and everything left in the channel become
  `Discarded`. This is settled from the connection's drain phase, which is the only
  place that knows the connection is gone for good.
- A job whose audio enumeration completed reports `Drained` even when the real-time
  tail wait was cut short by teardown, because the audio was written. Today that case
  reports nothing at all.
- An announcement whose satellite vanished between session lookup and queueing is
  reported as offline rather than dropped, using the refusal reason. The other two
  reasons stay dropped.
- The announcement-played metric moves to first audio, as above.

### Behaviour that must not change

Preemption semantics are untouched: a high-priority job marks every job queued before
it, high-priority jobs are exempt from that mark so a second one stacking in the gap
still plays, and a job marked preempt-on-start throws before touching its audio so a
source that ignores cancellation cannot drain anyway. The turn handshake is untouched —
segment tokens, epochs and the settle rule stay exactly as they are; only where
`Complete` and `Fail` are called from changes. The audio envelope framing is untouched,
including the rule that a job which wrote no chunks gets no audio-stop.

### Producer changes

The listening chime and the confirmation prompt drop their hand-rolled completion
sources for a single await, and keep their cancellation tokens because each has its own
reason to stop waiting that is nothing to do with playback. The reply tool binds its
segment to the outcome once, in one continuation that also logs anything that
continuation throws, and loses three release calls and three disposal calls. The
announcement service reads the refusal for its per-target status and reacts to the
preempted outcome for its metric. The alarm loop changes only to state its kind.

The per-satellite voice fallback becomes one resolver on the satellite session, used by
the reply tool, both confirmation-prompt paths and the announcement service. The alarm
controller keeps its deliberate exception — one synthesis per alert, no per-satellite
voice — and does not use the resolver.

### Documentation

The voice subsystem rules gain the playback queue alongside the gate factory, the turn
module and the capture module, and the queue's promise is stated there in one sentence.
That edit lands with the code, not before it.

## Testing Decisions

A good test here asserts what the producer learns or what the satellite would have
heard: which outcome a ticket reported, how many chunks reached the writer, which
metric was published, whether the microphone reopened. It does not assert that a
particular callback fired, which is the thing being deleted.

Red-green-refactor throughout, per the project rules. The teardown guarantee and the
one-outcome guarantee both start as failing tests against today's behaviour.

### Seams

No new seams. Two existing ones carry this work.

The queue's own seam is the current playback test file, which already drives the loop
directly against a recording writer delegate and a fake time provider, with jobs queued
through the real enqueue path. It is renamed for the type it now drives. This is the
highest seam for queue behaviour: the level above is the connection, which needs a
Wyoming event stream to say anything about audio ordering.

The satellite connection seam that candidate 3 introduces carries the two things the
queue cannot prove alone: that the listening chime now waits on an outcome, and that a
connection dropped mid-playback discards what was queued. The chime is unreachable
today, so this coverage is new rather than migrated.

Every producer keeps the seam it has — the announcement service, the confirmation
prompt, the reply tool, the turn-latency decomposition and the alarm controller — with
a real queue underneath, as those tests already use a real session and drain a real
loop.

### What gets tested

At the queue seam: every kind of job produces exactly one outcome, as one
parameterised test covering drained, preempted, failed, refused and discarded; each
refusal reason is produced by its own condition; a job preempted before its first pull
reports zero chunks written; a job cut mid-sentence reports the chunks that reached the
writer; a job whose audio completed before a teardown cut its tail reports drained; the
prefetch is disposed on each terminal outcome and never created for a refused job; the
loop starts the next job without waiting for a consumer of the previous outcome; the
first-audio callback still runs after the first write and still cannot abort playback.

At the connection seam: the chime returns when its outcome arrives; a drop mid-playback
settles the in-flight job and everything behind it.

At the producer seams: the reply tool releases each segment exactly once per outcome,
including refused, and the turn settles spoken or silent as it does today; the
confirmation prompt's existing spin-waits become awaits on an outcome; the announcement
service reports offline for a closed queue and dropped for the other two reasons; the
announcement-played metric is not published when synthesis fails; the turn-latency
decomposition still sums.

### Prior art

The playback test file itself is the model for the queue tests — recording writer, fake
time provider, jobs with synthetic audio. The turn tests are the model for asserting a
handshake without reaching inside it. The follow-up conversation tests are the model
for driving a loop with injected delegates. Candidate 3's spec describes the connection
seam and the fakes it reuses.

## Out of Scope

The reply tool's service-locator lookups and its private statics threading nine or ten
parameters. That is the noted item in the audit, whose fix is a reply-speaker module;
this work takes only the disposal duty and the voice fallback from it.

The satellite session's public capture field, and narrowing the wake arbiter's handle.
Both are noted items related to candidate 3.

Distinguishing a preemption caused by an alert dismissal from one caused by a
high-priority job cutting in. No producer needs the distinction, and the chunk count
covers the part that is worth asserting.

Candidate 1's metrics publishing module. Metric publishes move between callbacks here
but nothing changes about how they are published.

Any ordering guarantee between an outcome and the next job starting. The interface
states that none exists, deliberately.

The satellite firmware, the wire format, and the alarm controller's single-synthesis
voice rule.

## Further Notes

The candidate proposed a single `Task<PlaybackOutcome>` returned from queueing.
Grilling rejected that: one awaitable cannot answer "was this accepted" now and "how
did it end" later, and two producers need the first answer immediately — the
announcement service to fill its per-target status, the reply tool to release a segment
it just registered. Hence the ticket.

The candidate proposed that the queue own the audio source as a disposable handed to
it. Grilling went further, because the prefetch is already pumping before the queue
sees it, which leaves the refused case with a producer-side disposal. Having the queue
create it after acceptance deletes that path.

`Discarded` was not in the candidate. It came out of reading the loop's two catch
guards and its finally condition, all three of which exclude connection teardown, which
is why the two waiting producers pass cancellation tokens they should not need.

`PlaybackKind` was not in the candidate either. Every producer already encodes the same
fact as a label prefix, the test file matches on those strings, and the alert boolean is
set by exactly one producer whose label is the alarm prefix — so the kind was already
there, spelled as three separate conventions.
