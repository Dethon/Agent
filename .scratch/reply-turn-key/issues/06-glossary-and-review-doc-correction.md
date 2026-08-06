# 06 — Glossary and review-doc correction

**What to build:** the two written records that would otherwise send a later session down the
wrong path.

The glossary gains one term. **Turn key** belongs beside **Turn** in the conversation section,
not in the voice section — the key is minted for every channel, and voice is only its first
reader. It says what a turn is dispatched under, so a reply can say which turn it answers; the
four cases in ticket 03 fall out of it. No architecture decision record: the rule is small, it
follows from the existing definition of a turn, and it is reversible.

Candidate 4 of the architecture review needs rewriting in place. It currently describes this
work as "a module that owns the satellite connection's generation", cites five commits as one
bug class, and claims three stores each invented their own expiry. Reading the code did not
support any of it: two of the five commits are the class, the two idle registries' counters
guard timer renewal rather than connection lifetime, and the connection type already owns the
generation the review proposed to extract. Left as written, the next session to open that file
acts on the original framing.

**Blocked by:** 04 — The stream-handle machinery deletes.

**Status:** ready-for-agent

- [ ] The glossary defines **Turn key** in the conversation section, with the terms it should
      not be called.
- [ ] The glossary entry is free of implementation detail — no type names, no call sites.
- [ ] Candidate 4 of the architecture review is rewritten to describe what shipped, including
      the corrected commit count and the two claims that did not hold.
- [ ] The review's top-recommendation section still reads correctly after the rewrite.
- [ ] No architecture decision record is added.
