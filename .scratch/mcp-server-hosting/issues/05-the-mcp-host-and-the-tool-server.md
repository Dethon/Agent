# 05 — The MCP host and the tool server

**What to build:** Every MCP server in the repo starts the same three lines: register its
settings, start a server, add an HTTP transport. Thirteen copies. Nine of them then add
their own twelve-line call-tool filter, and seven of those nine are missing the
cancellation rule the channel servers treat as load-bearing.

Give the repo the two calls it has been writing by hand. One is the **MCP host** — the
three things every server has — and all thirteen use it. The other is the **tool server**:
the MCP host plus the error filter, for the nine servers that offer the agent things to
call. A dual-role server asks for the tool server and then the channel server, and the
shared registration from ticket 03 means it still ends up with one filter.

The visible change is for the agent: a cancelled call to a tool server stops coming back
as an error result it might retry. Seven servers gain that rule; none loses anything.

**Blocked by:** 03 — One call-tool error filter; 04 — One answer for settings.

**Status:** ready-for-agent

- [ ] Two calls in the hosting project: one registering the settings singleton, the server
      and the HTTP transport, and one that is the first plus the error filter. Both return
      the builder so each server's own chain continues unchanged.
- [ ] All thirteen servers use the host call. The nine tool servers use the tool-server
      call; the four channel servers use the host call and then the channel-server call.
- [ ] The two dual-role servers use the tool-server call and then the channel-server call,
      and end up with exactly one filter.
- [ ] The seven hand-written filter lambdas are deleted.
- [ ] A cancelled tool call on a tool server does not become an error result, and any other
      exception still does. Assert this over the wire on a real in-memory server rather
      than by counting registrations — the existing channel-server tests are the model.
- [ ] Each `ConfigModule` keeps its own signature, its own dependency registrations and its
      own tail of the builder chain. Only the lines every other server also had are gone.
- [ ] `Program.cs` is untouched beyond what ticket 04 already changed.
- [ ] `.claude/rules/mcp-tools.md` is corrected in this change. Its error-handling section
      currently instructs non-channel servers to register the filter in their own
      `ConfigModule`, which this ticket makes false. Nobody hand-writes a call-tool filter,
      the same way nobody hand-writes a filesystem tool.
- [ ] The contract table from ticket 02 stays green for all thirteen.

## Notes

Being a tool server and being a channel server are independent facts about a server, which
is why they are two calls rather than one call with a flag. The dual-role servers are the
proof: they are genuinely both.

This ticket and ticket 04 both touch all thirteen `ConfigModule` files — 04 the settings
method, this one the builder chain. They are sequenced rather than parallel for that
reason. Do not start this one until 04 has landed.
