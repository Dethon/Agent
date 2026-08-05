# 03 — Moving out of a live download answers one rule

**What to build:** A move is refused when either end crosses a live download's boundary, and the
reason the agent gets names the end that offended.

Moving a live download's directory, any directory containing it, or any payload file inside it
is refused: the download keeps writing and recreates what it lost, leaving the moved copy
orphaned. Moving something into a live download's directory is refused for the landing reason
from ticket 02. The move operation asks the rule once per end, with the intent belonging to that
end.

The mount's cross-mount hook — the one a move between two filesystems consults before it streams
— delegates to the same rule and returns the same refusal, because on this mount the delete that
ends a cross-mount move is the download's cancel.

Moves that touch no live download keep working, including a move of a download directory whose
download is already gone.

**Blocked by:** 02 — Landing inside a live download answers one rule.

**Status:** resolved

- [x] Moving a live download's directory is refused with the move-out reason.
- [x] Moving a directory that contains a live download is refused with that reason.
- [x] Moving a payload file out of a live download's directory is refused with that reason.
- [x] A move whose destination lands inside a live download's directory is refused with the
      landing reason.
- [x] When both ends offend, the refusal names the source.
- [x] The cross-mount hook returns the same refusal for a path on either side of the boundary.
- [x] Moving a leftover download directory succeeds.
- [x] Moving media paths unrelated to downloads still succeeds.
- [x] Dotted and absolute spellings of either end classify identically.
- [x] The move operation consults the rule exactly once per end.
