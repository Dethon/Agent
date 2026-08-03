# 07 — The four disk-backed servers

**What to build:** Every filesystem server registers the same way, and the wrapper files are gone.

The vault, sandbox and library servers, and printer's blob tools, move onto the disk-backed filesystem and register through the registrar. Their wrapper files are deleted, one server per commit. The library keeps its downloads overlay, so its downloads view is unchanged.

These four could not migrate with the others because they had no backend object to register from until the disk-backed filesystem existed.

The conformance test extends to every filesystem server. After this ticket, no server registers a filesystem tool by hand, and no server can advertise an operation its backend does not implement.

**Blocked by:** 05, 06 — the backend they register from, and the registrar they register through.

**Status:** done

- [x] Vault, sandbox, library and printer's blob tools register through the registrar, one server per commit.
- [x] All remaining filesystem wrapper files are deleted; roughly sixty-four in total across this ticket and 06.
- [x] The library's downloads view behaves exactly as before.
- [x] The conformance test covers every filesystem server, and every one passes.
- [x] The tool names and descriptions each server advertises are unchanged for every surviving operation.
- [x] The integration tests over hosted vault, library and sandbox servers pass unchanged.
