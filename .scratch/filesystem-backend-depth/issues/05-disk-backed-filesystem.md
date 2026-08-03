# 05 — A disk-backed filesystem on the base

**What to build:** The disk-backed servers get what the other servers already have: a backend object that answers the twelve operations. Until now they had none — their MCP tools composed the disk tool classes directly, which is why nothing could be registered generically for them.

One class derives from the base and implements the operations a disk root supports, parameterised by that root and by an optional downloads overlay. The vault and the sandbox pass a root alone. The library passes a root and an overlay, so it stays a composition rather than a root-path wrapper; flattening it would lose the downloads view.

Its descriptions override the base defaults where the existing per-server wording is more useful than the generic text, and that wording carries over unchanged.

No server is migrated onto it here. Registration and wrapper deletion for the disk-backed servers is ticket 07.

**Blocked by:** 01, 04 — it derives from the base, and it composes tools that must already return the shared result type.

**Status:** done

- [x] One class derives from the base and implements the operations a disk root supports.
- [x] It takes a root and an optional downloads overlay.
- [x] Constructed with an overlay, it serves the downloads view exactly as the library server does today.
- [x] Constructed without one, it behaves as the vault and sandbox roots do today.
- [x] Operations a disk root cannot perform are left unoverridden and return the unsupported envelope.
- [x] Containment is enforced through the single path jail.
- [x] Existing per-server description wording is preserved where it overrides the generic default.
- [x] No server registration changes; every server still builds and its tests pass.
