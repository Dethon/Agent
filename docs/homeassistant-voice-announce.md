# Home Assistant → Voice Announce

The `mcp-channel-voice` service exposes `POST /api/voice/announce` for non-conversational spoken alerts (Ring doorbell, intercom, alarms, etc.).

## Setup

1. Generate a token and put it in `DockerCompose/.env`:

       ANNOUNCE_TOKEN=$(openssl rand -hex 32)

2. Restart the channel:

       docker compose -p jackbot up -d mcp-channel-voice

3. Add the token to Home Assistant `secrets.yaml`:

       announce_token: "<the token from step 1>"

## `configuration.yaml`

    rest_command:
      voice_announce:
        url: "http://mcp-channel-voice:8080/api/voice/announce"
        method: POST
        headers:
          X-Announce-Token: !secret announce_token
          content-type: application/json
        payload: '{{ payload | tojson }}'

## Example automation

    - alias: Ring Intercom → common-area announce
      trigger:
        platform: event
        event_type: ring_doorbell_pressed
      action:
        service: rest_command.voice_announce
        data:
          payload:
            target:   { room: "Living Room" }
            text:     "Someone is at the door."
            priority: "High"

## Endpoint contract

Field      | Required | Notes
-----------|----------|-----------------------------------------------------
target     | yes      | one of `{ "satelliteId": "..." }`, `{ "room": "..." }`, `{ "all": true }`
text       | yes      | plain text — synthesized by the configured TTS provider
voice      | no       | overrides per-satellite default voice
priority   | no       | `Low` | `Normal` (default) | `High`

Status codes:

* `202 Accepted` — `{ announcementId, satellites: [{ id, status: queued|playing|offline }] }`
* `401 Unauthorized` — missing / wrong `X-Announce-Token`
* `404 Not Found` — unknown id or empty resolved target set
* `503 Service Unavailable` — announce subsystem disabled
