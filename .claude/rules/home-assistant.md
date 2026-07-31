---
paths:
  - "McpServerHomeAssistant/**"
  - "Domain/Tools/HomeAssistant/**"
  - "Domain/Prompts/HomeAssistantPrompt.cs"
---

# Home Assistant

Home Assistant runs at `http://<host>:8123` (published on all interfaces). On first run:

1. Create the owner account through the browser onboarding flow.
2. Profile menu → **Security → Long-Lived Access Tokens** → create one.
3. Set `HOMEASSISTANT__TOKEN=...` in `DockerCompose/.env` and restart `mcp-homeassistant`.
4. For the Roborock S8: Settings → Devices & Services → Add Integration → **Roborock**; the vacuum appears as `vacuum.<name>`.

The agent reaches HA in-network at `http://homeassistant:8123` via `McpServerHomeAssistant`. For voice alarms/reminders it creates events on a dedicated `calendar.assistant_alarms` calendar that an HA automation bridges to the voice announce endpoint; the `home_assistant_guide` prompt (`Domain/Prompts/HomeAssistantPrompt.cs`) teaches the idiom, and the one-time `rest_command` + automation provisioning lives in the HA instance itself.

The HA VFS engine is `Domain/Tools/HomeAssistant/Vfs/*.cs`.

## Music Assistant (podcast episodes)

Almost everything media goes through HA's `music_assistant.*` services. One thing cannot: **listing a
podcast's episodes**. `media_player.browse_media` has no podcast branch and raises `BrowseError`,
`search_media` / `music_assistant.search` never return episodes, and `get_library` only holds saved
items. That matters because `music_assistant.play_media` resolves a name across tracks/albums/
playlists/artists/radio/**shows** — never episodes — so an episode title silently plays the show's
newest episode and still reports success.

So a podcast episode is playable **only** by its exact MA uri (`<provider>://podcast_episode/<id>`),
and only MA's own websocket API can produce it. `IMusicAssistantClient` /
`Infrastructure/Clients/MusicAssistant/MusicAssistantClient.cs` talks to `ws://<ma>/ws` directly
(no HTTP command endpoint exists); it authenticates with a long-lived MA token, and accumulates
frames flagged `partial: true`.

The capability reaches the agent as `music_assistant.podcast_episodes.sh` in every media_player
directory. It is a **virtual action**: `HaMusicActions.PodcastEpisodes` is a synthetic
`HaServiceDefinition` injected via `HaCatalogProvider(extraServices:)` so glob/`--help`/exec resolve
it like a real service, and `HaFileSystem.ExecAsync` intercepts it and runs `HaPodcastEpisodes`
instead of calling HA. It is registered only when `MusicAssistant:Token` is set, so a deployment
without MA never advertises an action that cannot work.

MA runs `network_mode: host`, so `mcp-homeassistant` reaches it over `host.docker.internal`
(`extra_hosts: host-gateway`), not a container name.

## Music Assistant (restarting an episode)

MA keeps a **resume point** per podcast episode and audiobook, and `play_index` reads its
`seek_position` argument as falsy-or-set: a 0 means "no seek requested", so it substitutes
`resume_position_ms - 500` and the stream restarts half a second behind where the listener already
was (`music_assistant/controllers/player_queues.py`, MA 2.9.9). Both routes into it are affected —
`music_assistant.play_media` always resumes, and `media_player.media_seek` forwards straight to
`play_index` — and HA's `music_assistant.play_media` exposes no start-position field, so nothing the
agent can say means "second zero".

`HaFileSystem.NormalizeMediaSeek` therefore rewrites a `media_player.media_seek` of 0 to 1 second:
truthy for MA, inaudible to a listener. Keep that rewrite as long as MA reads the field this way —
without it "play it from the beginning" silently becomes "jump back half a second".
