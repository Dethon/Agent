# 02 — Both build paths project onto an agent spec

**What to build:** The two copies of "how to build an agent" become one. Each entry
point turns its definition into an **agent spec**, and one build step assembles
everything from that spec. Every difference between a top-level agent and a
**subagent** becomes a field with a value, readable side by side at the two
projection sites.

This fixes the live defect. Today the subagent path builds a metrics publisher, hands
it to the chat client and the tool-approval client, and does not hand it to the agent,
so a subagent publishes no first-token and no total-turn latency. After this ticket a
subagent publishes both, under its own agent identity and the parent's conversation
id. Its token, tool-call and tool-execution events keep arriving exactly as they do
now.

Start red: write the projection test asserting a subagent's spec carries its metrics
identity and the parent's conversation id, watch it fail, then build.

The agent spec is a plain value carrying no live collaborators. It holds the display
name, the metrics agent identity, the routing session id, the conversation id, the
user id, the model, the maximum context tokens, the reasoning effort, the resolved
provider routing, the description, the MCP server endpoints, the enabled features,
the whitelist patterns, the custom instructions, the language, whether it keeps
history, and the patchable model ids. It lives beside the factory in the
infrastructure layer, because the domain layer consumes none of it and the projection
needs the OpenRouter configuration.

The projection resolves everything that differs, so the build step never branches on
what kind of agent it is building. That includes the three identity strings, which are
formatted differently for an agent and a subagent, and the provider routing, which is
resolved by calling the resolver from ticket 01.

Two differences become readable fields rather than omissions. A subagent keeps no
history, so the build step gives it the null history store instead of the registered
one. A subagent's patchable model list is empty, and the reason goes next to it: a
config patch names the parent's model and an effort chosen for the parent's job, and
a subagent runs the model its own definition configures. No patch reaches a subagent
today, so this is defence in depth against a future change that copies the parent's
message properties down.

The subagent's fresh per-spawn routing session id keeps its meaning and gains the
comment saying why: a subagent does not share the parent's prompt cache.

The agent constructor is not touched here. The build step assembles its arguments.

**Blocked by:** 01, and the metrics publishing module effort
(`.scratch/metrics-publishing-module/`) landing first — both rewrite the same lines
inside the agent.

- [ ] A red test exists first: the projection of a subagent definition carries the metrics identity and the parent's conversation id. It fails against today's code.
- [ ] An agent spec record exists in the infrastructure layer, holding only plain values.
- [ ] Each entry point is a projection onto the spec followed by one build call.
- [ ] One build step assembles the metrics publisher, the chat client, the tool-approval client, the feature config, the domain tools and prompts, the history store and the agent, and never branches on what kind of agent it is building.
- [ ] A subagent publishes first-token and total-turn latency, under its own agent identity and the parent's conversation id.
- [ ] A subagent's spec says it keeps no history, and the build step resolves the null history store from that field.
- [ ] A subagent's spec carries an empty patchable model list, with the reason recorded beside it.
- [ ] A subagent's spec still excludes the subagent-spawning feature, stated as one readable line in its projection.
- [ ] The two feature-config constructions are collapsed into one, including the conversation-context provider.
- [ ] A table-driven test covers every field that differs between an agent's spec and a subagent's: display name, routing session id, conversation id, metrics identity, keeps-history, patchable model ids, enabled features and whitelist patterns.
- [ ] The projection is reachable from tests through the existing internals-visible-to arrangement, not by widening public API.
- [ ] The agent factory interface, both definition records and the settings schema are unchanged.
- [ ] Everything the existing tests already assert about agent construction, routing and conversation-context wiring still passes.

**Status:** ready-for-agent
