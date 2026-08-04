# 10 — The raw connection is gone

**What to build:** the contract step. Once every **hub call** goes through a verb that
answers or says **not live**, nothing reads the raw transport object any more, and the
accessor this whole candidate is named for can be deleted — from the **live
connection** interface and from the **hub connection** abstraction both.

What that buys is that the decision cannot be bypassed. While the accessor exists, a
new call can be written the old way and quietly reintroduce the rule this feature
removed. After this ticket, the only way to reach the server is through a verb that
makes the answer explicit, and SignalR's own client types live inside the factory that
builds connections and nowhere else in the client.

Check for stragglers before deleting rather than after. Anything still reaching for
the accessor belongs to one of tickets 05 through 09 and should be migrated there, not
patched here.

The transport's connection-state enum is not part of this and stays: the hub
connection's state property and the foreground reconnect policy both read it.

**Blocked by:** 05, 06, 07, 08, 09.

**Status:** done

- [x] No caller anywhere in the client reads the raw connection object.
- [x] The accessor is deleted from both the live connection interface and the hub
      connection abstraction.
- [x] SignalR client types appear in the client only in the connection factory and in
      the two places that read the connection-state enum.
- [x] The full client suite passes, and the WebChat end-to-end suite passes.
