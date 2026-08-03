# 01 — The minted marker tells the truth about this turn

**What to build:** An agent-initiated message delivered into a conversation that already
exists gets its live stream set up, and one delivered into a conversation the same turn
just minted does not get announced twice. That is what happens today, but only because
the caller passes a flag correcting a marker that has gone stale. After this ticket the
marker is right and there is no flag.

A delivery target is marked minted when the turn being announced is the turn that minted
it. When a group reuses its anchors for a later turn, those targets carry the marker
cleared, because nothing was minted for that turn. The turn-start announce reads the
marker and nothing else.

Behaviour is identical in every case. This is the first of three preparatory steps, and
the existing monitor suite must stay green without edits.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The turn-start announce takes no skip-minted argument and skips exactly the targets marked minted.
- [ ] Group anchors reused for a later turn carry the minted marker cleared.
- [ ] The group-opening turn still skips announcing the targets it minted.
- [ ] A later turn still announces those same conversations, now because the marker is cleared rather than because a flag said to ignore it.
- [ ] The announce tests that drive the resolver directly are rewritten against the marker instead of the removed argument, and keep their intent.
- [ ] The existing monitor test suite passes unchanged.
