# 02 — Route approvals through the channel itself

**What to build:** When a scheduled run asks the user to approve a tool, the approval request arrives in the same conversation the answer arrives in, and so does the auto-approval notice for a whitelisted tool. That is already true today, but only by accident of construction: the conversation id is curried into a two-method adapter and carried privately, so nothing at any call site shows that approvals go to one conversation while metrics were being stamped with another. This ticket removes the hiding place.

The conversation id moves onto the tool-approval-handler contract as the first parameter of both its methods, which gives that contract exactly the shape the channel connection already has. The channel connection then inherits the two methods instead of declaring identical ones, so a channel is an approval handler directly. The adapter class, the approval-handler factory delegate on the chat monitor, and the DI registration that supplied that delegate are all deleted; the monitor passes the approval channel itself, bound to the delivery identity resolved in ticket 01.

The approval chat client already holds a conversation id for stamping metrics. It becomes the id passed on every approval and auto-approval call, and it becomes required rather than optional — one string with one meaning, visible where the client is constructed.

That makes the id required on the sub-agent path too, which today constructs its approval chat client with none. The sub-agent factory takes the parent turn's delivery conversation id and passes it down, including through its own recursion for nested sub-agents. As a consequence, sub-agent tool-call and tool-execution events stop being published with a null conversation id and are attributed to the conversation that spawned them.

Every fake and mock approval handler in the unit and integration suites gains the parameter. The approval chat client's existing unit files already cover the whitelist, dynamic-approval, rejection and metrics paths; those passing unchanged is the statement that the contract change moved no behaviour.

**Blocked by:** 01 — Resolve the delivery identity once and build the turn from it. Both rewrite the group-anchors resolution and the same unit test file, and this ticket's approval binding consumes the single identity ticket 01 produces.

**Status:** done

- [x] A schedule fire that mints a WebChat conversation routes its approval requests and auto-approval notices to the minted channel and id, asserted in the delivery-identity unit file from ticket 01
- [x] The tool-approval-handler contract takes the conversation id on both methods and the channel connection inherits them rather than redeclaring them
- [x] The approval adapter class, the monitor's approval-handler factory parameter and its DI registration no longer exist
- [x] The approval chat client requires a conversation id and uses it for both the approval and the auto-approval call
- [x] A sub-agent's tool-call and tool-execution events carry the parent turn's delivery conversation id instead of null
- [x] The existing approval chat client unit files pass unchanged in behaviour
- [x] `dotnet test Tests/Unit` passes
