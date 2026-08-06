# 03 — The refusal is proven in a real two-mount topology

**What to build:** proof that the refusal fires in the topology production actually builds —
a media mount alongside another filesystem, both reached through the real MCP proxy.

Nothing exercises that today, which is exactly why the old guard could sit unreachable for
months without a test noticing. The multi-filesystem fixture already hosts two in-process
servers and already backs the cross-mount move and copy integration tests; it gains a third
host running the media library over a fake download client, so a download is live because the
fake says so and no containers are involved.

The test moves a live download's directory to the other mount through the real registry and the
real proxy, and asserts what the agent observes: a refusal naming the path, an empty
destination, and a download that is still live. A second case moves an ordinary media file the
same way and finds it arrives, so the check is not blanket-refusing the mount.

See `.scratch/move-out-check/spec.md`.

**Blocked by:** 02 — The media library refuses a move out of a live download, and the dead guard
is deleted.

**Status:** resolved

- [x] The multi-filesystem fixture hosts a media library mount alongside its existing ones, over
      a fake download client, with no new containers
- [x] A cross-mount move of a live download's directory through the real proxy is refused, and
      the refusal names the path
- [x] The destination mount holds nothing afterwards
- [x] The download is still live afterwards
- [x] A cross-mount move of an ordinary media file through the same topology succeeds
- [x] The existing cross-mount move and copy integration tests on that fixture still pass
