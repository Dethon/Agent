# 03 — A keep-alive holds the connection open through idle gaps

**What to build:** Raised lifetimes are not enough on their own. A gap longer than the idle
timeout still drops the connection, and at this traffic volume most gaps are longer than
any timeout worth configuring.

Add a scheduled request that keeps one connection to the hosted provider open. It targets
an endpoint that costs nothing, never a completion, because spending money on every fire
for no user-visible work is not acceptable. When it fails it publishes a metric, so the
pool going cold again is something an operator can see rather than a silent regression that
quietly gives back the win from ticket 02.

The local embedding server needs no equivalent. It is plain HTTP on a Docker bridge with no
handshake to amortise.

**Blocked by:** 02 — the keep-alive interval is chosen against the idle timeout that ticket
sets.

**Status:** done

- [x] Fires on an interval below the configured idle timeout
- [x] Targets a non-billable endpoint; no tokens are ever spent keeping a connection open
- [x] A failure publishes a metric and does not bring down the host process
- [x] Uses the injectable time provider, so the interval is testable without waiting for it
- [x] Does not delay startup and does not block shutdown
