# Spec — MCP server hosting

Status: ready-for-agent

Grilled from candidate 6 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the file and line evidence for every claim below. The settings-precedence
decision is recorded as `docs/adr/0005-user-secrets-outrank-environment-variables.md`.
Vocabulary follows the "MCP server hosting" section of `CONTEXT.md` — **MCP host**,
**tool server**, **channel server**, **dual-role server**, **outbound surface**,
**mount identity**.

Touches no file any other candidate in the batch touches, so it can run at any point.

## Problem Statement

There are thirteen MCP servers in this repo and every one of them starts the same
way: read settings from environment variables and user secrets, register the settings
object, start a server, add an HTTP transport. Thirteen hand-written copies of the
same eight-line block, plus thirteen more of the same three-line prologue.

The copies have already drifted apart in ways that matter, and the drift is invisible
because nothing compares them.

**Two of them disagree about how configuration binds.** `McpServerWebSearch` re-binds
its two optional nested sections by hand with the comment "Bind nested sections
explicitly for environment variable support". `McpServerHomeAssistant` has a
structurally identical optional nested section and binds it with the plain call. One
of them is wrong and nothing decides which. (It is WebSearch: a probe with the exact
record shapes shows the plain call binds `CAPSOLVER__APIKEY` and
`CAMOUFOX__WSENDPOINT` identically. The extra code has been dead since the commit that
added it.)

**All thirteen carry a guard that has never fired.** Each ends with
`?? throw new InvalidOperationException("Settings not found")`. The binder returns a
non-null instance from completely empty configuration, so that branch is unreachable.
A genuinely missing section — no `HomeAssistant` block at all — comes out as a null
sub-record and surfaces later as a `NullReferenceException` from wherever the value is
first read, with nothing in the message naming the missing key.

**Five of them read a config source that does not exist.** Scheduling, Printer,
Timers, Vault and Sandbox all call `AddUserSecrets<Program>()` but have no
`UserSecretsId` in their project file, so the call is a silent no-op. Nothing says so;
a developer adding a secret for one of those five would watch it be ignored.

**The order of two lines is load-bearing and written down nowhere.** The copied block
adds environment variables and then user secrets, so user secrets win — the reverse of
the framework default. `DockerCompose/.env` ships every secret as an empty placeholder
and compose exports an empty value as an empty string, so the reversal is what stops a
blank placeholder from overwriting a real key. Reverse it and CapSolver, web push and
the Music Assistant action switch themselves off while every server still reports
healthy.

Error handling has the same shape. Seven servers each carry their own twelve-line
call-tool filter lambda; `.claude/rules/mcp-tools.md` has promoted the duplication to a
documented convention rather than a module. All seven are missing the one rule
`AddChannelServer` states as load-bearing: a cancelled call must propagate as the abort
it is, not become an error result the caller can retry on.

And a mount's identity is written three times per server — in the backend, in the
resource address, and in the JSON body's name and mount point. Seven copies of a
three-way agreement that nothing checks. One server already has a hand-rolled fix for
exactly this, which tells you the problem was felt and solved once, locally.

Only one of the nine tool servers has any test of its registration at all.

## Solution

Being an MCP server becomes one call, the way being a channel server already is.

`Channels.Hosting` is renamed to **`Mcp.Hosting`** and gains three things next to
`AddChannelServer`:

- **`BindSettings<TSettings>`** — the one place any server reads its configuration.
  One documented answer on nested sections, one on user secrets, one on precedence.
  It also replaces the dead guard with a real one: a required section that bound to
  nothing fails at startup with a message naming it, instead of an unrelated
  `NullReferenceException` three frames later.
- **`AddMcpHost`** — the settings singleton, the server and the HTTP transport. All
  thirteen use it.
- **`AddToolServer`** — the MCP host plus the call-tool error filter. The nine tool
  servers use it.

The filter itself becomes one shared registration that both `AddToolServer` and
`AddChannelServer` ask for, installed at most once. A dual-role server can ask twice
and still get one. The cancellation rule that only channel servers had now holds for
every MCP server in the repo.

For a filesystem server, **`AddFileSystemResource`** joins `AddFileSystemTools` as the
second half of the same idea. The backend already declares which operations it
supports by overriding them; now it declares its mount identity the same way, and the
resource address, the mount point and the published name are all derived from the one
name. They cannot disagree, so there is nothing to keep in sync. Seven resource
classes delete and their prose moves next to the other descriptions the same backend
already writes.

The two dual-role servers stop hand-writing the four protocol stubs that exist only to
say "this server has no outbound surface". They say it as an argument instead.

What a reader gains: opening any `ConfigModule` shows that server's own dependencies
and nothing else. What the next server gains: the questions this spec answers are
answered once, in a place it cannot avoid.

## User Stories

1. As an operator, I want a server that is missing a required configuration section to
   fail at startup naming that section, so that I fix the config instead of reading a
   null-reference stack trace.
2. As an operator, I want a real secret in my user-secrets file to survive an empty
   placeholder in `.env`, so that a fresh checkout of the compose stack does not
   silently disable features I configured.
3. As an operator, I want every server to answer the same way about where its settings
   come from, so that I do not have to read nine files to learn where to put a value.
4. As an operator, I want a blank optional secret to keep meaning "this feature is
   off", so that a deployment without a CAPTCHA solver or push keys still starts.
5. As an operator, I want the compose `.env` placeholders to stay the checklist of what
   a deployment needs, so that removing a line is not how I turn something on.
6. As an agent, I want a tool that throws to come back as an error result on every
   server, so that one server's failure does not read differently from another's.
7. As an agent, I want a call I cancelled to come back as an abort rather than an error
   result, so that my pump does not retry work I deliberately stopped.
8. As an agent, I want every filesystem mount's published name to match the address I
   discovered it at, so that a mount I found is a mount I can address.
9. As an agent, I want a mount's description to describe the mount I actually got, so
   that the operations I am told about are the ones that exist.
10. As a developer adding a new tool server, I want one call that makes it a server, so
    that I copy nothing and cannot omit half of it.
11. As a developer adding a new tool server, I want the settings question answered for
    me, so that I do not have to decide whether nested sections need explicit binding.
12. As a developer adding a new tool server, I want error handling to arrive with the
    host, so that I never write a call-tool filter by hand.
13. As a developer adding a new filesystem server, I want the resource to come from the
    backend, so that I cannot publish a name that disagrees with the mount.
14. As a developer, I want a mount's prose to sit beside the rest of that backend's
    descriptions, so that everything the model reads about a mount is in one file.
15. As a developer, I want a dual-role server to declare that it has no outbound
    surface, so that "we drop replies" is a stated fact rather than two stub files.
16. As a developer, I want a real channel server that forgets its reply tool to fail
    rather than silently drop every reply, so that the stub default cannot hide a bug.
17. As a developer, I want a dual-role server that asks for the error filter twice to
    get one, so that composing the two calls cannot double-wrap the cancellation rule.
18. As a developer, I want the settings-precedence decision written down, so that I do
    not "fix" it toward the framework default and lose three features.
19. As a developer, I want to know that five servers do not read user secrets, so that
    I do not spend an afternoon on a secret that was never going to load.
20. As a developer, I want the dead "Settings not found" guard gone, so that I do not
    believe a failure mode that cannot happen.
21. As a developer, I want the redundant explicit nested binding gone, so that two
    files stop disagreeing about how configuration works.
22. As a developer, I want each `ConfigModule` to hold only its own dependencies, so
    that reading one tells me what that server actually needs.
23. As a developer, I want one test that covers registration for all thirteen servers,
    so that a server that half-registers fails a test rather than a deployment.
24. As a developer, I want a new server to fail that test until it is wired correctly,
    so that the table is a checklist rather than documentation.
25. As a developer, I want the mount-identity agreement asserted for all seven
    filesystem servers, so that the three-way copy cannot come back.
26. As a developer, I want the error filter's behaviour proved over the wire rather
    than by counting registrations, so that the test describes what a caller sees.
27. As a developer, I want `BindSettings` testable without booting a server, so that
    precedence and validation are cheap to assert.
28. As a developer reading `Mcp.Hosting`, I want the tool-server and channel-server
    calls side by side, so that the difference between the two kinds is visible in one
    place.
29. As a developer, I want the hosting project to stay free of Infrastructure, so that
    the two Domain-only channel servers do not inherit a browser automation library and
    the agent stack.
30. As a developer new to the repo, I want "tool server", "channel server" and
    "dual-role server" to be written-down terms, so that I do not invent three more
    names for the same three things.

## Implementation Decisions

### The project

`Channels.Hosting` is renamed to `Mcp.Hosting`, referenced by all thirteen servers
rather than the current six. The rename is what makes "the twin of `AddChannelServer`"
literal: both calls live in one file each, side by side, and a reader comparing them
sees exactly what a channel adds.

The existing dependency rule is unchanged and still enforced by test: Domain plus the
MCP server package, never Infrastructure. `BindSettings` adds the configuration binder,
environment-variables and user-secrets packages, which are Domain-safe. `ToolResponse`
lives in Infrastructure and stays out, reached through the `errorResult` parameter
`AddChannelServer` already takes and `AddToolServer` now takes too.

### Binding settings

One call replaces thirteen copies. It reads environment variables and then user
secrets, in that order, so **user secrets win** — see ADR-0005 for why, and do not
change it.

The user-secrets source is optional and keyed off the entry assembly, not a `Program`
type, because from a shared project `typeof(Program)` would resolve to the wrong
assembly. Five servers have no `UserSecretsId` and the source is simply absent for
them, which is exactly today's behaviour.

Nested sections bind through the plain call. `McpServerWebSearch`'s explicit re-bind of
its two optional sections is deleted; a probe with those exact record shapes confirmed
it changes nothing.

Validation replaces the dead null guard. Members carrying the `required` modifier are
walked recursively — the modifier emits an attribute reflection can read — and any that
bound to null fails startup with one message naming the member. **Null only, never
empty.** `McpChannelServiceBus`'s connection string, Telegram's bot tokens and
WebSearch's Brave key all ship as `""` in `appsettings.json`, and an empty CapSolver key
is how that feature is switched off; an empty-is-invalid rule would refuse to start
three shipped servers.

### The host and the tool server

`AddMcpHost(settings)` registers the settings as a singleton, starts the server and
adds the HTTP transport, returning the MCP server builder so the chain continues
unchanged. All thirteen servers use it.

`AddToolServer(settings)` is `AddMcpHost` plus the call-tool error filter. The nine
tool servers use it. Being a tool server and being a channel server are independent, so
the two dual-role servers call `AddToolServer` and then `AddChannelServer`.

`Program.cs` is unchanged apart from the `GetSettings` line becoming `BindSettings`.
The remaining six lines are framework ceremony — create, build, map, run — with no
policy in them and no way to drift into a bug. Collapsing them would hide where the
host is configured for no gain.

### One error filter

The filter moves out of both `AddToolServer` and `AddChannelServer` into a single
registration that installs at most once, guarded by a marker so a second request is a
no-op. Double-wrapping stops being expressible.

The rule it states, now for every MCP server rather than only the channel ones: a
cancelled call propagates as the abort it is; anything else is logged and becomes the
caller's error result. Seven tool servers gain the cancellation carve-out they never
had, so a cancelled `fs_exec` or web fetch no longer arrives as something to retry.

The first registration wins, which is the tool-server one on both dual-role servers.
Both pass the same error shape today, so behaviour is unchanged; the ticket should
still assert it rather than assume it.

`.claude/rules/mcp-tools.md` says non-channel servers register the filter in their own
`ConfigModule`. That line is now wrong and is rewritten: nobody writes a call-tool
filter by hand, the same way nobody writes an `fs_*` tool by hand.

### Outbound surface

`AddChannelServer` takes an argument declaring that the server has no outbound surface,
and registers the two no-op protocol tools itself. The four stub files in Scheduling and
Library delete.

Opt-in, not defaulted. A default-unless-overridden rule would let a real channel that
forgot its reply tool silently drop every reply, and at registration time nothing can
tell "deliberately absent" from "forgotten".

### Mount identity

`AddFileSystemResource<TBackend>()` is the twin of `AddFileSystemTools<TBackend>()` and
sits beside it. It registers the `filesystem://` resource from the backend, deriving the
resource address, the published name and the mount point from the backend's one name.
All seven mounts already satisfy that relationship. A mismatch stops being
representable, which is the same guarantee the tool registrar gives for capabilities.

The description moves onto the backend as a `DescribeMount` hook alongside
`DescribeRead`, `DescribeGlob` and the rest, so everything the model reads about a mount
is in one file. All seven descriptions can move: Vault and Library backends already hold
their path config, Printer holds its supported formats, and Scheduling's time zone
becomes a read off its injected `TimeProvider` instead of a static call — strictly more
correct, since that is the zone the engine actually computes in.

`DescribeMount` is abstract on the backend base and satisfied by a constructor argument
on the generic disk root, exactly as the mount name already is. Otherwise "Obsidian
vault" would be hardcoded into a type that is meant to be reusable.

Seven resource classes delete. `MediaFilesystem` keeps only its download-directory
helper; its name and mount-point constants were the hand-rolled fix for this problem on
one server and are no longer needed.

### What is not changed

Each `ConfigModule` keeps its signature, keeps its own DI registrations and keeps its
own tail of the builder chain — its tools, its prompts, its filesystem. The only lines
that leave are the ones every other server also had.

## Testing Decisions

A good test here asserts what a caller sees: a server that came up with the settings it
needs, a tool call that came back as an error result, a mount whose published name
matches the address it was discovered at. Not that a registration count is three, and
not that a private helper was called. The drift this spec removes was invisible
precisely because nothing compared the servers to each other, so the tests are
comparisons across servers rather than assertions about one.

### Seams

Three, two of which already exist.

**The real `ConfigModule` into a service collection, one row per server, all thirteen.**
This is the seam `ChannelReceiveContractTests` already uses for six servers, driving each
server's shipping registration entry point rather than hand-registering — which is the
whole point, since a hand-registered equivalent stays green against a module that forgot
something. Extending its table to thirteen makes it the one place that answers "what kind
of MCP server is this, and did it get the host it needs". Adding a server means adding a
row.

**The in-memory MCP server for filter behaviour.** `ChannelServerExtensionsTests` already
boots a real server and calls a tool over the wire to prove that a cancelled call does not
become an error result while any other exception does. The shared filter's behaviour and
the dual-role once-only case belong there, on the fixture that exists.

**A configuration builder for `BindSettings`.** New, and unavoidable: both other seams
start from an already-bound settings object, so precedence, nested binding and validation
are unreachable from either. It is a pure function over config sources, so the seam is
small.

Deliberately not proposed: connecting an MCP client to all thirteen real servers. The
registration seam plus the shared-call seam gets the same coverage without constructing a
browser automation stack, an IPP client and a Home Assistant client thirteen times.

### What gets tested

Through the registration table, for every server: its settings resolve as a singleton, it
has exactly one call-tool filter, and it registered the host. For the two dual-role
servers, that the protocol stubs are present and that asking for the filter twice yields
one. For the seven filesystem servers, that the resource address, the published name and
the mount point all agree with the backend's name — this is the assertion that folds into
`FileSystemServerConformanceTests`, which already enumerates those seven backends and
drives the tool registrar through a service collection.

Through the in-memory server: a throwing tool becomes an error result, a cancelled call
does not, and a caller's own error shape is honoured. The first two already exist for
channel servers and now cover tool servers too.

Through the configuration builder: a user secret beats an environment variable with the
same key — the ADR-0005 assertion, and the one most likely to be broken by someone tidying
the order. A nested optional section binds from an environment variable without explicit
re-binding. A missing required section fails with a message naming it. An empty string on
a required member does not fail. An entry assembly with no user-secrets id does not throw.

### Prior art

`Tests/Integration/Channels/ChannelReceiveContractTests.cs` for the per-server table,
including how it stands up unreachable Redis and a well-formed but fake broker connection
string so a registration theory runs without containers. The nine tool servers need the
same treatment for their own eager constructions.

`Tests/Integration/Channels/ChannelServerExtensionsTests.cs` for the in-memory server and
for the assembly-reference test, which must keep passing under the new name.

`Tests/Unit/Infrastructure/FileSystemServerConformanceTests.cs` for the shape of a
cross-server conformance assertion, and `Tests/Unit/McpServerTimers/ConfigModuleTests.cs`
as the one existing example of a tool server's registration under test.

Follow red-green-refactor.

## Out of Scope

The `Program.cs` files. Six lines of framework ceremony each, left alone.

Adding a `UserSecretsId` to the five servers that lack one. `BindSettings` preserves
today's behaviour exactly, which is that those five do not read user secrets. Turning that
on for a server is a deployment decision, made per server, not a side effect of this
change.

Everything a `ConfigModule` registers for itself. Home Assistant's conditional Music
Assistant client, Library's download watcher, Printer's spool and queue coordinator,
WebSearch's browser and solver — all untouched. This spec removes only the lines every
server had.

The filesystem tool registrar. `AddFileSystemTools` is unchanged; `AddFileSystemResource`
is added beside it.

The `.env` placeholder convention and the compose secrets mounts. ADR-0005 records why
they interact with binding order the way they do; nothing about them changes.

The `SendReplyTool` module extraction noted separately in the audit, the alert-routing
prompt duplication, and the record-directory filesystem base shared by timers and
schedules. Different candidates.

## Further Notes

The candidate framed this as thirteen copies of a settings block and seven of a filter.
Grilling found the settings block to be thirteen copies of a **wrong** answer in one case,
a **dead** answer in another, a **no-op** in five, and a **load-bearing and unrecorded**
answer in the ordering. The deduplication is the smaller half of the value; the larger half
is that four questions nobody had answered now have one answer each, in one place.

It also found the prologue to be thirteen copies rather than nine — every channel server
has it too — which is why `AddMcpHost` exists separately from `AddToolServer`.

Three claims in the candidate were checked against the code and did not survive as stated:
the WebSearch explicit nested binding is not a disagreement to resolve but redundant code
to delete; the "Settings not found" throw is unreachable; and `AddUserSecrets<Program>()`
without a `UserSecretsId` does not fail, it does nothing. The tickets should not restate
them as open questions.

`.claude/rules/mcp-tools.md` needs editing in the same change. Its error-handling section
currently instructs the opposite of what this spec establishes.
