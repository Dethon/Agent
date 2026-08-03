# 06 — A dual-role server declares it has no outbound surface

**What to build:** The scheduling and library servers are **dual-role**: they offer the
agent tools and they can also raise something with the agent unprompted. But neither can
carry a reply back to a person — a schedule fires and a download finishes, and there is
nobody on the other end to speak to. Each expresses that today with two files whose entire
content is a protocol stub that accepts a reply chunk and drops it, or auto-approves.

Make it an argument instead. A server declares it has no **outbound surface** when it
becomes a channel server, and the channel-server call registers the two no-op protocol
tools itself. Four files delete.

Opt-in, deliberately. A default-unless-overridden rule would let a real channel that forgot
its reply tool silently drop every reply, and at registration time nothing can tell
"deliberately absent" from "forgotten".

**Blocked by:** 05 — The MCP host and the tool server.

**Status:** ready-for-agent

- [ ] The channel-server call takes an argument declaring the server has no outbound
      surface, and registers the reply and approval protocol tools when it is given.
- [ ] The four stub files in the scheduling and library servers are deleted.
- [ ] Both servers still advertise the protocol tools, and both still answer the same way
      they do now — a dropped chunk and an auto-approval. Assert what a caller sees over
      the wire.
- [ ] A channel server that does not pass the argument gets no stubs, so one that forgot
      its reply tool still fails rather than silently dropping replies.
- [ ] The four real channel servers are untouched.
- [ ] The contract table from ticket 02 gains an assertion that the two dual-role servers
      advertise the protocol tools, and stays green.

## Notes

Direction is named from the agent's side, per `CONTEXT.md`: a reply travelling agent →
channel → person is **outbound**. The stub descriptions being deleted say "no inbound
surface" for the same thing; that wording is the outlier and goes with them. Do not
reintroduce it.
