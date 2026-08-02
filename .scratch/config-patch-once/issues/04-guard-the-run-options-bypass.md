# 04 — Make the run-options bypass visible

**What to build:** The agent builds its run options only when the caller supplied none. A caller who supplies a pre-built set therefore silently skips the agent's instructions, its tools, its reasoning effort and — after 02 — the config patch as well. Nothing tests that path and nothing reports it, so an agent can run stripped of everything that makes it that agent and look exactly like a normal turn from the outside.

Pre-built options arriving from a caller become visible rather than silently accepted. What the run loses is not a mode the system supports; it is a defect that currently has no symptom.

The bypass is not removed. Non-channel callers exist, and this ticket is about the silence, not the capability.

**Blocked by:** 02 — the patch must be among the things the bypass skips before the guard can describe what is lost.

**Status:** ready-for-agent

- [ ] A run with externally supplied options is surfaced rather than accepted silently.
- [ ] A test covers that path, which today has none.
- [ ] A normal turn, where the agent builds its own options, is unaffected and emits nothing new.
- [ ] The bypass still works — the run proceeds with the caller's options.
