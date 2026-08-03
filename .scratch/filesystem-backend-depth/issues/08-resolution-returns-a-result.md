# 08 — Path resolution returns a result

**What to build:** A path with no mount prefix costs the model one corrected call instead of a failed turn.

The system prompt promises that errors arrive as data rather than as exceptions, and it warns the model specifically about bare paths — writing `/notes/x.md` when the mount is `/vault`. That is the one case where the promise is false. Resolving a virtual path to a mount throws when nothing matches, and none of the twelve tool call sites catches it, so the most likely path mistake breaks the promise at every site at once.

Resolution returns a result instead of throwing. An unmounted path becomes an error envelope naming the path and listing the available mounts, exactly the information the current exception message carries, delivered as data.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] Resolution returns a result; no path input makes it throw.
- [x] An unmounted path yields an error envelope naming the path and the available mounts.
- [x] Every one of the ten tools the filesystem feature produces returns that envelope when given an unmounted path, asserted across all ten in the feature's existing test file.
- [x] No tool call site lets an exception escape for an unmounted path.
- [x] A mounted path resolves exactly as before, longest prefix first.
- [x] The tests were seen to fail before the change.
