# 01 — Reads answer one rule

**What to build:** A read of a media path gives the same answer whichever read the agent used.

A status file left behind by a download that no longer exists is a leftover: an ordinary file
the disk owns. Reading it as text already works and reading it in ranges already works; reading
it as a stream currently fails saying it is a read-only virtual file. After this ticket all
three agree and the leftover reads.

A live download's status file is a rendered view, so reading it as bytes is refused — in ranges
and as a stream alike, with the same reason, telling the agent to read it as text. Reading text
from a path that is not a status file is refused with a reason naming what this mount does read.

This ticket introduces the mount's one refusal rule with its two read intents. The rule takes
an intent and one path and answers either nothing or one refusal. The existing predicates stay
in place for now; later tickets move the remaining operations across and the last one removes
them.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] A streamed read of a leftover status file returns the file's bytes.
- [ ] A ranged read and a streamed read of the same leftover status file agree.
- [ ] A ranged read of a live download's status file is refused as an unsupported operation
      whose reason points at the text read.
- [ ] A streamed read of a live download's status file is refused with that same reason,
      carried as the typed filesystem exception rather than `NotSupportedException`.
- [ ] A text read of a live download's status file still returns its state, progress and eta.
- [ ] A text read of any other media path is still refused with a reason naming the status file
      as what this mount reads.
- [ ] Dotted, absolute and lookalike-id spellings classify identically for both read intents.
- [ ] The refusals for both read intents come from the new rule, not from a predicate chosen at
      the call site.
- [ ] Tests for the above live at the media mount seam and name paths and operations, never the
      rule or an intent.
