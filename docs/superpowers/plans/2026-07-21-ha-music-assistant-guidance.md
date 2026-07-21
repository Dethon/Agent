# HA Music Assistant Guidance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the nabu agent use Music Assistant reliably: bare-text `--media_id` values work, HA 5xx errors stop misleading the agent, and the prompt teaches library-first playlist resolution and MA-player identification.

**Architecture:** Three independent root-cause fixes inside `Domain`, all shipped via the `mcp-homeassistant` container: (1) `HaArgParser` object-selector coercion falls back to plain string, (2) `HaFileSystem.Exec` picks its error hint from `HomeAssistantException.StatusCode`, (3) the `HomeAssistantPrompt` Music playback section is rewritten from field evidence. Spec: `docs/superpowers/specs/2026-07-21-ha-music-assistant-guidance-design.md`.

**Tech Stack:** .NET 10, xUnit + Shouldly, existing `FakeHaClient` test fixture.

## Global Constraints

- Work on the current branch (`noise`); never switch branches.
- `.cs` files have **no trailing newline** (`.editorconfig` `insert_final_newline = false`).
- The pre-commit hook re-runs `dotnet format` and re-stages **whole** files — make the working tree match the commit you want.
- No XML doc comments; comments only for "why". Prefer LINQ over loops.
- Run only ONE `dotnet build`/`dotnet test` at a time (concurrent runs livelock WSL).
- TDD: watch each new test FAIL before implementing. Commit after each task (triplet).
- Commit messages end with:
  `Claude-Session: https://claude.ai/code/session_01TN86BmNE4svqMr5CbG3ooh`

---

### Task 1: Lenient object-selector coercion (`HaArgParser`) + help label

**Files:**
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaArgParser.cs` (the `object` branch of `Coerce`, ~lines 90-100)
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaServiceHelpRenderer.cs` (the `object` branch of `TypeOf`, ~lines 65-68)
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaArgParserTests.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaServiceHelpRendererTests.cs`
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemExecTests.cs` (tighten one fixture to production shape)

**Interfaces:**
- Consumes: `HaArgParser.Parse(IReadOnlyList<string>, HaServiceDefinition, string?)` (unchanged signature), `HaServiceHelpRenderer.TypeOf(JsonNode?)` (unchanged signature).
- Produces: object-selector fields accept bare text (coerced to a JSON string). Help renders object selectors as `TEXT or JSON`. No signature changes; later tasks don't depend on this one.

- [ ] **Step 1: Write the failing tests**

Add to `Tests/Unit/Domain/HomeAssistant/Vfs/HaArgParserTests.cs` (inside the existing class; `Svc()` already defines `advanced` with an `object` selector):

```csharp
[Fact]
public void Parse_ObjectSelector_BareText_FallsBackToString()
{
    HaArgParser.Parse(["--advanced", "chill relaxing music"], Svc())["advanced"]!
        .GetValue<string>().ShouldBe("chill relaxing music");
}

[Fact]
public void Parse_ObjectSelector_QuotedJsonString_StillParses()
{
    HaArgParser.Parse(["--advanced", "\"Liked Songs\""], Svc())["advanced"]!
        .GetValue<string>().ShouldBe("Liked Songs");
}

[Fact]
public void Parse_ObjectSelector_JsonArray_StillParses()
{
    ((JsonArray)HaArgParser.Parse(["--advanced", """["a","b"]"""], Svc())["advanced"]!)
        .Count.ShouldBe(2);
}
```

Add to `Tests/Unit/Domain/HomeAssistant/Vfs/HaServiceHelpRendererTests.cs`:

```csharp
[Fact]
public void Render_ObjectSelector_SaysTextOrJson()
{
    var svc = Service("music_assistant", "play_media", AnyEntityTarget(),
        ("media_id", Field(null, true, JsonNode.Parse("""{"object":{"multiple":false}}"""))));

    HaServiceHelpRenderer.Render("media_player.office", svc).ShouldContain("TEXT or JSON");
}
```

In `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemExecTests.cs`, method `Exec_RoutesCrossDomainMusicAssistantService_ByQualifiedName`, give `media_id` the selector production HA actually declares (today's fixture has no selector, which is why this test passes while the real system fails). Replace:

```csharp
Service("music_assistant", "play_media", DomainTarget("media_player"),
    ("media_id", new HaServiceField { Required = true }))
```

with:

```csharp
Service("music_assistant", "play_media", DomainTarget("media_player"),
    ("media_id", new HaServiceField { Required = true, Selector = JsonNode.Parse("""{"object":{"multiple":false}}""") }))
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaArgParserTests|FullyQualifiedName~HaServiceHelpRendererTests|FullyQualifiedName~HaFileSystemExecTests"`

Expected: `Parse_ObjectSelector_BareText_FallsBackToString` FAILS (ArgumentException "expects a JSON value"), `Render_ObjectSelector_SaysTextOrJson` FAILS (renders `JSON`), `Exec_RoutesCrossDomainMusicAssistantService_ByQualifiedName` FAILS (exit code 2, not 0). `Parse_ObjectSelector_QuotedJsonString_StillParses` and `Parse_ObjectSelector_JsonArray_StillParses` PASS (they pin current behavior so the fix can't regress it).

- [ ] **Step 3: Implement**

In `Domain/Tools/HomeAssistant/Vfs/HaArgParser.cs`, replace the `object` branch of `Coerce`:

```csharp
if (selector?["object"] is not null)
{
    try
    {
        return JsonNode.Parse(raw);
    }
    catch (JsonException)
    {
        // HA `object` selectors accept any JSON, and for fields like MA's `media_id` the
        // common value is a plain name. Non-JSON text becomes a JSON string so callers
        // aren't forced into '"..."' double-quoting.
        return JsonValue.Create(raw);
    }
}
```

In `Domain/Tools/HomeAssistant/Vfs/HaServiceHelpRenderer.cs`, in `TypeOf`, replace `return "JSON";` with:

```csharp
return "TEXT or JSON";
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaArgParserTests|FullyQualifiedName~HaServiceHelpRendererTests|FullyQualifiedName~HaFileSystemExecTests"`

Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaArgParser.cs Domain/Tools/HomeAssistant/Vfs/HaServiceHelpRenderer.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaArgParserTests.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaServiceHelpRendererTests.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemExecTests.cs
git commit -m "fix(ha-vfs): accept bare text for object-selector fields

MA's media_id is an object selector, so every natural --media_id \"name\"
call failed with 'expects a JSON value'. Unparseable tokens now coerce to
a JSON string; real JSON still parses. Help renders TEXT or JSON.

Claude-Session: https://claude.ai/code/session_01TN86BmNE4svqMr5CbG3ooh"
```

---

### Task 2: Status-aware exec error hints (`HaFileSystem.Exec`)

**Files:**
- Modify: `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs` (the `catch (HomeAssistantException)` block, ~lines 99-103)
- Test: `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemExecTests.cs`

**Interfaces:**
- Consumes: `HomeAssistantException.StatusCode` (`int?`, already exists in `Domain/Exceptions/HomeAssistantException.cs`); `FakeHaClient.CallHandler` for stubbing failures.
- Produces: exit-1 stderr is `ex.Message` plus a status-dependent hint — 400 keeps the field-types hint, 5xx points at library resolution, anything else gets no hint. No signature changes.

- [ ] **Step 1: Write the failing tests**

Add to `Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemExecTests.cs`:

```csharp
[Fact]
public async Task Exec_HaServerSideFailure_Returns1_WithResolveHint()
{
    var fs = Build(out var client);
    client.CallHandler = (_, _, _, _) =>
        throw new HomeAssistantException("Home Assistant returned 500: Server got itself in trouble", 500);
    var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh --brightness_pct 60", null, CancellationToken.None);

    var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
    exec.ExitCode.ShouldBe(1);
    exec.Stderr.ShouldContain("500");
    exec.Stderr.ShouldContain("browse_media.sh"); // points at library resolution, not at --help
    exec.Stderr.ShouldNotContain("field types");  // the 400 hint must not appear on a 5xx
}

[Fact]
public async Task Exec_HaFailure_NoStatusCode_Returns1_MessageOnly()
{
    var fs = Build(out var client);
    client.CallHandler = (_, _, _, _) => throw new HomeAssistantException("boom");
    var result = await fs.ExecAsync("entities/light/kitchen", "turn_on.sh", null, CancellationToken.None);

    var exec = result.ShouldBeOfType<FsResult<FsExecResult>.Ok>().Value;
    exec.ExitCode.ShouldBe(1);
    exec.Stderr.ShouldBe("boom"); // no hint appended
}
```

The existing `Exec_HaFailure_Returns1_WithHint` (400 → contains `--help`) stays untouched and must keep passing.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemExecTests"`

Expected: both new tests FAIL (today every failure gets the field-types hint), all others PASS.

- [ ] **Step 3: Implement**

In `Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs`, replace the catch block:

```csharp
catch (HomeAssistantException ex)
{
    // 400 = HA rejected the payload shape; 5xx = the payload was fine but the service
    // itself failed (e.g. play_media couldn't resolve a name in the MA library) — the
    // worst response there is nudging the caller back to --help to "fix" a shape that
    // was never wrong. 401/404 messages already say what's wrong; add nothing.
    var hint = ex.StatusCode switch
    {
        400 => $"\nRe-check the field types with `{serviceName}.sh --help`; don't retry the same shape.",
        >= 500 => "\nThe arguments were accepted but the action failed inside Home Assistant — a named item may not exist. For media, list the library (`browse_media.sh`) and use an exact title instead of retrying guesses.",
        _ => ""
    };
    return done(1, "", $"{ex.Message}{hint}");
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HaFileSystemExecTests"`

Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Tools/HomeAssistant/Vfs/HaFileSystem.Exec.cs Tests/Unit/Domain/HomeAssistant/Vfs/HaFileSystemExecTests.cs
git commit -m "fix(ha-vfs): stop misdiagnosing HA 5xx as argument-shape errors

The exec hint told the agent to re-check field types on every failure;
on a 500 (e.g. MA can't resolve a playlist name) that sent it in
circles. Hints now key off HomeAssistantException.StatusCode.

Claude-Session: https://claude.ai/code/session_01TN86BmNE4svqMr5CbG3ooh"
```

---

### Task 3: Music playback prompt rewrite (`HomeAssistantPrompt`)

**Files:**
- Modify: `Domain/Prompts/HomeAssistantPrompt.cs` (replace the `### Music playback` section, lines 106-124 of the current file)
- Test: `Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs`

**Interfaces:**
- Consumes: nothing from Tasks 1-2 (the prompt text assumes their behavior: bare-text `--media_id` works; 500 hint mentions `browse_media.sh`).
- Produces: `HomeAssistantPrompt.SystemPrompt` (same const) with the rewritten section. No signature changes.

- [ ] **Step 1: Write the failing test**

In `Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs`, keep `SystemPrompt_TeachesMusicPlaybackAndGroupingIdiom` exactly as is, and add:

```csharp
[Fact]
public void SystemPrompt_TeachesLibraryFirstPlaylistResolution()
{
    var prompt = HomeAssistantPrompt.SystemPrompt;

    prompt.ShouldContain("app_id");           // MA-player marker attribute
    prompt.ShouldContain("mass_player_type"); // MA-player marker attribute
    prompt.ShouldContain("browse_media.sh --media_content_id playlists"); // list saved playlists first
    prompt.ShouldContain("exact title");      // play what browse returned, never a guess
    prompt.ShouldContain("search_media.sh");  // named as the GLOBAL catalog search
    prompt.ShouldContain("500");              // 500 = unresolved item, not MA down
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantPromptTests"`

Expected: `SystemPrompt_TeachesLibraryFirstPlaylistResolution` FAILS (first missing anchor: `app_id`), the other four tests PASS.

- [ ] **Step 3: Replace the Music playback section**

In `Domain/Prompts/HomeAssistantPrompt.cs`, replace everything from `### Music playback` up to (not including) `### Notes` with:

```
### Music playback

Music plays through Music Assistant (MA). The MA player for a room is the `media_player`
whose `state.json` attributes include `app_id: music_assistant` and `mass_player_type`.
Other media_players (TVs, etc.) also list `music_assistant.*.sh` actions, but MA calls on
them do nothing — when a room has more than one player, read `state.json` and pick the MA
one. Default the target to the **speaking room**'s player (the room the request came
from) unless another room is named; "everywhere" => run it on every room's MA player.

- Tracks, artists, albums, radio: play directly by name from the player directory:
  `exec music_assistant.play_media.sh --media_id "miles davis"` — add
  `--media_type artist|album|track|radio` to disambiguate. Free-text names resolve
  through the streaming providers.
- Playlists ("my playlist", "songs I like", any saved list): NEVER guess the name —
  playlist names only resolve against the user's MA library. List it first:
  `exec browse_media.sh --media_content_id playlists --media_content_type music_assistant`
  then play the exact title it returned:
  `exec music_assistant.play_media.sh --media_id "<exact title>" --media_type playlist`.
- A 500 from `music_assistant.play_media.sh` means the item could not be resolved (the
  name isn't in the library) — NOT that MA is down. Browse the library and use an exact
  title instead of retrying name variants.
- `search_media.sh` searches the entire provider catalog (public Spotify etc.), not the
  user's saved items, and the URIs it returns are generally not playable via
  `play_media`. Use it only for content the user doesn't have saved, then play the
  result by its exact title.
- Do NOT use the bare `play_media.sh` (`media_player.play_media`): it needs a concrete
  `media_content_id`/URI you cannot know. Only `music_assistant.play_media.sh` resolves
  names.
- Transport: `media_play.sh` / `media_pause.sh` / `media_next_track.sh` / `volume_set.sh`
  on the player.
- Grouping (synced multi-room): `join.sh` (`media_player.join`; `--group_members` = the
  other players) to play in sync; `unjoin.sh` (`media_player.unjoin`) to split a room
  back out.
Music ducks automatically while the satellite speaks — never lower or pause music just to
talk.
```

(Keep the raw-string indentation identical to the surrounding prompt: 8 leading spaces inside the `"""` literal.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~HomeAssistantPromptTests"`

Expected: all PASS (both the new test and the existing anchors — `music_assistant.play_media.sh --media_id`, `media_player.play_media`, `` `join.sh` ``, `` `unjoin.sh` ``, `speaking room` — survive the rewrite).

- [ ] **Step 5: Commit**

```bash
git add Domain/Prompts/HomeAssistantPrompt.cs Tests/Unit/Domain/Prompts/HomeAssistantPromptTests.cs
git commit -m "feat(ha-prompt): library-first music guidance from field evidence

Teach the MA-player marker attributes, browse-before-play for
playlists, search_media's global scope, and that play_media 500s mean
an unresolved name — all failure modes observed in prod transcripts.

Claude-Session: https://claude.ai/code/session_01TN86BmNE4svqMr5CbG3ooh"
```

---

### Task 4: Full verification

- [ ] **Step 1: Run the whole unit suite**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"`

Expected: PASS (judge any failure by type: pre-existing known failures are documented in memory; nothing HA/prompt-related may fail).

- [ ] **Step 2: Build the solution**

Run: `dotnet build agent.sln`

Expected: Build succeeded, 0 errors.

## Deploy note (post-merge, manual)

All three changes ship in the `mcp-homeassistant` image. On the Pi 5 prod host: rebuild/restart `mcp-homeassistant` only. Reminder surfaced during investigation: `media_player.herfluffness_entertainment` (living-room MA speaker) is `unavailable` in HA — infra fix for the user, not this change.
