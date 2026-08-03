# 02 — Reparent the four non-disk backends and close the divergences

**What to build:** Glob and search behave the same on every non-disk mount, and unsupported replies stop naming internals.

The timers, scheduling, print-queue and Home Assistant backends are reparented onto the base and each deletes its copy of the shared code, overriding only the operations it actually implements. Four user-visible behaviours change as a result, and each is a behaviour change in its own right:

- **Schedules and timers gain the search regex timeout and the compilation guard.** Today both compile a user-supplied pattern with neither, so a search on those mounts can hang a turn or surface a raw exception instead of an envelope.
- **Print-queue gains base-path scoping and the dirs-only rule.** Today it ignores both, so the same glob call means different things depending on which mount it targets.
- **Home Assistant's unsupported messages name the operation the model called** instead of the C# method, so a model that called text-create is told about text-create.

Each of the four gets its own failing test first, in that backend's own existing test file. Capability does not change here: timers still overrides move at this point, and removing that override is ticket 06's job.

**Blocked by:** 01 — the base must exist before anything can be reparented onto it.

**Status:** done

- [x] All four backends derive from the base and override only the operations they implement.
- [x] Each backend's copy of the envelope helpers, glob prologue and search template is deleted.
- [x] A search on schedules and on timers is bounded by the match timeout, and a pattern that cannot compile returns an invalid-argument envelope.
- [x] A print-queue glob honours the base path, and a trailing slash yields directories only.
- [x] A Home Assistant unsupported reply names the operation, with no C# method name in the text.
- [x] Per-server description text that names real files is preserved word for word: the schedules and timers file names, and Home Assistant's state file and shell-entry usage.
- [x] Each of the four changes is asserted in that backend's existing test file, and each test was seen to fail before the fix.
