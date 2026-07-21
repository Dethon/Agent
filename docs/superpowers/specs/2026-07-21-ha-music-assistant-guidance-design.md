# HA Music Assistant Guidance — Design

**Date:** 2026-07-21
**Status:** Approved
**Problem owner:** nabu voice agent music playback via the `/ha` VFS

## Problem

Field evidence (nabu conversations from prod Redis, 2026-07-21) shows the agent
consistently struggles with Music Assistant:

1. **`--media_id` fails on the first try in every conversation.** HA declares
   `media_id` with an `object` selector, so `HaArgParser` demands parseable
   JSON. The natural `--media_id "Friday I'm in Love"` fails with
   `--media_id expects a JSON value`; the agent must rediscover the awkward
   `'"..."'` double-quoting each time. The current prompt's own example
   teaches the failing form.
2. **Playlist requests are guessed, not resolved.** For "la playlist de
   canciones que me gustan" the agent blind-guessed five names, each failing
   with an opaque 500 whose stderr hint ("Re-check the field types…") is
   wrong — the shape was fine, the name simply wasn't in the MA library. The
   agent concluded MA was down and gave up. Once the user explicitly said
   "explora mi biblioteca", `browse_media.sh` listed the real playlists and
   the exact title played instantly. Timeline analysis confirms:
   `music_assistant.play_media` 500s exactly when the item can't be resolved
   in the library (playlist names not in the library; `spotify--<instance>://`
   URIs returned by global `search_media.sh`). Tracks/artists by free-text
   name resolve fine.
3. **Wrong player picked.** "Música en el salón" targeted the Samsung TV
   entity (`device_class: tv`) because every `media_player` directory lists
   `music_assistant.*.sh` actions. Real MA players are identifiable by
   `app_id: music_assistant` / `mass_player_type` attributes in `state.json`;
   nothing teaches this.

Verified live: HA's `/api/services` returns **no field descriptions** (all
null), so richer help cannot come from HA metadata — it must come from our
parser leniency, error hints, and prompt.

## Design — three root-cause fixes

### 1. Lenient `object`-selector coercion (`HaArgParser`)

In `Coerce`, for `object` selectors: try `JsonNode.Parse`; on `JsonException`
fall back to `JsonValue.Create(raw)` (plain string) instead of throwing.

- `--media_id "chill music"` → JSON string `"chill music"`.
- `'{"a":1}'` / `'["x","y"]'` / legacy `'"text"'` → parsed JSON, unchanged.
- Bare tokens that happen to parse (`500` → number, `true` → bool) are passed
  as parsed JSON; HA validators coerce scalars, and ambiguity is acceptable.

`HaServiceHelpRenderer.TypeOf` renders `object` selectors as `TEXT or JSON`
(was `JSON`).

### 2. Status-aware error hint (`HaFileSystem.Exec`)

The `catch (HomeAssistantException)` branch appends a hint based on
`ex.StatusCode` (already carried by the exception):

- **400** → keep the current hint: re-check field types with `--help`, don't
  retry the same shape.
- **5xx** → new hint: the arguments were accepted but the action itself
  failed inside Home Assistant — e.g. a named item that doesn't exist; for
  media, resolve exact names from the library (`browse_media.sh`) instead of
  retrying guesses.
- **anything else** (401/404/null) → message only, no appended hint.

### 3. Music playback prompt rewrite (`HomeAssistantPrompt`)

Replace the "Music playback" section, teaching:

- **Pick the real MA player**: the room's Music Assistant player has
  `app_id: music_assistant` / `mass_player_type` in `state.json`. Other
  media_players (TVs) also list `music_assistant.*.sh` actions but calls on
  them silently do nothing. When a room has several players, check first.
- **Tracks/artists/albums**: play directly by name
  (`--media_id "miles davis"`), `--media_type` to disambiguate (bare text
  now works, per fix 1).
- **Playlists & "my library"**: never guess names. Resolve first —
  `browse_media.sh --media_content_id playlists --media_content_type
  music_assistant` lists the user's saved playlists in one call; then play
  the **exact title** with `--media_type playlist`. Playlist names only
  resolve against the MA library.
- **`search_media.sh` is the global catalog** (public provider content), not
  the user's saved items; its result URIs are generally not playable via
  `play_media` — use it only when the user wants something not saved.
- **A 500 from `play_media` means the item couldn't be resolved**, not that
  MA is down — go browse the library.
- Keep: transport scripts, `join.sh`/`unjoin.sh` grouping, speaking-room
  default targeting, auto-duck note, bare `play_media.sh` warning.

## Testing (TDD, one triplet per fix)

- `HaArgParserTests`: object selector — bare text → string; JSON
  object/array/quoted-string → parsed; existing behavior for other selectors
  untouched.
- Exec hint tests: stubbed `IHomeAssistantClient` throwing
  `HomeAssistantException` with 400 vs 500 → distinct stderr hints; non-4xx/5xx
  → no hint.
- `HomeAssistantPromptTests`: extend the music idiom test with new anchors —
  MA-player marker, browse-first playlist resolution, global-search warning.

## Deploy

All three changes live in `Domain`, shipped inside the `mcp-homeassistant`
container — one image rebuild on the Pi. Work on the current branch (`noise`).

## Out of scope / rejected

- Exposing `music_assistant.get_library` / `music_assistant.search` as new
  global VFS actions (need `config_entry_id` plumbing; `browse_media.sh`
  already covers library listing — YAGNI). Rejected by user 2026-07-21.
- The living-room MA speaker `media_player.herfluffness_entertainment` is
  currently `unavailable` in HA — infra issue, flagged to the user, not code.
