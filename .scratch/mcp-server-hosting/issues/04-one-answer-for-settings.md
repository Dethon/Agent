# 04 — One answer for settings

**What to build:** Every MCP server reads its configuration with the same eight-line
block, copied thirteen times, and the copies disagree. One of them re-binds its nested
sections by hand; the rest do not. All thirteen end with a guard that cannot fire. Five
of them read a config source their project does not have. And the order of two lines is
load-bearing in a way nothing records.

Replace all thirteen with one call that gives each question one answer.

An operator gets the visible change: a server missing a required section fails at startup
with a message naming that section, instead of coming up far enough to throw a null
reference from wherever the value is first read. And a real secret in the mounted
user-secrets directory keeps beating the empty placeholder that ships in the compose
environment file, which is what stops a fresh deployment from quietly switching off the
CAPTCHA solver, web push and the Music Assistant action.

**Blocked by:** 01 — Rename the hosting project to Mcp.Hosting; 02 — A server contract
table covering all thirteen.

**Status:** ready-for-agent

- [ ] One call in the hosting project binds settings, and all thirteen servers use it.
      Every hand-written copy is gone.
- [ ] Environment variables are added first and user secrets last, so **user secrets win**.
      This is deliberate and recorded in `docs/adr/0005-user-secrets-outrank-environment-variables.md`
      — read it before touching the order. Assert it: a user secret and an environment
      variable with the same key, and the secret is what binds.
- [ ] The user-secrets source is optional and keyed off the entry assembly, not a `Program`
      type. From a shared project `typeof(Program)` resolves to the wrong assembly.
- [ ] An entry assembly with no user-secrets id does not throw. Five servers are in exactly
      that state today and must keep starting.
- [ ] A nested optional section binds from an environment variable through the plain call.
      The explicit re-bind in the web-search server is deleted — it was verified during
      grilling to change nothing.
- [ ] A member carrying the `required` modifier that bound to null fails startup with one
      message naming the member. The modifier emits an attribute reflection can read.
- [ ] Validation rejects **null only, never empty**. Three shipped servers carry required
      members that ship as empty strings and are filled from secrets, and an empty optional
      key is how a feature is switched off. An empty-is-invalid rule would refuse to start
      them — assert that an empty required member passes.
- [ ] The unreachable `?? throw new InvalidOperationException("Settings not found")` guard
      is gone from all thirteen. The binder returns a non-null instance from empty
      configuration, so that branch has never run.
- [ ] The contract table from ticket 02 stays green for all thirteen.

## Notes

Test this over a configuration builder, not by booting a server. Precedence, nested binding
and validation are all unreachable from a seam that starts with an already-bound settings
object, and this is the only part of the spec that needs its own seam.

Do not add a `UserSecretsId` to the five servers that lack one. Preserving today's behaviour
exactly is the goal here; turning a new config source on for a server is a separate,
per-server decision.
