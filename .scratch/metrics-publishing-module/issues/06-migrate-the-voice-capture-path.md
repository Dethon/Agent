# 06 — Migrate the voice capture path

**What to build:** The code path that turns a spoken utterance into a dispatched transcript stops carrying its own metrics error handling.

This is where the reported defect lived, and it is where most of the duplication is. The satellite host, the noise-extraction speech-to-text decorator, the transcript dispatcher and the wake arbiter publish between them the large majority of the voice events in the codebase. Three of the five named safe-publish helpers are here, each with a slightly different signature, and around them sit four helpers whose only job is to build an event and route it through a guard: the verification-latency helper, the unknown-speaker helper, the wake-event helper that deliberately discards its task, and the safe-publish helper itself.

All of them collapse to a direct call. Ticket 01 already made the defect unreachable by giving the voice host a buffered publisher; this ticket removes the machinery that was compensating for its absence.

Two comments here document reasoning that is no longer true and must be rewritten rather than carried forward: the satellite host's note that a diagnostic publish is routed through the guard because the early-reject path is awaited with no catch above it, and its note that a publish must be swallowed or it faults the loop and wedges the satellite until reconnect. Both describe a failure mode the contract has removed.

Let synchronousness travel outward as in the other migration tickets. The fire-and-forget publish that currently discards a task becomes an ordinary statement.

**Blocked by:** 01.

**Status:** done

- [ ] Every publish site in the satellite host, the noise-extraction decorator, the transcript dispatcher and the wake arbiter uses the void call.
- [ ] The three named safe-publish helpers on this path are gone.
- [ ] The verification-latency, unknown-speaker and wake-event helpers are gone, their sites publishing directly.
- [ ] No publish on this path discards a task.
- [ ] The two comments describing a metrics failure that can no longer happen are rewritten or removed.
- [ ] Methods that became free of awaits are synchronous and have lost the `Async` suffix, along with their callers' awaits.
- [ ] The existing satellite host, wake arbitration and noise-extraction tests pass, including the integration ones.
