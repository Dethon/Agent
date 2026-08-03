# 02 — Reach config and browser push through interfaces

**What to build:** the two concrete services the effects depend on become interfaces, so an effect can be built in a test without an `HttpClient` or a Blazor JS runtime. `ConfigService` and the browser-side push service are the only concrete dependencies among `InitializationEffect`'s fourteen, and the push one is what makes that effect awkward to construct at all.

The config interface carries the app-config and space-config lookups. The push interface carries subscribe, resubscribe, unsubscribe and the subscribed check. Both live alongside the project's other client-side contracts, both concrete classes are registered against them, and every consumer switches over: three effects and the hub connection factory for config, two effects for push.

Name the push one for what it does — it manages the browser's own subscription. `Domain.Contracts.IPushNotificationService` already exists, sends a notification to a space from the server, and is a different thing on the other side of the system. Two same-named interfaces would make every mention ambiguous to a reader and to a grep.

No behaviour changes. This is a prefactor that makes tickets 03 and 06 possible.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] Every consumer of the config service depends on the interface, not the class.
- [x] Every consumer of the browser push service depends on the interface, not the class.
- [x] Both interfaces are registered so the app resolves the existing concrete implementations unchanged.
- [x] The browser push interface does not share a name with the server-side notification contract.
- [x] The existing push service tests still pass without modification.
- [x] The app starts, connects, resolves its space and loads its agent list exactly as before.
