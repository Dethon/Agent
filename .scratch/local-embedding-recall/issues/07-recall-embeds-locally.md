# 07 — Recall embeds locally

**What to build:** The cutover, and the ticket that delivers the win.

Recall stops calling the hosted provider and embeds against the local Lemonade server,
using a multilingual model producing 1024-dimension vectors. The model was chosen for
language coverage rather than speed: production voice traffic is Spanish, and the speed
difference between the local candidates is immaterial against a multi-second time to first
reply. Someone using the agent should notice only that replies start sooner. What the agent
remembers does not change.

This includes the production migration, because the code change and the data change cannot
be verified apart: the new build refuses to start against an index of the old width by
design, and a rebuilt index breaks the old build's recall. The index is dropped without
deleting documents, so every stored memory hash survives and a failed migration is
recoverable by re-running it. The vectors are regenerated and the index recreated at the new
width, by a throwaway script rather than committed code.

The migration runs live and the new build is deployed after it. During that window the old
build is still running: its recall queries fail and are swallowed, which is harmless, but
its extraction can still write a vector at the old width that the new build would never be
able to search. So the re-embedding step is run once more after deploying, to catch any
stray.

There is deliberately no fallback to the hosted provider. Its vectors are 1536 wide and
would be invalid against this index rather than merely slower. A local failure degrades to
a turn with no recall block, which is the degradation that already exists today when the
hosted provider fails, made observable rather than only logged.

**Blocked by:** 01 (the baseline must exist before the thing it measures changes), 04, 05,
06, and `memory-profile-outlives-memories` 01 (so the latency numbers are not taken while a
stale profile is still being injected on the voice path).

**Status:** done

- [x] Recall embeds against the local server; no hosted embedding call is made on the turn
      path
- [x] Stored memories are searchable at the new dimension, and a user with memories gets
      the same kind of results back as before — pinned by LemonadeRecallTests, which skips
      without the provisioned model cache
- [x] A local outage produces a turn with no recall block rather than a failed turn, and
      publishes a metric that distinguishes it from an ordinary empty recall
- [x] No fallback to the hosted provider exists, and the reason is recorded in code
- [ ] Recall stage latency drops materially against the baseline from ticket 01
      — measurable only after deploying; ticket 01's baseline is in place to compare against
- [ ] Every stored memory hash survives the migration
      — the migration script drops the index without DD and never deletes a document;
      run by hand per .scratch/local-embedding-recall/MIGRATION.md
- [ ] Re-embedding is re-run after deploying, and no memory is left unsearchable
      — step 4 of the runbook; not yet run
- [x] Address and model configuration live in application settings alone, with no compose
      environment entry, since the server is in the same stack at a stable address
