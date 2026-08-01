# Local Command Dispatcher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract local voice command routing out of `TranscriptDispatcher` into a `LocalCommands` module where destinations are DI-injected handlers.

**Architecture:** New folder `McpChannelVoice/Services/LocalCommands/` holds the `VoiceCommand` enum, the moved `VoiceCommandMatcher`, an `ILocalCommandHandler` destination contract, a `LocalCommandDispatcher` that builds a command→handler map from DI at construction (failing fast on duplicate or uncovered commands), and `SpeakerVolumeCommandHandler` owning the four existing commands. `TranscriptDispatcher` calls `TryHandleAsync` and keeps only gating, telemetry, and turn-end semantics. Pure refactor: wire behavior is identical.

**Tech Stack:** .NET 10, xUnit + Shouldly + Moq (existing test stack). Spec: `docs/superpowers/specs/2026-08-01-local-command-dispatcher-design.md`.

## Global Constraints

- Work in the worktree at `.claude/worktrees/local-command-dispatcher` (branch `worktree-local-command-dispatcher`). Never `cd` to the main checkout.
- `.cs` files have **no trailing newline** (`.editorconfig` `insert_final_newline = false`).
- No XML doc comments; comments explain "why" only.
- File-scoped namespaces; primary constructors for DI; LINQ over loops.
- Test naming: `{Method}_{Scenario}_{ExpectedResult}`; Shouldly assertions.
- Run tests with: `dotnet test Tests/Tests.csproj --nologo -v q --filter "<name filter>"`. Unit tests only; integration tests need Docker and must merely compile.
- Baseline note: `Tests.Integration.Clients.PlaywrightWebBrowserTests` (2 tests) fail without the Docker stack — pre-existing, ignore them.
- The pre-commit hook runs `dotnet format` on staged `.cs` files and re-stages them whole; stage complete files.

---

### Task 1: Move `VoiceCommand` and `VoiceCommandMatcher` into `Services/LocalCommands`

Pure move + namespace change; no behavior change, no new tests. Compile and the existing suite are the safety net.

**Files:**
- Create: `McpChannelVoice/Services/LocalCommands/VoiceCommand.cs`
- Create: `McpChannelVoice/Services/LocalCommands/VoiceCommandMatcher.cs` (moved from `McpChannelVoice/Services/VoiceCommandMatcher.cs`)
- Delete: `McpChannelVoice/Services/VoiceCommandMatcher.cs`
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs` (add using)
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (add using)
- Modify: `Tests/Unit/McpChannelVoice/VoiceCommandMatcherTests.cs` (add using)
- Modify: `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs` (add using)
- Modify: `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` (add using)
- Modify: `Tests/Integration/McpChannelVoice/WakeArbitrationHostTests.cs` (add using)

**Interfaces:**
- Consumes: nothing new.
- Produces: namespace `McpChannelVoice.Services.LocalCommands` containing `enum VoiceCommand { LocalVolumeUp, LocalVolumeDown, LocalMute, LocalUnmute }` and `VoiceCommandMatcher` (API unchanged: ctor `VoiceCommandMatcher(CommandSettings settings)`, method `VoiceCommand? Match(string? transcript)`).

- [ ] **Step 1: Create `VoiceCommand.cs`**

```csharp
namespace McpChannelVoice.Services.LocalCommands;

public enum VoiceCommand
{
    LocalVolumeUp,
    LocalVolumeDown,
    LocalMute,
    LocalUnmute
}
```
(No trailing newline.)

- [ ] **Step 2: Move the matcher**

`git mv McpChannelVoice/Services/VoiceCommandMatcher.cs McpChannelVoice/Services/LocalCommands/VoiceCommandMatcher.cs`, then edit the moved file: change `namespace McpChannelVoice.Services;` to `namespace McpChannelVoice.Services.LocalCommands;` and delete the `public enum VoiceCommand { ... }` block (lines 7–13 of the original) — the enum now lives in `VoiceCommand.cs`. Everything else stays byte-identical.

- [ ] **Step 3: Add `using McpChannelVoice.Services.LocalCommands;` to the six referencing files**

`McpChannelVoice/Services/TranscriptDispatcher.cs`, `McpChannelVoice/Modules/ConfigModule.cs`, `Tests/Unit/McpChannelVoice/VoiceCommandMatcherTests.cs`, `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`, `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs`, `Tests/Integration/McpChannelVoice/WakeArbitrationHostTests.cs` — inserted in the existing sorted using block of each file.

- [ ] **Step 4: Build and run the affected unit tests**

Run: `dotnet build Ziggurat.sln --nologo -v q`
Expected: 0 errors.
Run: `dotnet test Tests/Tests.csproj --nologo -v q --filter "FullyQualifiedName~VoiceCommandMatcherTests|FullyQualifiedName~TranscriptDispatcherTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "refactor(voice): move VoiceCommand and matcher into Services/LocalCommands"
```

---

### Task 2: `ILocalCommandHandler` contract and `SpeakerVolumeCommandHandler`

**Files:**
- Create: `McpChannelVoice/Services/LocalCommands/ILocalCommandHandler.cs`
- Create: `McpChannelVoice/Services/LocalCommands/SpeakerVolumeCommandHandler.cs`
- Test: `Tests/Unit/McpChannelVoice/SpeakerVolumeCommandHandlerTests.cs`

**Interfaces:**
- Consumes: `VoiceCommand` (Task 1), `SatelliteSession.TrySendControlAsync(WyomingEvent, CancellationToken)` (existing), `WyomingEvent.Header(string type, JsonObject data)` (existing).
- Produces:
  - `interface ILocalCommandHandler { IReadOnlySet<VoiceCommand> Commands { get; } Task<bool> HandleAsync(VoiceCommand command, SatelliteSession session, CancellationToken ct); }`
  - `sealed class SpeakerVolumeCommandHandler : ILocalCommandHandler` (parameterless).

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/SpeakerVolumeCommandHandlerTests.cs`:

```csharp
using McpChannelVoice.Services;
using McpChannelVoice.Services.LocalCommands;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SpeakerVolumeCommandHandlerTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Fact]
    public void Commands_CoversAllFourSpeakerCommands()
    {
        var handler = new SpeakerVolumeCommandHandler();

        handler.Commands.ShouldBe([
            VoiceCommand.LocalVolumeUp, VoiceCommand.LocalVolumeDown,
            VoiceCommand.LocalMute, VoiceCommand.LocalUnmute], ignoreOrder: true);
    }

    [Theory]
    [InlineData(VoiceCommand.LocalVolumeUp, "up")]
    [InlineData(VoiceCommand.LocalVolumeDown, "down")]
    [InlineData(VoiceCommand.LocalMute, "mute")]
    [InlineData(VoiceCommand.LocalUnmute, "unmute")]
    public async Task HandleAsync_EachCommand_SendsSpeakerVolumeWithItsAction(VoiceCommand command, string action)
    {
        var session = Session();
        var written = new List<WyomingEvent>();
        session.ControlWriter = (evt, _) => { written.Add(evt); return Task.CompletedTask; };
        var handler = new SpeakerVolumeCommandHandler();

        var sent = await handler.HandleAsync(command, session, default);

        sent.ShouldBeTrue();
        written.Count.ShouldBe(1);
        written[0].Type.ShouldBe("speaker-volume");
        written[0].Data["action"]!.GetValue<string>().ShouldBe(action);
    }

    [Fact]
    public async Task HandleAsync_NoControlWriter_ReturnsFalse()
    {
        var handler = new SpeakerVolumeCommandHandler();

        var sent = await handler.HandleAsync(VoiceCommand.LocalMute, Session(), default);

        sent.ShouldBeFalse();
    }
}
```
(No trailing newline.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --nologo -v q --filter "FullyQualifiedName~SpeakerVolumeCommandHandlerTests"`
Expected: build FAILS — `ILocalCommandHandler` / `SpeakerVolumeCommandHandler` do not exist.

- [ ] **Step 3: Write the implementation**

Create `McpChannelVoice/Services/LocalCommands/ILocalCommandHandler.cs`:

```csharp
namespace McpChannelVoice.Services.LocalCommands;

// A destination for local voice commands: an action the hub performs itself, without the agent.
// Registered as a DI collection; LocalCommandDispatcher routes by the Commands set each handler
// declares. HandleAsync's bool means "the action reached its destination" and drives the
// command/command_failed telemetry outcome upstream.
//
// This contract deliberately lives in the voice channel, not Domain: handlers act on a
// SatelliteSession, and other subsystems are separate processes reachable only via MCP/Redis.
// If a second consumer ever appears, promote it to the shared layers then.
public interface ILocalCommandHandler
{
    IReadOnlySet<VoiceCommand> Commands { get; }
    Task<bool> HandleAsync(VoiceCommand command, SatelliteSession session, CancellationToken ct);
}
```
(Add `using McpChannelVoice.Services;` if the compiler asks; `SatelliteSession` is in the parent namespace `McpChannelVoice.Services`, which is in scope from the nested namespace, so no using is needed.)

Create `McpChannelVoice/Services/LocalCommands/SpeakerVolumeCommandHandler.cs`:

```csharp
using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;

namespace McpChannelVoice.Services.LocalCommands;

public sealed class SpeakerVolumeCommandHandler : ILocalCommandHandler
{
    private static readonly IReadOnlyDictionary<VoiceCommand, string> _actions =
        new Dictionary<VoiceCommand, string>
        {
            [VoiceCommand.LocalVolumeUp] = "up",
            [VoiceCommand.LocalVolumeDown] = "down",
            [VoiceCommand.LocalMute] = "mute",
            [VoiceCommand.LocalUnmute] = "unmute"
        };

    public IReadOnlySet<VoiceCommand> Commands { get; } = _actions.Keys.ToHashSet();

    public Task<bool> HandleAsync(VoiceCommand command, SatelliteSession session, CancellationToken ct) =>
        session.TrySendControlAsync(
            WyomingEvent.Header("speaker-volume", new JsonObject { ["action"] = _actions[command] }), ct);
}
```
(No trailing newline on either file.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --nologo -v q --filter "FullyQualifiedName~SpeakerVolumeCommandHandlerTests"`
Expected: 6 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/LocalCommands/ILocalCommandHandler.cs \
        McpChannelVoice/Services/LocalCommands/SpeakerVolumeCommandHandler.cs \
        Tests/Unit/McpChannelVoice/SpeakerVolumeCommandHandlerTests.cs
git commit -m "feat(voice): add ILocalCommandHandler contract and speaker volume handler"
```

---

### Task 3: `LocalCommandDispatcher` and `LocalCommandResult`

**Files:**
- Create: `McpChannelVoice/Services/LocalCommands/LocalCommandDispatcher.cs`
- Test: `Tests/Unit/McpChannelVoice/LocalCommandDispatcherTests.cs`

**Interfaces:**
- Consumes: `VoiceCommandMatcher.Match(string?)` (Task 1), `ILocalCommandHandler` (Task 2).
- Produces:
  - `sealed record LocalCommandResult(VoiceCommand Command, bool Sent);`
  - `sealed class LocalCommandDispatcher` with ctor `(VoiceCommandMatcher matcher, IEnumerable<ILocalCommandHandler> handlers)` — throws `InvalidOperationException` on duplicate or uncovered commands — and method `Task<LocalCommandResult?> TryHandleAsync(string transcript, SatelliteSession session, CancellationToken ct)` (`null` = not a local command).

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/LocalCommandDispatcherTests.cs`:

```csharp
using McpChannelVoice.Services;
using McpChannelVoice.Services.LocalCommands;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class LocalCommandDispatcherTests
{
    private static SatelliteSession Session() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private static VoiceCommandMatcher Matcher() =>
        new(new CommandSettings
        {
            Phrases = new CommandPhrases
            {
                LocalVolumeUp = ["sube el volumen local"],
                LocalVolumeDown = ["baja el volumen local"],
                LocalMute = ["silencia el altavoz"],
                LocalUnmute = ["quita el silencio local"]
            }
        });

    private sealed class FakeHandler(IReadOnlySet<VoiceCommand> commands, bool result = true) : ILocalCommandHandler
    {
        public List<VoiceCommand> Handled { get; } = [];
        public IReadOnlySet<VoiceCommand> Commands => commands;

        public Task<bool> HandleAsync(VoiceCommand command, SatelliteSession session, CancellationToken ct)
        {
            Handled.Add(command);
            return Task.FromResult(result);
        }
    }

    private static readonly IReadOnlySet<VoiceCommand> _volumeCommands =
        new HashSet<VoiceCommand> { VoiceCommand.LocalVolumeUp, VoiceCommand.LocalVolumeDown };

    private static readonly IReadOnlySet<VoiceCommand> _muteCommands =
        new HashSet<VoiceCommand> { VoiceCommand.LocalMute, VoiceCommand.LocalUnmute };

    [Fact]
    public void Ctor_DuplicateCommandOwnership_Throws()
    {
        var all = Enum.GetValues<VoiceCommand>().ToHashSet();

        Should.Throw<InvalidOperationException>(
            () => new LocalCommandDispatcher(Matcher(), [new FakeHandler(all), new FakeHandler(_muteCommands)]));
    }

    [Fact]
    public void Ctor_UncoveredCommand_Throws()
    {
        Should.Throw<InvalidOperationException>(
            () => new LocalCommandDispatcher(Matcher(), [new FakeHandler(_volumeCommands)]));
    }

    [Fact]
    public async Task TryHandleAsync_NonCommandTranscript_ReturnsNull()
    {
        var sut = new LocalCommandDispatcher(
            Matcher(), [new FakeHandler(_volumeCommands), new FakeHandler(_muteCommands)]);

        var result = await sut.TryHandleAsync("sube el volumen", Session(), default);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task TryHandleAsync_MatchedCommand_RoutesToItsOwningHandler()
    {
        var volume = new FakeHandler(_volumeCommands);
        var mute = new FakeHandler(_muteCommands);
        var sut = new LocalCommandDispatcher(Matcher(), [volume, mute]);

        var result = await sut.TryHandleAsync("silencia el altavoz", Session(), default);

        result.ShouldNotBeNull();
        result.Command.ShouldBe(VoiceCommand.LocalMute);
        result.Sent.ShouldBeTrue();
        mute.Handled.ShouldBe([VoiceCommand.LocalMute]);
        volume.Handled.ShouldBeEmpty();
    }

    [Fact]
    public async Task TryHandleAsync_HandlerReportsFailure_ResultSentIsFalse()
    {
        var sut = new LocalCommandDispatcher(
            Matcher(),
            [new FakeHandler(_volumeCommands, result: false), new FakeHandler(_muteCommands)]);

        var result = await sut.TryHandleAsync("sube el volumen local", Session(), default);

        result.ShouldNotBeNull();
        result.Sent.ShouldBeFalse();
    }
}
```
(No trailing newline.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --nologo -v q --filter "FullyQualifiedName~LocalCommandDispatcherTests"`
Expected: build FAILS — `LocalCommandDispatcher` does not exist.

- [ ] **Step 3: Write the implementation**

Create `McpChannelVoice/Services/LocalCommands/LocalCommandDispatcher.cs`:

```csharp
namespace McpChannelVoice.Services.LocalCommands;

public sealed record LocalCommandResult(VoiceCommand Command, bool Sent);

public sealed class LocalCommandDispatcher
{
    private readonly VoiceCommandMatcher _matcher;
    private readonly IReadOnlyDictionary<VoiceCommand, ILocalCommandHandler> _routes;

    public LocalCommandDispatcher(VoiceCommandMatcher matcher, IEnumerable<ILocalCommandHandler> handlers)
    {
        _matcher = matcher;

        // Both checks throw at container build time, so a routing mistake is a startup crash
        // rather than a command silently dropped (or double-handled) on the first utterance.
        var claims = handlers
            .SelectMany(h => h.Commands.Select(command => (Command: command, Handler: h)))
            .ToList();

        var duplicates = claims
            .GroupBy(c => c.Command)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Local commands owned by more than one handler: {string.Join(", ", duplicates)}");
        }

        _routes = claims.ToDictionary(c => c.Command, c => c.Handler);

        var uncovered = Enum.GetValues<VoiceCommand>().Where(c => !_routes.ContainsKey(c)).ToList();
        if (uncovered.Count > 0)
        {
            throw new InvalidOperationException(
                $"Local commands with no registered handler: {string.Join(", ", uncovered)}");
        }
    }

    public async Task<LocalCommandResult?> TryHandleAsync(
        string transcript, SatelliteSession session, CancellationToken ct) =>
        _matcher.Match(transcript) is { } command
            ? new LocalCommandResult(command, await _routes[command].HandleAsync(command, session, ct))
            : null;
}
```
(No trailing newline.)

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --nologo -v q --filter "FullyQualifiedName~LocalCommandDispatcherTests"`
Expected: 5 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/LocalCommands/LocalCommandDispatcher.cs \
        Tests/Unit/McpChannelVoice/LocalCommandDispatcherTests.cs
git commit -m "feat(voice): add LocalCommandDispatcher routing commands to DI handlers"
```

---

### Task 4: Rewire `TranscriptDispatcher`, DI, and existing tests

**Files:**
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`
- Modify: `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`
- Modify: `Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs`
- Modify: `Tests/Integration/McpChannelVoice/WakeArbitrationHostTests.cs`

**Interfaces:**
- Consumes: `LocalCommandDispatcher.TryHandleAsync` and `LocalCommandResult` (Task 3), `SpeakerVolumeCommandHandler` (Task 2).
- Produces: `TranscriptDispatcher` ctor's 4th parameter becomes `LocalCommandDispatcher localCommands` (replacing `VoiceCommandMatcher matcher`); everything else in the signature is unchanged. `DispatchAsync` behavior is unchanged.

- [ ] **Step 1: Update the existing unit tests to the new seam (they must fail to compile first)**

In `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`, inside `Build(...)`, replace:

```csharp
            new VoiceCommandMatcher(commands ?? new CommandSettings()),
```

with:

```csharp
            new LocalCommandDispatcher(
                new VoiceCommandMatcher(commands ?? new CommandSettings()), [new SpeakerVolumeCommandHandler()]),
```

Apply the same replacement to the two inline constructions (`DispatchAsync_EmptyText_DropsAndPublishesDroppedMetric` and `DispatchAsync_Dispatched_PublishesCaptureAndWhisperStats` / `DispatchAsync_Dropped_PublishesCaptureAndWhisperStats`), which pass `new VoiceCommandMatcher(new CommandSettings())` directly:

```csharp
            new LocalCommandDispatcher(new VoiceCommandMatcher(new CommandSettings()), [new SpeakerVolumeCommandHandler()]),
```

No test bodies change — the command-path tests (`DispatchAsync_LocalCommand_SendsSpeakerVolumeAndSkipsTheAgent`, `DispatchAsync_EachLocalCommand_SendsItsAction`, outcome tests, gate-ordering test, music-request test) assert the same observable behavior through the new seam.

- [ ] **Step 2: Run the unit tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --nologo -v q --filter "FullyQualifiedName~TranscriptDispatcherTests"`
Expected: build FAILS — `TranscriptDispatcher` still takes `VoiceCommandMatcher`.

- [ ] **Step 3: Rewire `TranscriptDispatcher`**

In `McpChannelVoice/Services/TranscriptDispatcher.cs`:

1. Replace the ctor parameter `VoiceCommandMatcher matcher,` with `LocalCommandDispatcher localCommands,`.
2. Replace the whole local-command block (the `if (matcher.Match(transcript.Text) is { } command)` statement and its body) with:

```csharp
        // Local speaker commands are answered here and never reach the agent. Placed AFTER the
        // quality gate (garbage audio must not move a volume knob) and BEFORE GetOrCreateAsync,
        // which is a full create_conversation MCP round trip — matching first keeps the path fast
        // and keeps these out of conversation history.
        if (await localCommands.TryHandleAsync(transcript.Text, session, ct) is { } command)
        {
            logger.LogInformation(
                "Local command {Command} for {Satellite}: sent={Sent}",
                command.Command, session.SatelliteId, command.Sent);

            await PublishUtteranceEventAsync(
                session, transcript, similarity, stats, command.Sent ? "command" : "command_failed",
                manager.GetActiveConversationId(session.SatelliteId), ct);

            // False means "nothing reached the agent", which FollowUpConversation already turns into
            // EndConversation — the satellite gets its closing transcript and re-arms. No new
            // turn-end path is needed.
            return false;
        }
```

3. Remove now-unused usings if the build warns (`System.Text.Json.Nodes` and `McpChannelVoice.Services.WyomingProtocol` were only used by the removed block — verify with the compiler before deleting).

- [ ] **Step 4: Rewire DI in `ConfigModule`**

In `McpChannelVoice/Modules/ConfigModule.cs`, replace:

```csharp
            .AddSingleton(new VoiceCommandMatcher(settings.Commands))
```

with:

```csharp
            .AddSingleton(new VoiceCommandMatcher(settings.Commands))
            .AddSingleton<ILocalCommandHandler, SpeakerVolumeCommandHandler>()
            .AddSingleton<LocalCommandDispatcher>()
```

and in the `TranscriptDispatcher` factory replace:

```csharp
                sp.GetRequiredService<VoiceCommandMatcher>(),
```

with:

```csharp
                sp.GetRequiredService<LocalCommandDispatcher>(),
```

- [ ] **Step 5: Update the 17 integration-test constructions**

`Tests/Integration/McpChannelVoice/WyomingSatelliteHostTests.cs` (16 sites) and `Tests/Integration/McpChannelVoice/WakeArbitrationHostTests.cs` (1 site) all pass `new VoiceCommandMatcher(new CommandSettings())` as the 4th `TranscriptDispatcher` argument. Replace every occurrence of:

```csharp
new VoiceCommandMatcher(new CommandSettings())
```

with:

```csharp
new LocalCommandDispatcher(new VoiceCommandMatcher(new CommandSettings()), [new SpeakerVolumeCommandHandler()])
```

(`sed -i` on both files is fine; the pattern is identical everywhere.)

- [ ] **Step 6: Build everything and run the voice unit tests**

Run: `dotnet build Ziggurat.sln --nologo -v q`
Expected: 0 errors (integration tests compile; they are not run).
Run: `dotnet test Tests/Tests.csproj --nologo -v q --filter "FullyQualifiedName~Tests.Unit.McpChannelVoice"`
Expected: all pass.

- [ ] **Step 7: Commit**

```bash
git add -A
git commit -m "refactor(voice): route local commands through LocalCommandDispatcher"
```

---

### Task 5: Update `.claude/rules/voice.md` and run the full unit suite

**Files:**
- Modify: `.claude/rules/voice.md` (the "Local speaker commands" paragraph)

**Interfaces:** none — documentation and final verification.

- [ ] **Step 1: Update the local-commands paragraph**

In `.claude/rules/voice.md`, replace the sentence:

> `TranscriptDispatcher` checks it AFTER the gibberish gate and BEFORE `GetOrCreateAsync`, so poor audio cannot move a volume knob and a hit costs no `create_conversation` round trip. A hit writes `speaker-volume` through `SatelliteSession.ControlWriter` and returns `false`, which `FollowUpConversation` already turns into `EndConversation`.

with:

> `LocalCommandDispatcher` (`Services/LocalCommands/`) routes a match to the `ILocalCommandHandler` that owns it — handlers are DI-registered and self-declare their commands; construction fails fast on duplicate or unowned commands. `TranscriptDispatcher` calls it AFTER the gibberish gate and BEFORE `GetOrCreateAsync`, so poor audio cannot move a volume knob and a hit costs no `create_conversation` round trip. `SpeakerVolumeCommandHandler` writes `speaker-volume` through `SatelliteSession.ControlWriter`; a hit returns `false`, which `FollowUpConversation` already turns into `EndConversation`. A new local command is a new enum value plus a handler registration — `TranscriptDispatcher` stays untouched.

Keep the rest of the paragraph (local markers, whole-transcript matching) unchanged.

- [ ] **Step 2: Run the full unit suite**

Run: `dotnet test Tests/Tests.csproj --nologo -v q --filter "FullyQualifiedName~Tests.Unit"`
Expected: all pass, 0 failures.

- [ ] **Step 3: Commit**

```bash
git add .claude/rules/voice.md
git commit -m "docs(voice): describe the local command dispatcher module"
```
