# 01 — The move-out check crosses the seam, allowing by default

**What to build:** a mount can be asked whether a path may leave it, and a cross-mount move
asks before it streams. No mount refuses anything yet, so every existing move behaves exactly
as it does today — but the question now exists, travels across the MCP seam like every other
filesystem operation, and a backend that answers "no" stops the transfer before a single byte
is written.

The check joins the one filesystem operation list as `fs_move_out_check`, with no tool key and
no capability — like the two byte-streaming operations, it is transfer machinery rather than
something the model calls, so it appears in no mount's capability list and in no model-facing
tool set. It takes one mount-relative path. An ok payload means allowed; an error envelope
means refused and carries its code, message, hint and retryability through unchanged.

The backend contract gains the method and the backend base class implements it as allowed, so
a mount with no rule needs no code and registers no tool. This inverts what an override means
for this one operation — elsewhere an override declares "I can do this", here it declares "I
have something to refuse" — and that inversion belongs in a comment next to the operation.

The MCP proxy implements the method itself: it calls the wire tool when its client advertised
it, and answers allowed when it did not. Discovery already lists a client's tools once to
derive capabilities, so it hands the advertised names to the backend it constructs.

The transfer machinery asks the source once, on the move's source path, under the condition
that already tells it the source is to be deleted. For a directory move this runs before the
source listing, so a refused move does not even enumerate. No caller anywhere tests what kind
of backend it is holding.

See `.scratch/move-out-check/spec.md` and ADR-0015.

**Blocked by:** None — can start immediately.

**Status:** resolved

- [x] `fs_move_out_check` is the thirteenth entry in the one filesystem operation list, with a
      null tool key and null capability
- [x] The backend contract carries the check; the backend base class answers allowed
- [x] A backend that does not override it registers no tool and appears unchanged in its
      mount's capability list
- [x] A backend that does override it registers the tool, with its own description hook
- [x] The MCP proxy calls the wire tool when the client advertised it, and answers allowed
      when it did not
- [x] A cross-mount file move asks the source before streaming; a refusal is returned as the
      standard envelope and nothing is written to the destination
- [x] A cross-mount directory move asks the source before listing it; a refusal returns before
      any entry is enumerated or transferred
- [x] A cross-mount copy does not ask, and a same-mount move does not ask
- [x] Every existing cross-mount move and copy test passes unchanged
- [x] The server conformance tests cover the new operation's wiring and description hook
