# 07 — Mount identity comes from the backend

**What to build:** A filesystem backend already declares which operations it supports by
overriding them, and the tool registrar reads that — so a server cannot advertise an
operation its backend does not implement. Its **mount identity** works the opposite way:
the name is written three times per server, in the backend, in the resource address, and
in the resource body's name and mount point. Seven copies of a three-way agreement that
nothing checks. One server already carries a hand-rolled fix for this, which is the tell.

Give the resource the same treatment the tools already have. A call beside the existing
tool registrar registers the `filesystem://` resource from the backend and derives the
address, the published name and the mount point from the backend's one name. All seven
mounts already satisfy that relationship, so a mismatch stops being representable.

The description moves onto the backend as a hook beside the ones it already writes for
read, glob, search and the rest — so everything the model reads about a mount is in one
file. Seven resource classes delete.

For the agent, the guarantee is that a mount it discovered at an address is a mount it can
address, and that the description it reads describes the mount it actually got.

**Blocked by:** 05 — The MCP host and the tool server; 06 — A dual-role server declares it
has no outbound surface. **The edge on 06 is file contention, not dependency** — both
dual-role servers are also filesystem servers, so 06 and this ticket edit the same two
`ConfigModule` chains. Nothing here needs 06's outcome.

**Status:** ready-for-agent

- [ ] A registration call beside the filesystem tool registrar registers a backend's
      `filesystem://` resource, deriving the address, the published name and the mount
      point from the backend's name.
- [ ] A mount-description hook on the backend base sits alongside the existing per-operation
      description hooks. It is abstract on the base and satisfied by a constructor argument
      on the generic disk root, exactly as the mount name already is — otherwise a reusable
      disk type ends up with one deployment's prose hardcoded into it.
- [ ] All seven descriptions move to their backends, unchanged in substance. Each backend
      already holds what its text needs.
- [ ] The scheduling backend's time zone becomes a read off its injected time provider
      rather than a static call. That is the zone the engine actually computes in, so the
      two surfaces cannot drift.
- [ ] The seven resource classes are deleted.
- [ ] The library server's filesystem-identity constants are reduced to the
      download-directory helper. The name and mount point were the hand-rolled fix for this
      problem and are no longer needed.
- [ ] The existing filesystem conformance test gains the assertion, for all seven backends,
      that the resource address, the published name and the mount point all agree with the
      backend's name. It already enumerates those seven and drives the tool registrar
      through a service collection.
- [ ] Nothing about the filesystem tool registrar changes. This ticket adds beside it.
- [ ] The contract table from ticket 02 stays green.

## Notes

Capability stays per operation, not per path — this ticket does not touch that. It adds
the second half of the same idea: a backend declares what it is, and the registrar reads
it, so there is no second place for the answer to live.
