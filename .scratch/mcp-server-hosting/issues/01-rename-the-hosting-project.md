# 01 — Rename the hosting project to Mcp.Hosting

**What to build:** The project that holds `AddChannelServer` stops being named after
channels. It is about to hold the tool-server calls too, and thirteen servers rather
than six will reference it, so a project called `Channels.Hosting` would be lying to
nine of them.

Pure prefactor. Nothing changes about how any server behaves; the point is that every
later ticket in this spec adds to a project whose name already fits.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] The project, its directory, its root namespace and its solution entry are all
      `Mcp.Hosting`.
- [ ] Every project referencing it, every `using`, and the unit-test folder named after
      it follow the rename.
- [ ] `CLAUDE.md` and the rules files that name the project follow it too. Their prose
      is otherwise unchanged — this ticket renames, it does not restate.
- [ ] The test asserting the hosting project references nothing from Infrastructure
      still passes. That rule is the reason two channel servers stay lightweight and it
      must survive the rename intact.
- [ ] The whole solution builds and the existing suite passes with no test edited for
      any reason other than the name.

## Notes

Do not add anything to the project in this ticket. Tickets 03, 04 and 05 each add one
call; keeping this one to the rename means a conflict during those is a real conflict
rather than rename noise.
