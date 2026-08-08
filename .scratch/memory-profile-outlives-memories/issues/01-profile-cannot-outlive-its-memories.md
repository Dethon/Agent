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

- [ ] A user with memories and a profile still has a refreshed profile after a run
- [ ] A user whose memory entries have all been deleted has no profile after the next run
- [ ] A user holding a profile and no memories is visited by the run at all, asserted
      directly rather than only implied — enumeration is the actual defect
- [ ] A user who deletes some but not all of their memories keeps a profile
- [ ] No work is ever attempted on a user named after a profile key
- [ ] Recall for a user whose profile was removed attaches no profile, and the turn
      proceeds normally
- [ ] Removing a profile is recorded on the consolidation run's per-user event
- [ ] The regression test is written to fail against the current code before the fix
