# 07 — Migrate the voice announcement and reply path

**What to build:** The path that speaks a reply, rings an alarm and asks for approval publishes through the void call, and stops taking precautions against a metrics failure.

Four types here publish: the announcement service, the insistent announcement controller, the reply tool and the approval tool. Two of the five named safe-publish helpers are here.

Two sites need a hand edit rather than a mechanical replacement, because they treat a publish as a task. One reports every offline target by gathering a task per publish and awaiting them together; with nothing to gather it becomes a plain iteration. The other passes a publish as a callback whose type requires a task, so it needs an explicit completed task.

Three comments here reason about behaviour the contract has removed and must be rewritten. The reply tool takes its turn timestamp deliberately before the publish, because the publish is an awaited Redis round trip whose cost would otherwise land in no span at all — after this change the publish is a channel write and that ordering no longer matters. The reply tool's preemption path notes that a metrics blip used to leak a segment slot and wedge the microphone for the full reply timeout. The announcement service notes that its per-target publishes are best-effort, mirroring the satellite host's helper, which no longer exists.

Leave the single-use dispatch stamp and the segment-token release exactly where they are. They are correct for reasons unrelated to metrics, and their design is a separate candidate.

**Blocked by:** 01.

**Status:** ready-for-agent

- [ ] Every publish site in these four types uses the void call.
- [ ] The two named safe-publish helpers on this path are gone.
- [ ] The offline-target fan-out no longer gathers tasks.
- [ ] The task-typed publish callback compiles without wrapping a guard.
- [ ] The three comments describing a metrics failure or a publish cost that can no longer happen are rewritten or removed.
- [ ] The dispatch stamp is still consumed exactly once per reply, and a segment slot is still released before anything else on the preemption path.
- [ ] Methods that became free of awaits are synchronous and have lost the `Async` suffix, along with their callers' awaits.
- [ ] The existing announcement, insistent-announcement, reply-tool and approval-tool tests pass, including the end-to-end ones.
