# 05 — Vector dimension is configuration, verified at startup

**What to build:** The vector dimension is a constant, and the memory store only creates an
index when reading the live one fails. So an index of the wrong width is kept and never
noticed. A query at the wrong width then errors, recall's catch-all swallows it, and memory
silently returns nothing on every turn while everything still looks fine from outside.

Move the dimension to configuration, and add a startup check that compares it against the
live index's vector field and refuses to start when they disagree. An operator should learn
about a mismatch from a process that will not boot, not from memory quietly going missing
for weeks.

Ship this with the dimension still at its current value. It is a guard, not a migration.

Two details that matter. The check compares the **vector field's dimension only**, never the
whole schema: the live production index carries a tag field the code no longer creates, left
behind when a superseding feature was removed, so a full comparison would fail on day one
against a perfectly healthy index. And it runs at startup rather than lazily on first
recall, because a lazy check would be swallowed by the same catch-all it exists to defeat.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The dimension comes from configuration rather than a constant
- [ ] The startup check compares only the vector field's dimension
- [ ] An index carrying an extra tag field the code no longer creates still starts, since
      that is the live production shape
- [ ] A mismatch fails startup with a message naming both the configured and the live value
- [ ] A matching index starts normally, and an absent index is still created as before
- [ ] The check runs at startup, not on first recall
