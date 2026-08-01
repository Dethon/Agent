# STT short-phrase accuracy Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Raise Whisper accuracy on one-to-three-second voice commands by biasing every decode with a per-request prompt, stopping short utterances from being split, cleaning the container's initial prompt, tuning whisper-server's VAD and sampling flags, and making the transcript quality gate duration-aware.

**Architecture:** Five independent changes along the existing STT path. `OpenAiSpeechToText` gains a `prompt` multipart field built by a new pure `WhisperPromptBuilder` from configured text plus the prior segment's transcript. `SegmentedSpeechToText` stops splitting before a configurable age and chains each fragment's context. `TranscriptDispatcher` picks its `avg_logprob` floor from measured speech duration. The container-side decode flags move into `entrypoint.sh` env knobs. The eval harness learns to send a prompt and to build a synthetic short-command corpus.

**Tech Stack:** .NET 10, xUnit + Shouldly + Moq, POSIX sh (`entrypoint.sh`), Docker Compose, Python 3.11+ with uv/pytest for `scripts/stt-enhancement-eval`.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-01-stt-short-phrase-accuracy-design.md`.
- Red-Green-Refactor: write the failing test, run it, watch it fail, then implement.
- `.editorconfig` sets `insert_final_newline = false` — `.cs` files end with **no trailing newline**.
- The pre-commit hook runs `dotnet format` over staged `.cs` files and re-stages them whole. Make the working tree match the commit you want; partial staging does not survive.
- Unit tests run with `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"`. `Tests/Tests.csproj` is one project covering Unit, Integration and E2E — there is no `Tests/Unit` project file.
- Coding style: file-scoped namespaces, primary constructors, `record` for settings, LINQ over loops, comments explain *why* only, no XML doc comments.
- Test naming: `{Method}_{Scenario}_{ExpectedResult}`.
- Commit after each task with a message referencing what it delivers.
- Production model is `Whisper-Large-v3-Turbo`; nothing in this plan changes the model.

---

### Task 1: WhisperPromptBuilder

The pure composition unit: static configured text plus the prior segment's transcript, with
`{room}`/`{locality}` substituted and the result capped. Nothing else knows these rules.

**Files:**
- Create: `McpChannelVoice/Services/Stt/WhisperPromptBuilder.cs`
- Test: `Tests/Unit/McpChannelVoice/Stt/WhisperPromptBuilderTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static string? WhisperPromptBuilder.Build(string? template, string? room, string? locality, string? priorText, int maxChars)` — returns null when there is nothing to send.

- [ ] **Step 1: Write the failing tests**

Create `Tests/Unit/McpChannelVoice/Stt/WhisperPromptBuilderTests.cs`:

```csharp
using McpChannelVoice.Services.Stt;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class WhisperPromptBuilderTests
{
    [Fact]
    public void Build_TemplateOnly_SubstitutesRoomAndLocality()
    {
        var prompt = WhisperPromptBuilder.Build(
            "Órdenes en {room}, {locality}.", "la cocina", "Valladolid", null, 700);

        prompt.ShouldBe("Órdenes en la cocina, Valladolid.");
    }

    [Fact]
    public void Build_MissingLocality_CollapsesTheGapItLeaves()
    {
        var prompt = WhisperPromptBuilder.Build(
            "Órdenes en {room} {locality} ahora.", "la cocina", null, null, 700);

        prompt.ShouldBe("Órdenes en la cocina ahora.");
    }

    [Fact]
    public void Build_UnknownPlaceholder_IsLeftLiteral()
    {
        var prompt = WhisperPromptBuilder.Build("Pon {algo} en {room}.", "el salón", null, null, 700);

        prompt.ShouldBe("Pon {algo} en el salón.");
    }

    [Fact]
    public void Build_PriorText_GoesLastSoItSitsClosestToTheAudio()
    {
        var prompt = WhisperPromptBuilder.Build("Órdenes breves.", null, null, "pon el temporizador", 700);

        prompt.ShouldBe("Órdenes breves. pon el temporizador");
    }

    [Fact]
    public void Build_OverBudget_TrimsPriorTextFromItsFrontAtAWordBoundary()
    {
        // Static is 6 chars; a 20-char cap leaves 13 for the prior text after the joining space.
        var prompt = WhisperPromptBuilder.Build("Manda.", null, null, "uno dos tres cuatro", 20);

        prompt.ShouldBe("Manda. tres cuatro");
    }

    [Fact]
    public void Build_StaticAloneOverBudget_KeepsItWholeAndDropsPriorText()
    {
        var prompt = WhisperPromptBuilder.Build("Un texto largo de verdad.", null, null, "hola", 10);

        prompt.ShouldBe("Un texto largo de verdad.");
    }

    [Fact]
    public void Build_PriorTextOnly_IsTrimmedToTheBudget()
    {
        var prompt = WhisperPromptBuilder.Build(null, null, null, "uno dos tres cuatro", 12);

        prompt.ShouldBe("tres cuatro");
    }

    [Fact]
    public void Build_NothingToSay_ReturnsNull()
    {
        WhisperPromptBuilder.Build(null, "la cocina", "Valladolid", null, 700).ShouldBeNull();
        WhisperPromptBuilder.Build("   ", null, null, "  ", 700).ShouldBeNull();
    }

    [Fact]
    public void Build_TemplateThatResolvesToNothing_ReturnsNull()
    {
        WhisperPromptBuilder.Build("{room}", null, null, null, 700).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WhisperPromptBuilderTests"`
Expected: FAIL — build error, `WhisperPromptBuilder` does not exist.

- [ ] **Step 3: Write the implementation**

Create `McpChannelVoice/Services/Stt/WhisperPromptBuilder.cs`:

```csharp
namespace McpChannelVoice.Services.Stt;

// Builds the initial prompt posted with one transcription. Whisper reads the prompt as text
// that precedes the audio, so the prior segment's transcript goes LAST — closest to what is
// being decoded — and the configured vocabulary first.
//
// whisper.cpp caps the prompt at n_text_ctx/2 (224 tokens) and keeps the TAIL, which would
// silently eat the configured vocabulary on a long continuation. So the cap is applied here
// instead, and it is the prior text that gets trimmed (from its front, at a word boundary):
// operator-authored vocabulary always survives whole. maxChars is a character approximation
// of that token budget, deliberately under it rather than tuned to it.
public static class WhisperPromptBuilder
{
    public static string? Build(
        string? template, string? room, string? locality, string? priorText, int maxChars)
    {
        var configured = Collapse(Substitute(template, room, locality));
        var prior = Collapse(priorText);

        if (configured.Length == 0)
        {
            return prior.Length == 0 ? null : NullIfEmpty(Tail(prior, maxChars));
        }

        var budget = maxChars - configured.Length - 1;
        var tail = prior.Length == 0 || budget <= 0 ? "" : Tail(prior, budget);
        return tail.Length == 0 ? configured : $"{configured} {tail}";
    }

    private static string Substitute(string? template, string? room, string? locality) =>
        (template ?? string.Empty)
            .Replace("{room}", room ?? string.Empty, StringComparison.Ordinal)
            .Replace("{locality}", locality ?? string.Empty, StringComparison.Ordinal);

    // A substituted-away placeholder leaves a double space or a space before a comma; collapsing
    // runs of whitespace is what keeps a satellite with no Locality from reading as a typo.
    private static string Collapse(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Keeps the END of the text (the most recent context) and starts it on a whole word, so a
    // fragment never opens mid-syllable and mis-primes the decoder.
    private static string Tail(string text, int budget)
    {
        if (text.Length <= budget)
        {
            return text;
        }

        var cut = text[^budget..];
        var space = cut.IndexOf(' ');
        return space < 0 ? cut : cut[(space + 1)..];
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WhisperPromptBuilderTests"`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Stt/WhisperPromptBuilder.cs Tests/Unit/McpChannelVoice/Stt/WhisperPromptBuilderTests.cs
git commit -m "feat(voice): compose whisper initial prompts from config and prior context"
```

---

### Task 2: Send the prompt with every transcription

**Files:**
- Modify: `Domain/DTOs/Voice/TranscriptionOptions.cs`
- Modify: `McpChannelVoice/Settings/SttSettings.cs`
- Modify: `McpChannelVoice/Services/Stt/OpenAiSpeechToText.cs:40-49`
- Test: `Tests/Unit/McpChannelVoice/Stt/OpenAiSpeechToTextTests.cs`

**Interfaces:**
- Consumes: `WhisperPromptBuilder.Build` from Task 1.
- Produces: `OpenAiSttConfig.Prompt` (string?), `OpenAiSttConfig.MaxPromptChars` (int, default 700), `TranscriptionOptions.Locality` (string?), `TranscriptionOptions.Prompt` (string?, the prior-segment text consumed in Task 5).

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/McpChannelVoice/Stt/OpenAiSpeechToTextTests.cs`, before the closing brace:

```csharp
    [Fact]
    public async Task TranscribeAsync_NoConfiguredPrompt_OmitsThePromptField()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler);

        await sut.TranscribeAsync(Chunks(new byte[32]), new TranscriptionOptions(), CancellationToken.None);

        handler.Fields.ShouldNotContainKey("prompt");
    }

    [Fact]
    public async Task TranscribeAsync_ConfiguredPrompt_SendsItWithPlaceholdersResolved()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler, new OpenAiSttConfig { Prompt = "Órdenes en {room} ({locality})." });

        await sut.TranscribeAsync(
            Chunks(new byte[32]),
            new TranscriptionOptions { Room = "la cocina", Locality = "Valladolid" },
            CancellationToken.None);

        handler.Fields["prompt"].ShouldBe("Órdenes en la cocina (Valladolid).");
    }

    [Fact]
    public async Task TranscribeAsync_OptionsPrompt_IsAppendedAfterTheConfiguredText()
    {
        var handler = new StubHandler(_ => Json("""{ "text": "hola" }"""));
        var sut = Sut(handler, new OpenAiSttConfig { Prompt = "Órdenes breves." });

        await sut.TranscribeAsync(
            Chunks(new byte[32]),
            new TranscriptionOptions { Prompt = "pon el temporizador" },
            CancellationToken.None);

        handler.Fields["prompt"].ShouldBe("Órdenes breves. pon el temporizador");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiSpeechToTextTests"`
Expected: FAIL — build error, `OpenAiSttConfig` has no `Prompt`, `TranscriptionOptions` has no `Locality`/`Prompt`.

- [ ] **Step 3: Add the settings and options fields**

In `Domain/DTOs/Voice/TranscriptionOptions.cs`, after the existing `Room` property:

```csharp
    public string? Locality { get; init; }

    // Text that immediately precedes this audio — the prior segment's transcript when a
    // segmenting decorator split the utterance. Posted as whisper's initial prompt so a
    // fragment is decoded as the continuation it actually is.
    public string? Prompt { get; init; }
```

In `McpChannelVoice/Settings/SttSettings.cs`, inside `OpenAiSttConfig` after `Language`:

```csharp
    // Initial prompt posted with every transcription: it biases spelling and vocabulary, and on a
    // one-to-three-second command it carries proportionally far more weight than on a paragraph.
    // Supports {room} and {locality}, filled from the capturing satellite. A per-request prompt
    // replaces whisper-server's own --prompt for that request, so this is authoritative for hub
    // traffic and the container default only serves other callers.
    public string? Prompt { get; init; }

    // Character approximation of whisper's 224-token prompt window, deliberately under it.
    public int MaxPromptChars { get; init; } = 700;
```

- [ ] **Step 4: Send the field**

In `McpChannelVoice/Services/Stt/OpenAiSpeechToText.cs`, after the `language` block (line 46-49):

```csharp
        if (WhisperPromptBuilder.Build(
                config.Prompt, options.Room, options.Locality, options.Prompt, config.MaxPromptChars)
            is { } prompt)
        {
            content.Add(new StringContent(prompt), "prompt");
        }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiSpeechToTextTests"`
Expected: PASS — the three new tests plus the eleven that were already there.

- [ ] **Step 6: Commit**

```bash
git add Domain/DTOs/Voice/TranscriptionOptions.cs McpChannelVoice/Settings/SttSettings.cs McpChannelVoice/Services/Stt/OpenAiSpeechToText.cs Tests/Unit/McpChannelVoice/Stt/OpenAiSpeechToTextTests.cs
git commit -m "feat(voice): post a whisper initial prompt with every transcription"
```

---

### Task 3: Per-satellite prompt and the shipped default

**Files:**
- Modify: `McpChannelVoice/Settings/SatelliteConfig.cs`
- Modify: `McpChannelVoice/Services/Stt/TranscriptionOptionsFactory.cs`
- Modify: `McpChannelVoice/appsettings.json`
- Test: `Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs`

**Interfaces:**
- Consumes: `OpenAiSttConfig.Prompt` from Task 2.
- Produces: `SatelliteConfig.ResolvePrompt(string? global)`, `OpenAiSttOverrides.Prompt`, and `TranscriptionOptions.Locality` populated by `TranscriptionOptionsFactory.Create`.

- [ ] **Step 1: Write the failing tests**

Append to `Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs`, before the closing brace:

```csharp
    [Fact]
    public void ResolvePrompt_NoOverride_FallsBackToTheGlobal()
    {
        var config = new SatelliteConfig { Identity = "household", Room = "Kitchen" };

        config.ResolvePrompt("global text").ShouldBe("global text");
    }

    [Fact]
    public void ResolvePrompt_SatelliteOverride_Wins()
    {
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Kitchen",
            Stt = new SttOverrides { OpenAi = new OpenAiSttOverrides { Prompt = "room text" } }
        };

        config.ResolvePrompt("global text").ShouldBe("room text");
    }

    [Fact]
    public void TranscriptionOptionsFactory_Create_CarriesRoomAndLocality()
    {
        var config = new SatelliteConfig
        {
            Identity = "household",
            Room = "Fran's office",
            Locality = "Valladolid, Spain"
        };

        var options = McpChannelVoice.Services.Stt.TranscriptionOptionsFactory.Create(
            "fran-office-01", config, null, default);

        options.Room.ShouldBe("Fran's office");
        options.Locality.ShouldBe("Valladolid, Spain");
    }

    [Fact]
    public void OpenAiSttConfig_PromptDefaults_MatchTheShippedWindow()
    {
        var config = new OpenAiSttConfig();

        config.Prompt.ShouldBeNull();
        config.MaxPromptChars.ShouldBe(700);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSettingsBindingTests"`
Expected: FAIL — build error, `ResolvePrompt` and `OpenAiSttOverrides.Prompt` do not exist.

- [ ] **Step 3: Add the override and resolver**

In `McpChannelVoice/Settings/SatelliteConfig.cs`, add to `OpenAiSttOverrides`:

```csharp
    public string? Prompt { get; init; }
```

and next to the existing `ResolveAvgLogProbThreshold`:

```csharp
    public string? ResolvePrompt(string? global) => Stt?.OpenAi?.Prompt ?? global;
```

- [ ] **Step 4: Carry the locality**

In `McpChannelVoice/Services/Stt/TranscriptionOptionsFactory.cs`, add to the returned object
after `Room = config.Room`:

```csharp
            Locality = config.Locality,
            Prompt = null
```

Note `Prompt` stays null here: the factory builds the options for a whole utterance, and the
prior-segment text is filled in by `SegmentedSpeechToText` in Task 5.

- [ ] **Step 5: Ship a default prompt**

In `McpChannelVoice/appsettings.json`, inside `Stt.OpenAi` after `"Language": "es",`:

```json
            "Prompt": "Órdenes breves a un asistente de voz en español de España, en {room}: domótica, temporizadores, listas de la compra, música y preguntas generales.",
            "MaxPromptChars": 700,
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSettingsBindingTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Settings/SatelliteConfig.cs McpChannelVoice/Services/Stt/TranscriptionOptionsFactory.cs McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs
git commit -m "feat(voice): make the whisper prompt per-satellite and ship a default"
```

---

### Task 4: Never split a short utterance

**Files:**
- Modify: `McpChannelVoice/Settings/SttSettings.cs` (`SegmentedSttConfig`)
- Modify: `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs:52-68`
- Modify: `McpChannelVoice/appsettings.json`
- Test: `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `SegmentedSttConfig.FirstSplitAfterMs` (int, default 4000).

The existing test helper builds `SegmentedSttConfig` with 100 ms chunks, `SegmentSilenceMs = 300`
and `MinSegmentMs = 500`. Set `FirstSplitAfterMs = 0` in that helper so every existing test keeps
its current behaviour, and assert the new gating with an explicit value.

- [ ] **Step 1: Write the failing tests**

In `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`, change the `Config` helper to
carry the new field:

```csharp
    private static SegmentedSttConfig Config(int maxInFlight = 1, int firstSplitAfterMs = 0) => new()
    {
        Enabled = true,
        SilenceRmsThreshold = 500,
        SegmentSilenceMs = 300,
        MinSegmentMs = 500,
        MaxInFlightDecodes = maxInFlight,
        FirstSplitAfterMs = firstSplitAfterMs
    };
```

and append these tests before the closing brace:

```csharp
    [Fact]
    public async Task TranscribeAsync_UtteranceShorterThanFirstSplit_DecodesAsOneSegment()
    {
        var inner = new FakeStt();
        // 8 loud + 4 silent + 6 loud + 4 silent = 2.2 s, all under a 4 s first-split floor.
        var sut = New(inner, Config(firstSplitAfterMs: 4000));

        await sut.TranscribeAsync(
            Stream(Speech(8), Silence(4), Speech(6), Silence(4)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task TranscribeAsync_UtterancePastFirstSplit_ResumesSplitting()
    {
        var inner = new FakeStt();
        // 12 loud + 4 silent crosses 1.2 s of audio before the first pause closes a segment.
        var sut = New(inner, Config(firstSplitAfterMs: 1000));

        await sut.TranscribeAsync(
            Stream(Speech(12), Silence(4), Speech(6), Silence(4)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.Calls.ShouldBe(2);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SegmentedSpeechToTextTests"`
Expected: FAIL — build error, `SegmentedSttConfig` has no `FirstSplitAfterMs`.

- [ ] **Step 3: Add the setting**

In `McpChannelVoice/Settings/SttSettings.cs`, inside `SegmentedSttConfig`:

```csharp
    // Audio that must accumulate before the segmenting gate is allowed to split at all. A short
    // command decoded whole beats the same command decoded as two context-free fragments —
    // measured on prod, splitting "Pon el temporizador de 10 minutos en la cocina" produced a
    // wrong verb and a duplicated number. Only the FIRST split is gated: once an utterance has
    // proven itself long, later splits keep the overlap-with-speech latency win.
    public int FirstSplitAfterMs { get; init; } = 4000;
```

- [ ] **Step 4: Gate the split**

In `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs`, inside `TranscribeAsync`, declare an
elapsed counter next to the existing lists:

```csharp
        var elapsed = TimeSpan.Zero;
        var firstSplitAfter = TimeSpan.FromMilliseconds(config.FirstSplitAfterMs);
```

and change the read loop's body so the gate's decision is only acted on once the utterance is old
enough. The `all.Add(chunk); current.Add(chunk);` lines stay as they are; replace the `if` with:

```csharp
                elapsed += ChunkDuration(chunk);
                var decision = gate.Process(chunk.Data.Span, chunk.Format.SampleRateHz,
                    chunk.Format.SampleWidthBytes, chunk.Format.Channels);
                // Ignoring a decision does not disturb the gate: resumed speech resets its
                // trailing-silence run, and continued silence re-raises EndUtterance on the next
                // chunk past the floor.
                if (decision == SilenceGate.Decision.EndUtterance && elapsed >= firstSplitAfter)
                {
                    var closed = current;
                    current = new List<AudioChunk>();
                    gate.Reset();
                    segments.Add(new Segment(closed, StartDecode(closed, options, slot, ct)));
                }
```

Add the helper next to `DurationSeconds`:

```csharp
    private static TimeSpan ChunkDuration(AudioChunk chunk)
    {
        var bytesPerSecond =
            chunk.Format.SampleRateHz * chunk.Format.SampleWidthBytes * chunk.Format.Channels;
        return bytesPerSecond == 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds((double)chunk.Data.Length / bytesPerSecond);
    }
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SegmentedSpeechToTextTests"`
Expected: PASS — the two new tests plus every existing one.

- [ ] **Step 6: Set the shipped value**

In `McpChannelVoice/appsettings.json`, inside `Stt.Streaming` after `"MinSegmentMs": 800,`:

```json
            "FirstSplitAfterMs": 4000,
```

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Settings/SttSettings.cs McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs
git commit -m "feat(voice): stop the segmenting gate from splitting short utterances"
```

---

### Task 5: Chain each fragment's context

**Files:**
- Modify: `McpChannelVoice/Settings/SttSettings.cs` (`SegmentedSttConfig`)
- Modify: `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs`
- Modify: `McpChannelVoice/appsettings.json`
- Test: `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`

**Interfaces:**
- Consumes: `TranscriptionOptions.Prompt` from Task 2, `FirstSplitAfterMs` from Task 4.
- Produces: `SegmentedSttConfig.ChainContext` (bool, default true).

- [ ] **Step 1: Write the failing tests**

The existing `FakeStt` stub returns the chunk count as text and does not record options. Add a
recording stub and two tests to `Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs`:

```csharp
    // Records the options each segment was decoded with, and returns a distinct word per call so
    // the chained prompt is identifiable.
    private sealed class OptionsRecordingStt : ISpeechToText
    {
        private readonly Lock _lock = new();
        private int _calls;
        public List<string?> Prompts { get; } = [];

        public async Task<TranscriptionResult> TranscribeAsync(
            IAsyncEnumerable<AudioChunk> audio, TranscriptionOptions options, CancellationToken ct)
        {
            await foreach (var _ in audio.WithCancellation(ct)) { }
            int index;
            lock (_lock)
            {
                Prompts.Add(options.Prompt);
                index = _calls++;
            }
            return new TranscriptionResult { Text = $"seg{index}" };
        }
    }

    [Fact]
    public async Task TranscribeAsync_ChainContext_PassesThePriorSegmentTextAsThePrompt()
    {
        var inner = new OptionsRecordingStt();
        var sut = New(inner, Config(firstSplitAfterMs: 0) with { ChainContext = true });

        await sut.TranscribeAsync(
            Stream(Speech(6), Silence(4), Speech(6), Silence(4)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.Prompts.Count.ShouldBe(2);
        inner.Prompts[0].ShouldBeNull();
        inner.Prompts[1].ShouldBe("seg0");
    }

    [Fact]
    public async Task TranscribeAsync_ChainContextOff_LeavesThePromptAlone()
    {
        var inner = new OptionsRecordingStt();
        var sut = New(inner, Config(firstSplitAfterMs: 0) with { ChainContext = false });

        await sut.TranscribeAsync(
            Stream(Speech(6), Silence(4), Speech(6), Silence(4)),
            new TranscriptionOptions(), CancellationToken.None);

        inner.Prompts.Count.ShouldBe(2);
        inner.Prompts.ShouldAllBe(p => p == null);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SegmentedSpeechToTextTests"`
Expected: FAIL — build error, `SegmentedSttConfig` has no `ChainContext`.

- [ ] **Step 3: Add the setting**

In `McpChannelVoice/Settings/SttSettings.cs`, inside `SegmentedSttConfig`:

```csharp
    // Feed each segment the previous segment's transcript as whisper's initial prompt, so a
    // fragment is decoded as the continuation it is rather than as a standalone utterance.
    // This serializes decodes by construction, which is why MaxInFlightDecodes buys nothing
    // while it is on (SegmentedSpeechToText.Wrap warns if both are set).
    public bool ChainContext { get; init; } = true;
```

- [ ] **Step 4: Thread the prior task through the decode**

In `McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs`:

Change both `StartDecode` call sites to pass the previous segment's task. In the read loop:

```csharp
                    segments.Add(new Segment(closed, StartDecode(closed, options, slot, Previous(segments), ct)));
```

In the tail-flush block, the plain append becomes:

```csharp
                segments.Add(new Segment(current, StartDecode(current, options, slot, Previous(segments), ct)));
```

and the merge-backward branch must chain on the segment *before* the one it supersedes:

```csharp
                var prev = segments[^1];
                ObserveAndDiscard(prev.Task);
                var merged = prev.Audio.Concat(current).ToList();
                var beforePrev = segments.Count > 1 ? segments[^2].Task : null;
                segments[^1] = new Segment(merged, StartDecode(merged, options, slot, beforePrev, ct));
```

Add the helper:

```csharp
    private static Task<TranscriptionResult>? Previous(List<Segment> segments) =>
        segments.Count > 0 ? segments[^1].Task : null;
```

Change `StartDecode` to await it:

```csharp
    private Task<TranscriptionResult> StartDecode(
        IReadOnlyList<AudioChunk> chunks, TranscriptionOptions options, SemaphoreSlim slot,
        Task<TranscriptionResult>? previous, CancellationToken ct) =>
        Task.Run(async () =>
        {
            // Awaited BEFORE the slot is taken: the previous decode may still be holding it, and
            // taking it first would deadlock the chain at MaxInFlightDecodes = 1.
            var prior = config.ChainContext && previous is not null
                ? (await previous).Text
                : null;

            var acquired = false;
            try
            {
                await slot.WaitAsync(ct);
                acquired = true;
                return await inner.TranscribeAsync(
                    ToAsyncEnumerable(chunks), options with { Prompt = prior }, ct);
            }
            finally
            {
                if (acquired)
                {
                    slot.Release();
                }
            }
        }, ct);
```

- [ ] **Step 5: Warn on the contradictory combination**

In the same file, in `Wrap`, replace the body so the warning fires once at construction:

```csharp
    public static ISpeechToText Wrap(
        ISpeechToText inner, SegmentedSttConfig config, WyomingClientSettings gateSettings, ILoggerFactory loggers)
    {
        if (!config.Enabled)
        {
            return inner;
        }

        var logger = loggers.CreateLogger<SegmentedSpeechToText>();
        if (config.ChainContext && config.MaxInFlightDecodes > 1)
        {
            logger.LogWarning(
                "Stt.Streaming.ChainContext serializes decodes; MaxInFlightDecodes={MaxInFlight} buys no parallelism",
                config.MaxInFlightDecodes);
        }
        return new SegmentedSpeechToText(inner, config, gateSettings, logger);
    }
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SegmentedSpeechToTextTests"`
Expected: PASS — the two new tests plus every existing one, including the concurrency test that
pins `MaxConcurrent`.

- [ ] **Step 7: Set the shipped value**

In `McpChannelVoice/appsettings.json`, inside `Stt.Streaming` after `"FirstSplitAfterMs": 4000,`:

```json
            "ChainContext": true,
```

- [ ] **Step 8: Commit**

```bash
git add McpChannelVoice/Settings/SttSettings.cs McpChannelVoice/Services/Stt/SegmentedSpeechToText.cs McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/Stt/SegmentedSpeechToTextTests.cs
git commit -m "feat(voice): decode each utterance segment with the previous one as context"
```

---

### Task 6: Duration-aware quality gate

**Files:**
- Modify: `McpChannelVoice/Settings/SttSettings.cs` (`OpenAiSttConfig`)
- Modify: `McpChannelVoice/Settings/SatelliteConfig.cs`
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs:33-36`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs:55-63`
- Modify: `McpChannelVoice/appsettings.json`
- Test: `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `OpenAiSttConfig.ShortSpeechAvgLogProbThreshold` (double, default −1.4),
  `OpenAiSttConfig.FullThresholdSpeechMs` (int, default 2000),
  `SatelliteConfig.ResolveShortSpeechAvgLogProbThreshold(double global)`,
  `SatelliteConfig.ResolveSttFullThresholdSpeechMs(int global)`, and two new
  `TranscriptDispatcher` constructor parameters after `noSpeechProbThreshold`.

The resolver is named `ResolveSttFullThresholdSpeechMs` because `SatelliteConfig` already has a
`ResolveFullThresholdSpeechMs` for speaker verification.

- [ ] **Step 1: Write the failing tests**

The existing `Build` helper hard-codes the dispatcher's thresholds. Widen it and add the tests.
In `Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs`, change the `Build` signature and the
constructor call:

```csharp
    private static (TranscriptDispatcher Sut, VoiceConversationManager Manager, CapturingEmitter Emitter) Build(
        CommandSettings? commands = null, IMetricsPublisher? publisher = null,
        double shortSpeechAvgLogProbThreshold = -1.4, int fullThresholdSpeechMs = 2000)
```

```csharp
        var sut = new TranscriptDispatcher(
            emitter, publisher ?? Mock.Of<IMetricsPublisher>(), manager,
            new VoiceCommandMatcher(commands ?? new CommandSettings()),
            avgLogProbThreshold: -1.0, noSpeechProbThreshold: 0.6,
            shortSpeechAvgLogProbThreshold: shortSpeechAvgLogProbThreshold,
            fullThresholdSpeechMs: fullThresholdSpeechMs,
            time, NullLogger<TranscriptDispatcher>.Instance);
```

and append these tests before the closing brace:

```csharp
    [Fact]
    public async Task DispatchAsync_ShortSpeechUnderTheFullFloor_StillDispatches()
    {
        var (sut, _, emitter) = Build();
        var stats = new CaptureStats(0, 0, 900, "trailing_silence");

        var dispatched = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "para", AvgLogProb = -1.2 },
            "agent-1", stats, null, null, CancellationToken.None);

        dispatched.ShouldBeTrue();
        emitter.Captured.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DispatchAsync_SameScoreAtFullDuration_IsDropped()
    {
        var (sut, _, emitter) = Build();
        var stats = new CaptureStats(0, 0, 5000, "trailing_silence");

        var dispatched = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "para", AvgLogProb = -1.2 },
            "agent-1", stats, null, null, CancellationToken.None);

        dispatched.ShouldBeFalse();
        emitter.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_ShortSpeechUnderTheLooseFloorToo_IsStillDropped()
    {
        var (sut, _, emitter) = Build();
        var stats = new CaptureStats(0, 0, 900, "trailing_silence");

        var dispatched = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "para", AvgLogProb = -1.9 },
            "agent-1", stats, null, null, CancellationToken.None);

        dispatched.ShouldBeFalse();
        emitter.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatchAsync_NoCaptureStats_UsesTheFullFloor()
    {
        var (sut, _, emitter) = Build();

        var dispatched = await sut.DispatchAsync(
            Session(), new TranscriptionResult { Text = "para", AvgLogProb = -1.2 },
            "agent-1", null, null, null, CancellationToken.None);

        dispatched.ShouldBeFalse();
        emitter.Captured.ShouldBeEmpty();
    }
```

Also add the shipped-default assertion to `Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs`,
next to the existing `OpenAiSttConfig_DefaultThresholds_MatchShippedGibberishGate`:

```csharp
    [Fact]
    public void OpenAiSttConfig_ShortSpeechDefaults_MatchTheShippedGate()
    {
        var config = new OpenAiSttConfig();

        config.ShortSpeechAvgLogProbThreshold.ShouldBe(-1.4);
        config.FullThresholdSpeechMs.ShouldBe(2000);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests"`
Expected: FAIL — build error, `TranscriptDispatcher` has no such constructor parameters.

- [ ] **Step 3: Add the settings**

In `McpChannelVoice/Settings/SttSettings.cs`, inside `OpenAiSttConfig` after
`NoSpeechProbThreshold`:

```csharp
    // avg_logprob falls with utterance length for reasons that have nothing to do with being
    // wrong — measured on prod, a 2.9 s clip scored -0.12 and a 0.75 s clip -0.23 — so a single
    // floor drops correct short commands more readily than correct long ones. Below
    // FullThresholdSpeechMs of measured speech the looser floor applies. Mirrors the pair
    // SpeakerVerification already uses for the same reason.
    public double ShortSpeechAvgLogProbThreshold { get; init; } = -1.4;
    public int FullThresholdSpeechMs { get; init; } = 2000;
```

In `McpChannelVoice/Settings/SatelliteConfig.cs`, add to `OpenAiSttOverrides`:

```csharp
    public double? ShortSpeechAvgLogProbThreshold { get; init; }
    public int? FullThresholdSpeechMs { get; init; }
```

and next to `ResolveNoSpeechProbThreshold`:

```csharp
    public double ResolveShortSpeechAvgLogProbThreshold(double global) =>
        Stt?.OpenAi?.ShortSpeechAvgLogProbThreshold ?? global;

    // Distinct from ResolveFullThresholdSpeechMs above, which is speaker verification's.
    public int ResolveSttFullThresholdSpeechMs(int global) =>
        Stt?.OpenAi?.FullThresholdSpeechMs ?? global;
```

- [ ] **Step 4: Pick the floor by duration**

In `McpChannelVoice/Services/TranscriptDispatcher.cs`, add the two constructor parameters after
`noSpeechProbThreshold`:

```csharp
    double shortSpeechAvgLogProbThreshold,
    int fullThresholdSpeechMs,
```

and replace the `avgLogProbFloor` line (line 33) with:

```csharp
        var fullSpeechMs = session.Config.ResolveSttFullThresholdSpeechMs(fullThresholdSpeechMs);
        var avgLogProbFloor = stats is { } capture && capture.SpeechMs < fullSpeechMs
            ? session.Config.ResolveShortSpeechAvgLogProbThreshold(shortSpeechAvgLogProbThreshold)
            : session.Config.ResolveAvgLogProbThreshold(avgLogProbThreshold);
```

- [ ] **Step 5: Wire the settings**

In `McpChannelVoice/Modules/ConfigModule.cs`, in the `TranscriptDispatcher` registration, after
`noSpeechProbThreshold:`:

```csharp
                shortSpeechAvgLogProbThreshold: settings.Stt.OpenAi.ShortSpeechAvgLogProbThreshold,
                fullThresholdSpeechMs: settings.Stt.OpenAi.FullThresholdSpeechMs,
```

In `McpChannelVoice/appsettings.json`, inside `Stt.OpenAi` after `"NoSpeechProbThreshold": 0.6,`:

```json
            "ShortSpeechAvgLogProbThreshold": -1.4,
            "FullThresholdSpeechMs": 2000,
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TranscriptDispatcherTests"`
Expected: PASS — the four new tests plus every existing one.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Settings/SttSettings.cs McpChannelVoice/Settings/SatelliteConfig.cs McpChannelVoice/Services/TranscriptDispatcher.cs McpChannelVoice/Modules/ConfigModule.cs McpChannelVoice/appsettings.json Tests/Unit/McpChannelVoice/TranscriptDispatcherTests.cs
git commit -m "feat(voice): scale the gibberish gate floor to the utterance length"
```

---

### Task 7: Container decode flags and the cleaned initial prompt

**Files:**
- Modify: `DockerCompose/lemonade/entrypoint.sh`
- Modify: `DockerCompose/docker-compose.yml:586-596`
- Modify: `.claude/rules/voice.md`
- Test: `Tests/Integration/McpChannelVoice/LemonadeEntrypointConfigTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `STT_SUPPRESS_NST`, `STT_BEST_OF`, `STT_VAD_SPEECH_PAD_MS`, `STT_VAD_MIN_SPEECH_MS`.

These are integration tests: they need docker and a built `lemonade:latest`, and skip otherwise.
Run them, and if they skip on this host, say so plainly rather than reporting a pass.

- [ ] **Step 1: Write the failing tests**

In `Tests/Integration/McpChannelVoice/LemonadeEntrypointConfigTests.cs`, replace the two
`Valladolid` assertions — in `Entrypoint_Defaults_RestoreVadPromptAndBeamSize` change
`whisperArgs.ShouldContain("Valladolid.\"")` to
`whisperArgs.ShouldNotContain("p. ej.")` — and append:

```csharp
    [SkippableFact]
    public void Entrypoint_Defaults_AddSuppressNstBestOfAndVadPadding()
    {
        SeedVadModel();

        var whisperArgs = WhisperArgs(RunEntrypoint(("STT_BACKEND", "cpu")));

        whisperArgs.ShouldContain("--suppress-nst");
        whisperArgs.ShouldContain("--best-of 5");
        whisperArgs.ShouldContain("--vad-speech-pad-ms 150");
        whisperArgs.ShouldContain("--vad-min-speech-duration-ms 150");
    }

    [SkippableFact]
    public void Entrypoint_EmptyDecodeKnobs_DisableTheirFlags()
    {
        SeedVadModel();

        var whisperArgs = WhisperArgs(RunEntrypoint(
            ("STT_BACKEND", "cpu"),
            ("STT_SUPPRESS_NST", ""),
            ("STT_BEST_OF", ""),
            ("STT_VAD_SPEECH_PAD_MS", ""),
            ("STT_VAD_MIN_SPEECH_MS", "")));

        whisperArgs.ShouldNotContain("--suppress-nst");
        whisperArgs.ShouldNotContain("--best-of");
        whisperArgs.ShouldNotContain("--vad-speech-pad-ms");
        whisperArgs.ShouldNotContain("--vad-min-speech-duration-ms");
        whisperArgs.ShouldContain("--vad --vad-model");
    }

    [SkippableFact]
    public void Entrypoint_VadDisabled_EmitsNoVadPaddingFlags()
    {
        SeedVadModel();

        var whisperArgs = WhisperArgs(RunEntrypoint(
            ("STT_BACKEND", "cpu"),
            ("STT_VAD_THRESHOLD", "")));

        whisperArgs.ShouldNotContain("--vad");
        whisperArgs.ShouldContain("--suppress-nst");
    }

    [SkippableFact]
    public void Entrypoint_DecodeOverrides_PropagateToArgs()
    {
        SeedVadModel();

        var whisperArgs = WhisperArgs(RunEntrypoint(
            ("STT_BACKEND", "cpu"),
            ("STT_BEST_OF", "3"),
            ("STT_VAD_SPEECH_PAD_MS", "80"),
            ("STT_VAD_MIN_SPEECH_MS", "200")));

        whisperArgs.ShouldContain("--best-of 3");
        whisperArgs.ShouldContain("--vad-speech-pad-ms 80");
        whisperArgs.ShouldContain("--vad-min-speech-duration-ms 200");
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~LemonadeEntrypointConfigTests"`
Expected: FAIL on the new assertions (or SKIP if docker or `lemonade:latest` is unavailable — in
that case build the image first with `docker compose -f DockerCompose/docker-compose.yml build lemonade`,
and if that is not possible, record that this task's verification is docker-blocked).

- [ ] **Step 3: Add the knobs to the entrypoint**

In `DockerCompose/lemonade/entrypoint.sh`, next to the existing `BEAM_SIZE` / `VAD_THRESHOLD` /
`INITIAL_PROMPT` block, add:

```sh
BEST_OF="${STT_BEST_OF-5}"
SUPPRESS_NST="${STT_SUPPRESS_NST-1}"
VAD_SPEECH_PAD_MS="${STT_VAD_SPEECH_PAD_MS-150}"
VAD_MIN_SPEECH_MS="${STT_VAD_MIN_SPEECH_MS-150}"
```

and rewrite the `INITIAL_PROMPT` default, dropping the parenthetical and the `p. ej.` tail that
was observed being emitted verbatim on short audio:

```sh
INITIAL_PROMPT="${STT_INITIAL_PROMPT-Asistente de voz en español de España. Órdenes breves de domótica, temporizadores, listas de la compra, música y preguntas generales.}"
```

After the existing beam-size block, append the two unconditional flags:

```sh
# whisper.cpp keeps 2 candidates against OpenAI's 5; applies on temperature fallback.
if [ -n "$BEST_OF" ]; then
  WHISPER_ARGS="$WHISPER_ARGS --best-of $BEST_OF"
fi
# Non-speech tokens are what the round-1 eval saw as "[Música]" and YouTube boilerplate on
# near-unintelligible audio.
if [ -n "$SUPPRESS_NST" ]; then
  WHISPER_ARGS="$WHISPER_ARGS --suppress-nst"
fi
```

Inside the branch that already established VAD is usable (`if [ -s "$VAD_MODEL" ]; then`), after
the existing `--vad --vad-model ... --vad-threshold ...` line, add:

```sh
    # whisper.cpp pads a VAD segment by 30 ms, tight enough to clip a leading plosive off a
    # one-word command, and discards speech shorter than 250 ms outright — both are exactly
    # the short-command case.
    if [ -n "$VAD_SPEECH_PAD_MS" ]; then
      WHISPER_ARGS="$WHISPER_ARGS --vad-speech-pad-ms $VAD_SPEECH_PAD_MS"
    fi
    if [ -n "$VAD_MIN_SPEECH_MS" ]; then
      WHISPER_ARGS="$WHISPER_ARGS --vad-min-speech-duration-ms $VAD_MIN_SPEECH_MS"
    fi
```

Also update the file's header comment, which currently says the entrypoint restores "the
Wyoming-era decode-quality flags", to name the new short-phrase knobs too.

- [ ] **Step 4: Pass the knobs through compose**

In `DockerCompose/docker-compose.yml`, in the `lemonade` service's `environment` block after
`STT_BEAM_SIZE:`:

```yaml
      STT_SUPPRESS_NST:
      STT_BEST_OF:
      STT_VAD_SPEECH_PAD_MS:
      STT_VAD_MIN_SPEECH_MS:
```

Extend the comment above them so it lists the new defaults (suppress-nst on, best-of 5, VAD
padding 150 ms, VAD min speech 150 ms) alongside the existing three.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~LemonadeEntrypointConfigTests"`
Expected: PASS (or SKIP with the reason stated).

- [ ] **Step 6: Update the voice rule**

In `.claude/rules/voice.md`, the pipeline paragraph names the decode knobs as
"`STT_VAD_THRESHOLD`/`STT_INITIAL_PROMPT`/`STT_BEAM_SIZE` — defaults Silero VAD 0.6 + Castilian
initial prompt + beam 5". Extend that list with the four new knobs and their defaults, and add a
sentence that the hub now posts a per-request `prompt` (`Stt.OpenAi.Prompt`, `{room}`/`{locality}`
placeholders, per-satellite overridable) which replaces the container's `--prompt` for hub
traffic, that `Stt.Streaming` no longer splits before `FirstSplitAfterMs` and chains each
fragment's context, and that the gibberish gate's `avg_logprob` floor loosens below
`FullThresholdSpeechMs` of speech.

- [ ] **Step 7: Commit**

```bash
git add DockerCompose/lemonade/entrypoint.sh DockerCompose/docker-compose.yml .claude/rules/voice.md Tests/Integration/McpChannelVoice/LemonadeEntrypointConfigTests.cs
git commit -m "feat(voice): tune whisper-server VAD padding, sampling and initial prompt"
```

---

### Task 8: Eval harness sends a prompt

**Files:**
- Modify: `scripts/stt-enhancement-eval/stt_eval/lemonade_worker.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/backends.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/__main__.py`
- Modify: `scripts/stt-enhancement-eval/README.md`
- Test: `scripts/stt-enhancement-eval/tests/test_lemonade_worker.py`, `tests/test_backends.py`, `tests/test_cli.py`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `lemonade_worker.py --prompt TEXT`; `transcribe_files(backend, wavs, out_jsonl, prompt=None)`; `stt_eval transcribe --prompt TEXT --label NAME`.

`run_report` treats every directory under `runs/<run>/transcripts/` as a backend column, so
`--label` is what makes a prompted run appear beside an unprompted one in the report.

Run these tests with `uv run pytest` from `scripts/stt-enhancement-eval`.

- [ ] **Step 1: Write the failing tests**

`tests/test_lemonade_worker.py` currently only covers `_score` and has no audio fixture, so these
tests make their own. `_post_transcription` only reads the file's bytes, so any content works.

Add to `tests/test_lemonade_worker.py`:

```python
import json

from stt_eval import lemonade_worker


class _FakeResponse:
    def read(self):
        return b'{"text": "hola", "segments": []}'

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        return False


def _capture_post(monkeypatch, tmp_path, **kwargs):
    captured = {}

    def fake_urlopen(req, timeout=None):
        captured["body"] = req.data
        return _FakeResponse()

    monkeypatch.setattr(lemonade_worker.urllib.request, "urlopen", fake_urlopen)
    wav = tmp_path / "clip.wav"
    wav.write_bytes(b"RIFF____WAVEfmt ")
    lemonade_worker._post_transcription("h", 1, "m", str(wav), **kwargs)
    return captured["body"]


def test_post_transcription_includes_prompt_field_when_set(monkeypatch, tmp_path):
    body = _capture_post(monkeypatch, tmp_path, prompt="órdenes breves")

    assert b'name="prompt"' in body
    assert "órdenes breves".encode() in body


def test_post_transcription_omits_prompt_field_when_unset(monkeypatch, tmp_path):
    body = _capture_post(monkeypatch, tmp_path)

    assert b'name="prompt"' not in body
    assert b'name="language"' in body


def test_post_transcription_omits_prompt_field_when_empty(monkeypatch, tmp_path):
    body = _capture_post(monkeypatch, tmp_path, prompt="")

    assert b'name="prompt"' not in body
```

For the CLI, assert the new arguments parse. `tests/test_cli.py` currently shells out to
`python -m stt_eval --help` and imports nothing from the package, so add the import it needs:

```python
from stt_eval.__main__ import build_parser


def test_transcribe_stage_accepts_prompt_and_label():
    args = build_parser().parse_args(
        ["transcribe", "--backend", "lemonade", "--prompt", "hola", "--label", "lemonade-prompted"])

    assert args.prompt == "hola"
    assert args.label == "lemonade-prompted"


def test_transcribe_stage_label_defaults_to_none():
    args = build_parser().parse_args(["transcribe", "--backend", "lemonade"])

    assert args.label is None
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd scripts/stt-enhancement-eval && uv run pytest -q`
Expected: FAIL — `_post_transcription` takes no `prompt`, parser has no `--prompt`/`--label`.

- [ ] **Step 3: Implement the worker change**

In `lemonade_worker.py`, give `_post_transcription` a `prompt=None` parameter and build the field
list conditionally:

```python
def _post_transcription(host, port, model, wav_path, prompt=None):
    with open(wav_path, "rb") as fh:
        audio = fh.read()
    boundary = uuid.uuid4().hex
    fields = [("model", model), ("response_format", "verbose_json"), ("language", "es")]
    # Lemonade forwards this to whisper-server, where it replaces the server's own --prompt for
    # this request. Omitted entirely when unset, so an unprompted run keeps the container default.
    if prompt:
        fields.append(("prompt", prompt))
    parts = []
    for name, value in fields:
        parts.append(
            f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n'.encode()
        )
```

Add `--prompt` to the worker's argparse and pass `args.prompt` through the read loop's
`_post_transcription` call.

- [ ] **Step 4: Plumb it through the backend and the CLI**

In `backends.py`, add a `prompt: str | None = None` parameter to `transcribe_files` and to
`_lemonade`, append `["--prompt", prompt]` to the `docker run` argument list when it is set, and
dispatch with a keyword so `_medium` (which ignores it) still works:

```python
def transcribe_files(backend: str, wavs: list[Path], out_jsonl: Path, prompt: str | None = None) -> None:
    out_jsonl.parent.mkdir(parents=True, exist_ok=True)
    done = _done_wavs(out_jsonl)
    todo = [w for w in wavs if str(w) not in done and str(w.resolve()) not in done]
    if not todo:
        print(f"{out_jsonl}: complete ({len(done)} rows)")
        return
    if backend == "medium":
        if prompt:
            raise SystemExit("--prompt is only supported by the lemonade backend")
        _medium(todo, out_jsonl)
    else:
        _lemonade(todo, out_jsonl, prompt)
```

In `__main__.py`, add to the `transcribe` stage's arguments:

```python
        p.add_argument("--prompt", default=None,
                       help="whisper initial prompt to post with each clip (lemonade backend only)")
        p.add_argument("--label", default=None,
                       help="transcripts subdir name; defaults to the backend name. Use it to keep "
                            "two decode configs of the same backend side by side in the report.")
```

and in `_transcribe`, write under the label:

```python
        label = args.label or args.backend
        transcribe_files(args.backend, wavs, run_dir / "transcripts" / label / f"{cond}.jsonl",
                         prompt=args.prompt)
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `cd scripts/stt-enhancement-eval && uv run pytest -q`
Expected: PASS.

- [ ] **Step 6: Document it**

In `scripts/stt-enhancement-eval/README.md`, add a prompted-run line to the command block and one
sentence explaining that `--label` is what puts two decode configs side by side in the report:

```bash
uv run python -m stt_eval transcribe --backend lemonade --label lemonade-prompted \
  --prompt "Órdenes breves a un asistente de voz en español de España." --conditions raw
```

- [ ] **Step 7: Commit**

```bash
git add scripts/stt-enhancement-eval/stt_eval scripts/stt-enhancement-eval/tests scripts/stt-enhancement-eval/README.md
git commit -m "feat(eval): post a whisper prompt and label decode configs separately"
```

---

### Task 9: Synthetic short-command corpus

**Files:**
- Modify: `scripts/stt-enhancement-eval/stt_eval/phrases.py`
- Create: `scripts/stt-enhancement-eval/stt_eval/synth_stage.py`
- Modify: `scripts/stt-enhancement-eval/stt_eval/__main__.py`
- Modify: `scripts/stt-enhancement-eval/README.md`
- Test: `scripts/stt-enhancement-eval/tests/test_synth_stage.py`, `tests/test_phrases.py`, `tests/test_cli.py`

**Interfaces:**
- Consumes: `Utterance` from `stt_eval.manifest`.
- Produces: `SHORT_COMMANDS: list[str]` in `phrases.py`; `run_synth(run_dir: Path, base_url: str, voices: list[str], model: str, fetch=...) -> None` in `synth_stage.py`; `stt_eval synth` stage.

`run_synth` writes `runs/<run>/corpus/<id>.wav` (16 kHz mono s16le) and `runs/<run>/manifest.jsonl`
with `interference="none"` and `snr_db=None`, so the existing `transcribe` and `report` stages
consume it unchanged. The report's PASS/FAIL decision block is about enhancement at low SNR and is
degenerate on a clean-only corpus — the WER table is the output that matters here.

The `fetch` parameter is the seam that keeps the test offline: it takes `(text, voice) -> bytes`
returning wav bytes, and defaults to a function that POSTs Lemonade's `/api/v1/audio/speech`.

- [ ] **Step 1: Write the failing tests**

Create `scripts/stt-enhancement-eval/tests/test_synth_stage.py`:

```python
import io
import json
import wave
from pathlib import Path

import numpy as np
import soundfile as sf

from stt_eval.manifest import read_manifest
from stt_eval.phrases import SHORT_COMMANDS
from stt_eval.synth_stage import run_synth


def _wav_bytes(seconds=0.5, rate=24000):
    buf = io.BytesIO()
    samples = np.zeros(int(rate * seconds), dtype="float32")
    sf.write(buf, samples, rate, format="WAV", subtype="FLOAT")
    return buf.getvalue()


def test_run_synth_writes_16k_mono_pcm_and_a_manifest(tmp_path):
    calls = []

    def fake_fetch(text, voice):
        calls.append((text, voice))
        return _wav_bytes()

    run_synth(tmp_path, "http://lemonade:13305", ["em_santa", "ef_dora"], "kokoro-v1", fetch=fake_fetch)

    assert len(calls) == len(SHORT_COMMANDS) * 2
    manifest = read_manifest(tmp_path / "manifest.jsonl")
    assert len(manifest) == len(SHORT_COMMANDS) * 2
    assert {u.interference for u in manifest} == {"none"}
    assert all(u.snr_db is None for u in manifest)
    assert all(u.reference in SHORT_COMMANDS for u in manifest)

    for u in manifest:
        with wave.open(str(tmp_path / u.wav), "rb") as w:
            assert w.getframerate() == 16000
            assert w.getnchannels() == 1
            assert w.getsampwidth() == 2


def test_run_synth_is_idempotent(tmp_path):
    def fake_fetch(text, voice):
        return _wav_bytes()

    run_synth(tmp_path, "http://x", ["em_santa"], "kokoro-v1", fetch=fake_fetch)
    calls = []

    def counting_fetch(text, voice):
        calls.append(text)
        return _wav_bytes()

    run_synth(tmp_path, "http://x", ["em_santa"], "kokoro-v1", fetch=counting_fetch)
    assert calls == []
```

Add to `tests/test_phrases.py`:

```python
def test_short_commands_are_short_and_distinct():
    from stt_eval.phrases import SHORT_COMMANDS

    assert len(SHORT_COMMANDS) == len(set(SHORT_COMMANDS))
    assert all(len(p.split()) <= 6 for p in SHORT_COMMANDS)
```

Add to `tests/test_cli.py`:

```python
def test_synth_stage_parses():
    args = build_parser().parse_args(["synth", "--run", "short1"])

    assert args.stage == "synth"
    assert args.run == "short1"
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `cd scripts/stt-enhancement-eval && uv run pytest -q`
Expected: FAIL — no `SHORT_COMMANDS`, no `stt_eval.synth_stage`, no `synth` stage.

- [ ] **Step 3: Add the phrase list**

In `phrases.py`, after `PHRASES`:

```python
# Short commands — the one-to-three-second case whisper is weakest on. Kept to the shapes the
# household actually says, and deliberately including the bare one-word turns ("para", "sigue")
# that a VAD minimum-speech-duration can discard outright.
SHORT_COMMANDS = [
    "Para.",
    "Sigue.",
    "Sube el volumen.",
    "Baja el volumen.",
    "Apaga la luz del salón.",
    "Enciende la luz de la cocina.",
    "Pon música.",
    "Siguiente canción.",
    "Pon un temporizador de diez minutos.",
    "Cancela el temporizador.",
    "¿Qué hora es?",
    "Añade leche a la lista de la compra.",
]
```

- [ ] **Step 4: Write the synth stage**

Create `scripts/stt-enhancement-eval/stt_eval/synth_stage.py`:

```python
"""Builds a short-command corpus by synthesizing each phrase through Lemonade's Kokoro TTS.

SYNTHETIC SPEECH. There is no room, no reverberation, no far-field mic and no speaker variation
beyond the TTS voices, so a WER from this corpus does not transfer to a deployed satellite. What
it is good for is comparing two decode configurations against each other on identical audio —
which is what the prompt and whisper-flag changes need. Any result written from it must say so,
the same way results/2026-07-round1.md carries its synthetic-mixing caveat.
"""
import json
import re
import urllib.request
from pathlib import Path

import numpy as np
import soundfile as sf
import soxr

from .manifest import Utterance, write_manifest
from .phrases import SHORT_COMMANDS

TARGET_RATE = 16000


def _slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")[:40]


def _fetch_speech(base_url: str, model: str):
    def fetch(text: str, voice: str) -> bytes:
        body = json.dumps(
            {"model": model, "voice": voice, "input": text, "response_format": "wav"}
        ).encode()
        req = urllib.request.Request(
            f"{base_url.rstrip('/')}/api/v1/audio/speech",
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        with urllib.request.urlopen(req, timeout=120) as resp:
            return resp.read()

    return fetch


# Kokoro returns float32 at 24 kHz; the satellites send 16 kHz mono s16le, and the corpus must
# match what the hub actually posts or the decode is not being measured on prod-shaped audio.
def _to_16k_mono_pcm(wav_bytes: bytes, out_path: Path) -> None:
    import io

    samples, rate = sf.read(io.BytesIO(wav_bytes), dtype="float32", always_2d=True)
    mono = samples.mean(axis=1)
    if rate != TARGET_RATE:
        mono = soxr.resample(mono, rate, TARGET_RATE)
    out_path.parent.mkdir(parents=True, exist_ok=True)
    sf.write(out_path, np.clip(mono, -1.0, 1.0), TARGET_RATE, subtype="PCM_16")


def run_synth(
    run_dir: Path,
    base_url: str,
    voices: list[str],
    model: str,
    fetch=None,
) -> None:
    fetch = fetch or _fetch_speech(base_url, model)
    corpus = run_dir / "corpus"
    rows = []
    for voice in voices:
        for index, phrase in enumerate(SHORT_COMMANDS, start=1):
            uid = f"{voice}-{index:02d}-{_slug(phrase)}"
            wav = corpus / f"{uid}.wav"
            # Presence-based resume, matching every other stage in this harness.
            if not wav.exists():
                _to_16k_mono_pcm(fetch(phrase, voice), wav)
            rows.append(Utterance(
                id=uid, speaker=voice, take=index, wav=str(wav.relative_to(run_dir)),
                reference=phrase, interference="none", snr_db=None))
    write_manifest(run_dir / "manifest.jsonl", rows)
```

- [ ] **Step 5: Add the CLI stage**

In `__main__.py`, add `"synth"` to the stage tuple and the metavar, add its arguments in
`_add_stage_args`:

```python
    if name == "synth":
        p.add_argument("--lemonade", default="http://localhost:13305",
                       help="Lemonade base url used for Kokoro TTS")
        p.add_argument("--tts-voices", default="em_santa,ef_dora")
        p.add_argument("--tts-model", default="kokoro-v1")
```

and register the stage:

```python
def _synth(args: argparse.Namespace) -> None:
    from .synth_stage import run_synth
    run_synth(Path("runs") / args.run, args.lemonade,
              [v.strip() for v in args.tts_voices.split(",") if v.strip()], args.tts_model)


STAGES["synth"] = _synth
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `cd scripts/stt-enhancement-eval && uv run pytest -q`
Expected: PASS.

- [ ] **Step 7: Document it**

In `scripts/stt-enhancement-eval/README.md`, add a short-command section with the runnable
sequence and the synthetic caveat stated in the same breath:

```bash
# Short-command corpus: SYNTHETIC speech (Kokoro TTS), for comparing decode configs against each
# other on identical audio. It does not produce a WER that transfers to a real satellite.
uv run python -m stt_eval synth --run short1
uv run python -m stt_eval transcribe --run short1 --backend lemonade --conditions raw
uv run python -m stt_eval transcribe --run short1 --backend lemonade --label lemonade-prompted \
  --prompt "Órdenes breves a un asistente de voz en español de España." --conditions raw
uv run python -m stt_eval report --run short1
```

- [ ] **Step 8: Commit**

```bash
git add scripts/stt-enhancement-eval/stt_eval scripts/stt-enhancement-eval/tests scripts/stt-enhancement-eval/README.md
git commit -m "feat(eval): build a synthetic short-command corpus from Kokoro TTS"
```

---

### Task 10: Full suite and settings sanity

**Files:**
- Test: the whole unit suite.

- [ ] **Step 1: Run every unit test**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.Unit"`
Expected: PASS, at least the 2414 that passed at baseline plus the ~24 added here. Any test that
binds `appsettings.json` or asserts shipped defaults must agree with the new values; fix the
production setting or the test, whichever is actually wrong, and say which.

- [ ] **Step 2: Confirm the app still starts its DI graph**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: build succeeds with no warnings introduced by these changes.

- [ ] **Step 3: Commit any fixes and report**

Report which tasks were verified by passing tests and which (the docker-dependent Task 7 tests)
were skipped for lack of an environment, without rounding a skip up to a pass.
