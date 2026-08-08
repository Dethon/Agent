# 08 — Recall does not embed or search for an unremembered user

**What to build:** A user with no stored memory entries — an **unremembered user** — should
not pay for an embedding and a vector search of an empty set. Recall skips those two steps
for them.

It does not skip recall. This distinction is the whole ticket, and getting it wrong breaks
two things at once. The personality profile is still fetched and still attached, because a
user can have a profile and no memories, and on the voice path that profile is what carries
how the agent should speak to them. The turn is still enqueued for extraction, because
skipping that would mean a user with no memories could never acquire any, so the skip would
latch on and never release.

Emptiness is derived from stored state on each turn rather than cached, so a first stored
memory takes effect on the very next turn and there is nothing to invalidate.

Worth knowing while building it: this saved around 575 ms per turn while recall was hosted.
After ticket 07 the same skip saves under 20 ms. It is being taken anyway, with that
reduced value understood.

**Blocked by:** 07.

**Status:** done

- [x] No embedding is requested for a user with no memory entries
- [x] No vector search is issued for them
- [x] Their personality profile is still fetched, and still attached when one exists
- [x] The turn is still enqueued for extraction
- [x] A user who acquires their first memory is searched on the very next turn
- [x] A user whose memories are all removed returns to the skipped path
- [x] Emptiness is read from stored state, not from a cache that would need invalidating
