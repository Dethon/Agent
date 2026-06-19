# Home Assistant → Voice alarms & reminders (provisioning)

Alarms/reminders are HA calendar events that fire an insistent spoken announcement on the
voice satellites. The agent creates the events (via the `/ha` VFS `calendar.create_event`
action); HA fires them; one automation bridges them to the voice hub's announce endpoint.

## One-time setup

1. **Shared token.** Set `Announce__Token=<secret>` in `DockerCompose/.env` (already a key there).
   The `mcp-channel-voice` container reads it via `env_file`. Use the same value below.

2. **Local calendar.** Add a Local Calendar named `assistant_alarms`
   (Settings → Devices & Services → Add Integration → Local Calendar). It appears as
   `calendar.assistant_alarms`.

3. **rest_command** (in HA `configuration.yaml`). Note the **internal** URL/port: the hub listens
   on container port 8080 (published as 6015 on the host), reachable from HA on the compose network
   at `mcp-channel-voice:8080`:

       rest_command:
         voice_announce:
           url: "http://mcp-channel-voice:8080/api/voice/announce"
           method: POST
           headers:
             X-Announce-Token: !secret announce_token
           content_type: "application/json"
           payload: >-
             {"text": {{ summary | to_json }},
              "insistent": true,
              {{ params }} }

   Add `announce_token: <secret>` to HA `secrets.yaml`.

4. **Bridging automation** (fires on every event start of the alarms calendar; forwards the
   event summary as the spoken text and the event description's JSON as target/cap params):

       alias: Voice alarm bridge
       trigger:
         - platform: calendar
           event: start
           entity_id: calendar.assistant_alarms
       action:
         - service: rest_command.voice_announce
           data:
             summary: "{{ trigger.calendar_event.summary }}"
             # description is a JSON object: {"target": {...}, "gapSeconds":.., "maxRepeats":..}
             # strip the outer braces so it can be spliced into the rest_command payload object.
             params: "{{ trigger.calendar_event.description[1:-1] }}"
         # OPTIONAL belt-and-suspenders escalation (fires in parallel, at trigger time):
         # - service: notify.mobile_app_phone
         #   data: { message: "Alarm: {{ trigger.calendar_event.summary }}" }

   The `insistent: true` flag in the rest_command body routes the request to the hub's
   `InsistentAnnouncementController` (repeat-until-acknowledged). The user dismisses by saying
   "ok nabu" at any targeted satellite.

## Notes & limitations (v1)

- Conditional "escalate only if unacknowledged" is not built in — the optional parallel notify above
  fires at trigger time regardless. True ack-gated escalation needs a hub→HA callback (future).
- If no targeted satellite is online when the event fires, the hub records an `AlarmOffline` metric
  and nothing is spoken; the optional parallel notify still reaches another channel.
- Validate against your HA version that the local calendar supports `create_event` (with `rrule`),
  `get_events`, `delete_event`, and `update_event` as services on the calendar entity.
