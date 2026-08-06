# 01 — send_reply takes the params record

**What to build:** the reply-sending member of the channel connection contract takes the
reply params record instead of six positional parameters. Nothing a user can observe changes:
the same chunks reach the same channels in the same order. This is the prefactor that makes
the next ticket a one-line addition instead of two new positional arguments threaded through
every implementer and fake.

The record is already the wire shape — every channel server deserializes it on the far side
and the dispatcher rebuilds it by hand on this side. The positional list is the only place
left that describes the same thing a second way.

**Blocked by:** None — can start immediately.

**Status:** ready-for-agent

- [x] The channel connection contract's reply-sending member takes the reply params record.
- [x] The reply dispatcher builds the record once and passes it, rather than unpacking it into
      positional arguments.
- [x] The MCP channel connection implementation and every test fake or mock of the contract are
      updated.
- [x] No behaviour change: the existing monitor, dispatcher and channel tests pass unmodified
      except where they name the signature.
- [x] `dotnet format` clean, and the pre-commit hook's whole-file re-staging respected.
