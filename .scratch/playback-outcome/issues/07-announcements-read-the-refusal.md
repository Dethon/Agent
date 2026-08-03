# 07 — Announcements read the refusal, and played means played

**What to build:** somebody asking the hub to announce something to a speaker gets told what
actually happened to it. A speaker that went away between being looked up and being queued is
reported as offline, which is the truthful status the response already has, instead of being
reported as a dropped announcement — a busy speaker and a missing one stop looking the same.

The played metric also starts meaning what it says. It is published today the moment the
queue reaches the job, before any audio exists, so an announcement whose speech synthesis
then fails is counted as played and nothing else is ever recorded for it. Moving it to first
audio makes it truthful, and takes an awaited metric write out from in front of the first
audio pull.

The alarm loop keeps its own behaviour — one synthesis per alert replayed to every target,
the refcounted alert hold, the re-assert every round — and changes only to state its kind.

**Blocked by:** 04. Runs in parallel with 05 and 06.

**Status:** ready-for-agent

- [ ] The announcement service builds each target's status from the refusal: offline when the
      satellite is gone, dropped for a full queue or a low-priority job behind a queue, queued
      when accepted.
- [ ] The played metric is published at first audio rather than at dequeue, for both the
      announcement service and the alarm loop.
- [ ] An announcement whose synthesis fails publishes no played metric.
- [ ] The preempted-by-reply metric is published from the preempted outcome instead of a
      callback.
- [ ] The queued and error metrics still carry the same room and identity context they do now,
      including for an offline target.
- [ ] The alarm loop states the alarm kind and is otherwise untouched.
- [ ] Announcement tests cover all three refusal reasons mapping to their statuses, and the
      played metric being absent for a failed synthesis.
- [ ] The alarm tests, including the hold and refcounted release tests, pass unchanged.
