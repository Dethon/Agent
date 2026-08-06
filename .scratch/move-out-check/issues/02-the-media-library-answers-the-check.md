# 02 — The media library refuses a move out of a live download, and the dead guard is deleted

**What to build:** asking to move a live download somewhere else stops being a way to cancel it.

The media library answers the move-out check with the refusal it already has: the overlay's one
rule, asked with the move-out intent — the same call its own same-mount move makes for its
source end. No new reason, no new wording, no new predicate. What changes is that the answer now
reaches the agent, because the question crosses the seam.

The agent asking to move a live download's directory to another mount is told why and nothing is
attempted: no files streamed, no partial copy on the destination, and the download still
running. The same holds for a file inside a live download's directory and for an ancestor
directory that contains one. A leftover status file and any ordinary media file still move, and
a cross-mount copy out of a live download still works, because a copy leaves the source in place.

`ICrossMountMoveGuard`, the media backend's implementation of it, and the move tool's
cross-mount refusal helper are deleted in this ticket. The interface exists only to prevent this
bug and has never fired in any deployment.

See `.scratch/move-out-check/spec.md` and ADR-0015; the refusal itself is ADR-0014's move-out
intent, unchanged.

**Blocked by:** 01 — The move-out check crosses the seam, allowing by default.

**Status:** resolved

- [x] The media mount answers the move-out check with the overlay's rule, asked with the
      move-out intent
- [x] A move out of a live download's directory, of a path inside one, or of an ancestor of one
      is refused, with the reason and hint the same-mount move already gives
- [x] The refusal is marked permanent rather than retryable
- [x] A leftover status file and an ordinary media file outside any live download still move
      across mounts
- [x] A cross-mount copy out of a live download's directory still succeeds
- [x] A same-mount move on the media library is refused exactly as before
- [x] A move into a live download's directory is still refused by the landing rule
- [x] `ICrossMountMoveGuard`, its implementation and the move tool's cross-mount refusal helper
      are gone, and nothing type-tests a backend
- [x] The media mount's tests assert the new operation consults the rule with the right intent,
      alongside the existing operations
