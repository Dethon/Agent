# 04 — Delete's refusals answer one rule

**What to build:** Delete's two refusals come from the same rule as every other operation, while
everything delete *does* stays outside it.

The rule refuses deleting a live download's status file, because the status file is a view and
removing it would partially dismantle a download the agent meant to leave running. It refuses
deleting any path that is neither a download directory nor a status file, with a reason saying
what delete means on this mount.

Everything else is unchanged and stays where it is: deleting a live download's directory cancels
the download, clears its routing entry and removes its files; deleting a leftover download
directory removes it and its routing entry; deleting a leftover status file removes an ordinary
file. Those effects run only after the rule has said nothing. The rule answers "may I" and never
acts.

**Blocked by:** 01 — Reads answer one rule.

**Status:** ready-for-agent

- [ ] Deleting a live download's status file is refused with the read-only reason.
- [ ] Deleting a media path that is neither a download directory nor a status file is refused
      with a reason naming what delete does here.
- [ ] Deleting a live download's directory still cancels the download, removes its routing entry
      and removes its files, and still reports the cancel.
- [ ] A failure cleaning up a live download still aborts before the routing entry is touched.
- [ ] Deleting a leftover download directory still removes it and its routing entry.
- [ ] Deleting a leftover status file still removes the real file.
- [ ] Deleting a download directory that exists nowhere still reports not found.
- [ ] Dotted, absolute and lookalike-id spellings classify identically.
- [ ] The two refusals come from the rule; the cancel, routing removal and leftover recovery do
      not move into it.
