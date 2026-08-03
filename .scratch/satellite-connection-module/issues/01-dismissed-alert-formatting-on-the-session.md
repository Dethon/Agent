# 01 — Dismissed-alert formatting moves onto the satellite session

**What to build:** waking a satellite that is ringing an alarm still dismisses it, and the
next transcript dispatched within the snooze window still carries a description of what
was dismissed, so the agent can offer to snooze it. Nothing a household member observes
changes. What changes is where the description is composed: it moves out of the voice
host and onto the **satellite session**, next to the dismissal stash it feeds.

This is a prefactor for ticket 03. The host composes that description at three call
sites, and two of them end up on opposite sides of the seam that ticket 03 introduces.
Doing this first means ticket 03 never has to choose between copying the helper and
reaching across the seam for it.

**Blocked by:** None — can start immediately. Runs in parallel with ticket 02.

**Status:** ready-for-agent

- [ ] The satellite session exposes one operation that takes the dismissed alerts and the
      current time, composes the description and records it, replacing the host's private
      helper.
- [ ] The operation is a no-op when there are no dismissed alerts, matching the helper's
      current early return.
- [ ] All three host call sites — the wake frame, the legacy audio-start frame, and the
      post-dispatch fallback — go through it.
- [ ] The description format is unchanged: each alert rendered as its lowercased kind
      followed by its quoted text, several joined with "and".
- [ ] The existing satellite session dismissal-stash unit tests are extended to cover the
      new operation, including the multiple-alert join and the empty case.
- [ ] The alert-acknowledgement integration tests pass unchanged.
