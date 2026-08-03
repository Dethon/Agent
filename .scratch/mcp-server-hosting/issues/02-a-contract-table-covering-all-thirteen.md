# 02 — A server contract table covering all thirteen

**What to build:** Today one table drives the real registration entry point of the six
servers that are channel servers, and asserts each one's declared delivery policy. It
is the only thing in the repo that compares servers to each other, and it is the reason
the stale-subscriber defect cannot come back a fourth time.

Widen it to every MCP server in the repo. Each row drives that server's shipping
`ConfigModule` — never a hand-registered equivalent, which would stay green against a
module that forgot something — and asserts the things every server must have however it
is built: its settings resolve as a singleton, it registered a server and an HTTP
transport, and it has exactly one call-tool filter. Channel servers keep their policy
assertion on top.

This ticket adds no production code and should be green when it lands. That is the
point: it pins what thirteen hand-written copies currently do, so tickets 03 to 07 can
collapse them into one call and know immediately if any server came out different.

**Blocked by:** 01 — Rename the hosting project to Mcp.Hosting.

**Status:** ready-for-agent

- [ ] Every MCP server in the repo has a row, driving its real registration entry point.
- [ ] Each row asserts its settings resolve, its host is registered, and it has exactly
      one call-tool filter.
- [ ] The channel servers keep their existing delivery-policy assertion, unchanged.
- [ ] Deleting a server's host registration or adding a second filter fails this test.
      Prove it locally before finishing; a conformance test that cannot go red is
      decoration.
- [ ] The nine tool servers run without containers or network, the way the six existing
      rows already do — unreachable-but-well-formed connection details rather than live
      dependencies.
- [ ] Adding a server to the repo means adding one row here, and the test bodies are
      written once rather than per server.

## Notes

The nine tool servers construct clients eagerly in places. The existing six rows show
the technique for this: hand the module something well-formed that never dials out, and
keep timeouts short so nothing lingers in the background.

If a tool server turns out to be genuinely unable to register without a live dependency,
that is a finding worth writing down in the ticket comments rather than working around
quietly — it means that server cannot be started in a test at all today.
