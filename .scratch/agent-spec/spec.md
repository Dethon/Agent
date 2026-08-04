# Spec — Agent Spec

Status: done

Grilled from candidate 7 of `.scratch/architecture-audit-2026-08-03/candidates.md`,
which holds the exact file and line evidence for every claim below. No ADR: both
decisions here are reversible and the spec records them adequately. Vocabulary
follows `CONTEXT.md`: an **agent definition** is the long-lived configured
description, an **agent spec** is everything needed to build one running agent
resolved at the moment it is built, and a **subagent** is an agent another agent
spawns for one task.

Three claims in the candidate file are wrong and are corrected here. See Further
Notes.

## Problem Statement

There are two copies of "how to build an agent". One builds a top-level agent from
an agent definition; the other builds a subagent from a subagent definition. They
are about forty-five lines each and roughly eighty per cent identical: metrics
publisher, chat client, tool-approval client, feature config, domain tools, domain
prompts, the agent itself.

Because the two copies differ only in which optional arguments they pass, every
real behavioural difference between an agent and a subagent is expressed as an
omission. An omission is invisible at the call site. You cannot read the subagent
path and see what a subagent does differently; you can only notice, by comparing
the two functions line by line, that one of them stops passing something.

That has already cost a defect. The subagent path builds a metrics publisher and
hands it to the chat client and the tool-approval client, but not to the agent. The
agent's own latency emission checks its publisher for null and returns early, so a
subagent publishes no first-token latency and no total-turn latency at all. Nothing
at either call site says so. The dashboard's per-agent latency breakdown has been
silently missing every subagent since the feature was written.

The agent constructor is the other half of the same problem. It takes eighteen
parameters, twelve of them optional with null defaults, so "forgot to pass the
publisher" and "deliberately has no publisher" are the same expression.

There is also a hole cut straight through the middle of the factory for tests: a
chat-client factory delegate that short-circuits real chat-client construction. Its
parameter list mirrors an internal method's, so it has to be edited whenever
construction changes, and the tests that use it stop one hop short of the wire. A
comment on the one test that does drive the real path spells out why the delegate
seam is the weaker of the two.

## Solution

One agent spec that both definitions project onto, and one build step that consumes
it.

Each entry point becomes a projection: it reads a definition, resolves the values
that depend on the conversation and the deployment, and produces an agent spec.
Every difference between an agent and a subagent becomes a field with a value, read
side by side at the two projection sites. "A subagent keeps no history" is a false.
"A subagent honours no config patch from the user" is an empty list. Nothing is
expressed by not passing something.

The build step takes an agent spec and assembles the whole stack from it: the
metrics publisher, the chat client, the tool-approval client, the feature config,
the domain tools and prompts, the history store and the agent. It never branches on
what kind of agent it is building, because the projection has already resolved
everything that differs.

The agent takes the spec instead of eighteen positional parameters. The missing
subagent publisher stops being possible to express.

The chat-client delegate is deleted rather than moved. Provider-routing resolution
becomes its own small piece with its own tests, which is what the delegate was
really being used to reach, and the one test that needs a real chat client keeps
driving it through a capturing transport handler.

## User Stories

1. As a developer reading the subagent build path, I want every difference from the agent build path to be a field with a value, so that I can see what a subagent does differently without diffing two functions.
2. As a developer reading either build path, I want it to be a projection followed by one call, so that I can hold the whole of it in my head at once.
3. As a developer adding a new field to an agent, I want one place to add it, so that I cannot add it to one path and forget the other.
4. As a developer adding a new difference between agents and subagents, I want to express it as a field rather than an omitted argument, so that the next reader is not left inferring intent from absence.
5. As a developer building an agent, I want the constructor to take a spec, so that a forgotten collaborator is a compile error rather than a silently disabled feature.
6. As a developer building an agent, I want the build step never to branch on what kind of agent it is, so that a future difference cannot hide inside a switch.
7. As a developer spawning a subagent, I want its history store chosen from a readable flag, so that "a subagent starts fresh every time" is a fact about the spec and not a choice made at a call site.
8. As a developer spawning a subagent, I want its fresh per-spawn routing session visible where it is computed, so that the reason a subagent does not share the parent's prompt cache is written down next to the thing that causes it.
9. As a developer who later wants a per-task subagent model, I want the model to be a projection input, so that I can set it at spawn time without touching the user-facing config-patch machinery.
10. As a developer writing a test for the build paths, I want to assert on a plain value, so that I do not have to construct a chat client, a service provider or an agent to check what gets built.
11. As a developer writing a test for provider routing, I want to call the resolver directly, so that I am asserting on the rule rather than on what a captured delegate saw.
12. As a developer changing chat-client construction, I want no delegate whose parameter list mirrors it, so that the change does not ripple into a test-only signature.
13. As a maintainer, I want the escape hatch through the middle of the factory deleted rather than renamed, so that the seam count goes down instead of sideways.
14. As a maintainer, I want the identity strings an agent reports under resolved in one place per path, so that the display name, the routing session id and the metrics id cannot drift apart.
15. As a maintainer, I want the agent constructor's parameter count to fall from eighteen to six, so that the next collaborator added to it is noticed in review.
16. As a maintainer, I want the two feature-config constructions collapsed into one, so that the conversation-context provider is wired identically for agents and subagents by construction.
17. As a maintainer, I want the subagent feature exclusion to be one readable line in the subagent projection, so that "a subagent cannot spawn subagents" is stated rather than filtered in passing.
18. As a maintainer, I want the spec to be a plain value carrying no live collaborators, so that it can be compared, logged and asserted on as data.
19. As a maintainer, I want the glossary to distinguish a definition from a spec, so that the next agent-shaped record has a precedent to follow.
20. As an operator, I want a subagent to publish its first-token and total-turn latency, so that a slow subagent is visible instead of hiding inside its parent's tool-execution span.
21. As an operator, I want a subagent's latency attributed to its own agent identity, so that I can compare subagents against each other and against the agent that spawned them.
22. As an operator, I want a subagent's latency attributed to the parent's conversation, so that I can find which conversation a slow subagent ran in.
23. As an operator, I want to know that latency events do not sum within a conversation, so that I do not read a double-counted total as a regression.
24. As a dashboard user, I want the per-agent latency breakdown to include subagents, so that the breakdown is not silently incomplete.
25. As a dashboard user, I want subagent token usage, tool calls and tool-execution latency to keep arriving exactly as they do now, so that nothing I already rely on changes.
26. As a user of the web chat, I want my model and effort override to keep applying to the agent I am talking to, so that this change is invisible to me.
27. As a user of the web chat, I want my override never to reach a subagent, so that a subagent chosen for a task keeps the model that task was configured with.
28. As a future reviewer, I want the reason a subagent honours no config patch written next to the empty whitelist, so that I do not "fix" it by passing the parent's list.
29. As a future reviewer, I want the routing advisories to keep naming the agent or subagent that tripped them, so that a config mistake stays as findable as it is today.
30. As a future reviewer, I want the existing wire test to keep proving that resolved routing and the session id reach the request body, so that extracting the resolver does not trade an end-to-end assertion for a unit one.
31. As a test author, I want the projection reachable from tests without making it public API, so that the seam does not widen the class's surface.
32. As a test author, I want a table-driven test over agent versus subagent, so that a new field that differs is one row rather than a new test.

## Implementation Decisions

**One agent spec record, in the infrastructure layer, next to the factory that
builds it.** The domain layer consumes none of it, and the projection needs the
OpenRouter configuration, which is an infrastructure type. It carries: the display
name, the description, the metrics agent identity, the routing session id, the
conversation id, the user id, the model, the maximum context tokens, the reasoning
effort, the resolved provider routing, the MCP server endpoints, the enabled
features, the whitelist patterns, the custom instructions, the language, whether it
keeps history, and the patchable model ids. All plain values; no live collaborators.

**Each entry point becomes a projection onto that spec, and the build step consumes
it.** The build step assembles the metrics publisher, the chat client, the
tool-approval client, the feature config, the domain tools and prompts, the history
store and the agent. It never branches on what kind of agent it is building.

**The three identity strings are pre-resolved by the projection.** A top-level agent
and a subagent format their display name, their routing session id and their
advisory identity differently, and those formats stay at the two projection sites
rather than becoming a kind flag the build step switches on. A kind flag is the
conditional shape this spec exists to remove, and every future difference would be
tempted into the same switch. The cost accepted is that three format strings live in
the projections rather than in one place.

**Provider routing is resolved during projection, so the spec carries resolved
routing.** This follows from the previous decision: the advisory identity is one of
the strings the projection already computes, so resolution belongs on the same side
of the line. The advisory identity is therefore a local at the projection site
rather than a field on the spec. Advisories keep firing per construction with no
dedupe, exactly as today.

**Whether an agent keeps history is a field, and the build step resolves the store
from it.** A top-level agent gets the registered thread state store; a subagent gets
the null store. Putting the store instance on the spec was considered and rejected:
it would make the spec carry a live collaborator and stop it being comparable,
loggable data.

**A subagent publishes its own turn latency.** The metrics publisher reaches the
agent on both paths. A subagent's latency events carry its own agent identity and
the parent's conversation id, matching the existing rule that a subagent acts on the
parent's behalf and that downstream state is scoped by the parent's conversation.
The alternative of publishing with no conversation id was rejected: it would keep
conversation rollups additive but make it impossible to ask which conversation a
slow subagent ran in.

**Latency events are not additive within a conversation.** The parent's total-turn
latency already contains the time its subagents spent, so summing latency across
agents within one conversation double-counts. The per-agent breakdown is unaffected
because the agent identities differ. This is a consequence accepted rather than a
problem to solve; nothing in the dashboard sums across agents today.

**A subagent's patchable model list is empty, and the reason is recorded next to
it.** A config patch names a model from the parent's whitelist and an effort chosen
for the parent's job; a subagent runs the model its own definition configures,
because that is the point of having one. No patch can reach a subagent today, since
the patch rides the top-level user message and the subagent tool builds a fresh
message that copies only the sender and the conversation context. The empty list is
the second line of defence: if a future change ever copies the parent's message
properties down, the patch is rejected and logged rather than silently overriding
the subagent's model.

**A future per-task subagent model is a projection input, not a patch.** Because the
spec is projected fresh at every spawn, letting the parent choose a subagent's model
for a particular task means passing an override into the projection. It needs none
of the patch machinery and stays distinct from the user's client-side override,
which is the thing that must not leak downward.

**The agent takes the spec plus its collaborators.** The spec, the chat client, the
history store, the time provider, the logger factory and the shared prompt cache.
Eighteen parameters become six. The agent reads the fields it needs and ignores the
build-time ones, the way any handler takes a request object. Projecting a second,
narrower turn-side record was considered and rejected: two records and a second
projection to keep in step, for a class with one production caller.

**The chat-client factory delegate is deleted.** No production path passes it; it
exists solely so tests can short-circuit chat-client construction. Provider-routing
resolution becomes its own small piece, which is what the routing tests were
actually reaching for, and the one test that needs a real chat client keeps using
the transport handler that already exists for that purpose.

**The two feature-config constructions collapse into one** inside the build step,
including the conversation-context provider. Nothing else from the wider
conversation-context item is claimed here.

**Nothing changes in configuration or in the domain contracts.** The agent factory
interface keeps its signature, both definition records keep their fields, and the
settings schema is untouched. The subagent feature exclusion stays where it is, as
one readable line in the subagent projection.

**Sequenced after the metrics publishing module.** That work deletes the agent's
safe-publish helper, makes publishing a void call that cannot fail, and replaces
both turn stopwatches with a latency scope. Both efforts rewrite the same lines
inside the agent. Running it first means this spec folds a smaller, already-correct
class into the spec, and it protects that effort's accounting of which test
construction sites it leaves untouched.

## Testing Decisions

A good test here asserts what a caller can observe: what the projection produces
from a definition, and what the resolver produces from a declared and a global
routing object. It does not assert that a particular collaborator was constructed,
and it does not reach through a delegate to watch arguments go past.

**Two seams, both replacing an existing weaker one.**

*The projection is the primary seam.* It is a pure function from a definition plus
the conversation and deployment values to an agent spec, so it can be asserted on
directly with no chat client, no service provider and no agent constructed. It is
made visible to tests through the existing internals-visible-to arrangement rather
than by widening public API. The test is a table over agent versus subagent covering
every field that differs: display name, routing session id, conversation id, metrics
identity, keeps-history, patchable model ids, enabled features and whitelist
patterns. A new difference becomes a row.

*The routing resolver is the second seam*, and it replaces the seven routing tests
that currently capture through the chat-client delegate. They become direct
assertions on the resolver: an agent's own routing wins wholesale over the global
default, an absent one inherits it, neither resolving to null, and an advisory
firing names the agent or subagent that tripped it. Prior art for what these must
keep proving is the existing routing test set and the provider-routing rule
document, including the reason "neither declared" must resolve to null rather than
to an empty object.

**The defect is pinned by composition, deliberately.** That a subagent publishes its
turn latency is proved by two cheap tests rather than one expensive one: the
projection test asserts the subagent's spec carries the metrics identity and the
parent's conversation id, and the existing agent latency test already proves that an
agent holding a publisher emits first-token and total-turn latency against a mocked
chat client. Building a real subagent through the factory and asserting on emitted
events was considered and rejected, because it needs a test-only transport handler
threaded through the factory, which is the hole this spec is closing.

**The existing wire test stays as it is.** It drives real chat-client construction
through a capturing transport handler and asserts that the resolved routing and the
session id reach the request body. Its comment explains why it exists, and
extracting the resolver makes that comment more true rather than less.

**Red first.** The first test written is the projection asserting that a subagent's
spec carries the metrics identity and the parent's conversation id. It fails against
today's code.

**Known churn.** Fifteen test construction sites build the agent directly and move to
the spec constructor. One test drops the chat-client delegate and lets a real chat
client be constructed, which does no I/O at construction.

## Out of Scope

The wider conversation-context item: the options key magic string, the ambient read,
the stamping and reading of the context around MCP tool calls, and the path where a
caller-supplied run-options object silently drops the conversation context. That is
its own candidate; only the duplicated feature-config line is claimed here.

Merging the two definition records into one at the configuration level. They differ
by a handful of fields and merging them would touch the settings schema, both
registry option types, the agent factory interface and the DTO tests, for no gain
once the projection exists.

Changing the agent factory interface. Its five subagent parameters are exactly the
projection's inputs.

The reply-tool module extraction, the shared-mutable-capture item and the
channel-connection lifecycle item, all noted separately in the audit.

Anything about how the dashboard renders latency. This spec adds events to an
existing stream in an existing shape; no consumer changes.

## Further Notes

**Three corrections to the candidate file.** First, subagents are not metrics-blind
today: token usage, context truncation, tool calls and tool-execution latency all
already flow under the subagent's own identity, because the publisher does reach the
chat client and the tool-approval client. Only the agent's own emissions are
missing. Second, of the three latency stages the candidate names as missing, one is
unreachable for a subagent regardless — the warmup stage is published from a method
whose only production caller is the chat monitor, and a subagent is run directly by
its tool — and one measures a null history store and so times nothing. The real gap
is first-token and total-turn latency. Third, the candidate's claim that the agent's
turn behaviour is covered only by LLM-gated integration tests is wrong: there are
unit tests covering its latency emission, its conversation-context handling and its
deserialisation, all against a mocked chat client. That last correction is what makes
the composition-based test strategy above possible.

**The config-patch claim in the candidate is vacuous as written.** A subagent does
not "silently reject every config patch", because no patch is ever built for one. The
rejection path exists but nothing reaches it. The empty list this spec keeps is
defence in depth, not a live behaviour.

**Cross-candidate contact.** The only file this shares with another live candidate is
the agent itself, shared with the metrics publishing module. Ordering is recorded
above. No other candidate in the batch touches the factory, the definition records or
the agent constructor.
