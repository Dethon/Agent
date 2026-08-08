# A personality profile outlives the memories it was built from

Type: spec
Status: ready-for-agent

## Problem Statement

Someone asked the agent to forget what it knew about them. It did: the memory entries were
deleted, and the tool reported success.

The personality profile synthesised from those memories was not deleted. It is still in
storage, it is still read on every turn, and it is still being prepended to what the model
sees. In production this has been happening for thirteen days. The profile in question was
built from two memories, records that fact in its own metadata, and both of those memories
are gone.

The cause is how the periodic consolidation run decides which users to work on. It derives
the user list by scanning stored memory entries and reading the user out of each key. A
user whose memory entries have all been deleted therefore disappears from that list
entirely, and is never visited again. Their profile is neither refreshed nor removed. It
freezes at whatever it said the day before the last memory went away, and it stays there.

There is a second, smaller defect in the same scan. Profile keys share a prefix with memory
keys and are matched by the same pattern, so the scan yields a phantom user whose name is
the literal word from the profile key. The consolidation run then attempts to work on a
user that does not exist and fails quietly.

This is a correctness problem about what forgetting means. A person who asks to be
forgotten and is told it worked should not still be described to the model by an artifact
derived from what was deleted. It is also a quiet one: nothing errors, nothing is logged as
a failure, and the only external symptom is that the agent keeps treating someone according
to a description that no longer has any basis.

## Solution

Decide which users the consolidation run visits from both stored memories and stored
profiles, rather than from memories alone, so that a user with a profile is always visited
even when they have nothing else left.

When the run visits a user with no memory entries, remove their profile rather than
skipping them. A profile is a synthesis of memories; with no memories there is nothing for
it to be a synthesis of.

Stop the scan reading a profile key as though it named a user.

The first consolidation run after this ships cleans up the existing orphans by itself,
because the users holding them become visible to the run for the first time since their
memories were deleted.

## User Stories

1. As someone who asked the agent to forget me, I want everything derived from what it knew
   to go away too, so that forgetting means forgetting rather than deleting the source and
   keeping the summary.
2. As someone who asked the agent to forget me, I want to not be described to the model by
   a profile built from deleted memories, so that the agent stops acting on information I
   removed.
3. As someone who deleted my memories weeks ago, I want the leftover profile cleaned up
   without me having to ask again, so that a fix does not require me to notice the problem
   first.
4. As someone who has never had a memory stored, I want no personality profile to be
   applied to me, so that the agent's picture of me matches what it actually knows.
5. As someone using the voice satellite, I want the agent's description of how I like to be
   spoken to to reflect current memories, so that a thirteen-day-old summary is not steering
   every voice turn.
6. As someone whose memories still exist, I want my profile left alone, so that a fix aimed
   at orphans does not delete profiles that are doing their job.
7. As someone who deletes only some of my memories, I want my profile refreshed from what
   remains rather than removed, so that partial forgetting is not treated as total
   forgetting.
8. As someone who stores a memory again after having none, I want a profile to be
   synthesised again in the normal way, so that the cleanup is not a one-way door.
9. As an operator, I want the consolidation run to visit every user that has any stored
   state, so that no user can become invisible to it.
10. As an operator, I want the consolidation run to stop attempting work on a user that
    does not exist, so that its failures mean something when they happen.
11. As an operator, I want the cleanup to happen through the normal periodic run rather than
    a migration script, so that there is one code path to trust rather than two.
12. As an operator, I want removing a profile to be visible in the consolidation metrics, so
    that a profile disappearing is something I can see rather than something I infer.
13. As a developer, I want a test that deletes every memory for a user and asserts the
    profile is gone after the next run, so that this specific regression cannot come back.
14. As a developer, I want a test that asserts a profile key is never treated as a user, so
    that the phantom user cannot reappear.
15. As a developer, I want a test that asserts a user with remaining memories keeps a
    refreshed profile, so that the cleanup rule cannot over-reach.
16. As someone reading the repository later, I want the relationship between memories and a
    profile stated somewhere, so that the rule that a profile cannot outlive its memories is
    discoverable rather than folk knowledge.

## Implementation Decisions

**User enumeration draws on both memories and profiles.** The consolidation run's user list
becomes the union of users that have stored memory entries and users that have a stored
profile, deduplicated. This is what makes an orphaned profile visible again, and it is what
makes the cleanup self-healing without a migration step.

**A profile key never yields a user.** The scan currently matches profile keys with the
same pattern it uses for memory keys and reads a user out of the wrong position. Profile
keys are recognised as profiles and contribute the user they belong to, not a phantom.

**No memories means no profile.** When the run visits a user with zero memory entries, it
removes the profile rather than skipping the user. Removal is chosen over blanking so that
the absence of a profile is represented one way rather than two, and so that the recall
path's existing "profile is null" branch is the only case it has to handle.

**Cleanup rides the existing periodic run.** No migration script and no startup task. Once
enumeration includes profiles, the next scheduled consolidation visits the orphaned users,
finds no memories, and removes their profiles. Existing production data is fixed by
deploying, and the mechanism that fixes it is the same one that prevents recurrence.

**Removal is reported.** The consolidation run already publishes an event per user it
works on. Removing a profile is recorded there, so a profile disappearing is observable.

**The forget tool is not changed.** Clearing a profile at the moment the last memory is
forgotten would be stronger, because it would close the window between a forget and the
next scheduled run. That window is up to a day. It is deliberately left out of this spec:
it is a second mechanism doing the same job, and the enumeration fix is what makes the
system correct rather than merely prompt. See Further Notes.

## Testing Decisions

A good test here asserts stored state and what recall subsequently sees, not which internal
methods ran. The externally visible facts are whether a profile exists after a
consolidation run, and whether a turn for that user carries a recall block containing a
profile. Whether enumeration is a scan, a set union, or an index is not the test's business.

**Seam: the consolidation run, driven against a real Redis.** This is the highest seam that
still isolates the behaviour, and it is the seam that owns the bug. The repository's testing
rules prefer real dependencies, and the Redis fixture already exists and is already used by
the memory store's integration tests.

Cases worth pinning:

- A user with memories and a profile keeps a profile after a run, and the profile is
  refreshed rather than left stale.
- A user whose memories are all deleted has no profile after the next run. This is the
  regression itself, and it should be written to fail first against the current code.
- A user with a profile and no memories is visited at all. Enumeration is the actual defect,
  so it deserves its own assertion rather than only being implied by the case above.
- A user who deletes some but not all memories keeps a profile.
- A stored profile never causes work to be attempted on a user named after the profile key.
- After a profile is removed, recall for that user attaches no profile, and the turn still
  proceeds normally.

Prior art: the existing consolidation service unit tests for the run's own logic, and the
Redis memory store integration tests for driving real stored state through the Redis
fixture. The extraction worker drift tests are the closest existing example of a test that
sets up an awkward stored state and asserts what the pipeline does with it.

## Out of Scope

- **Clearing the profile at the moment of forgetting.** Discussed under Implementation
  Decisions and deliberately deferred.
- **Everything in the recall latency work.** Tracked separately under
  `local-embedding-recall`. This spec should land first, because it changes what recall
  returns and therefore what any before-and-after latency measurement means.
- **How profiles are synthesised.** The content, the prompt, and the confidence score are
  untouched. This is only about when a profile should cease to exist.
- **The extraction prompt's low yield.** Recorded as a finding in the research note under
  the other slug. Unrelated to this defect.
- **Retention of memory entries themselves.** Their expiry behaviour is unchanged.

## Further Notes

The production evidence, in order. The profile was written by a consolidation run on
2026-07-26 and records that it was based on two memories. A forget tool call succeeded at
01:00 on 2026-07-27. Consolidation events for that user appear on 2026-07-26 and never
again. The profile's own last-updated timestamp has not moved since. Live conversation
threads carry an empty memory list alongside that same frozen profile, which is the proof
that it is still being read to the model rather than merely sitting in storage.

Two users are currently affected in production. Both hold a substantive profile and zero
memories. Both are cleaned up by the first consolidation run after this ships.

There is a term worth adding to the project glossary out of this work and the latency work
together: an **unremembered user**, meaning one with no stored memory entries. It is the
condition this spec keys profile removal on, and it is also the condition the other spec
keys the recall skip on. Naming it prevents the more dangerous shorthand, "skip recall for
users with no memories", which invites skipping the whole recall step and with it the
profile fetch and the extraction enqueue.
