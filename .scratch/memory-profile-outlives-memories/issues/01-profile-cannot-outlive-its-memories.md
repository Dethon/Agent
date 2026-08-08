# 01 — A personality profile cannot outlive its memories

**What to build:** When someone's memories are all gone, the personality profile
synthesised from them goes too, without anyone having to ask a second time.

The periodic consolidation run decides which users to visit. Today it derives that list
from stored memory entries alone, so a user whose memories have all been deleted vanishes
from it and is never visited again, leaving a frozen profile that recall keeps reading to
the model. Make the run consider stored profiles as well, so a user holding only a profile
becomes visible again. When it visits a user with no memory entries, it removes their
profile rather than skipping them. It also stops reading a profile key as though it named
a user, which currently produces a phantom user the run quietly fails on.

Deploying this fixes the existing production orphans by itself: the next scheduled run
visits them for the first time since their memories were deleted, finds nothing, and clears
them. No migration step and no cleanup script.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [x] A user with memories and a profile still has a refreshed profile after a run
- [x] A user whose memory entries have all been deleted has no profile after the next run
- [x] A user holding a profile and no memories is visited by the run at all, asserted
      directly rather than only implied — enumeration is the actual defect
- [x] A user who deletes some but not all of their memories keeps a profile
- [x] No work is ever attempted on a user named after a profile key
- [x] Recall for a user whose profile was removed attaches no profile, and the turn
      proceeds normally
- [x] Removing a profile is recorded on the consolidation run's per-user event
- [x] The regression test is written to fail against the current code before the fix

## Comments

Implemented on `worktree-memory-profile-outlives-memories` (2026-08-08), three commits:

- `GetAllUserIdsAsync` now reads `memory:profile:<userId>` keys as their real user and
  never as a user named `profile`; `IMemoryStore` gained `DeleteProfileAsync`
  (integration-tested against real Redis, red first).
- `MemoryDreamingService` visits with zero memories delete the profile instead of
  synthesizing one, recorded as `ProfileRemoved` on `MemoryDreamingEvent`. The field is
  not `required` so events published before it existed still deserialize.
- A pin test asserts recall with no memories and no profile attaches no context and the
  turn proceeds.

Full suite: 3961 passed, 0 failed.
