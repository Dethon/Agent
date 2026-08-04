# 04 — Every job settles exactly one outcome

**What to build:** the **playback queue** starts answering, for every job anyone hands it,
exactly one question: how did this end. It was heard to the end, it was cut short, it
broke, it was refused, or the connection died before it could be heard. One answer per
job, always — including a job the queue turned away, and including every job still waiting
when a satellite drops.

The last part is the fix a household member would notice. Today a satellite that drops
mid-answer settles nothing at all: the job being played and everything queued behind it
produce no callback of any kind, which is why a confirmation prompt can only avoid waiting
forever by carrying its own cancellation token. After this ticket, a dropped connection
settles them as discarded, from the connection's drain phase — the one place that knows the
connection is gone for good.

This is the expand half of an expand–contract. The five existing callbacks keep firing
exactly as they do, so no producer changes and nothing breaks; tickets 05 to 07 move the
producers over one at a time, and ticket 08 deletes the callbacks. Write the guarantee test
first and watch it fail on today's behaviour.

The contract is fixed by `docs/adr/0003-playback-settles-by-outcome.md` and other tickets
depend on these exact shapes, so it is stated literally:

```csharp
readonly record struct PlaybackTicket(RefusalReason? Refused, Task<PlaybackOutcome> Completed);
sealed record PlaybackOutcome(PlaybackOutcomeKind Kind, int ChunksWritten = 0, Exception? Error = null);
enum PlaybackOutcomeKind { Drained, Preempted, Failed, Refused, Discarded }
enum RefusalReason { QueueClosed, QueueFull, LowPriorityBehindQueue }
```

**Blocked by:** 03.

**Status:** resolved

- [x] Queueing a job returns a ticket. It is synchronous, because every branch of the
      enqueue already answers immediately.
- [x] A refused ticket names its reason and carries an outcome that is already settled as
      refused, so a caller has one settle path rather than a branch.
- [x] The three refusal conditions map to distinct reasons: the satellite is gone, the
      queue already holds its limit for that kind, and a low-priority job arrived while
      anything was queued.
- [x] Every accepted job settles exactly once — drained, preempted, failed or discarded —
      and the outcome carries how many chunks reached the writer, plus the error for a
      failure.
- [x] The connection's drain phase settles the in-flight job and everything left in the
      channel as discarded.
- [x] A job whose audio completed before teardown cut its real-time tail reports drained,
      because the audio was written. Today that case reports nothing.
- [x] The queue signals an outcome and advances. It does not await anything a producer
      attached to it, and continuations cannot capture the loop's thread.
- [x] The five existing callbacks still fire on the same paths as before, so producers are
      untouched by this ticket.
- [x] One parameterised test proves exactly-one-outcome across all five kinds of ending,
      written red first.
- [x] Separate tests cover each refusal reason, a job preempted before its first pull
      reporting zero chunks, a job cut mid-sentence reporting what reached the writer, and
      the loop starting the next job without waiting for a consumer of the previous
      outcome.
- [x] All producer tests and the voice integration suite pass unchanged.
