# 09 — Record the decisions

**What to build:** The two records that outlive the work.

An architecture decision record for embeddings going local. It should say what was given up
— a hosted provider with redundancy, and a 1536-wide index — and what was bought with it,
that the choice was made on measurements taken on the production host rather than on
published benchmarks, why the index dimension follows the model rather than the reverse, and
why there is deliberately no cross-provider fallback. That last part matters most: without
it the absence of a fallback reads as an oversight to whoever finds it next, and someone
adds one that returns vectors of the wrong width.

A glossary entry for **unremembered user**, meaning a user with no stored memory entries. It
is the condition both ticket 08 and the profile fix key on. Naming it is what stops the
dangerous shorthand, "skip recall for users with no memories", from leading the next person
to skip the whole recall step and take the profile fetch and the extraction enqueue with it.
The glossary holds no implementation detail, so the entry says what the term means and what
not to call it, in the shape the existing entries use.

**Blocked by:** 07, 08.

**Status:** done

- [x] The decision record takes the next number in sequence and follows the existing format
- [x] It states the alternatives that were considered and why the local path won on
      measurement
- [x] It records the absence of a fallback as a decision, with its reason
- [x] The glossary entry defines the term and names what not to call it
- [x] The glossary entry carries no implementation detail
- [x] Neither document duplicates the research note or the spec; those remain the evidence
