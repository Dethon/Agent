# 05 — Contract: one question, one matrix

**What to build:** Nothing outside the overlay can ask half the ownership question any more, and
the suite describes the mount's behaviour as one table instead of seven site-specific
assertions.

By this point every operation refuses through the rule, so the three predicates have no callers
left outside the overlay. The spelling-only one goes entirely — it is the one that produced the
divergence, because a path's spelling never decides ownership. The two liveness predicates
become internal to the rule.

The tests become one table over intent by path class: a live download's directory, a live
download's status file, a payload file inside a live download's directory, a leftover status
file, a leftover download directory, and a path unrelated to downloads. Each cell asserts either
the operation succeeding or one refusal with its code and reason. Dotted, absolute and
lookalike-id spellings are rows against the same cells rather than their own group, since they
exist to prove the classification is the same one. The seven per-site assertions go: each passed
while disagreeing with its neighbour, which is how the divergence stayed invisible.

The overlay's own tests keep everything that is not a refusal — the rendered status file's
contents, existence answers, glob entries and merging, cancel-and-clean, leftover recovery and
routing removal.

**Blocked by:** 02 — Landing inside a live download answers one rule; 03 — Moving out of a live
download answers one rule; 04 — Delete's refusals answer one rule.

**Status:** resolved

- [x] The spelling-only predicate no longer exists.
- [x] The two liveness predicates are private to the overlay.
- [x] The overlay's public surface is the one rule plus the operations that produce something:
      read, info, glob entries and delete.
- [x] The refusal matrix covers every intent against every path class at the media mount seam.
- [x] Spelling variants are rows in that matrix, not a separate group.
- [x] The seven per-site refusal assertions are gone, with nothing they covered lost.
- [x] The overlay's non-refusal tests are untouched and still pass.
- [x] The per-server filesystem conformance test passes untouched — no capability drifted.
- [x] No test names the rule's method or an intent value.
