# 01 — One place stamps a satellite's identity onto a voice event

**What to build:** The three fields that name which satellite a voice metric is about
get set in one place instead of at twenty call sites.

`SatelliteId`, `Room` and `Identity` are set together on a `VoiceEvent`, always read off
a `SatelliteSession`, at twenty sites across seven files:

- `Services/WyomingSatelliteHost.cs:275`, `:290`, `:318`, `:379`, `:420`, `:449`
- `McpTools/SendReplyTool.cs:299`, `:335`, `:349`, `:374`, `:387`, `:461`
- `Services/WakeArbiter.cs:226`, `:234`, `:264`, `:318`
- `Services/AnnouncementService.cs:127`
- `Services/InsistentAnnouncementController.cs:202`
- `Services/TranscriptDispatcher.cs:130`
- `McpTools/RequestApprovalTool.cs:92`

After this ticket a caller says which satellite the event is about once, and the three
fields follow. Every other field on the event is still set at the call site — this
ticket is about the identity triple and nothing else.

**Where it lives.** In `McpChannelVoice`, not Domain. `VoiceEvent` is a Domain DTO and
must not learn what a `SatelliteSession` is; the stamping is an extension in the voice
server, which already knows both.

The `WakeArbiter` sites read through `handle.Session`, so they are stamped from a
session like the rest. Ticket `03` narrows that handle, and this ticket is what makes the
narrowed version able to carry one identity value instead of three separate fields.

Behaviour must not change. The emitted metric payloads are byte-for-byte what they are
today, including the sites that set `SatelliteId` from a local `id` variable rather than
from `session.SatelliteId` — check `AnnouncementService.cs:125` before assuming they are
the same value.

Start red: write a test asserting that a stamped event carries all three fields off a
session, watch it fail, then build.

**Blocked by:** None — can start immediately. Do this first: `02`, `03` and `04` all
shrink once it lands, and doing it after them means editing the same lines twice.

**Status:** ready-for-agent

- [ ] One extension in `McpChannelVoice` stamps a `VoiceEvent` with a session's identity.
- [ ] All twenty sites use it; `grep -rn "Identity = .*Config.Identity" McpChannelVoice` returns only the extension.
- [ ] `Domain/DTOs/Metrics/VoiceEvent.cs` is unchanged.
- [ ] `AnnouncementService.cs:125`'s `SatelliteId = id` is checked against `session.SatelliteId` and either shown to be the same value or left alone with a comment saying why.
- [ ] Existing voice metric tests pass unchanged.
