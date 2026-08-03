# 02 — Extract the playback queue

**What to build:** everything a household member hears on a satellite still arrives
exactly as it does now — a reply spoken sentence by sentence, an alarm cutting into it,
an announcement queued behind it, the earcon before the microphone reopens. What changes
is that the **playback queue** becomes its own module instead of five private fields and
a 205-line method on the **satellite session**, and the **satellite connection** is what
constructs it and runs its loop.

This is a mechanical relocation and nothing else. The job contract keeps all five
callbacks, the enqueue keeps taking a depth, preemption keeps working exactly as it does
— a reviewer should be able to check this ticket by eye. Every behavioural change in this
feature lands in a later ticket, on top of a module that already exists.

The satellite session keeps the microphone, the wake stash, the turn, the control writer
and the alert dismissal stash. It exposes the queue the same way it exposes the turn: as
a property, with no forwarding methods, because a pass-through layer would rebuild the
surface this extraction removes.

**Blocked by:** every ticket in `.scratch/satellite-connection-module/issues/`. The queue
is constructed and run by the satellite connection, which that work creates, and its
drain phase is where ticket 04 settles discarded jobs.

**Status:** ready-for-agent

- [ ] A new playback queue module owns the job channel, the enqueue sequence, the
      high-water preemption mark, the current job's cancellation source, the enqueue
      operation, the queue-depth reader, the preempt-current operation, the
      channel-completion operation and the playback loop.
- [ ] The satellite session exposes it as a property and forwards nothing.
- [ ] The satellite connection constructs it, launches its loop with the frame writer and
      the audio-envelope hooks, and completes it in the drain phase.
- [ ] The playback job record, its five callbacks and the depth argument are unchanged.
- [ ] Preemption is unchanged: the high-water mark, the exemption that lets a second
      high-priority job stack and still play, and the preempt-on-start throw that stops a
      cancellation-ignoring audio source from draining anyway.
- [ ] The playback test file is renamed for the type it now drives, with its assertions
      unchanged.
- [ ] The satellite session's remaining tests, all producer tests and the voice
      integration suite pass unchanged.
