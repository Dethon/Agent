# 06 — The reply settles from outcomes and the queue owns the prefetch

**What to build:** an answer still reaches a household member sentence by sentence with no
gap between sentences, an alarm still cuts into it and is heard immediately, and the
microphone still reopens as soon as the answer finishes rather than two minutes later. What
changes is that the reply path binds each segment to its job's outcome once, instead of
releasing it on three separate paths and disposing the in-flight speech synthesis on the
same three.

Those six paths are where the microphone-wedging bugs came from, and the comments there
record it: a segment that is never released leaves the turn outstanding until the reply
timeout, and a segment preempted before its first pull leaves a synthesis pump parked on a
full buffer holding an open response. One outcome covers every case, refusal included.

The **playback queue** takes over the prefetch entirely. It wraps a job's audio after it has
decided to accept it, and disposes it on every outcome — so the refused case has nothing to
dispose and that path stops existing rather than being handled. The reply path stops
referencing the prefetch type at all.

The turn handshake itself does not change. Segment tokens, epochs and the settle rule stay
exactly as they are; only where the release is called from moves.

**Blocked by:** 04. Runs in parallel with 05 and 07.

**Status:** resolved

- [x] The reply path registers its segment, queues the job, and binds the segment to the
      outcome in one place: drained completes it, and preempted, failed, refused or
      discarded fail it.
- [x] That binding guards itself, because the queue no longer swallows what a producer
      throws.
- [x] The three release calls and the three prefetch disposals are gone, along with the
      reply path's reference to the prefetch type.
- [x] The queue creates the prefetch for reply segments after accepting them, sized from its
      construction, and disposes it on every terminal outcome.
- [x] Prefetching being switched off is expressed by the queue having no prefetch size, and
      still means a segment's synthesis starts when the loop reaches it.
- [x] A preempted answer still settles as spoken when earlier audio reached the satellite and
      silent when none did, unchanged.
- [x] The queue's tests prove the prefetch is disposed on each terminal outcome and never
      created for a refused job.
- [x] The reply tests prove each segment is released exactly once per outcome, including a
      refused segment, and that a refused segment's text stays buffered for the next flush
      rather than being lost.
- [x] The turn-latency decomposition still sums, and the first-audio spans are still anchored
      to the turn's first reply segment.
