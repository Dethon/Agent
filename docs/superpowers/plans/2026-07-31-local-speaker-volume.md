# Local Speaker Volume Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let someone change a satellite's own master output level and mute by voice, with the transcript never reaching the agent or an LLM.

**Architecture:** The hub matches a finished transcript against a small Spanish phrase table before it dispatches to the agent. A hit writes a new `speaker-volume` Wyoming event to that satellite and ends the turn. The satellite drives its PipeWire sink with `wpctl` and plays a confirmation cue. Alarms temporarily override a local mute and restore it when the insistent loop ends.

**Tech Stack:** .NET 10 (`McpChannelVoice`), Rust 2021 (`satellite/`, a standalone crate outside the solution), xUnit + Shouldly + Moq, `wpctl` from WirePlumber.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-07-31-local-speaker-volume-design.md`. Read it before Task 1.
- TDD is mandatory: write the failing test, run it, watch it fail, then implement. Never write implementation first.
- `.editorconfig` sets `insert_final_newline = false` — **`.cs` files end with no trailing newline**. Rust files keep their trailing newline.
- The pre-commit hook runs `dotnet format` over staged `.cs` files and re-stages them whole. Partial staging does not survive a commit; make the working tree match the commit you want.
- Protocol version moves from `1.7` to `1.8` in exactly two places, and they must match: `satellite/src/wyoming/event.rs` (`PROTOCOL_VERSION`) and `McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs` (`ProtocolVersion`).
- New generic tunables go in `appsettings.json` alone. Nothing in this feature is a secret or a per-deployment value, so `DockerCompose/.env` and `docker-compose.yml` are **not** touched.
- C# style: file-scoped namespaces, primary constructors for DI, `record` for DTOs, LINQ over loops, no XML doc comments. Comments explain *why*, never *what*.
- Rust: run `cargo test` from `satellite/`, never from the repo root. `satellite/` is not in `Ziggurat.sln`.
- Test naming: `{Method}_{Scenario}_{ExpectedResult}` in `{ClassUnderTest}Tests.cs`. Use Shouldly (`result.ShouldBe(...)`), not xUnit asserts.
- Commit after each task. Message style follows the repo: `feat(voice): ...`, `feat(satellite): ...`.

## File Structure

**Hub — `McpChannelVoice/`**

| File | Responsibility |
| --- | --- |
| `Settings/CommandSettings.cs` (new) | The phrase table as typed settings |
| `Settings/VoiceSettings.cs` (modify) | Add `Commands` |
| `Services/VoiceCommandMatcher.cs` (new) | Normalize a transcript and match it to a command. Pure, no I/O |
| `Services/SatelliteSession.cs` (modify) | Add `ControlWriter` so a control event can be written from outside the connection scope |
| `Services/WyomingSatelliteHost.cs` (modify) | Set and clear `ControlWriter` |
| `Services/TranscriptDispatcher.cs` (modify) | The fast-path seam |
| `Services/InsistentAnnouncementController.cs` (modify) | Alert hold and release |
| `Services/WyomingProtocol/WyomingWriter.cs` (modify) | Protocol 1.8 |
| `Modules/ConfigModule.cs` (modify) | DI wiring |
| `appsettings.json` (modify) | Default phrase table |

**Satellite — `satellite/`**

| File | Responsibility |
| --- | --- |
| `src/volume.rs` (new) | Master-level control: `wpctl` invocations, `user_muted` state, probe backend for tests |
| `src/config.rs` (modify) | `--volume-sink`, `--volume-step` |
| `src/audio/playback.rs` (modify) | Cue variant that acknowledges when it has finished |
| `src/audio/cues.rs` (modify) | The new volume cue |
| `src/wyoming/event.rs` (modify) | Protocol 1.8 |
| `src/satellite/state_machine.rs` (modify) | Handle `speaker-volume`; per-connection hold guard |
| `src/main.rs` (modify) | Build the process-scoped `VolumeControl`, seed it, thread it into connections |
| `sounds/volume.wav` (new) | The confirmation tone |
| `scripts/provision-satellite-rs.sh` (modify, repo root) | Pass `--volume-sink` on music units |

**Tests**

| File | Covers |
| --- | --- |
| `Tests/Unit/McpChannelVoice/VoiceCommandMatcherTests.cs` (new) | Task 2 |
| `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs` (modify) | Task 4 |
| `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs` (modify or new) | Task 5 |
| Rust `#[cfg(test)] mod tests` inline per module | Tasks 6-10 |

## Task Order

Tasks 1-5 are the hub, 6-11 the satellite, 12 provisioning and docs. Task 1 is shared and must go first. The hub and satellite halves are otherwise independent — Task 3 produces the event shape both sides agree on.

---

### Task 1: Bump the wire protocol to 1.8

Both ends carry the version as one documented number, so it moves in a single commit before anything reads it.

**Files:**
- Modify: `satellite/src/wyoming/event.rs:3` and its test at `:59-62`
- Modify: `McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs:9-13`

**Interfaces:**
- Consumes: nothing
- Produces: `PROTOCOL_VERSION == "1.8"` (Rust), `WyomingWriter.ProtocolVersion == "1.8"` (C#, private)

- [ ] **Step 1: Update the failing Rust test**

In `satellite/src/wyoming/event.rs`, replace the existing `protocol_version_is_1_7` test:

```rust
    // The alert routing field on audio-start landed in 1.5, the listening-started event in
    // 1.6, the measured `room_rms` on run-pipeline in 1.7, the `speaker-volume` event in 1.8;
    // the constant is documented as ONE number shared with the hub's WyomingWriter.ProtocolVersion,
    // so it moves with the wire.
    #[test]
    fn protocol_version_is_1_8() {
        assert_eq!(PROTOCOL_VERSION, "1.8");
    }
```

- [ ] **Step 2: Run it and watch it fail**

```bash
cd satellite && cargo test protocol_version
```

Expected: FAIL — `assertion `left == right` failed: left: "1.7", right: "1.8"`.

- [ ] **Step 3: Bump the constant**

In `satellite/src/wyoming/event.rs`:

```rust
pub const PROTOCOL_VERSION: &str = "1.8"; // matches the hub's WyomingWriter
```

- [ ] **Step 4: Run it and watch it pass**

```bash
cd satellite && cargo test protocol_version
```

Expected: PASS.

- [ ] **Step 5: Bump the hub constant to match**

In `McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs`, replace lines 9-13:

```csharp
    // Must match satellite/src/wyoming/event.rs PROTOCOL_VERSION. Neither side validates the value
    // today, so the only cost of drift is a misleading wire trace — but the two are documented as
    // one number (satellite/CLAUDE.md), so they move together. 1.5 added `alert` on audio-start;
    // 1.6 added the `listening-started` event; 1.7 added `room_rms` on run-pipeline; 1.8 added the
    // `speaker-volume` event.
    private const string ProtocolVersion = "1.8";
```

- [ ] **Step 6: Build the hub**

```bash
dotnet build McpChannelVoice/McpChannelVoice.csproj
```

Expected: build succeeds.

- [ ] **Step 7: Commit**

```bash
git add satellite/src/wyoming/event.rs McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs
git commit -m "feat(voice): bump the Wyoming protocol to 1.8 for speaker-volume"
```

---

### Task 2: Phrase settings and the matcher

Pure text matching, no I/O and no dependency on anything else. Build it first so the dispatcher has something real to call.

**Files:**
- Create: `McpChannelVoice/Settings/CommandSettings.cs`
- Modify: `McpChannelVoice/Settings/VoiceSettings.cs`
- Create: `McpChannelVoice/Services/VoiceCommandMatcher.cs`
- Create: `Tests/Unit/McpChannelVoice/VoiceCommandMatcherTests.cs`

**Interfaces:**
- Consumes: nothing
- Produces:
  - `enum VoiceCommand { LocalVolumeUp, LocalVolumeDown, LocalMute, LocalUnmute }` in namespace `McpChannelVoice.Services`
  - `sealed class VoiceCommandMatcher(CommandSettings settings)` with `VoiceCommand? Match(string? transcript)`
  - `record CommandSettings { bool Enabled; CommandPhrases Phrases; }` and `record CommandPhrases` with four `IReadOnlyList<string>` properties, all in namespace `McpChannelVoice.Settings`
  - `VoiceSettings.Commands` of type `CommandSettings`

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/VoiceCommandMatcherTests.cs`:

```csharp
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceCommandMatcherTests
{
    private static VoiceCommandMatcher Build(bool enabled = true) =>
        new(new CommandSettings
        {
            Enabled = enabled,
            Phrases = new CommandPhrases
            {
                LocalVolumeUp = ["sube el volumen local", "sube el altavoz"],
                LocalVolumeDown = ["baja el volumen local"],
                LocalMute = ["silencia el altavoz"],
                LocalUnmute = ["quita el silencio local"]
            }
        });

    [Fact]
    public void Match_ExactPhrase_ReturnsCommand()
    {
        Build().Match("sube el volumen local").ShouldBe(VoiceCommand.LocalVolumeUp);
        Build().Match("baja el volumen local").ShouldBe(VoiceCommand.LocalVolumeDown);
        Build().Match("silencia el altavoz").ShouldBe(VoiceCommand.LocalMute);
        Build().Match("quita el silencio local").ShouldBe(VoiceCommand.LocalUnmute);
    }

    [Fact]
    public void Match_SecondAliasForSameCommand_ReturnsCommand()
    {
        Build().Match("sube el altavoz").ShouldBe(VoiceCommand.LocalVolumeUp);
    }

    // Whisper emits accents, casing and trailing punctuation; the configured phrases are written
    // plain. Both sides go through the same normalization so config stays readable.
    [Fact]
    public void Match_DifferentCaseAccentsAndPunctuation_StillMatches()
    {
        Build().Match("¡Sube el volumen LOCAL!").ShouldBe(VoiceCommand.LocalVolumeUp);
        Build().Match("  sube   el  volumen   local  ").ShouldBe(VoiceCommand.LocalVolumeUp);
        Build().Match("Silencia el altavóz.").ShouldBe(VoiceCommand.LocalMute);
    }

    // The whole point of whole-transcript matching: a compound request belongs to the agent, not
    // to the fast-path, or the rest of the sentence is silently thrown away.
    [Fact]
    public void Match_CommandEmbeddedInALongerSentence_ReturnsNull()
    {
        Build().Match("sube el volumen local y apaga la luz").ShouldBeNull();
        Build().Match("puedes sube el volumen local").ShouldBeNull();
    }

    [Fact]
    public void Match_UnknownPhrase_ReturnsNull()
    {
        Build().Match("que hora es").ShouldBeNull();
        Build().Match("sube el volumen").ShouldBeNull(); // no local marker: this is Music Assistant
    }

    [Fact]
    public void Match_EmptyOrNullTranscript_ReturnsNull()
    {
        Build().Match(null).ShouldBeNull();
        Build().Match("").ShouldBeNull();
        Build().Match("   ").ShouldBeNull();
    }

    [Fact]
    public void Match_Disabled_ReturnsNullForEveryPhrase()
    {
        Build(enabled: false).Match("sube el volumen local").ShouldBeNull();
    }

    [Fact]
    public void Match_NoPhrasesConfigured_ReturnsNull()
    {
        var empty = new VoiceCommandMatcher(new CommandSettings());
        empty.Match("sube el volumen local").ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run the tests and watch them fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceCommandMatcherTests"
```

Expected: FAIL to compile — `VoiceCommandMatcher`, `VoiceCommand`, `CommandSettings` and `CommandPhrases` do not exist.

- [ ] **Step 3: Add the settings records**

Create `McpChannelVoice/Settings/CommandSettings.cs`:

```csharp
namespace McpChannelVoice.Settings;

// Phrases the voice hub answers itself, without involving the agent or an LLM. Every phrase
// carries an explicit "local" marker so an ordinary music-volume request ("sube el volumen"),
// which belongs to the agent and Music Assistant, can never match one by accident.
public record CommandSettings
{
    public bool Enabled { get; init; } = true;
    public CommandPhrases Phrases { get; init; } = new();
}

public record CommandPhrases
{
    public IReadOnlyList<string> LocalVolumeUp { get; init; } = [];
    public IReadOnlyList<string> LocalVolumeDown { get; init; } = [];
    public IReadOnlyList<string> LocalMute { get; init; } = [];
    public IReadOnlyList<string> LocalUnmute { get; init; } = [];
}
```

- [ ] **Step 4: Add `Commands` to `VoiceSettings`**

In `McpChannelVoice/Settings/VoiceSettings.cs`, after the `Arbitration` line (line 18):

```csharp
    public CommandSettings Commands { get; init; } = new();
```

- [ ] **Step 5: Write the matcher**

Create `McpChannelVoice/Services/VoiceCommandMatcher.cs`:

```csharp
using System.Globalization;
using System.Text;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public enum VoiceCommand
{
    LocalVolumeUp,
    LocalVolumeDown,
    LocalMute,
    LocalUnmute
}

public sealed class VoiceCommandMatcher
{
    private readonly Dictionary<string, VoiceCommand> _phrases;

    public VoiceCommandMatcher(CommandSettings settings)
    {
        _phrases = settings.Enabled
            ? new[]
                {
                    (settings.Phrases.LocalVolumeUp, VoiceCommand.LocalVolumeUp),
                    (settings.Phrases.LocalVolumeDown, VoiceCommand.LocalVolumeDown),
                    (settings.Phrases.LocalMute, VoiceCommand.LocalMute),
                    (settings.Phrases.LocalUnmute, VoiceCommand.LocalUnmute)
                }
                .SelectMany(entry => entry.Item1.Select(phrase => (Key: Normalize(phrase), entry.Item2)))
                .Where(entry => entry.Key.Length > 0)
                .GroupBy(entry => entry.Key)
                .ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.Ordinal)
            : [];
    }

    // Whole-transcript match only. A command buried in a longer sentence is part of a request the
    // agent has to answer, and swallowing it here would silently drop the rest of what was said.
    public VoiceCommand? Match(string? transcript) =>
        transcript is not null && _phrases.TryGetValue(Normalize(transcript), out var command)
            ? command
            : null;

    // Whisper returns accented, capitalised, punctuated Spanish; the configured phrases are
    // written plain. Both sides run through this so config stays readable and a stray "¿...?"
    // cannot defeat a match.
    private static string Normalize(string text)
    {
        var folded = text
            .Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Where(c => !char.IsPunctuation(c) && !char.IsSymbol(c))
            .Select(char.ToLowerInvariant)
            .ToArray();

        return string.Join(' ', new string(folded).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
```

- [ ] **Step 6: Run the tests and watch them pass**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceCommandMatcherTests"
```

Expected: PASS, 8 tests.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Settings/CommandSettings.cs McpChannelVoice/Settings/VoiceSettings.cs \
        McpChannelVoice/Services/VoiceCommandMatcher.cs Tests/Unit/McpChannelVoice/VoiceCommandMatcherTests.cs
git commit -m "feat(voice): match local speaker commands against a phrase table"
```

---

### Task 3: A control-event write path on the session

`SatelliteSession` has a playback channel but no way to write a control event — the `WyomingClient` lives inside `WyomingSatelliteHost`'s per-connection scope. Both the fast-path (Task 4) and the alert hold (Task 5) need one.

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs`
- Modify: `McpChannelVoice/Services/WyomingSatelliteHost.cs:140-141` and its teardown at `:281`
- Create: `Tests/Unit/McpChannelVoice/SatelliteSessionControlTests.cs`

**Interfaces:**
- Consumes: `WyomingEvent.Header(string, JsonObject)` from `McpChannelVoice.Services.WyomingProtocol`
- Produces on `SatelliteSession`:
  - `Func<WyomingEvent, CancellationToken, Task>? ControlWriter { get; set; }`
  - `Task<bool> TrySendControlAsync(WyomingEvent evt, CancellationToken ct)` — returns false when no writer is attached or the write throws; never propagates

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/McpChannelVoice/SatelliteSessionControlTests.cs`:

```csharp
using System.Text.Json.Nodes;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionControlTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static WyomingEvent Event() =>
        WyomingEvent.Header("speaker-volume", new JsonObject { ["action"] = "up" });

    [Fact]
    public async Task TrySendControlAsync_WriterAttached_WritesAndReturnsTrue()
    {
        var session = Session();
        var written = new List<WyomingEvent>();
        session.ControlWriter = (evt, _) => { written.Add(evt); return Task.CompletedTask; };

        var sent = await session.TrySendControlAsync(Event(), default);

        sent.ShouldBeTrue();
        written.Count.ShouldBe(1);
        written[0].Type.ShouldBe("speaker-volume");
        written[0].Data["action"]!.GetValue<string>().ShouldBe("up");
    }

    // No writer means the satellite is not connected. A fast-path command must not throw on the
    // dispatch path just because a connection went away between transcript and action.
    [Fact]
    public async Task TrySendControlAsync_NoWriter_ReturnsFalse()
    {
        (await Session().TrySendControlAsync(Event(), default)).ShouldBeFalse();
    }

    [Fact]
    public async Task TrySendControlAsync_WriterThrows_ReturnsFalseWithoutPropagating()
    {
        var session = Session();
        session.ControlWriter = (_, _) => throw new IOException("socket closed");

        (await session.TrySendControlAsync(Event(), default)).ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionControlTests"
```

Expected: FAIL to compile — `ControlWriter` and `TrySendControlAsync` do not exist.

- [ ] **Step 3: Add the write path to the session**

In `McpChannelVoice/Services/SatelliteSession.cs`, after the `Config` property (line 74):

```csharp
    // Writes a control event on this satellite's live Wyoming connection. Set by
    // WyomingSatelliteHost when the connection is established and cleared on teardown, because the
    // WyomingClient itself lives only inside that per-connection scope. Null means not connected.
    public Func<WyomingEvent, CancellationToken, Task>? ControlWriter { get; set; }

    public async Task<bool> TrySendControlAsync(WyomingEvent evt, CancellationToken ct)
    {
        var writer = ControlWriter;
        if (writer is null)
        {
            return false;
        }

        try
        {
            await writer(evt, ct);
            return true;
        }
        catch (Exception)
        {
            // A control event is best-effort: the connection may be tearing down underneath us, and
            // a failed volume step must not take out the caller's path (a transcript dispatch or an
            // alarm loop). Callers log the false.
            return false;
        }
    }
```

- [ ] **Step 4: Run the tests and watch them pass**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionControlTests"
```

Expected: PASS, 3 tests.

- [ ] **Step 5: Attach the writer in the host**

In `McpChannelVoice/Services/WyomingSatelliteHost.cs`, immediately after line 140 (`var session = new SatelliteSession(id, config);`) and before `sessionRegistry.Register(session);`:

```csharp
        // The WyomingClient lives only in this scope, so hand the session a writer for control
        // events raised from outside it — the transcript fast-path and the insistent alert hold.
        session.ControlWriter = (evt, ct2) => client.WriteAsync(evt, ct2);
```

In the teardown `finally` at line 281, immediately before `sessionRegistry.Unregister(id);`:

```csharp
            session.ControlWriter = null;
```

- [ ] **Step 6: Build**

```bash
dotnet build McpChannelVoice/McpChannelVoice.csproj
```

Expected: build succeeds. If `session` is not in scope at line 281, move the assignment into the same scope where `sessionRegistry.Unregister(id)` can see it, or capture the session in a local declared before the `try`.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSession.cs McpChannelVoice/Services/WyomingSatelliteHost.cs \
        Tests/Unit/McpChannelVoice/SatelliteSessionControlTests.cs
git commit -m "feat(voice): give SatelliteSession a control-event write path"
```

---

### Task 4: The transcript fast-path

**Files:**
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs:54-61`
- Modify: `McpChannelVoice/appsettings.json`
- Modify: `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`

**Interfaces:**
- Consumes: `VoiceCommandMatcher.Match`, `VoiceCommand` (Task 2); `SatelliteSession.TrySendControlAsync` (Task 3)
- Produces: `TranscriptDispatcher` constructor gains a `VoiceCommandMatcher matcher` parameter, inserted **after** `VoiceConversationManager manager` and before `double avgLogProbThreshold`

- [ ] **Step 1: Write the failing tests**

In `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`, replace the `Build()` helper so it wires a matcher and returns the session, then add the new tests. The full replacement for the helper block:

```csharp
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static CommandSettings Commands(bool enabled = true) =>
        new()
        {
            Enabled = enabled,
            Phrases = new CommandPhrases
            {
                LocalVolumeUp = ["sube el volumen local"],
                LocalVolumeDown = ["baja el volumen local"],
                LocalMute = ["silencia el altavoz"],
                LocalUnmute = ["quita el silencio local"]
            }
        };

    private static (TranscriptDispatcher Sut, VoiceConversationManager Manager, CapturingEmitter Emitter) Build(
        CommandSettings? commands = null)
    {
        var factory = new Mock<IConversationFactory>();
        factory.Setup(f => f.CreateAsync(It.IsAny<CreateConversationParams>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                var identity = ConversationIdGenerator.CreateFor("topic-x");
                var topic = new TopicMetadata("topic-x", identity.ChatId, identity.ThreadId, "agent-1",
                    "household @ Kitchen", DateTimeOffset.UtcNow, null);
                return new ConversationCreation(identity, topic);
            });

        var manager = new VoiceConversationManager(
            factory.Object, new ReplyTextAccumulator(), new FakeTimeProvider(DateTimeOffset.UtcNow),
            TimeSpan.FromMinutes(5), NullLogger<VoiceConversationManager>.Instance);

        var emitter = new CapturingEmitter();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var sut = new TranscriptDispatcher(
            emitter, Mock.Of<IMetricsPublisher>(), manager,
            new VoiceCommandMatcher(commands ?? new CommandSettings()),
            avgLogProbThreshold: -1.0, noSpeechProbThreshold: 0.6, time, NullLogger<TranscriptDispatcher>.Instance);
        return (sut, manager, emitter);
    }
```

Add these tests to the class (they need `using System.Text.Json.Nodes;` and `using McpChannelVoice.Services.WyomingProtocol;` at the top of the file):

```csharp
    [Fact]
    public async Task DispatchAsync_LocalCommand_SendsSpeakerVolumeAndSkipsTheAgent()
    {
        var (sut, manager, emitter) = Build(Commands());
        var session = Session();
        var written = new List<WyomingEvent>();
        session.ControlWriter = (evt, _) => { written.Add(evt); return Task.CompletedTask; };

        var dispatched = await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "sube el volumen local", Confidence = 0.9 },
            "agent-1", null, null, null, default);

        dispatched.ShouldBeFalse();
        written.Count.ShouldBe(1);
        written[0].Type.ShouldBe("speaker-volume");
        written[0].Data["action"]!.GetValue<string>().ShouldBe("up");
        emitter.Captured.ShouldBeEmpty();
        manager.GetActiveConversationId("kitchen-01").ShouldBeNull();
    }

    [Theory]
    [InlineData("sube el volumen local", "up")]
    [InlineData("baja el volumen local", "down")]
    [InlineData("silencia el altavoz", "mute")]
    [InlineData("quita el silencio local", "unmute")]
    public async Task DispatchAsync_EachLocalCommand_SendsItsAction(string text, string action)
    {
        var (sut, _, _) = Build(Commands());
        var session = Session();
        var written = new List<WyomingEvent>();
        session.ControlWriter = (evt, _) => { written.Add(evt); return Task.CompletedTask; };

        await sut.DispatchAsync(
            session, new TranscriptionResult { Text = text, Confidence = 0.9 }, "agent-1", null, null, null, default);

        written[0].Data["action"]!.GetValue<string>().ShouldBe(action);
    }

    // The gibberish gate runs first on purpose: acting on audio the STT itself flagged as poor is
    // exactly the misfire the gate exists to prevent.
    [Fact]
    public async Task DispatchAsync_LowQualityTranscriptMatchingAPhrase_IsDroppedNotExecuted()
    {
        var (sut, _, emitter) = Build(Commands());
        var session = Session();
        var written = new List<WyomingEvent>();
        session.ControlWriter = (evt, _) => { written.Add(evt); return Task.CompletedTask; };

        var dispatched = await sut.DispatchAsync(
            session,
            new TranscriptionResult { Text = "sube el volumen local", AvgLogProb = -5.0 },
            "agent-1", null, null, null, default);

        dispatched.ShouldBeFalse();
        written.ShouldBeEmpty();
        emitter.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_NoControlWriter_ReturnsFalseWithoutThrowing()
    {
        var (sut, _, emitter) = Build(Commands());

        var dispatched = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "sube el volumen local", Confidence = 0.9 },
            "agent-1", null, null, null, default);

        dispatched.ShouldBeFalse();
        emitter.Captured.ShouldBeEmpty();
    }

    // "sube el volumen" without the local marker is a Music Assistant request and belongs to the
    // agent. It must travel the normal path untouched.
    [Fact]
    public async Task DispatchAsync_MusicVolumeRequest_StillReachesTheAgent()
    {
        var (sut, _, emitter) = Build(Commands());
        var session = Session();
        var written = new List<WyomingEvent>();
        session.ControlWriter = (evt, _) => { written.Add(evt); return Task.CompletedTask; };

        var dispatched = await sut.DispatchAsync(
            session, new TranscriptionResult { Text = "sube el volumen", Confidence = 0.9 },
            "agent-1", null, null, null, default);

        dispatched.ShouldBeTrue();
        written.ShouldBeEmpty();
        emitter.Captured.Count.ShouldBe(1);
        emitter.Captured[0].Content.ShouldBe("sube el volumen");
    }
```

- [ ] **Step 2: Run the tests and watch them fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests"
```

Expected: FAIL to compile — `TranscriptDispatcher` has no `VoiceCommandMatcher` parameter.

- [ ] **Step 3: Add the fast-path to the dispatcher**

In `McpChannelVoice/Services/TranscriptDispatcher.cs`, add `matcher` to the primary constructor between `manager` and `avgLogProbThreshold`:

```csharp
public sealed class TranscriptDispatcher(
    ChannelNotificationEmitter emitter,
    IMetricsPublisher publisher,
    VoiceConversationManager manager,
    VoiceCommandMatcher matcher,
    double avgLogProbThreshold,
    double noSpeechProbThreshold,
    TimeProvider timeProvider,
    ILogger<TranscriptDispatcher> logger)
```

Add `using McpChannelVoice.Services.WyomingProtocol;` and `using System.Text.Json.Nodes;` to the top of the file.

Then insert this block immediately after the low-quality `if` block closes (after line 66's `}`) and before the `var conversationId = ...` line:

```csharp
        // Local speaker commands are answered here and never reach the agent. Placed AFTER the
        // quality gate (garbage audio must not move a volume knob) and BEFORE GetOrCreateAsync,
        // which is a full create_conversation MCP round trip — matching first keeps the path fast
        // and keeps these out of conversation history.
        if (matcher.Match(transcript.Text) is { } command)
        {
            var action = command switch
            {
                VoiceCommand.LocalVolumeUp => "up",
                VoiceCommand.LocalVolumeDown => "down",
                VoiceCommand.LocalMute => "mute",
                VoiceCommand.LocalUnmute => "unmute",
                _ => null
            };

            var sent = action is not null && await session.TrySendControlAsync(
                WyomingEvent.Header("speaker-volume", new JsonObject { ["action"] = action }), ct);

            logger.LogInformation(
                "Local command {Command} for {Satellite}: sent={Sent}", command, session.SatelliteId, sent);

            await publisher.PublishAsync(
                new VoiceEvent
                {
                    Metric = VoiceMetric.UtteranceTranscribed,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    Outcome = sent ? "command" : "command_failed",
                    Confidence = transcript.Confidence,
                    Similarity = similarity,
                    AvgLogProb = transcript.AvgLogProb,
                    NoSpeechProb = transcript.NoSpeechProb,
                    CompressionRatio = transcript.CompressionRatio,
                    PeakRms = stats?.PeakRms,
                    SpeechMs = stats?.SpeechMs,
                    FloorRms = stats?.FloorRms,
                    TrailingRms = stats?.TrailingRms,
                    EndReason = stats?.EndReason,
                    ConversationId = manager.GetActiveConversationId(session.SatelliteId)
                },
                ct);

            // False means "nothing reached the agent", which FollowUpConversation already turns into
            // EndConversation — the satellite gets its closing transcript and re-arms. No new
            // turn-end path is needed.
            return false;
        }
```

- [ ] **Step 4: Run the tests and watch them pass**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests"
```

Expected: PASS. Existing tests in the class still pass because the default `CommandSettings` has no phrases.

- [ ] **Step 5: Wire the matcher into DI**

In `McpChannelVoice/Modules/ConfigModule.cs`, add a registration and pass it to the dispatcher. Replace lines 52-61 with:

```csharp
        services
            .AddSingleton<SatelliteSessionRegistry>()
            .AddSingleton(new VoiceCommandMatcher(settings.Commands))
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                sp.GetRequiredService<VoiceConversationManager>(),
                sp.GetRequiredService<VoiceCommandMatcher>(),
                avgLogProbThreshold: settings.Stt.OpenAi.AvgLogProbThreshold,
                noSpeechProbThreshold: settings.Stt.OpenAi.NoSpeechProbThreshold,
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<TranscriptDispatcher>>()))
```

- [ ] **Step 6: Add the default phrase table**

In `McpChannelVoice/appsettings.json`, inside the same object that holds `Satellites` and `Announce`, add:

```json
    "Commands": {
        "Enabled": true,
        "Phrases": {
            "LocalVolumeUp": ["sube el volumen local", "sube el altavoz"],
            "LocalVolumeDown": ["baja el volumen local", "baja el altavoz"],
            "LocalMute": ["silencia el altavoz", "mute local"],
            "LocalUnmute": ["quita el silencio local", "unmute local"]
        }
    },
```

Match the surrounding indentation exactly — check the file first, it uses 4 spaces.

- [ ] **Step 7: Run the whole voice unit suite**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpChannelVoice"
```

Expected: PASS. `ConfigModuleTests` constructs a `TranscriptDispatcher` directly at lines 96-106 — if it fails to compile, add `new VoiceCommandMatcher(new CommandSettings())` in the same position there.

- [ ] **Step 8: Commit**

```bash
git add McpChannelVoice/Services/TranscriptDispatcher.cs McpChannelVoice/Modules/ConfigModule.cs \
        McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/
git commit -m "feat(voice): answer local speaker commands before the agent dispatch"
```

---

### Task 5: Alert hold around the insistent loop

A local mute must not swallow a timer or an alarm. The hub owns the loop's start and end, so it drives the hold — `AnnounceSettings.GapSeconds` is per-request overridable, so a satellite-side timer would be guessing.

**Files:**
- Modify: `McpChannelVoice/Services/InsistentAnnouncementController.cs:74-143`
- Modify or create: `Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs`

**Interfaces:**
- Consumes: `SatelliteSession.TrySendControlAsync` (Task 3)
- Produces: no new public API — behaviour only

- [ ] **Step 1: Write the failing tests**

`Tests/Unit/McpChannelVoice/InsistentAnnouncementControllerTests.cs` already exists with a
`BuildHarness` / `PumpPlays` / `DrainPumpAsync` / `WaitUntilAsync` set of helpers. Add
`using System.Text.Json.Nodes;` to the file, then add these two tests plus the shared recorder:

```csharp
    // Records the speaker-volume actions the controller writes to a satellite.
    private static Func<IReadOnlyList<string>> RecordVolumeActions(SatelliteSession session)
    {
        var actions = new List<string>();
        session.ControlWriter = (evt, _) =>
        {
            if (evt.Type == "speaker-volume")
            {
                lock (actions)
                { actions.Add(evt.Data["action"]!.GetValue<string>()); }
            }
            return Task.CompletedTask;
        };
        return () => { lock (actions) { return actions.ToList(); } };
    }

    // A local mute must never swallow a timer or an alarm: the hold unmutes for the ring and the
    // release puts the user's mute back. Both bracket the loop, so the speaker is audible for the
    // whole insistent sequence rather than for one round of it.
    [Fact]
    public async Task Start_InsistentAlert_BracketsTheRingWithAlertHoldAndRelease()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var session = h.Sessions.Get("kitchen-01")!;
        var actions = RecordVolumeActions(session);
        var (pump, _) = PumpPlays(session, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "eggs",
                Kind = AnnounceKind.Timer,
                Insistent = new() { MaxRepeats = 1 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => actions().Contains("alert-release"), TimeSpan.FromSeconds(5));
        actions().ShouldBe(["alert-hold", "alert-release"]);

        await DrainPumpAsync(pump, time, session);
    }

    // The release lives in the loop's finally, so it has to fire on the path that ends an alarm in
    // practice — someone dismissing it — not only on repeats running out.
    [Fact]
    public async Task Start_DismissedMidRing_StillSendsAlertRelease()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var h = BuildHarness(time, online: true, satelliteIds: "kitchen-01");
        var session = h.Sessions.Get("kitchen-01")!;
        var actions = RecordVolumeActions(session);
        var (pump, _) = PumpPlays(session, time);

        await h.Controller.StartAsync(
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "wake up",
                Kind = AnnounceKind.Alarm,
                Insistent = new() { MaxRepeats = 12 }
            },
            CancellationToken.None);

        await WaitUntilAsync(() => actions().Contains("alert-hold"), TimeSpan.FromSeconds(5));
        h.Alerts.DismissAll();

        await WaitUntilAsync(() => actions().Contains("alert-release"), TimeSpan.FromSeconds(5));

        await DrainPumpAsync(pump, time, session);
    }
```

- [ ] **Step 2: Run the tests and watch them fail**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnouncementControllerTests"
```

Expected: FAIL — no `speaker-volume` events are recorded.

- [ ] **Step 3: Send the hold and release**

In `McpChannelVoice/Services/InsistentAnnouncementController.cs`, add `using System.Text.Json.Nodes;` and `using McpChannelVoice.Services.WyomingProtocol;` if absent.

Add this private helper to the class:

```csharp
    // A local mute is the user's, but it must not silence a timer or an alarm. The hold unmutes
    // the speaker for the duration of the ring; the release restores whatever the user had set.
    // Both are best-effort: a satellite that never got the hold simply rings at its current level.
    private async Task SendAlertHoldAsync(IReadOnlyList<string> targetIds, string action)
    {
        var evt = WyomingEvent.Header("speaker-volume", new JsonObject { ["action"] = action });
        foreach (var session in OnlineSessions(targetIds))
        {
            await session.TrySendControlAsync(evt, CancellationToken.None);
        }
    }
```

In `RunLoopAsync`, after `var buffered = await BufferAudioAsync(handle.Token);` and before `var start = time.GetTimestamp();`:

```csharp
            await SendAlertHoldAsync(targetIds, "alert-hold");
```

In the `finally` at line 139-142, before `alerts.Discard(handle);`:

```csharp
            await SendAlertHoldAsync(targetIds, "alert-release");
```

Note: the `finally` becomes `async`-bearing, which is fine — `RunLoopAsync` is already `async Task`.

- [ ] **Step 4: Run the tests and watch them pass**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~InsistentAnnouncementControllerTests"
```

Expected: PASS.

- [ ] **Step 5: Run the whole voice unit suite**

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpChannelVoice"
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/Services/InsistentAnnouncementController.cs Tests/Unit/McpChannelVoice/
git commit -m "feat(voice): unmute a satellite for the length of an insistent alert"
```

---

### Task 6: Satellite volume configuration flags

**Files:**
- Modify: `satellite/src/config.rs`

**Interfaces:**
- Consumes: nothing
- Produces on `Config`: `pub volume_sink: Option<String>` (default `None`), `pub volume_step: u8` (default `10`); flags `--volume-sink <name>` and `--volume-step <pct>`

- [ ] **Step 1: Write the failing tests**

Add to the `tests` module in `satellite/src/config.rs`:

```rust
    #[test]
    fn volume_flags_parse_and_default_off() {
        let off = Config::default();
        assert_eq!(off.volume_sink, None, "no sink configured = local volume control off");
        assert_eq!(off.volume_step, 10);

        let on = Config::parse(pico_args::Arguments::from_vec(vec![
            "--volume-sink".into(), "@DEFAULT_AUDIO_SINK@".into(),
            "--volume-step".into(), "5".into(),
        ]))
        .unwrap();
        assert_eq!(on.volume_sink.as_deref(), Some("@DEFAULT_AUDIO_SINK@"));
        assert_eq!(on.volume_step, 5);
    }

    /// A zero step would make every command a silent no-op that still beeps, which reads as
    /// broken hardware rather than as misconfiguration.
    #[test]
    fn zero_volume_step_is_rejected() {
        assert!(Config::parse(args(&["--volume-step", "0"])).is_err());
    }
```

- [ ] **Step 2: Run them and watch them fail**

```bash
cd satellite && cargo test config::tests::volume
```

Expected: FAIL to compile — no field `volume_sink` on `Config`.

- [ ] **Step 3: Add the fields and parsing**

In `satellite/src/config.rs`, add to the `Config` struct after `music_restore_grace_ms`:

```rust
    // The satellite's own master output level — the PipeWire sink everything ultimately feeds,
    // driven with wpctl. Distinct from the per-source ALSA softvols (Music / TTS / Alert): this is
    // the physical volume knob an amp HAT like the MiniAmp does not have. None = feature off,
    // mirroring music_mixer, because PipeWire is installed only on music units.
    pub volume_sink: Option<String>,
    pub volume_step: u8,
```

In `Default`:

```rust
            volume_sink: None,
            volume_step: 10,
```

In `parse`, after the `--music-restore-grace-ms` line:

```rust
        if let Some(v) = pa.opt_value_from_str::<_, String>("--volume-sink")? { c.volume_sink = Some(v); }
        if let Some(v) = pa.opt_value_from_str::<_, u8>("--volume-step")? {
            anyhow::ensure!(v >= 1, "--volume-step must be at least 1 (got {v})");
            c.volume_step = v;
        }
```

Add both flags to the doc comment above `from_args`:

```rust
    ///        --volume-sink <name> --volume-step <pct>
```

- [ ] **Step 4: Run them and watch them pass**

```bash
cd satellite && cargo test config
```

Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add satellite/src/config.rs
git commit -m "feat(satellite): add --volume-sink and --volume-step flags"
```

---

### Task 7: The volume control module

**Files:**
- Create: `satellite/src/volume.rs`
- Modify: `satellite/src/main.rs` (add `mod volume;`)

**Interfaces:**
- Consumes: `crate::audio::build_command` (already exists, `pub(crate)`)
- Produces in `crate::volume`:
  - `pub struct VolumeControl`
  - `pub fn new(sink: Option<String>, step: u8) -> Arc<VolumeControl>`
  - `pub fn enabled(&self) -> bool`
  - `pub fn user_muted(&self) -> bool`
  - `pub async fn seed(&self)` — read the sink's mute state once at startup
  - `pub async fn step(&self, up: bool) -> anyhow::Result<()>`
  - `pub async fn set_sink_mute(&self, muted: bool) -> anyhow::Result<()>` — sink only, leaves `user_muted` alone
  - `pub async fn set_user_mute(&self, muted: bool) -> anyhow::Result<()>` — sink plus `user_muted`, rolled back on failure
  - `pub fn restore_user_mute_detached(&self)` — synchronous fire-and-forget, for `Drop`

- [ ] **Step 1: Write the failing tests**

Create `satellite/src/volume.rs` with only the tests and a `use super::*;`, so the module compiles as a test-first shell. Write the whole file including tests now, but leave the implementation for Step 3 — that is, write this test module first and let it fail:

```rust
#[cfg(test)]
mod tests {
    use super::*;

    fn probe(step: u8) -> (Arc<std::sync::Mutex<Vec<String>>>, Arc<VolumeControl>) {
        VolumeControl::probe_pair(step)
    }

    #[tokio::test]
    async fn step_up_and_down_issue_relative_wpctl_calls_capped_at_unity() {
        let (log, vol) = probe(10);
        vol.step(true).await.unwrap();
        vol.step(false).await.unwrap();
        let calls = log.lock().unwrap().clone();
        assert_eq!(calls.len(), 2);
        assert!(calls[0].contains("set-volume"), "got {}", calls[0]);
        assert!(calls[0].contains("10%+"), "got {}", calls[0]);
        assert!(calls[0].contains("-l 1.0"), "a step must not push the sink past unity: {}", calls[0]);
        assert!(calls[1].contains("10%-"), "got {}", calls[1]);
    }

    #[tokio::test]
    async fn set_user_mute_tracks_the_users_intent() {
        let (_, vol) = probe(10);
        assert!(!vol.user_muted());
        vol.set_user_mute(true).await.unwrap();
        assert!(vol.user_muted());
        vol.set_user_mute(false).await.unwrap();
        assert!(!vol.user_muted());
    }

    /// The alert hold unmutes the sink for a ringing alarm WITHOUT forgetting that the user asked
    /// for silence — otherwise the release would have nothing to restore.
    #[tokio::test]
    async fn set_sink_mute_does_not_touch_the_users_intent() {
        let (log, vol) = probe(10);
        vol.set_user_mute(true).await.unwrap();
        vol.set_sink_mute(false).await.unwrap();
        assert!(vol.user_muted(), "the hold must not clear the user's mute");
        let calls = log.lock().unwrap().clone();
        assert!(calls[0].contains("set-mute") && calls[0].ends_with('1'), "got {}", calls[0]);
        assert!(calls[1].contains("set-mute") && calls[1].ends_with('0'), "got {}", calls[1]);
    }

    /// A failed set-mute must not leave the satellite believing a mute that never landed: the
    /// next alert-release would then mute a speaker the user never silenced.
    #[tokio::test]
    async fn failed_set_user_mute_rolls_the_tracked_state_back() {
        let vol = VolumeControl::failing(10);
        assert!(vol.set_user_mute(true).await.is_err());
        assert!(!vol.user_muted(), "a failed mute must not be remembered as muted");
    }

    #[tokio::test]
    async fn disabled_control_makes_every_action_a_no_op() {
        let vol = VolumeControl::new(None, 10);
        assert!(!vol.enabled());
        vol.step(true).await.unwrap();
        vol.set_user_mute(true).await.unwrap();
        assert!(!vol.user_muted(), "a disabled control tracks nothing");
    }
}
```

- [ ] **Step 2: Run and watch it fail**

```bash
cd satellite && cargo test volume
```

Expected: FAIL to compile — `VolumeControl` does not exist. (`mod volume;` must already be in `main.rs`; add it in this step if the compiler says the file is not part of the crate.)

- [ ] **Step 3: Implement the module**

Put this above the test module in `satellite/src/volume.rs`:

```rust
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use tracing::warn;

/// The satellite's own MASTER output level: the PipeWire sink that music, replies, cues and
/// alerts all end up in, driven with `wpctl`. This is deliberately not one of the per-source ALSA
/// softvols (`Music` / `TTS` / `Alert`) — those carry calibration, and `Music` is written by the
/// ducker on every turn. Driving the master keeps the two independent: they simply multiply.
///
/// Wireplumber persists the sink's level and mute in its own state, so nothing here is written to
/// disk and a level survives a restart on its own.
pub struct VolumeControl {
    backend: Backend,
    step: u8,
    user_muted: AtomicBool,
}

enum Backend {
    /// No sink configured: PipeWire is installed only on music units, so a voice-only satellite
    /// has nothing to drive. Mirrors `music_mixer: None` disabling ducking.
    Disabled,
    Real { sink: String },
    #[cfg(test)]
    Probe(Arc<std::sync::Mutex<Vec<String>>>),
    #[cfg(test)]
    Failing,
}

impl VolumeControl {
    pub fn new(sink: Option<String>, step: u8) -> Arc<Self> {
        let backend = match sink {
            Some(s) => Backend::Real { sink: s },
            None => Backend::Disabled,
        };
        Arc::new(Self { backend, step, user_muted: AtomicBool::new(false) })
    }

    /// pub(crate) so state_machine's tests can drive a real control without a wpctl binary.
    #[cfg(test)]
    pub(crate) fn probe_pair(step: u8) -> (Arc<std::sync::Mutex<Vec<String>>>, Arc<Self>) {
        let log = Arc::new(std::sync::Mutex::new(Vec::new()));
        let control =
            Arc::new(Self { backend: Backend::Probe(log.clone()), step, user_muted: AtomicBool::new(false) });
        (log, control)
    }

    #[cfg(test)]
    fn failing(step: u8) -> Arc<Self> {
        Arc::new(Self { backend: Backend::Failing, step, user_muted: AtomicBool::new(false) })
    }

    pub fn enabled(&self) -> bool {
        !matches!(self.backend, Backend::Disabled)
    }

    pub fn user_muted(&self) -> bool {
        self.user_muted.load(Ordering::SeqCst)
    }

    /// Read the sink's current mute once at startup so the satellite's idea of the user's intent
    /// matches what wireplumber restored. A failed or unparsable read leaves it unmuted, which is
    /// the safe direction: the speaker is audible and one spoken command fixes it.
    pub async fn seed(&self) {
        let Backend::Real { sink } = &self.backend else { return };
        match run_capture(&format!("wpctl get-volume {sink}")).await {
            Ok(out) => {
                let muted = out.contains("[MUTED]");
                self.user_muted.store(muted, Ordering::SeqCst);
                tracing::info!(muted, "seeded local volume mute state");
            }
            Err(e) => warn!("could not read sink mute state, assuming unmuted: {e:#}"),
        }
    }

    /// `-l 1.0` caps the sink at unity, so repeated steps up cannot push it into software gain.
    pub async fn step(&self, up: bool) -> anyhow::Result<()> {
        let sign = if up { '+' } else { '-' };
        self.run(&format!("set-volume -l 1.0 {{sink}} {}%{sign}", self.step)).await
    }

    /// Sets the sink only. The alert hold uses this to unmute a ringing alarm without forgetting
    /// that the user asked for silence.
    pub async fn set_sink_mute(&self, muted: bool) -> anyhow::Result<()> {
        self.run(&format!("set-mute {{sink}} {}", u8::from(muted))).await
    }

    /// Sets the sink AND records the user's intent. Rolled back on failure so a mute that never
    /// landed cannot be re-applied later by an alert release.
    pub async fn set_user_mute(&self, muted: bool) -> anyhow::Result<()> {
        if !self.enabled() {
            warn!("local mute ignored: no --volume-sink configured");
            return Ok(()); // track nothing, so a later alert release has nothing to restore
        }

        let previous = self.user_muted();
        self.user_muted.store(muted, Ordering::SeqCst);
        if let Err(e) = self.set_sink_mute(muted).await {
            self.user_muted.store(previous, Ordering::SeqCst);
            return Err(e);
        }
        Ok(())
    }

    /// Fail-safe restore for Drop, which cannot await: fire a detached std wpctl, never awaited.
    /// Same shape as music.rs's DuckGuard restore, and for the same reason.
    pub fn restore_user_mute_detached(&self) {
        let Backend::Real { sink } = &self.backend else { return };
        let mut cmd = std::process::Command::new("wpctl");
        cmd.args(["set-mute", sink, if self.user_muted() { "1" } else { "0" }])
            .stdin(std::process::Stdio::null())
            .stdout(std::process::Stdio::null())
            .stderr(std::process::Stdio::null());
        let _ = cmd.spawn();
    }

    async fn run(&self, template: &str) -> anyhow::Result<()> {
        match &self.backend {
            Backend::Disabled => {
                warn!("local volume command ignored: no --volume-sink configured");
                Ok(())
            }
            Backend::Real { sink } => {
                let cmdline = format!("wpctl {}", template.replace("{sink}", sink));
                let status = crate::audio::build_command(&cmdline)
                    .stdout(std::process::Stdio::null())
                    .stderr(std::process::Stdio::null())
                    .status()
                    .await?;
                anyhow::ensure!(status.success(), "wpctl exited with {status}");
                Ok(())
            }
            #[cfg(test)]
            Backend::Probe(log) => {
                log.lock().unwrap().push(template.replace("{sink}", "SINK"));
                Ok(())
            }
            #[cfg(test)]
            Backend::Failing => anyhow::bail!("wpctl failed"),
        }
    }
}

async fn run_capture(cmdline: &str) -> anyhow::Result<String> {
    let out = crate::audio::build_command(cmdline)
        .stderr(std::process::Stdio::null())
        .output()
        .await?;
    anyhow::ensure!(out.status.success(), "command exited with {}", out.status);
    Ok(String::from_utf8_lossy(&out.stdout).into_owned())
}
```

Add `mod volume;` to the module list at the top of `satellite/src/main.rs`, keeping alphabetical order (after `mod satellite;`, before `mod wake;`).

- [ ] **Step 4: Run and watch it pass**

```bash
cd satellite && cargo test volume
```

Expected: PASS, 5 tests.

- [ ] **Step 5: Commit**

```bash
git add satellite/src/volume.rs satellite/src/main.rs
git commit -m "feat(satellite): add the PipeWire master volume control"
```

---

### Task 8: The confirmation cue

**Files:**
- Create: `satellite/sounds/volume.wav`
- Modify: `satellite/src/audio/cues.rs`

**Interfaces:**
- Consumes: `decode_wav_pcm` (already in `cues.rs`)
- Produces: `Cues::volume(&self) -> Option<Vec<u8>>`

- [ ] **Step 1: Generate the tone**

The cue must be 22 050 Hz mono S16LE and audibly different from `awake.wav` and `done.wav`. Listen to those first to pick something distinct:

```bash
cd satellite && soxi sounds/awake.wav sounds/done.wav
```

Generate a short two-tone blip with `sox` (install with `sudo apt-get install -y sox` if missing):

```bash
cd satellite && sox -n -r 22050 -c 1 -b 16 sounds/volume.wav \
  synth 0.06 sine 880 synth 0.06 sine 1320 : \
  fade q 0.005 0 0.02 gain -6
```

If `sox` is unavailable, use ffmpeg:

```bash
cd satellite && ffmpeg -y -f lavfi -i "sine=frequency=880:duration=0.06,volume=0.5" \
  -f lavfi -i "sine=frequency=1320:duration=0.06,volume=0.5" \
  -filter_complex "[0:a][1:a]concat=n=2:v=0:a=1,afade=t=out:st=0.10:d=0.02" \
  -ar 22050 -ac 1 -sample_fmt s16 sounds/volume.wav
```

Verify the format:

```bash
cd satellite && soxi sounds/volume.wav
```

Expected: `Sample Rate: 22050`, `Channels: 1`, `Precision: 16-bit`. Anything else will fail `decode_wav_pcm`'s assertion.

- [ ] **Step 2: Write the failing test**

In `satellite/src/audio/cues.rs`, add to the `tests` module:

```rust
    #[test]
    fn decodes_the_volume_cue() {
        let cues = Cues::new(&Config::default()).unwrap();
        assert!(!cues.volume().unwrap().is_empty());
    }
```

- [ ] **Step 3: Run and watch it fail**

```bash
cd satellite && cargo test cues
```

Expected: FAIL to compile — no method `volume` on `Cues`.

- [ ] **Step 4: Add the cue**

In `satellite/src/audio/cues.rs`:

```rust
const VOLUME_WAV: &[u8] = include_bytes!("../../sounds/volume.wav");
```

Add the field to the struct:

```rust
    pub(crate) volume_pcm: Vec<u8>,
```

Decode it in `new`:

```rust
            volume_pcm: decode_wav_pcm(VOLUME_WAV)?,
```

And the accessor. It is deliberately not behind a flag: it is the only feedback a local command has, and the whole point of the cue is that hearing it means the command landed.

```rust
    /// Confirmation for a local volume or mute command. Deliberately always on: with no reply
    /// spoken and no LED change, it is the only signal that the command landed.
    pub fn volume(&self) -> Option<Vec<u8>> {
        Some(self.volume_pcm.clone())
    }
```

- [ ] **Step 5: Run and watch it pass**

```bash
cd satellite && cargo test cues
```

Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add satellite/sounds/volume.wav satellite/src/audio/cues.rs
git commit -m "feat(satellite): add the local volume confirmation cue"
```

---

### Task 9: A cue that reports when it has finished

Muting the sink silences an in-flight cue, so the mute must wait for the cue to finish. `PlaybackHandle::cue` is fire-and-forget today. The pump already awaits `finish()` on a cue inline, so an acknowledgement sent right after lands at true playback end.

**Files:**
- Modify: `satellite/src/audio/playback.rs`

**Interfaces:**
- Consumes: nothing
- Produces: `PlaybackHandle::cue_then(&self, pcm: Vec<u8>, done: tokio::sync::oneshot::Sender<()>)`, and a `PlaybackCmd::CueThen(Vec<u8>, oneshot::Sender<()>)` variant

- [ ] **Step 1: Write the failing tests**

Add to the `tests` module in `satellite/src/audio/playback.rs`, following the existing test helpers there:

```rust
    /// The mute path depends on this: the acknowledgement must arrive only AFTER the cue has
    /// actually finished playing, or the mute silences its own confirmation.
    #[tokio::test]
    async fn cue_then_acknowledges_after_the_cue_has_played() {
        let (handle, _done_rx, _task) = pump();
        let (tx, rx) = tokio::sync::oneshot::channel();
        handle.cue_then(vec![0u8; 64], tx);
        rx.await.expect("the pump must acknowledge a played cue");
    }

    /// A cue dropped because a stream is active still has to acknowledge, otherwise a pending
    /// mute would hang forever waiting for a sound that is never going to play.
    #[tokio::test]
    async fn cue_then_acknowledges_even_when_the_cue_is_dropped() {
        let (mut handle, _done_rx, _task) = pump();
        handle.start(false).await.unwrap();
        let (tx, rx) = tokio::sync::oneshot::channel();
        handle.cue_then(vec![0u8; 64], tx);
        rx.await.expect("a dropped cue must still acknowledge");
    }
```

- [ ] **Step 2: Run and watch them fail**

```bash
cd satellite && cargo test playback::tests::cue_then
```

Expected: FAIL to compile — no method `cue_then`.

- [ ] **Step 3: Add the variant and the handle method**

In `satellite/src/audio/playback.rs`, add to `PlaybackCmd` after the `Cue` variant:

```rust
    /// Same as `Cue`, plus an acknowledgement sent once the sound has finished — or immediately
    /// if it was dropped. The local mute needs it: muting the sink would otherwise cut off the
    /// cue that confirms the mute.
    CueThen(Vec<u8>, tokio::sync::oneshot::Sender<()>),
```

Add to `impl PlaybackHandle`, next to `cue`:

```rust
    /// try_send like `cue`. A failed send drops the sender, so the waiter resolves at once and
    /// the caller proceeds — which is the wanted behaviour when the pump is backlogged.
    pub fn cue_then(&self, pcm: Vec<u8>, done: tokio::sync::oneshot::Sender<()>) {
        let _ = self.cmd_tx.try_send(PlaybackCmd::CueThen(pcm, done));
    }
```

In `run_pump`'s `match cmd`, after the `PlaybackCmd::Cue` arm:

```rust
            PlaybackCmd::CueThen(pcm, done) => {
                if !streaming {
                    if let Err(e) = play_cue(&snd_command, &pcm).await {
                        tracing::warn!("cue playback failed: {e:#}");
                    }
                }
                // Acknowledge on BOTH paths — played and dropped — so a caller sequencing an
                // action after the sound is never left waiting on one that will not come.
                let _ = done.send(());
                Ok(())
            }
```

- [ ] **Step 4: Run and watch them pass**

```bash
cd satellite && cargo test playback
```

Expected: PASS, including the existing playback tests.

- [ ] **Step 5: Commit**

```bash
git add satellite/src/audio/playback.rs
git commit -m "feat(satellite): let a cue report when it has finished playing"
```

---

### Task 10: Handle `speaker-volume` on the satellite

**Files:**
- Modify: `satellite/src/satellite/state_machine.rs`
- Modify: `satellite/src/main.rs`

**Interfaces:**
- Consumes: `VolumeControl` (Task 7), `Cues::volume` (Task 8), `PlaybackHandle::cue_then` (Task 9), `Config::volume_sink` / `volume_step` (Task 6)
- Produces: `run_connection` gains a fifth parameter `volume: Arc<crate::volume::VolumeControl>`

- [ ] **Step 1: Write the failing tests**

Add to the `tests` module in `satellite/src/satellite/state_machine.rs`, alongside the existing `handle_hub_event` tests. These follow the same shape as `audio_start_marked_alert_routes_to_the_alert_sink` — a duplex stream, a `Ctx`, a `PlaybackHandle` from `pump()`, then `handle_hub_event`.

```rust
    use std::sync::atomic::{AtomicBool, Ordering};
    use std::sync::Arc;

    fn speaker_volume(action: &str) -> WyomingEvent {
        WyomingEvent::with_data("speaker-volume", json!({ "action": action }))
    }

    /// Everything a speaker-volume test needs: a duplex sink for the writer, a probe volume
    /// control that records its wpctl calls instead of running them, and a per-connection hold.
    struct VolFixture {
        log: Arc<std::sync::Mutex<Vec<String>>>,
        vol: Arc<crate::volume::VolumeControl>,
        held: Arc<AtomicBool>,
        cues: Cues,
        led: watch::Sender<LedState>,
    }

    fn vol_fixture() -> VolFixture {
        let (log, vol) = crate::volume::VolumeControl::probe_pair(10);
        let (led, _led_rx) = watch::channel(LedState::Idle);
        std::mem::forget(_led_rx); // keep the channel open for the whole test
        VolFixture { log, vol, held: Arc::new(AtomicBool::new(false)), cues: cues(), led }
    }

    async fn feed(f: &VolFixture, playback: &mut PlaybackHandle, action: &str) {
        let (mut a, _b) = tokio::io::duplex(4096);
        let ctx = Ctx { cues: &f.cues, led: &f.led, volume: &f.vol, alert_held: &f.held };
        let mut mode = Mode::Idle;
        let mut phase = LedState::Idle;
        handle_hub_event(speaker_volume(action), &mut mode, &mut phase, None, &mut a, playback, &ctx)
            .await
            .unwrap();
    }

    // The mute is deliberately applied only after the confirmation cue has drained, in a detached
    // task, so it cannot silence its own cue — hence the poll rather than a bare assert.
    async fn wait_for_mute(vol: &Arc<crate::volume::VolumeControl>, expected: bool) {
        for _ in 0..200 {
            if vol.user_muted() == expected {
                return;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
        panic!("mute never became {expected}");
    }

    #[tokio::test]
    async fn speaker_volume_mute_then_unmute_tracks_the_users_intent() {
        let f = vol_fixture();
        let (mut playback, _done_rx, _pump) = pump();

        feed(&f, &mut playback, "mute").await;
        wait_for_mute(&f.vol, true).await;

        feed(&f, &mut playback, "unmute").await;
        assert!(!f.vol.user_muted());

        let calls = f.log.lock().unwrap().clone();
        assert!(calls.iter().any(|c| c.starts_with("set-mute") && c.ends_with('1')), "got {calls:?}");
        assert!(calls.iter().any(|c| c.starts_with("set-mute") && c.ends_with('0')), "got {calls:?}");
    }

    #[tokio::test]
    async fn speaker_volume_up_and_down_step_the_sink() {
        let f = vol_fixture();
        let (mut playback, _done_rx, _pump) = pump();

        feed(&f, &mut playback, "up").await;
        feed(&f, &mut playback, "down").await;

        let calls = f.log.lock().unwrap().clone();
        assert_eq!(calls.len(), 2, "got {calls:?}");
        assert!(calls[0].contains("10%+"), "got {}", calls[0]);
        assert!(calls[1].contains("10%-"), "got {}", calls[1]);
    }

    // An alarm must ring even on a muted speaker, and the user's mute must come back afterwards.
    // The hold unmutes the SINK without clearing the user's intent — otherwise the release would
    // have nothing to restore and a dismissed alarm would leave the speaker permanently unmuted.
    #[tokio::test]
    async fn alert_hold_unmutes_and_release_restores_the_users_mute() {
        let f = vol_fixture();
        let (mut playback, _done_rx, _pump) = pump();

        f.vol.set_user_mute(true).await.unwrap();
        f.log.lock().unwrap().clear();

        feed(&f, &mut playback, "alert-hold").await;
        assert!(f.vol.user_muted(), "the hold must not clear the user's intent");
        assert!(f.held.load(Ordering::SeqCst));
        assert!(
            f.log.lock().unwrap().last().unwrap().ends_with('0'),
            "the sink is unmuted for the ring"
        );

        feed(&f, &mut playback, "alert-release").await;
        assert!(!f.held.load(Ordering::SeqCst));
        assert!(
            f.log.lock().unwrap().last().unwrap().ends_with('1'),
            "the user's mute comes back after the alarm"
        );
    }

    // A stray or duplicated release must not be able to change the mute state on its own.
    #[tokio::test]
    async fn alert_release_without_a_hold_is_a_no_op() {
        let f = vol_fixture();
        let (mut playback, _done_rx, _pump) = pump();

        feed(&f, &mut playback, "alert-release").await;

        assert!(f.log.lock().unwrap().is_empty(), "a release with no hold must write nothing");
    }

    // A newer hub must never be able to drop an older satellite's connection.
    #[tokio::test]
    async fn unknown_speaker_volume_action_is_ignored() {
        let f = vol_fixture();
        let (mut playback, _done_rx, _pump) = pump();

        feed(&f, &mut playback, "teleport").await;

        assert!(f.log.lock().unwrap().is_empty());
        assert!(!f.vol.user_muted());
    }
```

If `std::mem::forget(_led_rx)` reads badly to you, store the receiver on `VolFixture` instead — the point is only that the watch channel must outlive the test, or `led.send` starts failing.

- [ ] **Step 2: Run and watch them fail**

```bash
cd satellite && cargo test state_machine
```

Expected: FAIL to compile — `Ctx` has no `volume` field and `VolumeControl::probe_pair` is not reachable.

- [ ] **Step 3: Thread the control through and handle the event**

In `satellite/src/satellite/state_machine.rs`:

Add to `struct Ctx<'a>` — both fields go here rather than on `handle_hub_event`'s signature, which is exactly what `Ctx` exists for (clippy's argument limit):

```rust
    volume: &'a std::sync::Arc<crate::volume::VolumeControl>,
    /// Per-connection: an alert hold is outstanding, so the sink is unmuted for a ringing alarm
    /// and the user's mute has to be put back when it ends — or when the connection dies.
    alert_held: &'a std::sync::Arc<std::sync::atomic::AtomicBool>,
```

Change `run_connection`'s signature:

```rust
pub async fn run_connection(
    reader: OwnedReadHalf, writer: OwnedWriteHalf, cfg: Config, models: Option<WakeModels>,
    volume: std::sync::Arc<crate::volume::VolumeControl>,
) -> anyhow::Result<()> {
```

Declare the hold flag next to the other per-connection state and build `Ctx` with both:

```rust
    let alert_held = std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false));
    let ctx = Ctx { cues: &cues, led: &led_tx, volume: &volume, alert_held: &alert_held };
```

(match the existing `Ctx` construction in `run_connection` — the `cues`/`led` bindings are already there under whatever names it uses.)

Add a guard so a connection that dies mid-alarm restores the user's mute:

```rust
/// Restores the user's mute if the connection dies while an alert hold is outstanding. Drop
/// cannot await, so it fires a detached wpctl — the same fail-safe shape as music.rs's DuckGuard.
struct HoldGuard {
    volume: std::sync::Arc<crate::volume::VolumeControl>,
    held: std::sync::Arc<std::sync::atomic::AtomicBool>,
}

impl Drop for HoldGuard {
    fn drop(&mut self) {
        if self.held.load(std::sync::atomic::Ordering::SeqCst) {
            self.volume.restore_user_mute_detached();
        }
    }
}
```

Create the guard right after the pumps are spawned so every exit path — the `?` error paths and the task abort on connection supersede alike — passes through it:

```rust
    let _hold_guard = HoldGuard { volume: volume.clone(), held: alert_held.clone() };
```

Add the event arm in `handle_hub_event`, before the `other => warn!(...)` arm:

```rust
        // Local speaker volume (protocol 1.8). The hub sends intent, never numbers — step size
        // lives on the satellite, next to the hardware it applies to. Read defensively: this is
        // peer-supplied and runs on the connection's event path, where a panic drops the satellite.
        "speaker-volume" => {
            let action = e.data_obj().get("action").and_then(|v| v.as_str()).unwrap_or("").to_string();
            match action.as_str() {
                "up" | "down" => {
                    if ctx.volume.step(action == "up").await.is_ok() {
                        if let Some(pcm) = ctx.cues.volume() { playback.cue(pcm); }
                    }
                }
                "unmute" => {
                    if ctx.volume.set_user_mute(false).await.is_ok() {
                        if let Some(pcm) = ctx.cues.volume() { playback.cue(pcm); }
                    }
                }
                // Cue FIRST, mute after it has drained: muting the sink would otherwise silence
                // the very sound confirming the mute. The wait runs in a detached task so a
                // ~300 ms cue cannot stall mic forwarding on the select! loop.
                "mute" => {
                    let volume = ctx.volume.clone();
                    match ctx.cues.volume() {
                        Some(pcm) => {
                            let (tx, rx) = tokio::sync::oneshot::channel();
                            playback.cue_then(pcm, tx);
                            tokio::spawn(async move {
                                let _ = rx.await; // Err = cue dropped -> mute at once
                                if let Err(e) = volume.set_user_mute(true).await {
                                    warn!("local mute failed: {e:#}");
                                }
                            });
                        }
                        None => {
                            if let Err(e) = volume.set_user_mute(true).await {
                                warn!("local mute failed: {e:#}");
                            }
                        }
                    }
                }
                // An alarm must ring even on a muted speaker. The sink is unmuted WITHOUT clearing
                // the user's intent, so the release has something to restore.
                "alert-hold" => {
                    ctx.alert_held.store(true, std::sync::atomic::Ordering::SeqCst);
                    if ctx.volume.user_muted() {
                        if let Err(e) = ctx.volume.set_sink_mute(false).await {
                            warn!("alert unmute failed: {e:#}");
                        }
                    }
                }
                "alert-release" => {
                    if ctx.alert_held.swap(false, std::sync::atomic::Ordering::SeqCst) {
                        if let Err(e) = ctx.volume.set_sink_mute(ctx.volume.user_muted()).await {
                            warn!("alert mute restore failed: {e:#}");
                        }
                    }
                }
                other => warn!("ignoring speaker-volume action {other}"),
            }
        }
```

`alert_held` reaches `handle_hub_event` either as a new parameter or on `Ctx` — put it on `Ctx` as `alert_held: &'a std::sync::Arc<std::sync::atomic::AtomicBool>`, which keeps the argument count within clippy's limit (the reason `Ctx` exists).

In `satellite/src/main.rs`, build and seed the control before the accept loop:

```rust
    let volume = volume::VolumeControl::new(cfg.volume_sink.clone(), cfg.volume_step);
    // Process-scoped, not per-connection: a hub reconnect must not forget that the user muted the
    // speaker. Seeded from the sink so wireplumber's restored state and ours agree at boot.
    volume.seed().await;
```

and clone it into each spawned connection:

```rust
        let volume = volume.clone();
        active = Some(tokio::spawn(async move {
            if let Err(e) = satellite::state_machine::run_connection(r, w, cfg, models, volume).await {
                error!("connection ended with error: {e:#}");
            }
        }));
```

- [ ] **Step 4: Run and watch them pass**

```bash
cd satellite && cargo test
```

Expected: PASS across the crate. Existing `run_connection` call sites in tests need the new argument — pass `crate::volume::VolumeControl::new(None, 10)`.

- [ ] **Step 5: Check clippy**

```bash
cd satellite && cargo clippy --all-targets -- -D warnings
```

Expected: clean. If `too_many_arguments` fires on `handle_hub_event`, move `alert_held` onto `Ctx` as described.

- [ ] **Step 6: Commit**

```bash
git add satellite/src/satellite/state_machine.rs satellite/src/main.rs satellite/src/volume.rs
git commit -m "feat(satellite): handle speaker-volume commands and the alert mute hold"
```

---

### Task 11: Release build check

The satellite ships as a static aarch64-musl binary. A change that compiles on the host can still break the cross build.

**Files:** none

- [ ] **Step 1: Run the full satellite test suite**

```bash
cd satellite && cargo test
```

Expected: PASS.

- [ ] **Step 2: Cross-compile the release binary**

```bash
cd satellite && ./scripts/build-release.sh
```

Expected: builds. Never use bare `cargo zigbuild` — the script's CC shim is required.

- [ ] **Step 3: Commit only if something needed fixing**

```bash
git add -A satellite/
git commit -m "fix(satellite): keep the musl release build green"
```

If nothing changed, skip the commit.

---

### Task 12: Provisioning and documentation

**Files:**
- Modify: `scripts/provision-satellite-rs.sh`
- Modify: `satellite/CLAUDE.md`
- Modify: `.claude/rules/voice.md`

- [ ] **Step 1: Pass the flag on music units**

In `scripts/provision-satellite-rs.sh`, find the music drop-in `ExecStart` that already carries `--music-mixer Music --music-card ${outctl}` (around line 386) and add on the same continuation:

```
  --volume-sink @DEFAULT_AUDIO_SINK@ \\
```

It belongs only on the music path: PipeWire is installed only there, so a voice-only unit correctly gets no local volume control.

- [ ] **Step 2: Document the satellite side**

In `satellite/CLAUDE.md`, add a section after **Music ducking**:

```markdown
## Local speaker volume

`volume.rs` drives the PipeWire sink (`--volume-sink`, `--volume-step`, default 10 points) with
`wpctl` on the hub's `speaker-volume` event (protocol 1.8, actions `up`/`down`/`mute`/`unmute`/
`alert-hold`/`alert-release`). This is the MASTER — deliberately not one of the `Music`/`TTS`/
`Alert` softvols, which carry calibration and, in `Music`'s case, are rewritten by the ducker on
every turn. Master and softvol multiply, so ducking is untouched by this feature. Absent
`--volume-sink` the whole thing is a no-op with a warning, mirroring `music_mixer: None`.

`user_muted` is process-scoped so a hub reconnect cannot forget the user's mute, and is seeded
once at boot from `wpctl get-volume` (which prints `[MUTED]`) so wireplumber's restored state and
ours agree. Wireplumber persists level and mute itself; nothing is written to disk here.

**An alarm must ring on a muted speaker.** `alert-hold` unmutes the sink WITHOUT clearing
`user_muted`, and `alert-release` — which the hub sends from the insistent loop's `finally`, so it
covers dismissal and cancellation alike — puts it back. The hold is per-connection and a
`HoldGuard` restores it on teardown, so a hub that dies mid-alarm leaves the speaker audible
rather than silently muted.

**The mute cue must not be silenced by its own mute**: `mute` plays the cue via
`PlaybackHandle::cue_then` and applies the mute on the acknowledgement, which the pump sends after
`play_cue` drains — and also when it drops the cue for an active stream, so the mute still lands.
The wait is a detached task, never the `select!` loop.
```

- [ ] **Step 3: Document the hub side**

In `.claude/rules/voice.md`, add after the **Alert routing** paragraph:

```markdown
**Local speaker commands.** `VoiceCommandMatcher` matches a normalized whole transcript (lowercase,
accents and punctuation stripped, whitespace collapsed) against `VoiceSettings.Commands.Phrases`.
`TranscriptDispatcher` checks it AFTER the gibberish gate and BEFORE `GetOrCreateAsync`, so poor
audio cannot move a volume knob and a hit costs no `create_conversation` round trip. A hit writes
`speaker-volume` through `SatelliteSession.ControlWriter` and returns `false`, which
`FollowUpConversation` already turns into `EndConversation`. Every phrase carries an explicit local
marker: "sube el volumen" is a Music Assistant request and still belongs to the agent. Matching is
whole-transcript only, so a compound sentence goes to the agent intact.
`InsistentAnnouncementController` brackets its ring loop with `alert-hold`/`alert-release` so a
local mute never swallows a timer.
```

- [ ] **Step 4: Verify the provisioning script still parses**

```bash
bash -n scripts/provision-satellite-rs.sh
```

Expected: no output.

- [ ] **Step 5: Commit**

```bash
git add scripts/provision-satellite-rs.sh satellite/CLAUDE.md .claude/rules/voice.md
git commit -m "docs(voice): document local speaker volume and provision the sink flag"
```

---

## Verification

After Task 12, before claiming completion:

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit.McpChannelVoice"
cd satellite && cargo test && cargo clippy --all-targets -- -D warnings
```

All three must pass. Report the actual output — do not claim success without it.

On-device checks that unit tests cannot cover, to run after deploying to a music unit:

- **`wpctl` is reachable from the unit at all.** The satellite runs as a system service with
  `XDG_RUNTIME_DIR` threaded in by provisioning, which is how its `aplay` already reaches
  PipeWire — but confirm before anything else, because every other check depends on it:
  `sudo systemctl show nabu-satellite -p Environment` should list `XDG_RUNTIME_DIR`, and
  `sudo XDG_RUNTIME_DIR=/run/user/1000 wpctl get-volume @DEFAULT_AUDIO_SINK@` should print a
  volume rather than a connection error.
- "sube el volumen local" raises the speaker and beeps once.
- "silencia el altavoz" beeps, *then* goes silent — the beep must be fully audible.
- With the speaker muted, a timer still rings, and the mute returns after the alarm ends or is dismissed.
- "sube el volumen" still reaches the agent and moves the Music Assistant player, not the master.
- Music ducking during a normal turn is unchanged at a reduced master level.
