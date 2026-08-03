# 01 — Serialise turns within a conversation

**What to build:** Two messages sent in quick succession to the same conversation are answered in the order they were sent. Today they are consumed concurrently, so a follow-up can be answered before the message it follows, and three pieces of state shared across a conversation's turns are mutated from two turns at once.

Turns within one conversation group become sequential. Everything else stays concurrent: different conversations still run in parallel, and fan-out across multiple delivery targets for one turn is untouched.

This is an observable behaviour change, and it was accepted deliberately. Sequential turns are what the rest of the stack already assumes — the alternative is defending shared state in three separate modules against a concurrency nobody wanted.

Serialising is load-bearing for state this ticket does not otherwise touch, and that dependency must be written down at the site rather than left to be rediscovered:

- The tool-approval client's dynamically-approved tool set is an unsynchronised collection mutated during a turn.
- The chat client's reasoning, cost and cached-token queues are drained per update and per response, so two interleaved streams on the same client cross-attribute each other's values.

Neither is fixed here. Serialising makes both correct in practice; anyone reintroducing concurrent turns re-breaks them.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] Two messages queued for one conversation produce non-overlapping turn windows, asserted by a test written before the change and watched to fail.
- [x] The existing multi-target fan-out concurrency test still passes unchanged.
- [x] Different conversations still run concurrently.
- [x] A comment at the serialisation point names the three pieces of shared state that now depend on it.
- [x] Both assertions — this axis serialises, that axis does not — live in the same test file, so the trade is visible in one place.
