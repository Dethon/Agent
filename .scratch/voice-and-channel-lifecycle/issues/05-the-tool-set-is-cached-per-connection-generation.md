# 05 — The tool set is cached per connection generation

**What to build:** An agent-initiated turn stops paying a round trip per delivery target
asking a channel what tools it has, and a reconnect still sees a redeployed server's new
tools.

`McpChannelConnection` asks the far end what tools it offers before two of its calls, every
time: `CreateConversationAsync` runs `ListToolsAsync` at `:362` to check for
`create_conversation`, and `RegisterAgentsAsync` runs it again at `:403` to check for
`register_agents`. `Domain/Monitor/DeliveryTargetResolver.cs:51` and `:91` call
`CreateConversationAsync` per turn per target on agent-initiated turns, so a scheduled
announcement to three targets pays three round trips before any of them is asked to do
anything.

The answer cannot change inside one **connection generation** — one successful connect or
reconnect. Every server in this repo registers its tools during `ConfigModule` construction,
before the transport starts. So the tool set is fetched once per generation and the two
capability probes read the cached set; a reconnect discards it. See
`docs/adr/0012-a-servers-tool-set-is-fixed-for-a-connection-generation.md` for why the
generation is the unit rather than the process or a timer.

Nothing else about the two calls changes: an absent tool still means the same thing it means
today, and `CreateConversationAsync` still returns null for it.

## The seam, and the prefactor ticket 06 also needs

`Tests/Integration/McpServers/InMemoryMcpServer.cs` already boots a real MCP server over
loopback Kestrel and hands back a client, but not its endpoint URL. Expose the endpoint so a
test can point a real `McpChannelConnection` at a real server. Do it here, because 06 needs
it too and this ticket is the smaller of the two.

**No test-only hook goes into production code.**
`.scratch/agent-spec/issues/01-routing-resolver-and-delete-delegate.md` deleted a factory
delegate that existed only so tests could cut through a seam; this must not reintroduce the
pattern. Counting probes is done by watching a real server, not by injecting a fake client.

Start red: write a test asserting that two `CreateConversationAsync` calls on one connection
cause one `ListToolsAsync`, and that a reconnect causes another. Watch it fail, then build.

**Blocked by:** None — can start immediately. Touches no file the voice tickets touch.

**Status:** ready-for-agent

- [ ] `InMemoryMcpServer` exposes its endpoint URL.
- [ ] The tool set is fetched once per connection generation and read from cache by both capability probes.
- [ ] A reconnect discards the cached set.
- [ ] An integration test pins the probe count across two calls on one connection and across a reconnect.
- [ ] No new constructor parameter, delegate or virtual member exists on the connection solely for testing.
- [ ] A channel server that does not offer `create_conversation` still yields null, and `DeliveryTargetResolver`'s behaviour is unchanged.
