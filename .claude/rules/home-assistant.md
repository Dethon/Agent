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
