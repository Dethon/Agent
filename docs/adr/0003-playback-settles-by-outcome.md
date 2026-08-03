# 0003 — Satellite playback settles by outcome

Status: accepted
Date: 2026-08-03

## Context

`PlaybackJob` carried five callbacks — `OnStarted`, `OnPreempted`, `OnDrained`,
`OnFirstAudio`, `OnFailed`. Three of them are terminal and mutually exclusive, and
that rule was the whole contract while being stated nowhere in the type. Each of the
six producers rediscovered it:

- The listening chime (`WyomingSatelliteHost.cs:559-570`) and the approval prompt
  (`RequestApprovalTool.cs:139-148`) hand-rolled the identical idiom: settle one
  `TaskCompletionSource` from three of the five callbacks, then await it.
- `SendReplyTool` released its segment on three separate paths and disposed its
  prefetched synthesis on the same three, with comments at `:345-347` and `:354-358`
  recording what breaks when one is missed — a leaked segment wedges the microphone
  for the full ~120 s reply timeout.
- A refused enqueue was not a callback at all. `EnqueuePlaybackAsync` returned
  `false` and each caller synthesised the terminal outcome itself.
- Connection teardown settled nothing. Both catches in the loop were guarded by
  `!ct.IsCancellationRequested` and the `finally` skipped `OnDrained` on the same
  condition, so when a satellite dropped, the in-flight job and every job still
  queued behind it produced no callback of any kind. That gap is why every awaiting
  producer had to carry its own `WaitAsync(ct)` to avoid hanging forever.

The terminal callbacks were also awaited inside the playback loop, which put
announce's `AnnouncePreemptedReply` Redis round trip into the seam between two
sentences of one answer, and required the loop to swallow anything a producer threw.

## Decision

The playback queue is its own module and guarantees **exactly one outcome per job**,
with no exceptions:

```csharp
readonly record struct PlaybackTicket(RefusalReason? Refused, Task<PlaybackOutcome> Completed);
sealed record PlaybackOutcome(PlaybackOutcomeKind Kind, int ChunksWritten = 0, Exception? Error = null);
enum PlaybackOutcomeKind { Drained, Preempted, Failed, Refused, Discarded }
enum RefusalReason { QueueClosed, QueueFull, LowPriorityBehindQueue }
```

A refused job's `Completed` is already completed as `Refused`, so a caller has one
settle path rather than a branch. `Discarded` covers teardown: the connection's drain
settles the in-flight job and everything left in the channel.

**The queue signals an outcome and advances. It never awaits a producer's reaction.**
Producers that need to wait await `Completed` on their own task; producers with
nothing to wait for attach a continuation and guard it themselves. The queue's own
owner keeps awaited, guarded hooks for the frame writer, the audio-start/stop
envelope and the per-job error metric, because those are the connection's work, not a
producer's.

The queue owns the audio source's lifetime. It wraps a reply's lazy synthesis in the
prefetch buffer itself, after deciding acceptance, and disposes it on every terminal
outcome — so no producer disposes anything, and the refused-job disposal path stops
existing rather than being handled.

`PlaybackKind` (`Reply`, `Preamble`, `Announce`, `Alarm`, `Chime`, `Approval`)
replaces the label-prefix convention and is what the queue reads to pick the depth
limit, whether to prefetch, and whether the audio is alert-routed. The two depth
limits move into the queue's construction; `Label` survives as free text for logs.

## Considered options

**Add `OnRefused` and `OnDiscarded` callbacks.** Completes the set at seven
callbacks. The mutual-exclusion rule stays undocumented and the hand-rolled
settle-from-N-callbacks idiom stays in both producers that need to wait.

**`bool TryEnqueue(job, out Task<PlaybackOutcome>)`.** Matches the repo's `TryX`
idiom, but refusal stops being an outcome, so `SendReplyTool` keeps two settle paths
— which is the duplication this record exists to remove.

**One awaited terminal hook per job.** Preserves today's exact ordering, where a
segment's release completes before the next segment starts. Rejected: it keeps a
Redis publish between two sentences of an answer and keeps the loop's
swallow-everything guards, and no producer actually depends on that ordering — the
follow-up chime is high-priority and preempts, so it does not race the queue.

**A queue that owns synthesis** (`Speak(text, kind)` plus `Play(chunks, kind)`).
Deepest option: the per-satellite voice fallback and the prefetch both vanish from
producers. Rejected because the queue would gain `ITextToSpeech` and `VoiceSettings`,
and reply policy would start landing on the thing whose one job is deciding what is
audible next. The voice fallback is instead resolved once on the satellite session.

## Consequences

- Awaiting a playback outcome no longer needs a cancellation token for correctness.
  The chime and the approval prompt keep theirs, because each has its own reason to
  stop waiting that has nothing to do with playback.
- There is no ordering between an outcome being signalled and the next job starting.
  Anything that needs "the queue is idle now" must ask the queue, not infer it from
  having observed an outcome.
- A producer's continuation runs unobserved, so it carries its own `try`/`catch`. The
  loop no longer does that for it.
- Announce reports `"offline"` instead of `"dropped"` when the satellite vanished
  between session resolution and the enqueue, which is the truthful status it already
  had a code path for.
- `AnnouncePlayed` is published at first audio rather than at dequeue, so it is no
  longer published for an announcement whose synthesis then failed. Expect its count
  to sit slightly below `AnnounceQueued` where it previously matched.
- A producer cannot bring its own prefetch. A new producer that wants one adds a
  `PlaybackKind`, which is also where its depth limit is declared.
- "Every enqueued job produces exactly one terminal outcome, refused and discarded
  included" becomes a single parameterised test instead of being re-proved per
  producer, and `ChunksWritten` makes "preempted before it spoke a single chunk"
  assertable without counting label strings.
- Sequenced after the satellite connection module: `Discarded` is settled by that
  module's drain phase, which is the only place that knows the connection is gone for
  good.
