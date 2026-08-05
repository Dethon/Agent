# 02 — Landing inside a live download answers one rule

**What to build:** Every way of putting bytes inside a live download's directory is refused for
the same reason: anything placed there is removed when the download is cancelled.

That covers a copy whose destination lands inside, a ranged byte write, and a streamed write —
the last being how a cross-mount copy arrives, so streaming in from another filesystem is not a
way around the rule. The refusal names the offending path and hints at waiting for the download
to finish or choosing somewhere outside the download's directory.

The separate "status file is a virtual read-only file" refusal on the write side goes away. A
write to a live download's status file lands inside that download's directory, so the landing
reason already covers it and is the more useful of the two. A write to a leftover status file is
a write to an ordinary file and succeeds.

**Blocked by:** 01 — Reads answer one rule.

**Status:** resolved

- [x] A copy whose destination is inside a live download's directory is refused with the landing
      reason.
- [x] A ranged byte write inside a live download's directory is refused with that reason.
- [x] A streamed write inside a live download's directory is refused with that reason, carried
      as the typed filesystem exception, and nothing is written to disk.
- [x] A write to a live download's status file is refused with the landing reason.
- [x] A write to a leftover status file succeeds.
- [x] A copy out of a live download's directory still succeeds — only the destination side is
      asked.
- [x] Writes to media paths unrelated to downloads still succeed.
- [x] Dotted, absolute and lookalike-id spellings of a destination classify identically.
- [x] All of the above refuse through the one rule with the landing intent.
