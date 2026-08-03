# 06 — One feature gate

**What to build:** Whether an agent has a feature enabled is answered in one place, with
its fail-open rule written down once instead of implied twice.

The same eight lines are copied into the recall hook and the extraction worker. Both are
fail-open in two ways that neither states: an agent id that is absent and an agent id that
resolves to no definition each leave the feature enabled. A reader of either copy has to
work that out from the shape of the condition.

Replace both with one extension on the agent definition provider that answers whether a
given agent has a named feature enabled. Parameterise it by feature name rather than
hard-coding memory: the name is already a literal at both call sites, so parameterising
costs nothing and the next feature that needs gating does not add a second helper. The
comparison stays case-insensitive, as both copies do today.

State the fail-open policy once, where the extension lives, and cover it: a null agent id
is enabled, an unknown agent id is enabled, a known agent without the feature is disabled,
a known agent with it is enabled.

The two existing service-level tests that assert recall and extraction skip their work for
an agent without the feature stay exactly as they are. They test behaviour at the service,
which is still the thing that matters, and they are the regression check that this move
changed nothing.

This ticket edits the recall hook, contested with the metrics publishing effort, which
must have landed first.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [ ] One extension on the agent definition provider answers whether an agent has a named feature enabled.
- [ ] It takes the feature name as an argument and compares case-insensitively.
- [ ] The recall hook and the extraction worker both use it, and neither contains the inline check.
- [ ] The fail-open policy is stated once alongside the extension.
- [ ] Tests cover null agent id, unknown agent id, known agent without the feature, and known agent with it.
- [ ] The existing recall and extraction feature-gate tests pass unchanged.
