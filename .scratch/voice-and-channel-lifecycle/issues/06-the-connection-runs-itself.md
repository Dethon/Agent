# 06 — The connection runs itself, and says what not connected means

**What to build:** A channel connection is started once and runs for its lifetime, so that
the order it must be driven in lives in the thing the order is about, and a caller can read
from the interface what each member does when there is no connection.

## The lifecycle

`Infrastructure/Clients/Channels/McpChannelConnection.cs` is 461 lines implementing
`IChannelConnection` and `IMcpChannelConnection`, neither of which describes any of its
lifecycle. The order it must be driven in lives in `Agent/App/ChannelConnectionHost.cs`:
connect, register the catalog, watch health every 30 seconds, reconnect, re-register
(`:29-49`), plus two near-identical 28-line retry loops at `:66-93` and `:95-122` that differ
only in which verb they call and one log string.

`RunAsync(endpoint, catalog, ct)` on `IMcpChannelConnection` owns that sequence and returns
when cancelled. `ChannelConnectionHost` keeps what is genuinely its own: reading the endpoint
map, deciding which connections have an endpoint, and starting one run each. The two retry
loops become one.

`ConnectAsync`, `ReconnectAsync`, `IsHealthyAsync` and `RegisterAgentsAsync` stop being part
of the interface a caller drives, since driving them in the right order is what `RunAsync`
now does. Whether they stay public on the concrete type is the implementer's call; the
interface should not advertise a sequence nobody outside is allowed to run.

`Domain/Contracts/IChannelConnection.cs` does not change. Domain sees a channel it can send
on, and that is right.

## The five states

Being **not connected** has five behaviours today:

- `SendReplyAsync` (`:288`) and `RequestApprovalAsync` (`:312`, `:334`) throw, via
  `EnsureConnected` at `:455-461`
- `CreateConversationAsync` (`:355-358`) returns null
- `RegisterAgentsAsync` (`:396-401`) returns silently
- `IsHealthyAsync` (`:427-430`) returns false
- `Messages` (`:54`) yields forever

**None of them changes.** At least one is load-bearing: `DeliveryTargetResolver.cs:51` and
`:91` read `CreateConversationAsync`'s null as "this channel mints nothing", which is also
how an attach-only channel and a channel without the tool are handled — no exception would
serve there. They get stated on the interface instead of discovered. See
`docs/adr/0011-not-connected-is-five-behaviours-and-stays-that-way.md`.

**Seam:** the real connection driven against a real in-memory MCP server, using the endpoint
exposed by ticket 05. `FakeMcpChannelConnection` in `ChannelConnectionHostTests` stays for
what the host still owns — which endpoints get a run — and shrinks as the retry loops leave
it. No test-only hook goes into production code.

Start red: write a test asserting that a run registers the agent catalog after connecting and
re-registers after a reconnect, driven through `RunAsync` against a real server. Watch it
fail, then build.

**Blocked by:** 05. Both edit the same connection type, and 05 lands the test seam this
ticket drives.

**Status:** ready-for-agent

- [ ] `IMcpChannelConnection` has `RunAsync(endpoint, catalog, ct)` owning connect, register, health, reconnect, re-register.
- [ ] The interface no longer advertises the individual lifecycle verbs as things a caller drives.
- [ ] `ChannelConnectionHost` has one retry loop, not two, and no ordering knowledge beyond starting a run per endpoint.
- [ ] The five not-connected behaviours are stated on the interface and none of them changed.
- [ ] `Domain/Contracts/IChannelConnection.cs` is unchanged.
- [ ] An integration test drives `RunAsync` against a real server and covers connect-then-register and reconnect-then-re-register.
- [ ] `ChannelConnectionHostTests` still passes, rewritten against `RunAsync` where it asserted ordering the host no longer owns.
- [ ] `CLAUDE.md`'s Channel Architecture section mentions that a connection runs itself, if the section would otherwise be wrong.
