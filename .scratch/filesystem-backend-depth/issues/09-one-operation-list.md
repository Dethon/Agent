# 09 — One operation list

**What to build:** Adding a filesystem operation is one edit, and a mistyped tool name is caught rather than passed.

Six places enumerate the same twelve operations today: the backend contract's own signatures, the payload-type table, the capability map used when discovering a remote server's filesystem, the session's filter set of filesystem tool names, and the tool feature's key set and factory array. All six must be edited together for a new operation to work, and missing one leaves the operation half-existing.

They collapse into one list derived from the base. Payload validation also stops failing open: today an unknown tool name returns success without validating anything, so a typo in a name silently skips the check it was meant to perform.

**Blocked by:** 07 — the surface must be fully derived from backends before the lists that describe it can be replaced by one.

**Status:** ready-for-agent

- [ ] One list of operations is derived from the base; the other five enumerations are gone.
- [ ] Payload validation fails on an unknown tool name instead of returning success.
- [ ] Adding an operation to the base makes it appear in the registrar, the capability list, the session filter and the tool feature with no further edit, demonstrated by a test.
- [ ] Capability derivation for remote filesystem servers still maps advertised tool names to the leaf names the model calls, in the same display order.
- [ ] The session still filters raw filesystem tools out while the domain tools are active.
- [ ] All existing conformance and consistency tests pass unchanged.
