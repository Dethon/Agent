# 03 — One path jail for the disk-backed tools

**What to build:** A path is inside a mount root or outside it, decided one way. Today eight copies of the containment check guard the disk tools, and they disagree: one compares ordinally, one ignoring case, and two branch on the operating system. They also compare prefixes without requiring a separator, so a root of `/library` admits a sibling directory named `/library-backup`.

This is a prefactor for the rewrite that follows. It replaces the eight copies with one value type built from the canonical root, applying a single comparison rule and requiring a separator after the prefix. Behaviour changes in one visible way: a sibling directory whose name merely extends the root's name is now refused, which is what users expect and what the check was meant to do.

Return types and call shapes are unchanged; the tools still throw here. Making them return the shared result type is ticket 04.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] One value type decides containment, built from the canonical root.
- [x] All eight hand-written copies of the check are gone.
- [x] A path whose prefix matches the root but is not followed by a separator is refused.
- [x] One comparison rule applies, and the operating-system-conditional branches are gone.
- [x] A path legitimately inside the root is still accepted, including one reached through a relative segment.
- [x] The existing per-tool test files cover the refusal and the acceptance, and the tests were seen to fail before the fix.
- [x] The tools' return types are unchanged; no wrapper or server needs editing.
