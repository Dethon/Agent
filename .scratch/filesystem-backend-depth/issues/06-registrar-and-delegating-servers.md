# 06 — Registrar, and the four backend-delegating servers

**What to build:** A mount advertises exactly the operations its backend can really perform, and the timers mount stops lying.

Today a server registers its MCP tools by hand, one wrapper file per operation. Capability derivation reads the registered tool names, so a wrapper that exists for surface completeness makes the system prompt promise the model an operation that cannot work. Timers is the live case: it registers a move tool whose own description says the operation is unsupported, the prompt tells the model that `/timers` supports move, and the model burns a turn discovering otherwise.

A generic registrar takes a backend and registers an `fs_*` tool only where that backend overrode the operation. Capability stops being declared anywhere. Timers stops overriding move, and the move tool, its description and the advertised capability all disappear together — the lie becomes unrepresentable rather than corrected.

The four backend-delegating servers — timers, scheduling, Home Assistant, and printer's delegating tools — move onto the registrar and their wrapper files are deleted, one server per commit. Each operation's description now comes from the backend's hook, so the words the model reads must not change for any operation that survives.

Capability is per operation, not per path. A backend may override an operation and still refuse particular paths, and the registrar cannot and should not answer that. Assert it deliberately here so nobody later refines it into a per-path check.

The conformance test lands here, covering these four servers. It is the point of the whole feature: for each server, the advertised `fs_*` tool names, the operations the backend overrides, and the capabilities the mount publishes are the same set.

**Blocked by:** 01, 02 — the registrar reflects over overrides, so the backends must already be reparented for an override to mean anything.

**Status:** done

- [x] A registrar registers an `fs_*` tool for exactly the operations a given backend overrides.
- [x] Timers no longer overrides move; the tool, its description and the advertised capability are all gone.
- [x] Timers, scheduling, Home Assistant and printer's delegating tools register through the registrar, with their wrapper files deleted, one server per commit.
- [x] Descriptions come from the backend hooks and are unchanged for every surviving operation, including the per-server text that names real files.
- [x] A conformance test asserts, per server, that advertised tool names equal overridden operations equal published capabilities.
- [x] The timers move case was seen to fail that test before the override was removed.
- [x] A test pins that capability is per operation: a backend that overrides an operation and refuses some paths still advertises it.
- [x] The disk-backed servers are untouched and still register by hand.
