# 04 — Disk tools return the shared result type natively

**What to build:** A failure on a disk-backed mount is the same kind of thing as a failure on any other mount. Today the disk tools throw, and the throw is turned into an error envelope at the MCP boundary by a mapping from exception type to error code that has to be kept in step with the domain's codes by hand. An exception type nobody mapped becomes a generic internal error, and the model is told nothing useful.

The thirteen disk tool classes — read, create, edit, search, glob, move, remove, copy, file info and the blob operations — return the shared result type directly. Error cases become the envelopes the base already defines: not found, invalid argument, already exists, timeout. Nothing on the filesystem path throws to the boundary any more.

This is the largest single change in the feature. The text-search tool is the biggest file in it. Rewrite each tool behind the tests it already has, then change its return type — the two must not be one commit.

The domain's own search-output enumeration is deleted here in favour of the virtual filesystem's. The string round-trip that made the two enums agreeing an unchecked coincidence goes away with the native return type.

The exception-to-envelope conversion at the boundary is **not** deleted wholesale. Every server installs it as a catch-all filter over all its tools, including two servers that have no filesystem at all, so removing it would strip envelopes from property search, web search and Home Assistant service calls. What goes here is the filesystem path's dependence on it, and the mapping arms that exist only for filesystem exception types. The catch-all filter itself stays for non-filesystem tools.

**Blocked by:** 03 — the containment check must be one thing before thirteen tools are rewritten around it.

**Status:** ready-for-agent

- [ ] All thirteen disk tools return the shared result type; none throws on an expected failure.
- [ ] Each error case maps to the envelope the base defines for it, and no code path invents a new code.
- [ ] The existing per-tool test files keep their cases, with envelope assertions replacing exception assertions.
- [ ] Each tool was rewritten behind its existing tests before its return type changed, in separate commits.
- [ ] The domain's duplicate search-output enumeration is deleted; one enumeration remains.
- [ ] Exception-type mapping arms specific to filesystem failures are removed.
- [ ] The catch-all filter and the rest of the mapping still serve non-filesystem tools, and the non-filesystem servers still build and pass.
- [ ] The integration tests over hosted vault, library and sandbox servers pass unchanged.
