# Voice Satellites Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build an Alexa-like voice interface for the agent: small Wyoming satellites in rooms capture audio after wake-word, a new `McpChannelVoice` server runs STT, dispatches the transcript to the agent via the existing MCP HTTP channel protocol, then streams a TTS reply back to the originating satellite. The channel also owns each satellite's audio output: external callers (Home Assistant, scripts, the agent) push announcements via `POST /api/voice/announce`.

**Architecture:** New `McpChannelVoice` .NET project mirrors `McpChannelTelegram`/`McpChannelSignalR`. It hosts (a) a Wyoming TCP server (port 10700) for inbound satellite connections, (b) the MCP HTTP transport (port 5010) that the agent connects to, and (c) the announce HTTP endpoint (same port). STT/TTS are pluggable behind `ISpeechToText`/`ITextToSpeech` contracts; default adapters speak Wyoming over TCP to `wyoming-faster-whisper` and `wyoming-piper`. Per-satellite identity/room/wake-word come from configuration. A `Voice` dashboard page surfaces KPIs and breakdowns via the existing metrics pipeline.

**Tech Stack:** .NET 10, MCP HTTP transport (existing), Wyoming protocol (newline-delimited JSON + binary payload over TCP), `wyoming-faster-whisper`, `wyoming-piper`, `wyoming-satellite` + `openWakeWord` on Pi Zero 2 W, Redis Pub/Sub (existing metrics), Blazor WASM (Dashboard.Client).

---

## File Structure

### New project: `McpChannelVoice/`

| File | Responsibility |
|------|---------------|
| `McpChannelVoice.csproj` | Project file referencing Domain + Infrastructure |
| `Program.cs` | Bootstrap: config → DI → MCP HTTP + announce endpoint + Wyoming server |
| `Dockerfile` | Multi-stage build (mirror Telegram) |
| `appsettings.json` | Voice config skeleton (provider, satellites, announce) |
| `appsettings.Development.json` | Dev overrides |
| `Modules/ConfigModule.cs` | `GetSettings()` + `ConfigureChannel()` DI wiring |
| `Modules/VoiceModule.cs` | STT/TTS provider switch (Wyoming vs OpenAI) |
| `Settings/VoiceSettings.cs` | Strongly-typed settings record tree |
| `McpTools/SendReplyTool.cs` | `[McpServerToolType]` — routes reply text to originating satellite's session |
| `McpTools/RequestApprovalTool.cs` | `[McpServerToolType]` — speaks prompt, opens capture window, parses yes/no |
| `Services/WyomingProtocol.cs` | Static helpers: read/write Wyoming events (JSON header + optional binary payload) |
| `Services/WyomingServer.cs` | TCP listener; spawns a `SatelliteSession` per inbound satellite |
| `Services/WyomingClient.cs` | Wyoming TCP client (used by adapters to talk to whisper/piper) |
| `Services/SatelliteSession.cs` | One satellite connection: capture → STT → notify → reply queue → playback |
| `Services/SatelliteRegistry.cs` | id → identity/room/overrides; reverse lookups (room → ids, all) |
| `Services/ConfidenceGate.cs` | Drops empty/low-confidence transcripts before dispatch |
| `Services/ApprovalGrammarParser.cs` | Parses yes/no/sí/no/cancel/confirm in EN+ES |
| `Services/ChannelNotificationEmitter.cs` | Builds `channel/message` notifications; routes to all MCP sessions |
| `Services/AnnounceEndpoint.cs` | `POST /api/voice/announce` route handler + token auth |
| `Services/AnnouncementService.cs` | Resolve target → synthesize → enqueue per satellite |
| `Services/VoiceMetricsPublisher.cs` | Thin wrapper around `IMetricsPublisher` that fills voice dimensions |

### Existing projects — additions

| Path | Change |
|------|--------|
| `Domain/Contracts/ISpeechToText.cs` | New |
| `Domain/Contracts/ITextToSpeech.cs` | New |
| `Domain/DTOs/Voice/AudioChunk.cs` | New |
| `Domain/DTOs/Voice/TranscriptionResult.cs` | New |
| `Domain/DTOs/Voice/TranscriptionOptions.cs` | New |
| `Domain/DTOs/Voice/SynthesisOptions.cs` | New |
| `Domain/DTOs/Metrics/Enums/VoiceDimension.cs` | New |
| `Domain/DTOs/Metrics/Enums/VoiceMetric.cs` | New |
| `Domain/DTOs/Metrics/VoiceMetricEvent.cs` | New (polymorphic `MetricEvent` derived) |
| `Domain/DTOs/Metrics/HeartbeatEvent.cs` | Add `voice.connected` source string (no enum change) |
| `Infrastructure/Clients/Voice/WyomingSpeechToText.cs` | New |
| `Infrastructure/Clients/Voice/WyomingTextToSpeech.cs` | New |
| `Infrastructure/Clients/Voice/OpenAiSpeechToText.cs` | New (Slice 6) |
| `Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs` | New (Slice 6) |
| `Agent/appsettings.json` | Add `channelEndpoints[]` entry for voice |
| `Observability/Services/MetricsQueryService.cs` | Add `GetVoiceGroupedAsync` + helpers |
| `Observability/MetricsApiEndpoints.cs` | Add `/api/metrics/voice/*` routes |
| `Dashboard.Client/Pages/Voice.razor` | New page (KPIs + charts + recent utterances) |
| `Dashboard.Client/Layout/MainLayout.razor` | Add `/voice` NavLink |
| `Dashboard.Client/Pages/Overview.razor` | Add two KPI cards |
| `Dashboard.Client/Services/MetricsApiService.cs` | Add `GetVoice*Async` methods |
| `DockerCompose/docker-compose.yml` | Add `mcp-channel-voice`, `wyoming-whisper`, `wyoming-piper`; add `OPENAI_API_KEY`, `ANNOUNCE_TOKEN` to `mcp-channel-voice` env |
| `DockerCompose/.env` | Add `ANNOUNCE_TOKEN=changeme`, `OPENAI_API_KEY=` placeholders |
| `scripts/provision-satellite.sh` | New one-shot Pi-side script |
| `Tests/Unit/McpChannelVoice/*` | All unit test files |
| `Tests/Integration/McpChannelVoice/*` | All integration test files |

### Solution file
- `agent.sln` — add `McpChannelVoice` project.

---

## Conventions used throughout this plan

- **Namespaces** are file-scoped (`namespace X;`).
- **Records** for DTOs and settings. **Primary constructors** for services with DI dependencies.
- **`IReadOnlyList<T>`** for collection returns. **LINQ over loops** where readable.
- **`TimeProvider`** (injected) for any time-dependent code; never `DateTime.UtcNow` directly.
- **No XML doc comments.** Inline comments only when explaining a non-obvious "why".
- **Tests** under `Tests/Unit/McpChannelVoice/...` and `Tests/Integration/McpChannelVoice/...` using xUnit + FluentAssertions (match existing test projects).
- **Commit cadence**: one commit per task. Use Conventional Commit prefixes (`feat:`, `test:`, `chore:`, `docs:`).
- **Run commands** at repo root unless otherwise stated. `dotnet test` filters use `--filter "FullyQualifiedName~<pattern>"`.

---

# Slice 0 — Domain contracts, DTOs, and metric enums

This slice introduces the cross-cutting types every downstream slice will reference. No behavior yet; the goal is that `Domain` compiles with the new public surface and a couple of trivial tests pass.

### Task 0.1: Add voice audio + transcription DTOs

**Files:**
- Create: `Domain/DTOs/Voice/AudioChunk.cs`
- Create: `Domain/DTOs/Voice/TranscriptionResult.cs`
- Create: `Domain/DTOs/Voice/TranscriptionOptions.cs`
- Create: `Domain/DTOs/Voice/SynthesisOptions.cs`
- Test: `Tests/Unit/McpChannelVoice/Domain/VoiceDtoTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Domain/VoiceDtoTests.cs
namespace Tests.Unit.McpChannelVoice.Domain;

using global::Domain.DTOs.Voice;
using FluentAssertions;
using Xunit;

public class VoiceDtoTests
{
    [Fact]
    public void AudioChunk_carries_pcm_payload_and_sample_rate()
    {
        var chunk = new AudioChunk(new byte[] { 1, 2, 3, 4 }, SampleRate: 16000, Channels: 1, BitsPerSample: 16);
        chunk.Data.Should().HaveCount(4);
        chunk.SampleRate.Should().Be(16000);
    }

    [Fact]
    public void TranscriptionResult_defaults_language_to_null_when_unknown()
    {
        var r = new TranscriptionResult(Text: "hola", Language: null, Confidence: 0.9);
        r.Language.Should().BeNull();
        r.Confidence.Should().BeApproximately(0.9, 1e-6);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceDtoTests"`
Expected: FAIL with compile error — types `AudioChunk`, `TranscriptionResult` not found.

- [ ] **Step 3: Implement the DTOs**

```csharp
// Domain/DTOs/Voice/AudioChunk.cs
namespace Domain.DTOs.Voice;

public record AudioChunk(byte[] Data, int SampleRate, int Channels, int BitsPerSample);
```

```csharp
// Domain/DTOs/Voice/TranscriptionResult.cs
namespace Domain.DTOs.Voice;

public record TranscriptionResult(string Text, string? Language, double Confidence);
```

```csharp
// Domain/DTOs/Voice/TranscriptionOptions.cs
namespace Domain.DTOs.Voice;

public record TranscriptionOptions(string? Language = null, string? Model = null);
```

```csharp
// Domain/DTOs/Voice/SynthesisOptions.cs
namespace Domain.DTOs.Voice;

public record SynthesisOptions(string? Voice = null, double? Speed = null);
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceDtoTests"`
Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Voice Tests/Unit/McpChannelVoice/Domain/VoiceDtoTests.cs
git commit -m "feat(voice): add audio + transcription DTOs"
```

---

### Task 0.2: Add ISpeechToText and ITextToSpeech contracts

**Files:**
- Create: `Domain/Contracts/ISpeechToText.cs`
- Create: `Domain/Contracts/ITextToSpeech.cs`
- Test: `Tests/Unit/McpChannelVoice/Domain/VoiceContractsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Domain/VoiceContractsTests.cs
namespace Tests.Unit.McpChannelVoice.Domain;

using global::Domain.Contracts;
using global::Domain.DTOs.Voice;
using FluentAssertions;
using System.Reflection;
using Xunit;

public class VoiceContractsTests
{
    [Fact]
    public void ISpeechToText_has_TranscribeAsync_with_streaming_input_signature()
    {
        var method = typeof(ISpeechToText).GetMethod("TranscribeAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(Task<TranscriptionResult>));
        method.GetParameters().Select(p => p.ParameterType).Should().Equal(
            typeof(IAsyncEnumerable<AudioChunk>),
            typeof(TranscriptionOptions),
            typeof(CancellationToken));
    }

    [Fact]
    public void ITextToSpeech_has_SynthesizeAsync_returning_audio_stream()
    {
        var method = typeof(ITextToSpeech).GetMethod("SynthesizeAsync");
        method.Should().NotBeNull();
        method!.ReturnType.Should().Be(typeof(IAsyncEnumerable<AudioChunk>));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceContractsTests"`
Expected: FAIL — interfaces not found.

- [ ] **Step 3: Implement the interfaces**

```csharp
// Domain/Contracts/ISpeechToText.cs
namespace Domain.Contracts;

using Domain.DTOs.Voice;

public interface ISpeechToText
{
    Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct);
}
```

```csharp
// Domain/Contracts/ITextToSpeech.cs
namespace Domain.Contracts;

using Domain.DTOs.Voice;

public interface ITextToSpeech
{
    IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        CancellationToken ct);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceContractsTests"`
Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Contracts/ISpeechToText.cs Domain/Contracts/ITextToSpeech.cs Tests/Unit/McpChannelVoice/Domain/VoiceContractsTests.cs
git commit -m "feat(voice): add ISpeechToText and ITextToSpeech contracts"
```

---

### Task 0.3: Add voice metric and dimension enums

**Files:**
- Create: `Domain/DTOs/Metrics/Enums/VoiceDimension.cs`
- Create: `Domain/DTOs/Metrics/Enums/VoiceMetric.cs`
- Test: `Tests/Unit/McpChannelVoice/Domain/VoiceEnumsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Domain/VoiceEnumsTests.cs
namespace Tests.Unit.McpChannelVoice.Domain;

using global::Domain.DTOs.Metrics.Enums;
using FluentAssertions;
using Xunit;

public class VoiceEnumsTests
{
    [Fact]
    public void VoiceDimension_contains_required_values()
    {
        Enum.GetNames<VoiceDimension>().Should().Contain(new[]
        {
            nameof(VoiceDimension.SatelliteId),
            nameof(VoiceDimension.Room),
            nameof(VoiceDimension.Identity),
            nameof(VoiceDimension.WakeWord),
            nameof(VoiceDimension.Language),
            nameof(VoiceDimension.SttProvider),
            nameof(VoiceDimension.SttModel),
            nameof(VoiceDimension.TtsProvider),
            nameof(VoiceDimension.TtsVoice),
            nameof(VoiceDimension.Outcome),
            nameof(VoiceDimension.Source),
            nameof(VoiceDimension.Priority)
        });
    }

    [Fact]
    public void VoiceMetric_contains_required_values()
    {
        Enum.GetNames<VoiceMetric>().Should().Contain(new[]
        {
            nameof(VoiceMetric.WakeTriggered),
            nameof(VoiceMetric.UtteranceTranscribed),
            nameof(VoiceMetric.AudioSeconds),
            nameof(VoiceMetric.SttLatencyMs),
            nameof(VoiceMetric.TtsLatencyMs),
            nameof(VoiceMetric.WakeToFirstAudioMs),
            nameof(VoiceMetric.ApprovalResolved),
            nameof(VoiceMetric.SttError),
            nameof(VoiceMetric.TtsError),
            nameof(VoiceMetric.AnnouncePlayed),
            nameof(VoiceMetric.AnnounceQueued),
            nameof(VoiceMetric.AnnounceError),
            nameof(VoiceMetric.AnnouncePreemptedReply)
        });
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests"`
Expected: FAIL — enums not found.

- [ ] **Step 3: Implement the enums**

```csharp
// Domain/DTOs/Metrics/Enums/VoiceDimension.cs
namespace Domain.DTOs.Metrics.Enums;

public enum VoiceDimension
{
    SatelliteId,
    Room,
    Identity,
    WakeWord,
    Language,
    SttProvider,
    SttModel,
    TtsProvider,
    TtsVoice,
    Outcome,
    Source,
    Priority
}
```

```csharp
// Domain/DTOs/Metrics/Enums/VoiceMetric.cs
namespace Domain.DTOs.Metrics.Enums;

public enum VoiceMetric
{
    WakeTriggered,
    UtteranceTranscribed,
    AudioSeconds,
    SttLatencyMs,
    TtsLatencyMs,
    WakeToFirstAudioMs,
    ApprovalResolved,
    SttError,
    TtsError,
    AnnouncePlayed,
    AnnounceQueued,
    AnnounceError,
    AnnouncePreemptedReply
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests"`
Expected: 2 tests passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/VoiceDimension.cs Domain/DTOs/Metrics/Enums/VoiceMetric.cs Tests/Unit/McpChannelVoice/Domain/VoiceEnumsTests.cs
git commit -m "feat(voice): add VoiceDimension + VoiceMetric enums"
```

---

### Task 0.4: Add VoiceMetricEvent polymorphic type

**Files:**
- Modify: `Domain/DTOs/Metrics/MetricEvent.cs` — add `[JsonDerivedType]` for the new event
- Create: `Domain/DTOs/Metrics/VoiceMetricEvent.cs`
- Test: `Tests/Unit/McpChannelVoice/Domain/VoiceMetricEventTests.cs`

> **Before editing:** Read `Domain/DTOs/Metrics/MetricEvent.cs` to confirm the `[JsonDerivedType]` attribute pattern used by `TokenUsageEvent`, `ToolCallEvent`, etc. Mirror that exact pattern.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Domain/VoiceMetricEventTests.cs
namespace Tests.Unit.McpChannelVoice.Domain;

using System.Text.Json;
using global::Domain.DTOs.Metrics;
using global::Domain.DTOs.Metrics.Enums;
using FluentAssertions;
using Xunit;

public class VoiceMetricEventTests
{
    [Fact]
    public void VoiceMetricEvent_serializes_with_voice_discriminator()
    {
        MetricEvent ev = new VoiceMetricEvent
        {
            Timestamp = DateTimeOffset.UnixEpoch,
            AgentId = "jack",
            ConversationId = "kitchen-01",
            Metric = VoiceMetric.WakeTriggered,
            Value = 1,
            Dimensions = new Dictionary<string, string>
            {
                [nameof(VoiceDimension.SatelliteId)] = "kitchen-01",
                [nameof(VoiceDimension.Room)] = "Kitchen"
            }
        };
        var json = JsonSerializer.Serialize(ev);
        json.Should().Contain("\"type\":\"voice\"");
        json.Should().Contain("\"Metric\":\"WakeTriggered\"");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceMetricEventTests"`
Expected: FAIL — `VoiceMetricEvent` not found.

- [ ] **Step 3: Implement the event**

```csharp
// Domain/DTOs/Metrics/VoiceMetricEvent.cs
namespace Domain.DTOs.Metrics;

using System.Text.Json.Serialization;
using Domain.DTOs.Metrics.Enums;

public record VoiceMetricEvent : MetricEvent
{
    public required VoiceMetric Metric { get; init; }
    public required double Value { get; init; }
    public IReadOnlyDictionary<string, string> Dimensions { get; init; } = new Dictionary<string, string>();
}
```

Then add the discriminator to `MetricEvent`:

```csharp
// Domain/DTOs/Metrics/MetricEvent.cs — add to the existing [JsonDerivedType] list
[JsonDerivedType(typeof(VoiceMetricEvent), "voice")]
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceMetricEventTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Metrics/VoiceMetricEvent.cs Domain/DTOs/Metrics/MetricEvent.cs Tests/Unit/McpChannelVoice/Domain/VoiceMetricEventTests.cs
git commit -m "feat(voice): add VoiceMetricEvent"
```

---

# Slice 1 — McpChannelVoice skeleton

End state: agent boots, lists the channel, and a stub `channel/message` from a fake source flows through to a logging sink. No audio yet, no Wyoming, no STT/TTS.

### Task 1.1: Create the McpChannelVoice project and add to solution

**Files:**
- Create: `McpChannelVoice/McpChannelVoice.csproj`
- Create: `McpChannelVoice/Program.cs` (minimal placeholder)
- Modify: `agent.sln`

> **Before:** Read `McpChannelTelegram/McpChannelTelegram.csproj` to copy its `<TargetFramework>`, package references, and project references exactly.

- [ ] **Step 1: Write csproj**

```xml
<!-- McpChannelVoice/McpChannelVoice.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <UserSecretsId>mcp-channel-voice</UserSecretsId>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Domain\Domain.csproj" />
    <ProjectReference Include="..\Infrastructure\Infrastructure.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- Mirror MCP + hosting packages from McpChannelTelegram.csproj exactly -->
  </ItemGroup>
</Project>
```

Open `McpChannelTelegram/McpChannelTelegram.csproj` and copy the `<PackageReference>` entries (e.g., `ModelContextProtocol.AspNetCore`, `Microsoft.Extensions.Hosting`, `Microsoft.Extensions.Configuration.UserSecrets`) verbatim into the new csproj.

- [ ] **Step 2: Write minimal Program.cs**

```csharp
// McpChannelVoice/Program.cs
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "McpChannelVoice");
app.Run();
```

- [ ] **Step 3: Add to solution**

```bash
dotnet sln agent.sln add McpChannelVoice/McpChannelVoice.csproj
```

- [ ] **Step 4: Verify the project builds**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice agent.sln
git commit -m "chore(voice): scaffold McpChannelVoice project"
```

---

### Task 1.2: Add VoiceSettings record + appsettings.json

**Files:**
- Create: `McpChannelVoice/Settings/VoiceSettings.cs`
- Create: `McpChannelVoice/appsettings.json`
- Create: `McpChannelVoice/appsettings.Development.json`
- Test: `Tests/Unit/McpChannelVoice/Settings/VoiceSettingsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Settings/VoiceSettingsTests.cs
namespace Tests.Unit.McpChannelVoice.Settings;

using global::McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using FluentAssertions;
using Xunit;

public class VoiceSettingsTests
{
    [Fact]
    public void Binds_full_settings_tree_from_configuration()
    {
        var json = """
        {
          "Voice": {
            "WyomingServer": { "Host": "0.0.0.0", "Port": 10700 },
            "Stt": { "Provider": "Wyoming", "Wyoming": { "Host": "wyoming-whisper", "Port": 10300, "Model": "base" } },
            "Tts": { "Provider": "Wyoming", "Wyoming": { "Host": "wyoming-piper", "Port": 10200, "Voice": "es_ES-davefx-medium" } },
            "ConfidenceThreshold": 0.4,
            "Announce": { "Enabled": true, "Token": "tok", "BindToLoopbackOnly": false, "QueueMaxDepth": 8, "DefaultPriority": "Normal" },
            "Satellites": {
              "kitchen-01": { "Identity": "household", "Room": "Kitchen", "WakeWord": "hey_jarvis" }
            }
          }
        }
        """;
        var config = new ConfigurationBuilder().AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json))).Build();
        var settings = config.GetSection("Voice").Get<VoiceSettings>()!;

        settings.WyomingServer.Port.Should().Be(10700);
        settings.Stt.Provider.Should().Be("Wyoming");
        settings.Stt.Wyoming!.Model.Should().Be("base");
        settings.Tts.Wyoming!.Voice.Should().Be("es_ES-davefx-medium");
        settings.ConfidenceThreshold.Should().BeApproximately(0.4, 1e-6);
        settings.Announce.Enabled.Should().BeTrue();
        settings.Satellites["kitchen-01"].Identity.Should().Be("household");
        settings.Satellites["kitchen-01"].Room.Should().Be("Kitchen");
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSettingsTests"`
Expected: FAIL — `VoiceSettings` type missing.

- [ ] **Step 3: Implement VoiceSettings**

```csharp
// McpChannelVoice/Settings/VoiceSettings.cs
namespace McpChannelVoice.Settings;

public record VoiceSettings
{
    public WyomingServerSettings WyomingServer { get; init; } = new();
    public SttSettings Stt { get; init; } = new();
    public TtsSettings Tts { get; init; } = new();
    public double ConfidenceThreshold { get; init; } = 0.4;
    public AnnounceSettings Announce { get; init; } = new();
    public IReadOnlyDictionary<string, SatelliteSettings> Satellites { get; init; } = new Dictionary<string, SatelliteSettings>();
}

public record WyomingServerSettings
{
    public string Host { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 10700;
}

public record SttSettings
{
    public string Provider { get; init; } = "Wyoming";
    public WyomingBackendSettings? Wyoming { get; init; }
    public OpenAiBackendSettings? OpenAi { get; init; }
}

public record TtsSettings
{
    public string Provider { get; init; } = "Wyoming";
    public WyomingBackendSettings? Wyoming { get; init; }
    public OpenAiBackendSettings? OpenAi { get; init; }
}

public record WyomingBackendSettings
{
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string? Model { get; init; }
    public string? Voice { get; init; }
}

public record OpenAiBackendSettings
{
    public string Model { get; init; } = "";
    public string? Voice { get; init; }
}

public record AnnounceSettings
{
    public bool Enabled { get; init; } = true;
    public string Token { get; init; } = "";
    public bool BindToLoopbackOnly { get; init; }
    public int QueueMaxDepth { get; init; } = 8;
    public string DefaultPriority { get; init; } = "Normal";
}

public record SatelliteSettings
{
    public required string Identity { get; init; }
    public required string Room { get; init; }
    public string WakeWord { get; init; } = "hey_jarvis";
    public SttSettings? Stt { get; init; }
    public TtsSettings? Tts { get; init; }
}
```

- [ ] **Step 4: Write appsettings.json**

```json
{
  "Logging": { "LogLevel": { "Default": "Information" } },
  "Voice": {
    "WyomingServer": { "Host": "0.0.0.0", "Port": 10700 },
    "Stt": {
      "Provider": "Wyoming",
      "Wyoming": { "Host": "wyoming-whisper", "Port": 10300, "Model": "base" },
      "OpenAi": { "Model": "whisper-1" }
    },
    "Tts": {
      "Provider": "Wyoming",
      "Wyoming": { "Host": "wyoming-piper", "Port": 10200, "Voice": "es_ES-davefx-medium" },
      "OpenAi": { "Model": "tts-1", "Voice": "alloy" }
    },
    "ConfidenceThreshold": 0.4,
    "Announce": {
      "Enabled": true,
      "Token": "",
      "BindToLoopbackOnly": false,
      "QueueMaxDepth": 8,
      "DefaultPriority": "Normal"
    },
    "Satellites": {
      "kitchen-01":     { "Identity": "household", "Room": "Kitchen",     "WakeWord": "hey_jarvis" },
      "living-room-01": { "Identity": "household", "Room": "Living Room", "WakeWord": "hey_jarvis" },
      "bedroom-01":     { "Identity": "francisco", "Room": "Bedroom",     "WakeWord": "hey_jarvis" }
    }
  }
}
```

- [ ] **Step 5: Write appsettings.Development.json**

```json
{
  "Logging": { "LogLevel": { "Default": "Debug" } },
  "Voice": {
    "Stt": { "Wyoming": { "Host": "localhost" } },
    "Tts": { "Wyoming": { "Host": "localhost" } },
    "Announce": { "Token": "dev-token" }
  }
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSettingsTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add McpChannelVoice/Settings McpChannelVoice/appsettings*.json Tests/Unit/McpChannelVoice/Settings
git commit -m "feat(voice): add VoiceSettings and appsettings skeletons"
```

---

### Task 1.3: SatelliteRegistry — forward lookups

**Files:**
- Create: `McpChannelVoice/Services/SatelliteRegistry.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/SatelliteRegistryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/SatelliteRegistryTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using global::McpChannelVoice.Services;
using global::McpChannelVoice.Settings;
using FluentAssertions;
using Xunit;

public class SatelliteRegistryTests
{
    private static SatelliteRegistry Build() => new(new VoiceSettings
    {
        Satellites = new Dictionary<string, SatelliteSettings>
        {
            ["kitchen-01"]  = new() { Identity = "household", Room = "Kitchen",     WakeWord = "hey_jarvis" },
            ["bedroom-01"]  = new() { Identity = "francisco", Room = "Bedroom",     WakeWord = "hey_jarvis" },
            ["living-01"]   = new() { Identity = "household", Room = "Living Room", WakeWord = "hey_jarvis" },
            ["living-02"]   = new() { Identity = "household", Room = "Living Room", WakeWord = "hey_jarvis" }
        }
    });

    [Fact]
    public void Lookup_returns_settings_for_known_id()
    {
        Build().Lookup("kitchen-01")!.Identity.Should().Be("household");
    }

    [Fact]
    public void Lookup_returns_null_for_unknown_id()
    {
        Build().Lookup("ghost").Should().BeNull();
    }

    [Fact]
    public void IsKnown_returns_true_only_for_configured_ids()
    {
        var r = Build();
        r.IsKnown("kitchen-01").Should().BeTrue();
        r.IsKnown("ghost").Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteRegistryTests"`
Expected: FAIL — `SatelliteRegistry` not found.

- [ ] **Step 3: Implement SatelliteRegistry (forward lookups only — reverse lookups in Slice 4)**

```csharp
// McpChannelVoice/Services/SatelliteRegistry.cs
namespace McpChannelVoice.Services;

using McpChannelVoice.Settings;

public class SatelliteRegistry(VoiceSettings settings)
{
    private readonly IReadOnlyDictionary<string, SatelliteSettings> satellites = settings.Satellites;

    public SatelliteSettings? Lookup(string satelliteId) =>
        satellites.TryGetValue(satelliteId, out var s) ? s : null;

    public bool IsKnown(string satelliteId) => satellites.ContainsKey(satelliteId);
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteRegistryTests"`
Expected: 3 tests passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteRegistry.cs Tests/Unit/McpChannelVoice/Services/SatelliteRegistryTests.cs
git commit -m "feat(voice): satellite registry with forward lookups"
```

---

### Task 1.4: ChannelNotificationEmitter (mirror McpChannelTelegram)

**Files:**
- Create: `McpChannelVoice/Services/ChannelNotificationEmitter.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/ChannelNotificationEmitterTests.cs`

> **Before:** Read `McpChannelTelegram/Services/ChannelNotificationEmitter.cs` (or whichever file owns session registration + notification emission for Telegram). Mirror it: keep the same public methods and broadcast shape, but adapt the payload to include voice metadata (room, satelliteId).

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/ChannelNotificationEmitterTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using global::McpChannelVoice.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class ChannelNotificationEmitterTests
{
    [Fact]
    public async Task Emits_no_op_when_no_sessions_registered()
    {
        var emitter = new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance);
        var sent = await emitter.EmitMessageNotificationAsync(
            conversationId: "kitchen-01",
            sender: "household",
            content: "what's the weather",
            agentId: "jack",
            room: "Kitchen");
        sent.Should().Be(0);
    }

    [Fact]
    public void Register_then_unregister_session_changes_count()
    {
        var emitter = new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance);
        emitter.RegisterSession("s1", server: null!);
        emitter.SessionCount.Should().Be(1);
        emitter.UnregisterSession("s1");
        emitter.SessionCount.Should().Be(0);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelNotificationEmitterTests"`
Expected: FAIL — class not found.

- [ ] **Step 3: Implement the emitter**

```csharp
// McpChannelVoice/Services/ChannelNotificationEmitter.cs
namespace McpChannelVoice.Services;

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

public class ChannelNotificationEmitter(ILogger<ChannelNotificationEmitter> logger)
{
    private readonly ConcurrentDictionary<string, IMcpServer> sessions = new();

    public int SessionCount => sessions.Count;

    public void RegisterSession(string sessionId, IMcpServer server) => sessions[sessionId] = server;

    public void UnregisterSession(string sessionId) => sessions.TryRemove(sessionId, out _);

    public async Task<int> EmitMessageNotificationAsync(
        string conversationId,
        string sender,
        string content,
        string agentId,
        string? room = null,
        CancellationToken ct = default)
    {
        if (sessions.IsEmpty)
        {
            logger.LogDebug("No MCP sessions registered; dropping notification for {ConversationId}", conversationId);
            return 0;
        }

        var payload = new
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Metadata = new { Room = room },
            Timestamp = DateTimeOffset.UtcNow
        };

        var sent = 0;
        foreach (var (id, server) in sessions)
        {
            try
            {
                await server.SendNotificationAsync("notifications/channel/message", payload, cancellationToken: ct);
                sent++;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to deliver notification to session {SessionId}", id);
            }
        }
        return sent;
    }
}
```

> Note: the actual `IMcpServer` type comes from `ModelContextProtocol.Server`. If the exact method name differs (e.g., `SendNotificationAsync` vs `SendNotificationAsync<T>`), copy the working call from `McpChannelTelegram/Services/ChannelNotificationEmitter.cs`.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelNotificationEmitterTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/ChannelNotificationEmitter.cs Tests/Unit/McpChannelVoice/Services/ChannelNotificationEmitterTests.cs
git commit -m "feat(voice): channel notification emitter"
```

---

### Task 1.5: Dummy SendReplyTool + RequestApprovalTool

**Files:**
- Create: `McpChannelVoice/McpTools/SendReplyTool.cs`
- Create: `McpChannelVoice/McpTools/RequestApprovalTool.cs`

> **Before:** Read `McpChannelTelegram/McpTools/SendReplyTool.cs` to copy the `[McpServerToolType]` + `[McpServerTool]` attribute layout and the parameter list. We will keep the signature identical so the agent talks to it transparently; the **body** logs only for now.

- [ ] **Step 1: Write SendReplyTool**

```csharp
// McpChannelVoice/McpTools/SendReplyTool.cs
namespace McpChannelVoice.McpTools;

using System.ComponentModel;
using Domain.DTOs.Channel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class SendReplyTool
{
    [McpServerTool(Name = "send_reply")]
    [Description("Send a reply to the originating voice satellite.")]
    public static Task<object?> McpRun(
        string conversationId,
        string content,
        ReplyContentType contentType,
        bool isComplete,
        string? messageId,
        ILogger<SendReplyToolMarker> logger)
    {
        logger.LogInformation("[stub] send_reply conversation={ConversationId} type={Type} complete={Complete}",
            conversationId, contentType, isComplete);
        return Task.FromResult<object?>(new { ok = true });
    }
}

public sealed class SendReplyToolMarker;
```

- [ ] **Step 2: Write RequestApprovalTool**

```csharp
// McpChannelVoice/McpTools/RequestApprovalTool.cs
namespace McpChannelVoice.McpTools;

using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class RequestApprovalTool
{
    [McpServerTool(Name = "request_approval")]
    [Description("Ask the user (via voice) to approve a tool call.")]
    public static Task<object?> McpRun(
        string conversationId,
        string mode,
        string requests,
        ILogger<RequestApprovalToolMarker> logger)
    {
        logger.LogInformation("[stub] request_approval conversation={ConversationId} mode={Mode}",
            conversationId, mode);
        return Task.FromResult<object?>(new { approved = false, reason = "stub" });
    }
}

public sealed class RequestApprovalToolMarker;
```

- [ ] **Step 3: Build to confirm the project still compiles**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add McpChannelVoice/McpTools
git commit -m "feat(voice): stub send_reply and request_approval tools"
```

---

### Task 1.6: ConfigModule + Program.cs (DI wiring, MCP HTTP transport)

**Files:**
- Create: `McpChannelVoice/Modules/ConfigModule.cs`
- Rewrite: `McpChannelVoice/Program.cs`

> **Before:** Open `McpChannelTelegram/Modules/ConfigModule.cs`. Copy the `GetSettings()` chain (env vars → user secrets → bind) and the `AddMcpServer()` + `WithHttpTransport()` + `WithTools<>()` registration shape. Adapt to voice.

- [ ] **Step 1: Implement ConfigModule**

```csharp
// McpChannelVoice/Modules/ConfigModule.cs
namespace McpChannelVoice.Modules;

using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

public static class ConfigModule
{
    public static VoiceSettings GetSettings(this WebApplicationBuilder builder)
    {
        builder.Configuration
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>(optional: true);
        return builder.Configuration.GetSection("Voice").Get<VoiceSettings>()
               ?? throw new InvalidOperationException("Voice settings missing");
    }

    public static IServiceCollection ConfigureChannel(this IServiceCollection services, VoiceSettings settings)
    {
        services.AddSingleton(settings);
        services.AddSingleton<SatelliteRegistry>();
        services.AddSingleton<ChannelNotificationEmitter>();

        services.AddMcpServer()
            .WithHttpTransport(opt =>
            {
                opt.RunSessionHandler = async (ctx, server, ct) =>
                {
                    var emitter = ctx.Services!.GetRequiredService<ChannelNotificationEmitter>();
                    emitter.RegisterSession(ctx.SessionId, server);
                    try
                    {
                        await server.RunAsync(ct);
                    }
                    finally
                    {
                        emitter.UnregisterSession(ctx.SessionId);
                    }
                };
            })
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>();

        return services;
    }
}
```

> If the `RunSessionHandler` shape differs in the installed `ModelContextProtocol.AspNetCore` version, copy the exact pattern from `McpChannelTelegram`.

- [ ] **Step 2: Rewrite Program.cs**

```csharp
// McpChannelVoice/Program.cs
using McpChannelVoice.Modules;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.GetSettings();
builder.Services.ConfigureChannel(settings);

var app = builder.Build();
app.MapMcp("/mcp");
app.Run();
```

- [ ] **Step 3: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add McpChannelVoice/Modules McpChannelVoice/Program.cs
git commit -m "feat(voice): DI wiring + MCP HTTP transport"
```

---

### Task 1.7: Dockerfile

**Files:**
- Create: `McpChannelVoice/Dockerfile`

> **Before:** Read `McpChannelTelegram/Dockerfile`. Copy it verbatim; only swap project name occurrences.

- [ ] **Step 1: Write Dockerfile**

```dockerfile
# McpChannelVoice/Dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
EXPOSE 10700

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["McpChannelVoice/McpChannelVoice.csproj", "McpChannelVoice/"]
COPY ["Domain/Domain.csproj", "Domain/"]
COPY ["Infrastructure/Infrastructure.csproj", "Infrastructure/"]
RUN dotnet restore "McpChannelVoice/McpChannelVoice.csproj"
COPY . .
WORKDIR "/src/McpChannelVoice"
RUN dotnet publish "McpChannelVoice.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "McpChannelVoice.dll"]
```

- [ ] **Step 2: Build the image locally to confirm**

Run: `docker build -f McpChannelVoice/Dockerfile -t mcp-channel-voice:dev .`
Expected: image builds.

- [ ] **Step 3: Commit**

```bash
git add McpChannelVoice/Dockerfile
git commit -m "chore(voice): Dockerfile"
```

---

### Task 1.8: Wire mcp-channel-voice into docker-compose

**Files:**
- Modify: `DockerCompose/docker-compose.yml`
- Modify: `DockerCompose/.env`

> **Before:** Read the `mcp-channel-telegram` block in `docker-compose.yml` and the `agent` service's `ChannelEndpoints` env. Add a `mcp-channel-voice` block analogously.

- [ ] **Step 1: Add the service**

Append to `DockerCompose/docker-compose.yml`:

```yaml
mcp-channel-voice:
  image: mcp-channel-voice:latest
  container_name: mcp-channel-voice
  build:
    context: ${REPOSITORY_PATH}
    dockerfile: McpChannelVoice/Dockerfile
  restart: unless-stopped
  ports:
    - "5010:8080"
    - "10700:10700"
  environment:
    - ASPNETCORE_URLS=http://+:8080
    - Voice__Announce__Token=${ANNOUNCE_TOKEN}
    - OPENAI_API_KEY=${OPENAI_API_KEY}
  env_file:
    - .env
  networks:
    - jackbot
  depends_on:
    base-sdk:
      condition: service_started
    redis:
      condition: service_healthy
```

- [ ] **Step 2: Add the channel endpoint to the agent service**

Locate the `agent` service's `Agent__ChannelEndpoints__*` env (or `appsettings.json`-driven config) and add an entry. If endpoints come from `Agent/appsettings.json`, edit that file instead:

```json
{
  "channelEndpoints": [
    { "channelId": "voice", "endpoint": "http://mcp-channel-voice:8080/mcp" }
  ]
}
```

(Merge with existing entries; don't replace them.)

- [ ] **Step 3: Add ANNOUNCE_TOKEN to .env**

Append to `DockerCompose/.env`:

```
ANNOUNCE_TOKEN=changeme
OPENAI_API_KEY=
```

- [ ] **Step 4: Bring up the stack and confirm the agent sees the channel**

Run (Linux/WSL):

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent mcp-channel-voice
docker compose -p jackbot logs --tail=80 agent | grep -i voice
```

Expected: agent log line mentioning the `voice` channel endpoint reachable.

- [ ] **Step 5: Commit**

```bash
git add DockerCompose/docker-compose.yml DockerCompose/.env Agent/appsettings.json
git commit -m "chore(voice): docker compose + channel endpoint"
```

---

### Task 1.9: Heartbeat metric (voice.connected)

**Files:**
- Create: `McpChannelVoice/Services/VoiceMetricsPublisher.cs`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (register publisher + emit heartbeat)
- Test: `Tests/Unit/McpChannelVoice/Services/VoiceMetricsPublisherTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/VoiceMetricsPublisherTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using global::Domain.Contracts;
using global::Domain.DTOs.Metrics;
using global::Domain.DTOs.Metrics.Enums;
using global::McpChannelVoice.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

public class VoiceMetricsPublisherTests
{
    [Fact]
    public async Task PublishWake_emits_event_with_dimensions()
    {
        var underlying = Substitute.For<IMetricsPublisher>();
        var pub = new VoiceMetricsPublisher(underlying);
        await pub.PublishAsync(VoiceMetric.WakeTriggered, value: 1, dims: new Dictionary<VoiceDimension, string>
        {
            [VoiceDimension.SatelliteId] = "kitchen-01",
            [VoiceDimension.Room] = "Kitchen"
        });

        await underlying.Received(1).PublishAsync(
            Arg.Is<VoiceMetricEvent>(e =>
                e.Metric == VoiceMetric.WakeTriggered &&
                e.Dimensions["SatelliteId"] == "kitchen-01"),
            Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceMetricsPublisherTests"`
Expected: FAIL — class missing.

- [ ] **Step 3: Implement VoiceMetricsPublisher**

```csharp
// McpChannelVoice/Services/VoiceMetricsPublisher.cs
namespace McpChannelVoice.Services;

using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

public class VoiceMetricsPublisher(IMetricsPublisher underlying)
{
    public Task PublishAsync(
        VoiceMetric metric,
        double value,
        IReadOnlyDictionary<VoiceDimension, string>? dims = null,
        string? agentId = null,
        string? conversationId = null,
        CancellationToken ct = default)
    {
        var ev = new VoiceMetricEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            AgentId = agentId,
            ConversationId = conversationId,
            Metric = metric,
            Value = value,
            Dimensions = (dims ?? new Dictionary<VoiceDimension, string>())
                .ToDictionary(kv => kv.Key.ToString(), kv => kv.Value)
        };
        return underlying.PublishAsync(ev, ct);
    }
}
```

- [ ] **Step 4: Wire it in ConfigModule and emit a heartbeat at startup**

Edit `McpChannelVoice/Modules/ConfigModule.cs` — extend `ConfigureChannel`:

```csharp
services.AddSingleton<VoiceMetricsPublisher>();
services.AddHostedService<VoiceHeartbeatService>();
```

Create `McpChannelVoice/Services/VoiceHeartbeatService.cs`:

```csharp
// McpChannelVoice/Services/VoiceHeartbeatService.cs
namespace McpChannelVoice.Services;

using Domain.Contracts;
using Domain.DTOs.Metrics;
using Microsoft.Extensions.Hosting;

public class VoiceHeartbeatService(IMetricsPublisher publisher, TimeProvider time) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await publisher.PublishAsync(new HeartbeatEvent
            {
                Timestamp = time.GetUtcNow(),
                Source = "voice.connected"
            }, stoppingToken);
            await Task.Delay(TimeSpan.FromSeconds(30), time, stoppingToken);
        }
    }
}
```

> If `HeartbeatEvent` requires different properties, mirror the shape used by `McpChannelTelegram`'s heartbeat (read it first).

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceMetricsPublisherTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/Services/VoiceMetricsPublisher.cs McpChannelVoice/Services/VoiceHeartbeatService.cs McpChannelVoice/Modules/ConfigModule.cs Tests/Unit/McpChannelVoice/Services/VoiceMetricsPublisherTests.cs
git commit -m "feat(voice): metrics publisher + heartbeat"
```

---

### Task 1.10: Smoke test — agent lists the voice channel

**Files:**
- (no new files; verification only)

- [ ] **Step 1: Bring the stack up**

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent mcp-channel-voice redis
```

- [ ] **Step 2: Verify the channel is reachable**

```bash
curl -s http://localhost:5010/mcp -X POST -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","method":"tools/list","id":1}'
```

Expected: JSON listing `send_reply` and `request_approval` tools.

- [ ] **Step 3: Verify the agent reports the channel connected**

```bash
docker compose -p jackbot logs --tail=200 agent | grep -Ei "channel.*voice"
```

Expected: log line indicating the voice channel is connected.

- [ ] **Step 4: Commit (no-op marker, optional)**

If anything needed fixing (e.g., missing config), commit those fixes here.

---

# Slice 2 — Wyoming server + STT path

End state: a real `wyoming-satellite` (or desktop running it) wakes, speaks, and the agent receives the transcript as a `channel/message` with `sender` = the configured identity. No TTS reply yet.

> **Wyoming protocol primer** (read once, used throughout Slice 2):
> - Newline-delimited JSON over TCP. Each event is one line of JSON, optionally followed by binary `data` (length `data_length`) and/or binary `payload` (length `payload_length`).
> - Event JSON shape: `{"type": "<event-type>", "data": { ... }, "data_length": 0, "payload_length": 0, "version": "1.5"}` (omit length fields when 0).
> - Key event types we care about: `info` (handshake), `describe`, `transcribe`, `audio-start`, `audio-chunk` (payload = raw PCM), `audio-stop`, `transcript`, `synthesize`, `voice-started`, `voice-stopped`, `run-pipeline`. See https://github.com/rhasspy/wyoming for full reference.
> - Audio chunks ship with `{"type":"audio-chunk","data":{"rate":16000,"width":2,"channels":1},"payload_length":N}\n<N raw PCM bytes>`.

### Task 2.1: WyomingProtocol — frame read/write

**Files:**
- Create: `McpChannelVoice/Services/WyomingProtocol.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/WyomingProtocolTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/WyomingProtocolTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using System.Text;
using System.Text.Json;
using global::McpChannelVoice.Services;
using FluentAssertions;
using Xunit;

public class WyomingProtocolTests
{
    [Fact]
    public async Task Writes_event_with_no_payload_as_single_json_line()
    {
        using var ms = new MemoryStream();
        await WyomingProtocol.WriteEventAsync(ms, "audio-stop", data: null, payload: null, CancellationToken.None);
        var line = Encoding.UTF8.GetString(ms.ToArray()).TrimEnd('\n');
        var doc = JsonDocument.Parse(line);
        doc.RootElement.GetProperty("type").GetString().Should().Be("audio-stop");
    }

    [Fact]
    public async Task Round_trip_event_with_binary_payload()
    {
        using var ms = new MemoryStream();
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        await WyomingProtocol.WriteEventAsync(ms, "audio-chunk",
            data: new { rate = 16000, width = 2, channels = 1 }, payload: payload, CancellationToken.None);

        ms.Position = 0;
        var ev = await WyomingProtocol.ReadEventAsync(ms, CancellationToken.None);
        ev!.Type.Should().Be("audio-chunk");
        ev.Payload.Should().BeEquivalentTo(payload);
        ev.Data.RootElement.GetProperty("rate").GetInt32().Should().Be(16000);
    }

    [Fact]
    public async Task ReadEvent_returns_null_on_end_of_stream()
    {
        using var ms = new MemoryStream();
        var ev = await WyomingProtocol.ReadEventAsync(ms, CancellationToken.None);
        ev.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingProtocolTests"`
Expected: FAIL — class not found.

- [ ] **Step 3: Implement WyomingProtocol**

```csharp
// McpChannelVoice/Services/WyomingProtocol.cs
namespace McpChannelVoice.Services;

using System.Buffers;
using System.Text;
using System.Text.Json;

public record WyomingEvent(string Type, JsonDocument Data, byte[]? ExtraData, byte[]? Payload);

public static class WyomingProtocol
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = null };

    public static async Task WriteEventAsync(
        Stream stream,
        string type,
        object? data,
        byte[]? payload,
        CancellationToken ct,
        byte[]? extraData = null)
    {
        var header = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["data"] = data ?? new { },
            ["data_length"] = extraData?.Length ?? 0,
            ["payload_length"] = payload?.Length ?? 0,
            ["version"] = "1.5"
        };
        var json = JsonSerializer.Serialize(header, JsonOpts);
        var line = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(line, ct);
        if (extraData is { Length: > 0 }) await stream.WriteAsync(extraData, ct);
        if (payload is { Length: > 0 }) await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<WyomingEvent?> ReadEventAsync(Stream stream, CancellationToken ct)
    {
        var line = await ReadLineAsync(stream, ct);
        if (line is null) return null;

        var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString() ?? throw new InvalidOperationException("Wyoming event missing type");
        var dataLen = root.TryGetProperty("data_length", out var dl) ? dl.GetInt32() : 0;
        var payloadLen = root.TryGetProperty("payload_length", out var pl) ? pl.GetInt32() : 0;

        var extra = dataLen > 0 ? await ReadExactAsync(stream, dataLen, ct) : null;
        var payload = payloadLen > 0 ? await ReadExactAsync(stream, payloadLen, ct) : null;

        var dataDoc = root.TryGetProperty("data", out var d)
            ? JsonDocument.Parse(d.GetRawText())
            : JsonDocument.Parse("{}");
        return new WyomingEvent(type, dataDoc, extra, payload);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one.AsMemory(0, 1), ct);
            if (read == 0) return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buffer.ToArray());
            buffer.WriteByte(one[0]);
        }
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var off = 0;
        while (off < count)
        {
            var n = await stream.ReadAsync(buf.AsMemory(off, count - off), ct);
            if (n == 0) throw new EndOfStreamException("Wyoming stream ended mid-payload");
            off += n;
        }
        return buf;
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingProtocolTests"`
Expected: 3 tests passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol.cs Tests/Unit/McpChannelVoice/Services/WyomingProtocolTests.cs
git commit -m "feat(voice): Wyoming protocol framer"
```

---

### Task 2.2: WyomingClient — outbound TCP client

**Files:**
- Create: `McpChannelVoice/Services/WyomingClient.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/WyomingClientTests.cs`

This is the thin client used by `WyomingSpeechToText` and `WyomingTextToSpeech` to talk to the whisper/piper containers. Wraps a `TcpClient` and exposes `SendEventAsync` / `ReadEventAsync`.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/WyomingClientTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using System.Net;
using System.Net.Sockets;
using global::McpChannelVoice.Services;
using FluentAssertions;
using Xunit;

public class WyomingClientTests
{
    [Fact]
    public async Task Round_trips_an_event_to_a_loopback_echo_server()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var s = server.GetStream();
            var ev = await WyomingProtocol.ReadEventAsync(s, CancellationToken.None);
            await WyomingProtocol.WriteEventAsync(s, "echo:" + ev!.Type, data: null, payload: ev.Payload, CancellationToken.None);
        });

        await using var client = await WyomingClient.ConnectAsync("127.0.0.1", port, CancellationToken.None);
        await client.SendEventAsync("ping", data: new { x = 1 }, payload: null, CancellationToken.None);
        var resp = await client.ReadEventAsync(CancellationToken.None);
        resp!.Type.Should().Be("echo:ping");
        listener.Stop();
        await serverTask;
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingClientTests"`
Expected: FAIL.

- [ ] **Step 3: Implement WyomingClient**

```csharp
// McpChannelVoice/Services/WyomingClient.cs
namespace McpChannelVoice.Services;

using System.Net.Sockets;

public sealed class WyomingClient : IAsyncDisposable
{
    private readonly TcpClient tcp;
    private readonly NetworkStream stream;

    private WyomingClient(TcpClient tcp)
    {
        this.tcp = tcp;
        stream = tcp.GetStream();
    }

    public static async Task<WyomingClient> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        return new WyomingClient(tcp);
    }

    public Task SendEventAsync(string type, object? data, byte[]? payload, CancellationToken ct) =>
        WyomingProtocol.WriteEventAsync(stream, type, data, payload, ct);

    public Task<WyomingEvent?> ReadEventAsync(CancellationToken ct) =>
        WyomingProtocol.ReadEventAsync(stream, ct);

    public async ValueTask DisposeAsync()
    {
        await stream.DisposeAsync();
        tcp.Dispose();
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingClientTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingClient.cs Tests/Unit/McpChannelVoice/Services/WyomingClientTests.cs
git commit -m "feat(voice): Wyoming TCP client"
```

---

### Task 2.3: WyomingSpeechToText adapter

**Files:**
- Create: `Infrastructure/Clients/Voice/WyomingSpeechToText.cs`
- Test: `Tests/Unit/McpChannelVoice/Infrastructure/WyomingSpeechToTextTests.cs`

> The adapter opens a fresh Wyoming connection per call, sends `transcribe` (optionally with `language`/`name`), streams `audio-start` + N×`audio-chunk` + `audio-stop`, then reads `transcript` and returns the result. Confidence is not always provided by Whisper; default to 1.0 when absent (the channel-level `ConfidenceGate` is the place to filter on length/empty text).

- [ ] **Step 1: Write the failing test (using a loopback fake server)**

```csharp
// Tests/Unit/McpChannelVoice/Infrastructure/WyomingSpeechToTextTests.cs
namespace Tests.Unit.McpChannelVoice.Infrastructure;

using System.Net;
using System.Net.Sockets;
using global::Domain.DTOs.Voice;
using global::Infrastructure.Clients.Voice;
using global::McpChannelVoice.Services;
using FluentAssertions;
using Xunit;

public class WyomingSpeechToTextTests
{
    [Fact]
    public async Task Streams_audio_then_returns_transcript_text()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var fakeWhisper = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var s = server.GetStream();
            // Drain client events until audio-stop, then send transcript
            while (true)
            {
                var ev = await WyomingProtocol.ReadEventAsync(s, CancellationToken.None);
                if (ev is null) break;
                if (ev.Type == "audio-stop")
                {
                    await WyomingProtocol.WriteEventAsync(s, "transcript",
                        data: new { text = "hola mundo", language = "es" }, payload: null, CancellationToken.None);
                    break;
                }
            }
        });

        var stt = new WyomingSpeechToText(host: "127.0.0.1", port: port, model: "base");
        async IAsyncEnumerable<AudioChunk> Frames()
        {
            yield return new AudioChunk(new byte[640], 16000, 1, 16);
            yield return new AudioChunk(new byte[640], 16000, 1, 16);
            await Task.CompletedTask;
        }
        var result = await stt.TranscribeAsync(Frames(), new TranscriptionOptions(Language: "es"), CancellationToken.None);
        result.Text.Should().Be("hola mundo");
        result.Language.Should().Be("es");
        listener.Stop();
        await fakeWhisper;
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingSpeechToTextTests"`
Expected: FAIL.

- [ ] **Step 3: Implement WyomingSpeechToText**

```csharp
// Infrastructure/Clients/Voice/WyomingSpeechToText.cs
namespace Infrastructure.Clients.Voice;

using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;

public class WyomingSpeechToText(string host, int port, string? model = null) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        await using var client = await WyomingClient.ConnectAsync(host, port, ct);
        await client.SendEventAsync("transcribe", new
        {
            name = options.Model ?? model,
            language = options.Language
        }, payload: null, ct);

        await client.SendEventAsync("audio-start", new { rate = 16000, width = 2, channels = 1 }, payload: null, ct);
        await foreach (var chunk in audio.WithCancellation(ct))
        {
            await client.SendEventAsync("audio-chunk",
                new { rate = chunk.SampleRate, width = chunk.BitsPerSample / 8, channels = chunk.Channels },
                payload: chunk.Data,
                ct);
        }
        await client.SendEventAsync("audio-stop", data: null, payload: null, ct);

        while (true)
        {
            var ev = await client.ReadEventAsync(ct);
            if (ev is null) throw new InvalidOperationException("Wyoming STT closed before transcript");
            if (ev.Type != "transcript") continue;
            var text = ev.Data.RootElement.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            var language = ev.Data.RootElement.TryGetProperty("language", out var l) ? l.GetString() : null;
            return new TranscriptionResult(text, language, Confidence: 1.0);
        }
    }
}
```

> **Project reference:** The adapter lives in `Infrastructure` but uses `WyomingClient` from `McpChannelVoice`. Avoid the cycle by either (a) moving `WyomingClient` + `WyomingProtocol` to `Infrastructure/Clients/Voice/Wyoming/` (preferred) — do this now if the build fails. If you do, update the namespaces and the prior tests' usings, and re-run.

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingSpeechToTextTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Voice/WyomingSpeechToText.cs Tests/Unit/McpChannelVoice/Infrastructure/WyomingSpeechToTextTests.cs
git commit -m "feat(voice): WyomingSpeechToText adapter"
```

---

### Task 2.4: ConfidenceGate

**Files:**
- Create: `McpChannelVoice/Services/ConfidenceGate.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/ConfidenceGateTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/ConfidenceGateTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using global::Domain.DTOs.Voice;
using global::McpChannelVoice.Services;
using FluentAssertions;
using Xunit;

public class ConfidenceGateTests
{
    private static readonly ConfidenceGate Gate = new(threshold: 0.4);

    [Fact]
    public void Drops_empty_text() => Gate.ShouldDispatch(new TranscriptionResult("", "es", 0.95)).Should().BeFalse();

    [Fact]
    public void Drops_whitespace_only() => Gate.ShouldDispatch(new TranscriptionResult("   ", "es", 0.95)).Should().BeFalse();

    [Fact]
    public void Drops_below_threshold() => Gate.ShouldDispatch(new TranscriptionResult("hola", "es", 0.2)).Should().BeFalse();

    [Fact]
    public void Passes_above_threshold() => Gate.ShouldDispatch(new TranscriptionResult("hola", "es", 0.9)).Should().BeTrue();
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConfidenceGateTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// McpChannelVoice/Services/ConfidenceGate.cs
namespace McpChannelVoice.Services;

using Domain.DTOs.Voice;

public class ConfidenceGate(double threshold)
{
    public bool ShouldDispatch(TranscriptionResult result) =>
        !string.IsNullOrWhiteSpace(result.Text) && result.Confidence >= threshold;
}
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConfidenceGateTests"`
Expected: 4 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/ConfidenceGate.cs Tests/Unit/McpChannelVoice/Services/ConfidenceGateTests.cs
git commit -m "feat(voice): confidence gate"
```

---

### Task 2.5: SatelliteSession — wake → capture → STT → dispatch

**Files:**
- Create: `McpChannelVoice/Services/SatelliteSession.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/SatelliteSessionTests.cs`

The `SatelliteSession` owns one Wyoming TCP connection from one satellite. It runs a loop:
1. Read events until `audio-start` (means wake fired + capture started).
2. Stream subsequent `audio-chunk` payloads into an `IAsyncEnumerable<AudioChunk>` consumed by `ISpeechToText`.
3. On `audio-stop`, await transcription, apply `ConfidenceGate`, dispatch a `channel/message` notification via the emitter, publish metrics.
4. Loop (the connection stays open across utterances).

- [ ] **Step 1: Write the failing test (uses a fake `ISpeechToText`)**

```csharp
// Tests/Unit/McpChannelVoice/Services/SatelliteSessionTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using System.Net;
using System.Net.Sockets;
using global::Domain.Contracts;
using global::Domain.DTOs.Voice;
using global::McpChannelVoice.Services;
using global::McpChannelVoice.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class SatelliteSessionTests
{
    [Fact]
    public async Task End_to_end_one_utterance_dispatches_notification_with_identity()
    {
        var registry = new SatelliteRegistry(new VoiceSettings
        {
            Satellites = new Dictionary<string, SatelliteSettings>
            {
                ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" }
            }
        });

        var stt = Substitute.For<ISpeechToText>();
        stt.TranscribeAsync(Arg.Any<IAsyncEnumerable<AudioChunk>>(), Arg.Any<TranscriptionOptions>(), Arg.Any<CancellationToken>())
            .Returns(new TranscriptionResult("turn the kitchen light on", "en", 0.9));

        var emitter = new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance);
        var metrics = new VoiceMetricsPublisher(Substitute.For<IMetricsPublisher>());
        var gate = new ConfidenceGate(0.4);

        // Fake satellite TCP socket
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync("127.0.0.1", port);
        var serverSocket = await listener.AcceptTcpClientAsync();

        var session = new SatelliteSession(
            satelliteId: "kitchen-01",
            connection: serverSocket,
            registry, emitter, stt, gate, metrics,
            logger: NullLogger<SatelliteSession>.Instance);

        var runTask = session.RunAsync(CancellationToken.None);

        await using (var s = clientTcp.GetStream())
        {
            await WyomingProtocol.WriteEventAsync(s, "audio-start", new { rate = 16000, width = 2, channels = 1 }, null, CancellationToken.None);
            await WyomingProtocol.WriteEventAsync(s, "audio-chunk", new { rate = 16000, width = 2, channels = 1 }, new byte[640], CancellationToken.None);
            await WyomingProtocol.WriteEventAsync(s, "audio-stop", null, null, CancellationToken.None);
            clientTcp.Close();
        }
        await runTask;

        session.LastDispatchedSender.Should().Be("household");
        session.LastDispatchedText.Should().Be("turn the kitchen light on");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionTests"`
Expected: FAIL — class not found.

- [ ] **Step 3: Implement SatelliteSession**

```csharp
// McpChannelVoice/Services/SatelliteSession.cs
namespace McpChannelVoice.Services;

using System.Net.Sockets;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using Microsoft.Extensions.Logging;

public class SatelliteSession(
    string satelliteId,
    TcpClient connection,
    SatelliteRegistry registry,
    ChannelNotificationEmitter emitter,
    ISpeechToText stt,
    ConfidenceGate gate,
    VoiceMetricsPublisher metrics,
    ILogger<SatelliteSession> logger)
{
    public string SatelliteId => satelliteId;
    public string? LastDispatchedSender { get; private set; }
    public string? LastDispatchedText { get; private set; }

    public async Task RunAsync(CancellationToken ct)
    {
        var settings = registry.Lookup(satelliteId)
            ?? throw new InvalidOperationException($"Unknown satellite {satelliteId}");
        await using var stream = connection.GetStream();

        while (!ct.IsCancellationRequested)
        {
            var first = await SafeReadAsync(stream, ct);
            if (first is null) return;

            switch (first.Type)
            {
                case "audio-start":
                    await HandleUtteranceAsync(stream, settings.Identity, settings.Room, ct);
                    break;
                case "info":
                case "describe":
                    // No-op; could send back our capabilities.
                    break;
                default:
                    logger.LogDebug("Ignoring satellite event {Type}", first.Type);
                    break;
            }
        }
    }

    private async Task HandleUtteranceAsync(NetworkStream stream, string identity, string room, CancellationToken ct)
    {
        await metrics.PublishAsync(VoiceMetric.WakeTriggered, 1, new Dictionary<VoiceDimension, string>
        {
            [VoiceDimension.SatelliteId] = satelliteId,
            [VoiceDimension.Room] = room,
            [VoiceDimension.Identity] = identity
        }, ct: ct);

        var chunks = Channel.CreateUnbounded<AudioChunk>();
        var pumpTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var ev = await WyomingProtocol.ReadEventAsync(stream, ct);
                    if (ev is null || ev.Type == "audio-stop") break;
                    if (ev.Type == "audio-chunk" && ev.Payload is { Length: > 0 })
                    {
                        chunks.Writer.TryWrite(new AudioChunk(ev.Payload, 16000, 1, 16));
                    }
                }
            }
            finally
            {
                chunks.Writer.TryComplete();
            }
        }, ct);

        TranscriptionResult result;
        try
        {
            result = await stt.TranscribeAsync(chunks.Reader.ReadAllAsync(ct), new TranscriptionOptions(), ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "STT failed for {SatelliteId}", satelliteId);
            await metrics.PublishAsync(VoiceMetric.SttError, 1, new Dictionary<VoiceDimension, string>
            {
                [VoiceDimension.SatelliteId] = satelliteId
            }, ct: ct);
            await pumpTask;
            return;
        }
        await pumpTask;

        if (!gate.ShouldDispatch(result))
        {
            logger.LogDebug("Dropping low-confidence/empty transcript from {SatelliteId}", satelliteId);
            return;
        }

        LastDispatchedSender = identity;
        LastDispatchedText = result.Text;
        await metrics.PublishAsync(VoiceMetric.UtteranceTranscribed, 1, new Dictionary<VoiceDimension, string>
        {
            [VoiceDimension.SatelliteId] = satelliteId,
            [VoiceDimension.Room] = room,
            [VoiceDimension.Identity] = identity,
            [VoiceDimension.Language] = result.Language ?? "unknown"
        }, ct: ct);
        await emitter.EmitMessageNotificationAsync(
            conversationId: satelliteId,
            sender: identity,
            content: result.Text,
            agentId: "voice",
            room: room,
            ct: ct);
    }

    private static async Task<WyomingEvent?> SafeReadAsync(NetworkStream s, CancellationToken ct)
    {
        try { return await WyomingProtocol.ReadEventAsync(s, ct); }
        catch (IOException) { return null; }
        catch (ObjectDisposedException) { return null; }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSession.cs Tests/Unit/McpChannelVoice/Services/SatelliteSessionTests.cs
git commit -m "feat(voice): satellite session — wake to dispatch"
```

---

### Task 2.6: WyomingServer — accept satellite connections

**Files:**
- Create: `McpChannelVoice/Services/WyomingServer.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/WyomingServerTests.cs`

The `WyomingServer` runs as a `BackgroundService`. It binds to the configured host/port, accepts TCP connections, reads the inbound `info`/`describe` handshake to extract the satellite id, validates it against the registry, and hands off to a new `SatelliteSession.RunAsync`.

> **Satellite id source:** `wyoming-satellite` includes `name` in its `info` event under `data.satellite.name`. If the handshake does not carry an id, the channel rejects the connection. (See https://github.com/rhasspy/wyoming-satellite README.)

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/WyomingServerTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using System.Net.Sockets;
using global::Domain.Contracts;
using global::Domain.DTOs.Voice;
using global::McpChannelVoice.Services;
using global::McpChannelVoice.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class WyomingServerTests
{
    [Fact]
    public async Task Rejects_connection_with_unknown_satellite_name()
    {
        var registry = new SatelliteRegistry(new VoiceSettings
        {
            Satellites = new Dictionary<string, SatelliteSettings>
            {
                ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" }
            }
        });
        var stt = Substitute.For<ISpeechToText>();
        var emitter = new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance);
        var metrics = new VoiceMetricsPublisher(Substitute.For<IMetricsPublisher>());
        var gate = new ConfidenceGate(0.4);

        var server = new WyomingServer("127.0.0.1", 0, registry, stt, emitter, gate, metrics,
            sessionLogger: NullLogger<SatelliteSession>.Instance,
            logger: NullLogger<WyomingServer>.Instance);

        await server.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync("127.0.0.1", server.BoundPort);
        await using var s = client.GetStream();
        await WyomingProtocol.WriteEventAsync(s,
            "info",
            data: new { satellite = new { name = "ghost" } },
            payload: null,
            CancellationToken.None);

        // Server closes the socket. Reading returns null/EOF.
        var ev = await WyomingProtocol.ReadEventAsync(s, CancellationToken.None);
        ev.Should().BeNull();

        await server.StopAsync(CancellationToken.None);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingServerTests"`
Expected: FAIL.

- [ ] **Step 3: Implement WyomingServer**

```csharp
// McpChannelVoice/Services/WyomingServer.cs
namespace McpChannelVoice.Services;

using System.Net;
using System.Net.Sockets;
using Domain.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public class WyomingServer(
    string host,
    int port,
    SatelliteRegistry registry,
    ISpeechToText stt,
    ChannelNotificationEmitter emitter,
    ConfidenceGate gate,
    VoiceMetricsPublisher metrics,
    ILogger<SatelliteSession> sessionLogger,
    ILogger<WyomingServer> logger) : BackgroundService
{
    private TcpListener? listener;
    public int BoundPort { get; private set; }

    public override Task StartAsync(CancellationToken ct)
    {
        listener = new TcpListener(IPAddress.Parse(host), port);
        listener.Start();
        BoundPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        logger.LogInformation("Wyoming server listening on {Host}:{Port}", host, BoundPort);
        return base.StartAsync(ct);
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener!.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { return; }

            _ = Task.Run(() => HandleAsync(client, ct), ct);
        }
    }

    private async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            await using var s = client.GetStream();
            var info = await WyomingProtocol.ReadEventAsync(s, ct);
            if (info is null) return;
            var satelliteId = ExtractSatelliteId(info);
            if (satelliteId is null || !registry.IsKnown(satelliteId))
            {
                logger.LogWarning("Rejecting unknown satellite {SatelliteId}", satelliteId ?? "<missing>");
                client.Close();
                return;
            }

            var session = new SatelliteSession(satelliteId, client, registry, emitter, stt, gate, metrics, sessionLogger);
            await session.RunAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Satellite connection terminated");
        }
        finally
        {
            client.Dispose();
        }
    }

    private static string? ExtractSatelliteId(WyomingEvent info)
    {
        var root = info.Data.RootElement;
        return root.TryGetProperty("satellite", out var sat) && sat.TryGetProperty("name", out var n)
            ? n.GetString()
            : null;
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        listener?.Stop();
        await base.StopAsync(ct);
    }
}
```

- [ ] **Step 4: Register the server in ConfigModule**

Edit `McpChannelVoice/Modules/ConfigModule.cs` — extend `ConfigureChannel`:

```csharp
services.AddSingleton<ConfidenceGate>(_ => new ConfidenceGate(settings.ConfidenceThreshold));
services.AddSingleton<WyomingServer>(sp => new WyomingServer(
    settings.WyomingServer.Host,
    settings.WyomingServer.Port,
    sp.GetRequiredService<SatelliteRegistry>(),
    sp.GetRequiredService<ISpeechToText>(),
    sp.GetRequiredService<ChannelNotificationEmitter>(),
    sp.GetRequiredService<ConfidenceGate>(),
    sp.GetRequiredService<VoiceMetricsPublisher>(),
    sp.GetRequiredService<ILogger<SatelliteSession>>(),
    sp.GetRequiredService<ILogger<WyomingServer>>()));
services.AddHostedService(sp => sp.GetRequiredService<WyomingServer>());
```

And register the STT adapter (Slice 6 will introduce the switch; for now hard-wire Wyoming):

```csharp
services.AddSingleton<ISpeechToText>(_ => new WyomingSpeechToText(
    settings.Stt.Wyoming!.Host,
    settings.Stt.Wyoming!.Port,
    settings.Stt.Wyoming!.Model));
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingServerTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/Services/WyomingServer.cs McpChannelVoice/Modules/ConfigModule.cs Tests/Unit/McpChannelVoice/Services/WyomingServerTests.cs
git commit -m "feat(voice): Wyoming server accepts satellites and dispatches sessions"
```

---

### Task 2.7: Add wyoming-whisper to docker-compose

**Files:**
- Modify: `DockerCompose/docker-compose.yml`

- [ ] **Step 1: Append the service**

```yaml
wyoming-whisper:
  image: rhasspy/wyoming-whisper:latest
  container_name: wyoming-whisper
  command: --model base --language es --device cpu --uri tcp://0.0.0.0:10300
  ports:
    - "10300:10300"
  volumes:
    - whisper-data:/data
  restart: unless-stopped
  networks:
    - jackbot

# under top-level `volumes:`
whisper-data:
```

- [ ] **Step 2: Make `mcp-channel-voice` depend on it**

```yaml
mcp-channel-voice:
  depends_on:
    wyoming-whisper:
      condition: service_started
```

- [ ] **Step 3: Bring it up**

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build mcp-channel-voice wyoming-whisper
```

- [ ] **Step 4: Commit**

```bash
git add DockerCompose/docker-compose.yml
git commit -m "chore(voice): add wyoming-whisper service"
```

---

### Task 2.8: Integration test — fake satellite end-to-end via real whisper

**Files:**
- Create: `Tests/Integration/McpChannelVoice/SttEndToEndTests.cs`
- Create: `Tests/Integration/McpChannelVoice/Fixtures/canned-es.wav` (small WAV with "hola" recorded)

> The integration suite already uses Docker via existing fixtures. Mirror that pattern. If a docker-compose fixture is needed, check `Tests/Integration/Fixtures/` for the canonical helper; otherwise the test can run against a `wyoming-whisper` container booted manually.

- [ ] **Step 1: Write the integration test**

```csharp
// Tests/Integration/McpChannelVoice/SttEndToEndTests.cs
namespace Tests.Integration.McpChannelVoice;

using System.Net.Sockets;
using global::McpChannelVoice.Services;
using FluentAssertions;
using Xunit;

[Trait("Category", "Integration")]
public class SttEndToEndTests
{
    [Fact(Skip = "Requires wyoming-whisper container on localhost:10300")]
    public async Task Spoken_word_round_trips_to_transcript()
    {
        var bytes = await File.ReadAllBytesAsync("Fixtures/canned-es.wav");
        // strip 44-byte WAV header → raw PCM
        var pcm = bytes.AsMemory(44).ToArray();
        var stt = new Infrastructure.Clients.Voice.WyomingSpeechToText("localhost", 10300, "base");

        async IAsyncEnumerable<global::Domain.DTOs.Voice.AudioChunk> Frames()
        {
            const int frame = 1600 * 2; // 100ms @ 16kHz mono 16-bit
            for (var i = 0; i < pcm.Length; i += frame)
            {
                var len = Math.Min(frame, pcm.Length - i);
                yield return new global::Domain.DTOs.Voice.AudioChunk(pcm.AsMemory(i, len).ToArray(), 16000, 1, 16);
                await Task.Delay(10);
            }
        }
        var result = await stt.TranscribeAsync(Frames(), new global::Domain.DTOs.Voice.TranscriptionOptions("es"), CancellationToken.None);
        result.Text.ToLowerInvariant().Should().Contain("hola");
    }
}
```

- [ ] **Step 2: Record a short WAV**

On the host: `arecord -r 16000 -c 1 -f S16_LE -d 2 Tests/Integration/McpChannelVoice/Fixtures/canned-es.wav` and say "hola".
If a microphone is unavailable, generate one with piper:

```bash
docker run --rm -i rhasspy/wyoming-piper:latest \
  python3 -c "from piper import PiperVoice; v=PiperVoice.load('/usr/share/piper/voices/es_ES-davefx-medium.onnx'); v.synthesize('hola', open('/tmp/out.wav','wb'))" > Tests/Integration/McpChannelVoice/Fixtures/canned-es.wav
```

- [ ] **Step 3: Boot whisper and run (manual gate, test is `Skip`-marked)**

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d wyoming-whisper
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SttEndToEndTests" --logger "console;verbosity=detailed"
```

Remove the `Skip` locally and verify the test passes; restore the `Skip` for CI.

- [ ] **Step 4: Commit**

```bash
git add Tests/Integration/McpChannelVoice
git commit -m "test(voice): STT integration test scaffold"
```

---

### Task 2.9: Voice dashboard page — skeleton with two charts

**Files:**
- Create: `Dashboard.Client/Pages/Voice.razor`
- Modify: `Dashboard.Client/Layout/MainLayout.razor` (add nav link)
- Modify: `Dashboard.Client/Services/MetricsApiService.cs` (add voice methods)
- Modify: `Observability/MetricsApiEndpoints.cs` (add voice routes)
- Modify: `Observability/Services/MetricsQueryService.cs` (add voice grouped query)

> **Before:** Open `Dashboard.Client/Pages/Tools.razor` and `Observability/MetricsApiEndpoints.cs` to copy the existing patterns exactly.

- [ ] **Step 1: Add the voice grouped query to MetricsQueryService**

Append to `Observability/Services/MetricsQueryService.cs`:

```csharp
public async Task<IReadOnlyDictionary<string, decimal>> GetVoiceGroupedAsync(
    Domain.DTOs.Metrics.Enums.VoiceDimension dimension,
    Domain.DTOs.Metrics.Enums.VoiceMetric metric,
    DateTimeOffset from,
    DateTimeOffset to)
{
    var events = await GetEventsAsync<Domain.DTOs.Metrics.VoiceMetricEvent>("metrics:voice:", from, to);
    return events
        .Where(e => e.Metric == metric)
        .GroupBy(e => e.Dimensions.TryGetValue(dimension.ToString(), out var v) ? v : "(none)")
        .ToDictionary(g => g.Key, g => (decimal)g.Sum(e => e.Value));
}
```

- [ ] **Step 2: Add API routes**

Append to `Observability/MetricsApiEndpoints.cs`:

```csharp
group.MapGet("/voice", async (MetricsQueryService q, DateTimeOffset from, DateTimeOffset to) =>
    await q.GetEventsAsync<Domain.DTOs.Metrics.VoiceMetricEvent>("metrics:voice:", from, to));

group.MapGet("/voice/grouped", async (
    MetricsQueryService q,
    Domain.DTOs.Metrics.Enums.VoiceDimension dimension,
    Domain.DTOs.Metrics.Enums.VoiceMetric metric,
    DateTimeOffset from,
    DateTimeOffset to) =>
        await q.GetVoiceGroupedAsync(dimension, metric, from, to));
```

- [ ] **Step 3: Add API client methods**

Append to `Dashboard.Client/Services/MetricsApiService.cs`:

```csharp
public Task<IReadOnlyList<VoiceMetricEvent>> GetVoiceAsync(DateTimeOffset from, DateTimeOffset to) =>
    GetJsonAsync<IReadOnlyList<VoiceMetricEvent>>($"/api/metrics/voice?from={from:O}&to={to:O}");

public Task<IReadOnlyDictionary<string, decimal>> GetVoiceGroupedAsync(
    VoiceDimension dimension, VoiceMetric metric, DateTimeOffset from, DateTimeOffset to) =>
        GetJsonAsync<IReadOnlyDictionary<string, decimal>>(
            $"/api/metrics/voice/grouped?dimension={dimension}&metric={metric}&from={from:O}&to={to:O}");
```

- [ ] **Step 4: Add Voice.razor**

> Open `Dashboard.Client/Pages/Tools.razor` for the exact `DynamicChart`/`PillSelector`/`KpiCard` usage. Mirror it; pre-select `Room` and `UtteranceTranscribed` as default group/metric, plus a second chart with `SatelliteId` × `WakeToFirstAudioMs`.

```razor
@page "/voice"
@inject MetricsApiService Api
@inject LocalStorageService Storage

<PageHeader Title="Voice" />

<div class="grid grid-cols-2 gap-4 mb-6">
    <KpiCard Title="Utterances (24h)" Value="@utterances24h" />
    <KpiCard Title="STT errors (24h)" Value="@sttErrors24h" />
</div>

<DynamicChart Title="Utterances by room"
              Group="@VoiceDimension.Room"
              Metric="@VoiceMetric.UtteranceTranscribed"
              Loader="LoadGrouped" />

<DynamicChart Title="STT errors by provider+model"
              Group="@VoiceDimension.SttProvider"
              Metric="@VoiceMetric.SttError"
              Loader="LoadGrouped" />

@code {
    private decimal utterances24h;
    private decimal sttErrors24h;

    protected override async Task OnInitializedAsync()
    {
        var to = DateTimeOffset.UtcNow;
        var from = to.AddHours(-24);
        var byMetric = await Api.GetVoiceAsync(from, to);
        utterances24h = byMetric.Count(e => e.Metric == VoiceMetric.UtteranceTranscribed);
        sttErrors24h = byMetric.Count(e => e.Metric == VoiceMetric.SttError);
    }

    private Task<IReadOnlyDictionary<string, decimal>> LoadGrouped(VoiceDimension d, VoiceMetric m, DateTimeOffset from, DateTimeOffset to) =>
        Api.GetVoiceGroupedAsync(d, m, from, to);
}
```

- [ ] **Step 5: Add nav entry**

Edit `Dashboard.Client/Layout/MainLayout.razor` and add (mirroring the existing `/tools` link):

```razor
<NavLink class="nav-item" href="voice" Match="NavLinkMatch.Prefix">
    <i class="icon-mic"></i> Voice
</NavLink>
```

- [ ] **Step 6: Build dashboard client**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: build succeeded.

- [ ] **Step 7: Commit**

```bash
git add Observability Dashboard.Client
git commit -m "feat(voice): dashboard page + API for voice metrics"
```

---

# Slice 3 — TTS path

End state: agent reply is spoken back through the originating satellite. MVP demo.

### Task 3.1: WyomingTextToSpeech adapter

**Files:**
- Create: `Infrastructure/Clients/Voice/WyomingTextToSpeech.cs`
- Test: `Tests/Unit/McpChannelVoice/Infrastructure/WyomingTextToSpeechTests.cs`

- [ ] **Step 1: Write the failing test (uses a loopback fake server)**

```csharp
// Tests/Unit/McpChannelVoice/Infrastructure/WyomingTextToSpeechTests.cs
namespace Tests.Unit.McpChannelVoice.Infrastructure;

using System.Net;
using System.Net.Sockets;
using global::Domain.DTOs.Voice;
using global::Infrastructure.Clients.Voice;
using global::McpChannelVoice.Services;
using FluentAssertions;
using Xunit;

public class WyomingTextToSpeechTests
{
    [Fact]
    public async Task Streams_chunks_from_piper_until_audio_stop()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fakePiper = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var s = server.GetStream();
            var first = await WyomingProtocol.ReadEventAsync(s, CancellationToken.None);
            first!.Type.Should().Be("synthesize");
            await WyomingProtocol.WriteEventAsync(s, "audio-start", new { rate = 22050, width = 2, channels = 1 }, null, CancellationToken.None);
            await WyomingProtocol.WriteEventAsync(s, "audio-chunk", new { rate = 22050, width = 2, channels = 1 }, new byte[] { 9, 9, 9 }, CancellationToken.None);
            await WyomingProtocol.WriteEventAsync(s, "audio-stop", null, null, CancellationToken.None);
        });

        var tts = new WyomingTextToSpeech("127.0.0.1", port, "es_ES-davefx-medium");
        var collected = new List<byte>();
        await foreach (var c in tts.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
            collected.AddRange(c.Data);
        collected.Should().BeEquivalentTo(new byte[] { 9, 9, 9 });
        listener.Stop();
        await fakePiper;
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingTextToSpeechTests"`
Expected: FAIL.

- [ ] **Step 3: Implement WyomingTextToSpeech**

```csharp
// Infrastructure/Clients/Voice/WyomingTextToSpeech.cs
namespace Infrastructure.Clients.Voice;

using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;

public class WyomingTextToSpeech(string host, int port, string? voice = null) : ITextToSpeech
{
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await using var client = await WyomingClient.ConnectAsync(host, port, ct);
        await client.SendEventAsync("synthesize", new
        {
            text,
            voice = new { name = options.Voice ?? voice }
        }, payload: null, ct);

        var rate = 22050; var width = 2; var channels = 1;
        while (true)
        {
            var ev = await client.ReadEventAsync(ct);
            if (ev is null || ev.Type == "audio-stop") yield break;
            if (ev.Type == "audio-start")
            {
                var root = ev.Data.RootElement;
                if (root.TryGetProperty("rate", out var r)) rate = r.GetInt32();
                if (root.TryGetProperty("width", out var w)) width = w.GetInt32();
                if (root.TryGetProperty("channels", out var c)) channels = c.GetInt32();
                continue;
            }
            if (ev.Type == "audio-chunk" && ev.Payload is { Length: > 0 })
            {
                yield return new AudioChunk(ev.Payload, rate, channels, width * 8);
            }
        }
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingTextToSpeechTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Voice/WyomingTextToSpeech.cs Tests/Unit/McpChannelVoice/Infrastructure/WyomingTextToSpeechTests.cs
git commit -m "feat(voice): WyomingTextToSpeech adapter"
```

---

### Task 3.2: SatelliteSession — playback queue + reply routing

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/SatelliteSessionPlaybackTests.cs`

The session gains:
1. A reference to `ITextToSpeech` (passed via constructor).
2. A method `EnqueueReplyAsync(string text, CancellationToken ct)` that synthesizes audio and streams it back to the satellite via Wyoming `audio-start` / `audio-chunk*` / `audio-stop`.
3. A static `SatelliteSessionRegistry` keyed by satellite id that the `SendReplyTool` looks up.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/SatelliteSessionPlaybackTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using System.Net;
using System.Net.Sockets;
using global::Domain.Contracts;
using global::Domain.DTOs.Voice;
using global::McpChannelVoice.Services;
using global::McpChannelVoice.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class SatelliteSessionPlaybackTests
{
    [Fact]
    public async Task EnqueueReply_streams_audio_chunks_to_satellite()
    {
        var registry = new SatelliteRegistry(new VoiceSettings
        {
            Satellites = new Dictionary<string, SatelliteSettings>
            {
                ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" }
            }
        });
        var stt = Substitute.For<ISpeechToText>();
        var tts = Substitute.For<ITextToSpeech>();
        async IAsyncEnumerable<AudioChunk> ttsOut()
        {
            yield return new AudioChunk(new byte[] { 1, 2 }, 22050, 1, 16);
            yield return new AudioChunk(new byte[] { 3, 4 }, 22050, 1, 16);
            await Task.CompletedTask;
        }
        tts.SynthesizeAsync(Arg.Any<string>(), Arg.Any<SynthesisOptions>(), Arg.Any<CancellationToken>()).Returns(ttsOut());

        var emitter = new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance);
        var metrics = new VoiceMetricsPublisher(Substitute.For<IMetricsPublisher>());
        var gate = new ConfidenceGate(0.4);

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var clientTcp = new TcpClient();
        await clientTcp.ConnectAsync("127.0.0.1", port);
        var serverSocket = await listener.AcceptTcpClientAsync();

        var session = new SatelliteSession("kitchen-01", serverSocket, registry, emitter, stt, gate, metrics, tts, NullLogger<SatelliteSession>.Instance);
        await session.EnqueueReplyAsync("hola", CancellationToken.None);

        await using var s = clientTcp.GetStream();
        var ev1 = await WyomingProtocol.ReadEventAsync(s, CancellationToken.None);
        ev1!.Type.Should().Be("audio-start");
        var ev2 = await WyomingProtocol.ReadEventAsync(s, CancellationToken.None);
        ev2!.Type.Should().Be("audio-chunk");
        var ev3 = await WyomingProtocol.ReadEventAsync(s, CancellationToken.None);
        ev3!.Type.Should().Be("audio-chunk");
        var ev4 = await WyomingProtocol.ReadEventAsync(s, CancellationToken.None);
        ev4!.Type.Should().Be("audio-stop");

        listener.Stop();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionPlaybackTests"`
Expected: FAIL (constructor mismatch — `tts` parameter missing).

- [ ] **Step 3: Extend SatelliteSession**

Update the constructor signature to add `ITextToSpeech tts` (between `metrics` and `logger`), and add:

```csharp
public async Task EnqueueReplyAsync(string text, CancellationToken ct)
{
    var settings = registry.Lookup(satelliteId)!;
    var stream = connection.GetStream();
    AudioChunk? first = null;
    await foreach (var chunk in tts.SynthesizeAsync(text, new SynthesisOptions(Voice: settings.Tts?.Wyoming?.Voice), ct))
    {
        if (first is null)
        {
            first = chunk;
            await WyomingProtocol.WriteEventAsync(stream, "audio-start",
                new { rate = chunk.SampleRate, width = chunk.BitsPerSample / 8, channels = chunk.Channels },
                payload: null, ct);
        }
        await WyomingProtocol.WriteEventAsync(stream, "audio-chunk",
            new { rate = chunk.SampleRate, width = chunk.BitsPerSample / 8, channels = chunk.Channels },
            payload: chunk.Data, ct);
    }
    await WyomingProtocol.WriteEventAsync(stream, "audio-stop", data: null, payload: null, ct);
}
```

Also expose a static registry:

```csharp
// inside SatelliteSession
private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SatelliteSession> Active = new();
public static SatelliteSession? Find(string satelliteId) => Active.TryGetValue(satelliteId, out var s) ? s : null;
public void Register() => Active[satelliteId] = this;
public void Unregister() => Active.TryRemove(satelliteId, out _);
```

Call `Register()`/`Unregister()` at the start/end of `RunAsync`. The session also self-registers when constructed by tests that don't call `RunAsync` — add an explicit `Register()` call in `EnqueueReplyAsync` for symmetry, or have the test call `session.Register()` before `EnqueueReplyAsync`.

> Update the existing `SatelliteSessionTests` constructor call to pass `tts: Substitute.For<ITextToSpeech>()`.

- [ ] **Step 4: Run all session tests to verify**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSession"`
Expected: all passing.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSession.cs Tests/Unit/McpChannelVoice/Services/SatelliteSessionPlaybackTests.cs Tests/Unit/McpChannelVoice/Services/SatelliteSessionTests.cs
git commit -m "feat(voice): session playback queue + TTS"
```

---

### Task 3.3: SendReplyTool — wire to SatelliteSession

**Files:**
- Modify: `McpChannelVoice/McpTools/SendReplyTool.cs`
- Test: `Tests/Unit/McpChannelVoice/McpTools/SendReplyToolTests.cs`

The tool resolves `conversationId` → `SatelliteSession.Find(satelliteId)` and calls `EnqueueReplyAsync`. Only `ReplyContentType.Text` (or whatever the existing enum's "final user-facing text" variant is) triggers TTS; reasoning/tool-call chunks are dropped (logged at debug). Audio is only spoken when `isComplete` is `true` to avoid speaking partial streams; intermediate chunks are concatenated by a small `ReplyBuffer` keyed by `conversationId`.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/McpTools/SendReplyToolTests.cs
namespace Tests.Unit.McpChannelVoice.McpTools;

using global::Domain.DTOs.Channel;
using global::McpChannelVoice.McpTools;
using global::McpChannelVoice.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class SendReplyToolTests
{
    [Fact]
    public async Task Buffers_then_flushes_to_session_on_isComplete()
    {
        var buffer = new ReplyBuffer();
        var fakeSession = Substitute.For<ISatellitePlayback>();
        ReplyBuffer.SessionLookup = _ => fakeSession;

        await SendReplyTool.McpRun("kitchen-01", "hola ", ReplyContentType.Text, isComplete: false, messageId: null,
            buffer, NullLogger<SendReplyToolMarker>.Instance);
        await SendReplyTool.McpRun("kitchen-01", "mundo", ReplyContentType.Text, isComplete: true, messageId: null,
            buffer, NullLogger<SendReplyToolMarker>.Instance);

        await fakeSession.Received(1).EnqueueReplyAsync("hola mundo", Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SendReplyToolTests"`
Expected: FAIL.

- [ ] **Step 3: Implement ReplyBuffer + extract ISatellitePlayback**

```csharp
// McpChannelVoice/Services/ISatellitePlayback.cs
namespace McpChannelVoice.Services;

public interface ISatellitePlayback
{
    Task EnqueueReplyAsync(string text, CancellationToken ct);
}
```

Make `SatelliteSession` implement `ISatellitePlayback`.

```csharp
// McpChannelVoice/Services/ReplyBuffer.cs
namespace McpChannelVoice.Services;

using System.Collections.Concurrent;
using System.Text;

public class ReplyBuffer
{
    public static Func<string, ISatellitePlayback?> SessionLookup { get; set; } = SatelliteSession.Find;

    private readonly ConcurrentDictionary<string, StringBuilder> buffers = new();

    public async Task AppendAsync(string conversationId, string content, bool isComplete, CancellationToken ct)
    {
        var sb = buffers.GetOrAdd(conversationId, _ => new StringBuilder());
        lock (sb) sb.Append(content);
        if (!isComplete) return;

        string text;
        lock (sb) text = sb.ToString();
        buffers.TryRemove(conversationId, out _);

        var session = SessionLookup(conversationId);
        if (session is not null) await session.EnqueueReplyAsync(text, ct);
    }
}
```

Then update `SendReplyTool`:

```csharp
public static async Task<object?> McpRun(
    string conversationId,
    string content,
    ReplyContentType contentType,
    bool isComplete,
    string? messageId,
    ReplyBuffer buffer,
    ILogger<SendReplyToolMarker> logger)
{
    if (contentType != ReplyContentType.Text)
    {
        logger.LogDebug("Ignoring non-text reply chunk type={Type}", contentType);
        return new { ok = true };
    }
    await buffer.AppendAsync(conversationId, content, isComplete, CancellationToken.None);
    return new { ok = true };
}
```

Register `ReplyBuffer` in `ConfigModule.ConfigureChannel`:

```csharp
services.AddSingleton<ReplyBuffer>();
services.AddSingleton<ITextToSpeech>(_ => new WyomingTextToSpeech(
    settings.Tts.Wyoming!.Host,
    settings.Tts.Wyoming!.Port,
    settings.Tts.Wyoming!.Voice));
```

Also update `WyomingServer.HandleAsync` to pass the resolved `ITextToSpeech` into `SatelliteSession`. Update its constructor + the registration in `ConfigModule` to inject `ITextToSpeech` (use `IServiceProvider` if needed).

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SendReplyToolTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice Tests/Unit/McpChannelVoice
git commit -m "feat(voice): send_reply buffers text and speaks via session"
```

---

### Task 3.4: Add wyoming-piper to docker-compose

**Files:**
- Modify: `DockerCompose/docker-compose.yml`

- [ ] **Step 1: Append**

```yaml
wyoming-piper:
  image: rhasspy/wyoming-piper:latest
  container_name: wyoming-piper
  command: --voice es_ES-davefx-medium --uri tcp://0.0.0.0:10200
  ports:
    - "10200:10200"
  volumes:
    - piper-data:/data
  restart: unless-stopped
  networks:
    - jackbot

# under volumes:
piper-data:
```

- [ ] **Step 2: Add depends_on**

```yaml
mcp-channel-voice:
  depends_on:
    wyoming-piper:
      condition: service_started
```

- [ ] **Step 3: Bring up**

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build mcp-channel-voice wyoming-piper
```

- [ ] **Step 4: Commit**

```bash
git add DockerCompose/docker-compose.yml
git commit -m "chore(voice): add wyoming-piper service"
```

---

### Task 3.5: TTS latency + wake-to-first-audio metrics

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs`
- Modify: `Dashboard.Client/Pages/Overview.razor` (add two KPI cards)
- Test: `Tests/Unit/McpChannelVoice/Services/SatelliteSessionMetricsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/SatelliteSessionMetricsTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using global::Domain.Contracts;
using global::Domain.DTOs.Metrics;
using global::Domain.DTOs.Metrics.Enums;
using global::Domain.DTOs.Voice;
using global::McpChannelVoice.Services;
using global::McpChannelVoice.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class SatelliteSessionMetricsTests
{
    [Fact]
    public async Task EnqueueReply_publishes_TtsLatencyMs()
    {
        // build session against an in-memory fake satellite as in earlier tests;
        // assert that a VoiceMetricEvent with Metric=TtsLatencyMs is published.
        // (Implementation should record stopwatch around the tts.SynthesizeAsync loop.)
        // ... (full setup mirrors SatelliteSessionPlaybackTests)
    }
}
```

(Flesh out the body using the same fake-TcpListener pattern as `SatelliteSessionPlaybackTests`; substitute the metrics publisher with `Substitute.For<IMetricsPublisher>()` and assert it received a `VoiceMetricEvent` with `Metric == VoiceMetric.TtsLatencyMs`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionMetricsTests"`
Expected: FAIL.

- [ ] **Step 3: Add metrics in EnqueueReplyAsync**

Wrap the synthesis loop with a `Stopwatch`:

```csharp
var sw = Stopwatch.StartNew();
// ... existing synth loop ...
sw.Stop();
await metrics.PublishAsync(VoiceMetric.TtsLatencyMs, sw.ElapsedMilliseconds, new Dictionary<VoiceDimension, string>
{
    [VoiceDimension.SatelliteId] = satelliteId,
    [VoiceDimension.TtsProvider] = "Wyoming",
    [VoiceDimension.TtsVoice] = settings.Tts?.Wyoming?.Voice ?? "default"
}, ct: ct);
```

For `WakeToFirstAudioMs`: record the wake timestamp in `HandleUtteranceAsync` and stash it; when `EnqueueReplyAsync` writes its **first** audio-chunk, compute the delta and publish.

- [ ] **Step 4: Overview KPI cards**

Edit `Dashboard.Client/Pages/Overview.razor` and append two `KpiCard` instances:

```razor
<KpiCard Title="Utterances (24h)" Value="@voiceUtterances24h" />
<KpiCard Title="Median voice latency (24h)" Value="@medianVoiceLatencyMs" />
```

Compute in `OnInitializedAsync` from `Api.GetVoiceAsync(from, to)`.

- [ ] **Step 5: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSession"`
Expected: all passing.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice Dashboard.Client Tests/Unit/McpChannelVoice
git commit -m "feat(voice): TTS latency + wake-to-first-audio metrics"
```

---

### Task 3.6: Integration test — send_reply round-trip

**Files:**
- Create: `Tests/Integration/McpChannelVoice/TtsRoundTripTests.cs`

- [ ] **Step 1: Write**

```csharp
// Tests/Integration/McpChannelVoice/TtsRoundTripTests.cs
namespace Tests.Integration.McpChannelVoice;

using System.Net.Sockets;
using global::McpChannelVoice.Services;
using FluentAssertions;
using Xunit;

[Trait("Category", "Integration")]
public class TtsRoundTripTests
{
    [Fact(Skip = "Requires mcp-channel-voice + wyoming-piper running")]
    public async Task SendReply_streams_audio_back_through_open_session()
    {
        // Connect a fake satellite TCP client to mcp-channel-voice:10700
        // Send info handshake with satellite.name=kitchen-01
        // Invoke send_reply via HTTP MCP /mcp on port 5010
        // Read audio frames from the fake satellite stream
        // Assert at least one audio-chunk arrives followed by audio-stop
    }
}
```

- [ ] **Step 2: Manually unskip and run with the real stack**

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d mcp-channel-voice wyoming-piper
# (locally unskip the test, then re-skip before committing)
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TtsRoundTripTests"
```

- [ ] **Step 3: Commit**

```bash
git add Tests/Integration/McpChannelVoice/TtsRoundTripTests.cs
git commit -m "test(voice): TTS round-trip integration scaffold"
```

---

# Slice 4 — Announce HTTP endpoint

End state: `POST /api/voice/announce` plays a spoken message on a chosen target (id / room / all) with priority semantics. Home Assistant doorbell automation can trigger it.

### Task 4.1: SatelliteRegistry reverse lookups

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteRegistry.cs`
- Modify: `Tests/Unit/McpChannelVoice/Services/SatelliteRegistryTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `SatelliteRegistryTests`:

```csharp
[Fact]
public void FindByRoom_returns_all_satellites_in_room_case_insensitive()
{
    var r = Build();
    r.FindByRoom("living room").Should().BeEquivalentTo(new[] { "living-01", "living-02" });
}

[Fact]
public void FindByRoom_unknown_returns_empty()
{
    Build().FindByRoom("Garage").Should().BeEmpty();
}

[Fact]
public void All_returns_every_satellite_id()
{
    Build().All().Should().BeEquivalentTo(new[] { "kitchen-01", "bedroom-01", "living-01", "living-02" });
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteRegistryTests"`
Expected: FAIL on the three new tests.

- [ ] **Step 3: Implement**

Add to `SatelliteRegistry`:

```csharp
public IReadOnlyList<string> FindByRoom(string room) =>
    satellites
        .Where(kv => string.Equals(kv.Value.Room, room, StringComparison.OrdinalIgnoreCase))
        .Select(kv => kv.Key)
        .ToList();

public IReadOnlyList<string> All() => satellites.Keys.ToList();
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteRegistryTests"`
Expected: all passing.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteRegistry.cs Tests/Unit/McpChannelVoice/Services/SatelliteRegistryTests.cs
git commit -m "feat(voice): satellite registry reverse lookups"
```

---

### Task 4.2: AnnouncePriority + AnnounceRequest DTOs

**Files:**
- Create: `McpChannelVoice/Services/AnnounceModels.cs`

- [ ] **Step 1: Implement**

```csharp
// McpChannelVoice/Services/AnnounceModels.cs
namespace McpChannelVoice.Services;

public enum AnnouncePriority { Low, Normal, High }

public record AnnounceTarget(string? SatelliteId = null, string? Room = null, bool All = false);

public record AnnounceRequest(
    AnnounceTarget Target,
    string Text,
    string? Voice = null,
    AnnouncePriority Priority = AnnouncePriority.Normal);

public record AnnounceSatelliteStatus(string Id, string Status);

public record AnnounceResponse(string AnnouncementId, IReadOnlyList<AnnounceSatelliteStatus> Satellites);
```

- [ ] **Step 2: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add McpChannelVoice/Services/AnnounceModels.cs
git commit -m "feat(voice): announce DTOs"
```

---

### Task 4.3: Per-session priority playback queue

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/SatelliteSessionQueueTests.cs`

The session gains a small bounded queue of playback items. `High` preempts whatever is in-flight (cancel + emit `AnnouncePreemptedReply` if the cancelled item was a reply); `Normal` queues; `Low` is dropped if anything is queued.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/SatelliteSessionQueueTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using global::McpChannelVoice.Services;
using FluentAssertions;
using Xunit;

public class SatelliteSessionQueueTests
{
    [Fact]
    public void Normal_after_normal_queues_behind()
    {
        var q = new PlaybackQueue(maxDepth: 8);
        q.TryEnqueue(new PlaybackItem("a", AnnouncePriority.Normal, IsReply: true)).Should().BeTrue();
        q.TryEnqueue(new PlaybackItem("b", AnnouncePriority.Normal, IsReply: false)).Should().BeTrue();
        q.Depth.Should().Be(2);
        q.Dequeue()!.Text.Should().Be("a");
        q.Dequeue()!.Text.Should().Be("b");
    }

    [Fact]
    public void Low_dropped_when_anything_queued()
    {
        var q = new PlaybackQueue(maxDepth: 8);
        q.TryEnqueue(new PlaybackItem("a", AnnouncePriority.Normal, false)).Should().BeTrue();
        q.TryEnqueue(new PlaybackItem("b", AnnouncePriority.Low, false)).Should().BeFalse();
    }

    [Fact]
    public void High_preempts_and_returns_cancelled_item()
    {
        var q = new PlaybackQueue(maxDepth: 8);
        q.TryEnqueue(new PlaybackItem("normal-reply", AnnouncePriority.Normal, IsReply: true)).Should().BeTrue();
        var preempt = q.Preempt(new PlaybackItem("high", AnnouncePriority.High, false));
        preempt!.Text.Should().Be("normal-reply");
        q.Dequeue()!.Text.Should().Be("high");
    }

    [Fact]
    public void Queue_overflow_drops_oldest_low_first()
    {
        var q = new PlaybackQueue(maxDepth: 2);
        q.TryEnqueue(new PlaybackItem("low", AnnouncePriority.Low, false)).Should().BeTrue();
        q.TryEnqueue(new PlaybackItem("n1", AnnouncePriority.Normal, false)).Should().BeTrue();
        var accepted = q.TryEnqueue(new PlaybackItem("n2", AnnouncePriority.Normal, false));
        accepted.Should().BeTrue();
        q.Depth.Should().Be(2);
        q.Dequeue()!.Text.Should().Be("n1");
        q.Dequeue()!.Text.Should().Be("n2");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionQueueTests"`
Expected: FAIL.

- [ ] **Step 3: Implement PlaybackQueue + PlaybackItem**

```csharp
// McpChannelVoice/Services/PlaybackQueue.cs
namespace McpChannelVoice.Services;

public record PlaybackItem(string Text, AnnouncePriority Priority, bool IsReply, string? Voice = null);

public class PlaybackQueue(int maxDepth)
{
    private readonly LinkedList<PlaybackItem> items = new();

    public int Depth => items.Count;

    public bool TryEnqueue(PlaybackItem item)
    {
        lock (items)
        {
            if (item.Priority == AnnouncePriority.Low && items.Count > 0) return false;
            if (items.Count >= maxDepth)
            {
                var lowest = items.FirstOrDefault(i => i.Priority == AnnouncePriority.Low);
                if (lowest is null) return false;
                items.Remove(lowest);
            }
            items.AddLast(item);
            return true;
        }
    }

    public PlaybackItem? Preempt(PlaybackItem highItem)
    {
        if (highItem.Priority != AnnouncePriority.High) throw new ArgumentException("Only High preempts");
        lock (items)
        {
            var cancelled = items.First?.Value;
            items.Clear();
            items.AddFirst(highItem);
            return cancelled;
        }
    }

    public PlaybackItem? Dequeue()
    {
        lock (items)
        {
            if (items.First is null) return null;
            var v = items.First.Value;
            items.RemoveFirst();
            return v;
        }
    }
}
```

- [ ] **Step 4: Wire into SatelliteSession**

Update `SatelliteSession` to own a `PlaybackQueue` and a single playback worker `Task`. `EnqueueReplyAsync` and a new `EnqueueAnnounceAsync(PlaybackItem)` write through the queue. The worker loop dequeues and runs synthesis + playback. When `Preempt` returns a cancelled item that was a reply (`IsReply == true`), publish `AnnouncePreemptedReply`.

```csharp
public async Task<AnnounceSatelliteStatus> EnqueueAnnounceAsync(PlaybackItem item, CancellationToken ct)
{
    if (item.Priority == AnnouncePriority.High)
    {
        var cancelled = queue.Preempt(item);
        if (cancelled is { IsReply: true })
        {
            await metrics.PublishAsync(VoiceMetric.AnnouncePreemptedReply, 1, new Dictionary<VoiceDimension, string>
            {
                [VoiceDimension.SatelliteId] = satelliteId
            }, ct: ct);
        }
        SignalWorker();
        return new AnnounceSatelliteStatus(satelliteId, "playing");
    }

    if (!queue.TryEnqueue(item))
        return new AnnounceSatelliteStatus(satelliteId, "dropped");
    SignalWorker();
    return new AnnounceSatelliteStatus(satelliteId, queue.Depth == 1 ? "playing" : "queued");
}
```

Where `SignalWorker()` is a `SemaphoreSlim` release; the worker is started in `RunAsync` after handshake.

- [ ] **Step 5: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSession"`
Expected: all passing.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/Services Tests/Unit/McpChannelVoice/Services/SatelliteSessionQueueTests.cs
git commit -m "feat(voice): per-session priority playback queue"
```

---

### Task 4.4: AnnouncementService

**Files:**
- Create: `McpChannelVoice/Services/AnnouncementService.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/AnnouncementServiceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/AnnouncementServiceTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using global::Domain.Contracts;
using global::McpChannelVoice.Services;
using global::McpChannelVoice.Settings;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class AnnouncementServiceTests
{
    private static SatelliteRegistry Registry() => new(new VoiceSettings
    {
        Satellites = new Dictionary<string, SatelliteSettings>
        {
            ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" },
            ["living-01"]  = new() { Identity = "household", Room = "Living Room", WakeWord = "hey_jarvis" },
            ["living-02"]  = new() { Identity = "household", Room = "Living Room", WakeWord = "hey_jarvis" }
        }
    });

    [Fact]
    public async Task Resolve_room_targets_all_room_satellites_and_calls_offline_for_missing_sessions()
    {
        var registry = Registry();
        var lookup = Substitute.For<ISatellitePlaybackLookup>();
        lookup.Find(Arg.Any<string>()).Returns((ISatellitePlayback?)null);
        var svc = new AnnouncementService(registry, lookup, Substitute.For<IMetricsPublisher>(), NullLogger<AnnouncementService>.Instance);

        var resp = await svc.AnnounceAsync(new AnnounceRequest(new AnnounceTarget(Room: "Living Room"), "hello"), source: "ha", CancellationToken.None);

        resp.Satellites.Should().HaveCount(2);
        resp.Satellites.Should().OnlyContain(s => s.Status == "offline");
    }

    [Fact]
    public async Task Resolve_unknown_target_throws()
    {
        var svc = new AnnouncementService(Registry(), Substitute.For<ISatellitePlaybackLookup>(),
            Substitute.For<IMetricsPublisher>(), NullLogger<AnnouncementService>.Instance);
        var act = () => svc.AnnounceAsync(new AnnounceRequest(new AnnounceTarget(SatelliteId: "ghost"), "hi"), source: "ha", CancellationToken.None);
        await act.Should().ThrowAsync<UnknownTargetException>();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnouncementServiceTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// McpChannelVoice/Services/AnnouncementService.cs
namespace McpChannelVoice.Services;

using Domain.Contracts;
using Domain.DTOs.Metrics.Enums;
using Microsoft.Extensions.Logging;

public class UnknownTargetException(string message) : Exception(message);

public interface ISatellitePlaybackLookup
{
    ISatellitePlayback? Find(string satelliteId);
}

public class SatellitePlaybackLookup : ISatellitePlaybackLookup
{
    public ISatellitePlayback? Find(string satelliteId) => SatelliteSession.Find(satelliteId);
}

public class AnnouncementService(
    SatelliteRegistry registry,
    ISatellitePlaybackLookup lookup,
    IMetricsPublisher metricsUnderlying,
    ILogger<AnnouncementService> logger)
{
    private readonly VoiceMetricsPublisher metrics = new(metricsUnderlying);

    public async Task<AnnounceResponse> AnnounceAsync(AnnounceRequest request, string source, CancellationToken ct)
    {
        var ids = Resolve(request.Target);
        var results = new List<AnnounceSatelliteStatus>();
        foreach (var id in ids)
        {
            var session = lookup.Find(id);
            var priority = request.Priority;
            if (session is null)
            {
                results.Add(new AnnounceSatelliteStatus(id, "offline"));
                await metrics.PublishAsync(VoiceMetric.AnnounceError, 1, new Dictionary<VoiceDimension, string>
                {
                    [VoiceDimension.SatelliteId] = id,
                    [VoiceDimension.Outcome] = "offline",
                    [VoiceDimension.Source] = source
                }, ct: ct);
                continue;
            }
            if (session is SatelliteSession s)
            {
                var status = await s.EnqueueAnnounceAsync(new PlaybackItem(request.Text, priority, IsReply: false, request.Voice), ct);
                results.Add(status);
                var metric = status.Status == "dropped" ? VoiceMetric.AnnounceError : VoiceMetric.AnnounceQueued;
                await metrics.PublishAsync(metric, 1, new Dictionary<VoiceDimension, string>
                {
                    [VoiceDimension.SatelliteId] = id,
                    [VoiceDimension.Priority] = priority.ToString(),
                    [VoiceDimension.Source] = source
                }, ct: ct);
            }
        }
        return new AnnounceResponse(Guid.NewGuid().ToString("N"), results);
    }

    private IReadOnlyList<string> Resolve(AnnounceTarget target)
    {
        if (target.All) return registry.All();
        if (target.Room is { } room)
        {
            var ids = registry.FindByRoom(room);
            if (ids.Count == 0) throw new UnknownTargetException($"No satellites in room '{room}'");
            return ids;
        }
        if (target.SatelliteId is { } sid)
        {
            if (!registry.IsKnown(sid)) throw new UnknownTargetException($"Unknown satellite '{sid}'");
            return new[] { sid };
        }
        throw new UnknownTargetException("Empty target");
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnouncementServiceTests"`
Expected: all passing.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services Tests/Unit/McpChannelVoice/Services/AnnouncementServiceTests.cs
git commit -m "feat(voice): announcement service"
```

---

### Task 4.5: AnnounceEndpoint with token auth

**Files:**
- Create: `McpChannelVoice/Services/AnnounceEndpoint.cs`
- Modify: `McpChannelVoice/Program.cs` (map the route)
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (register `AnnouncementService` + lookup)
- Test: `Tests/Unit/McpChannelVoice/Services/AnnounceEndpointAuthTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/AnnounceEndpointAuthTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using System.Net;
using System.Net.Http.Json;
using global::McpChannelVoice.Services;
using global::McpChannelVoice.Settings;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class AnnounceEndpointAuthTests
{
    private static HttpClient BuildClient(string configuredToken)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var settings = new VoiceSettings { Announce = new AnnounceSettings { Enabled = true, Token = configuredToken } };
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(new AnnouncementService(
            new SatelliteRegistry(settings),
            Substitute.For<ISatellitePlaybackLookup>(),
            Substitute.For<Domain.Contracts.IMetricsPublisher>(),
            NullLogger<AnnouncementService>.Instance));
        var app = builder.Build();
        AnnounceEndpoint.Map(app);
        app.StartAsync().GetAwaiter().GetResult();
        return app.GetTestClient();
    }

    [Fact]
    public async Task Returns_401_when_token_header_missing()
    {
        var client = BuildClient("secret");
        var resp = await client.PostAsJsonAsync("/api/voice/announce", new AnnounceRequest(new AnnounceTarget(SatelliteId: "kitchen-01"), "hi"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Returns_401_when_token_header_wrong()
    {
        var client = BuildClient("secret");
        client.DefaultRequestHeaders.Add("X-Announce-Token", "wrong");
        var resp = await client.PostAsJsonAsync("/api/voice/announce", new AnnounceRequest(new AnnounceTarget(SatelliteId: "kitchen-01"), "hi"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnounceEndpointAuthTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// McpChannelVoice/Services/AnnounceEndpoint.cs
namespace McpChannelVoice.Services;

using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

public static class AnnounceEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/voice/announce", async (HttpContext ctx, AnnouncementService svc, VoiceSettings settings, AnnounceRequest req, CancellationToken ct) =>
        {
            if (!settings.Announce.Enabled) return Results.StatusCode(503);
            if (!ctx.Request.Headers.TryGetValue("X-Announce-Token", out var token) || token.ToString() != settings.Announce.Token)
                return Results.Unauthorized();
            var source = ctx.Request.Headers.TryGetValue("X-Announce-Source", out var s) ? s.ToString() : "ha";
            try
            {
                var resp = await svc.AnnounceAsync(req, source, ct);
                return Results.Accepted(value: resp);
            }
            catch (UnknownTargetException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }
}
```

- [ ] **Step 4: Wire in Program.cs**

```csharp
// McpChannelVoice/Program.cs
using McpChannelVoice.Modules;
using McpChannelVoice.Services;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.GetSettings();
builder.Services.ConfigureChannel(settings);

var app = builder.Build();
app.MapMcp("/mcp");
AnnounceEndpoint.Map(app);
app.Run();
```

And in `ConfigureChannel`:

```csharp
services.AddSingleton<ISatellitePlaybackLookup, SatellitePlaybackLookup>();
services.AddSingleton<AnnouncementService>();
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnounceEndpointAuthTests"`
Expected: 2 tests passed.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice Tests/Unit/McpChannelVoice/Services/AnnounceEndpointAuthTests.cs
git commit -m "feat(voice): announce HTTP endpoint with token auth"
```

---

### Task 4.6: ANNOUNCE_TOKEN end-to-end wiring + smoke test

**Files:**
- Confirm: `DockerCompose/docker-compose.yml`, `DockerCompose/.env`, `McpChannelVoice/appsettings.json`, `McpChannelVoice/appsettings.Development.json` already carry the placeholder from Task 1.8.

- [ ] **Step 1: Verify placeholder wiring is present**

Run: `grep -n ANNOUNCE_TOKEN DockerCompose/.env DockerCompose/docker-compose.yml McpChannelVoice/appsettings*.json`
Expected: present in all four files.

- [ ] **Step 2: Smoke test with curl**

Boot the stack and call:

```bash
curl -i -X POST http://localhost:5010/api/voice/announce \
  -H "X-Announce-Token: changeme" \
  -H "Content-Type: application/json" \
  -d '{"target":{"satelliteId":"kitchen-01"},"text":"hello"}'
```

Expected (no live satellite): `202 Accepted` with `{"satellites":[{"id":"kitchen-01","status":"offline"}]}`.
With a wrong token: `401`.

- [ ] **Step 3: Commit any remaining config tweaks**

```bash
git add DockerCompose McpChannelVoice
git commit -m "chore(voice): ANNOUNCE_TOKEN smoke verified"
```

---

### Task 4.7: Announcements-by-source chart

**Files:**
- Modify: `Dashboard.Client/Pages/Voice.razor`

- [ ] **Step 1: Add chart**

Append to `Voice.razor`:

```razor
<DynamicChart Title="Announcements by source"
              Group="@VoiceDimension.Source"
              Metric="@VoiceMetric.AnnouncePlayed"
              Loader="LoadGrouped" />
```

- [ ] **Step 2: Build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Pages/Voice.razor
git commit -m "feat(voice): announcements by source chart"
```

---

# Slice 5 — Approval over voice

End state: `request_approval` round-trips on Spanish and English yes/no.

### Task 5.1: ApprovalGrammarParser

**Files:**
- Create: `McpChannelVoice/Services/ApprovalGrammarParser.cs`
- Test: `Tests/Unit/McpChannelVoice/Services/ApprovalGrammarParserTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/ApprovalGrammarParserTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using global::McpChannelVoice.Services;
using FluentAssertions;
using Xunit;

public class ApprovalGrammarParserTests
{
    private readonly ApprovalGrammarParser parser = new();

    [Theory]
    [InlineData("yes")]
    [InlineData("YES PLEASE")]
    [InlineData("sí")]
    [InlineData("si")]
    [InlineData("confirm")]
    [InlineData("ok")]
    [InlineData("okay")]
    public void Recognises_positive(string input)
        => parser.Parse(input).Should().Be(ApprovalDecision.Yes);

    [Theory]
    [InlineData("no")]
    [InlineData("nope")]
    [InlineData("cancel")]
    [InlineData("cancela")]
    public void Recognises_negative(string input)
        => parser.Parse(input).Should().Be(ApprovalDecision.No);

    [Theory]
    [InlineData("yes please cancel that")]
    [InlineData("uhh")]
    [InlineData("")]
    public void Returns_ambiguous_when_unclear(string input)
        => parser.Parse(input).Should().Be(ApprovalDecision.Ambiguous);
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ApprovalGrammarParserTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// McpChannelVoice/Services/ApprovalGrammarParser.cs
namespace McpChannelVoice.Services;

public enum ApprovalDecision { Yes, No, Ambiguous }

public class ApprovalGrammarParser
{
    private static readonly HashSet<string> YesWords = new(StringComparer.OrdinalIgnoreCase)
    { "yes", "yeah", "yep", "yup", "ok", "okay", "confirm", "confirmed", "go", "sí", "si", "claro", "vale", "adelante" };
    private static readonly HashSet<string> NoWords = new(StringComparer.OrdinalIgnoreCase)
    { "no", "nope", "nah", "cancel", "cancela", "para", "stop", "decline" };

    public ApprovalDecision Parse(string utterance)
    {
        if (string.IsNullOrWhiteSpace(utterance)) return ApprovalDecision.Ambiguous;
        var tokens = utterance.ToLowerInvariant().Split(new[] { ' ', '.', ',', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        var hasYes = tokens.Any(t => YesWords.Contains(t));
        var hasNo = tokens.Any(t => NoWords.Contains(t));
        if (hasYes && !hasNo) return ApprovalDecision.Yes;
        if (hasNo && !hasYes) return ApprovalDecision.No;
        return ApprovalDecision.Ambiguous;
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ApprovalGrammarParserTests"`
Expected: all passing.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/ApprovalGrammarParser.cs Tests/Unit/McpChannelVoice/Services/ApprovalGrammarParserTests.cs
git commit -m "feat(voice): approval grammar parser (EN+ES)"
```

---

### Task 5.2: IVoiceApprovalSession + AskAsync on SatelliteSession

**Files:**
- Create: `McpChannelVoice/Services/IVoiceApprovalSession.cs`
- Modify: `McpChannelVoice/Services/SatelliteSession.cs`
- Test: extend `Tests/Unit/McpChannelVoice/Services/SatelliteSessionTests.cs`

- [ ] **Step 1: Write a failing test**

Append to `SatelliteSessionTests`:

```csharp
[Fact]
public async Task AskAsync_speaks_prompt_and_returns_next_transcript()
{
    // Set up session as in earlier tests, with a fake TTS that yields a tiny chunk
    // and a fake STT returning "yes" for the next capture.
    // Call AskAsync("approve?", ct) — assert it returns "yes" and that an audio-stop
    // was written to the satellite stream before STT was invoked.
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionTests.AskAsync"`
Expected: FAIL.

- [ ] **Step 3: Implement**

Add interface:

```csharp
// McpChannelVoice/Services/IVoiceApprovalSession.cs
namespace McpChannelVoice.Services;

public interface IVoiceApprovalSession
{
    Task<string> AskAsync(string prompt, CancellationToken ct);
}
```

Make `SatelliteSession` implement it. The implementation:
1. Awaits `EnqueueReplyAsync(prompt, ct)` so the prompt has been played.
2. Stores a `TaskCompletionSource<string>` in `approvalCapture`.
3. In `HandleUtteranceAsync`, after STT returns, if `approvalCapture is { } cap`, complete it with `result.Text` and skip the normal dispatch.
4. Returns the resulting text or empty on cancel/timeout (`Task.WhenAny` with `Task.Delay(TimeSpan.FromSeconds(8))`).

```csharp
private TaskCompletionSource<string>? approvalCapture;

public async Task<string> AskAsync(string prompt, CancellationToken ct)
{
    await EnqueueReplyAsync(prompt, ct);
    var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    approvalCapture = tcs;
    using var reg = ct.Register(() => tcs.TrySetCanceled());
    var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(8), ct));
    approvalCapture = null;
    return done == tcs.Task ? await tcs.Task : "";
}
```

In `HandleUtteranceAsync` after transcript:

```csharp
if (approvalCapture is { } cap) { cap.TrySetResult(result.Text); return; }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionTests"`
Expected: all passing.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services Tests/Unit/McpChannelVoice/Services/SatelliteSessionTests.cs
git commit -m "feat(voice): AskAsync one-shot capture for approvals"
```

---

### Task 5.3: RequestApprovalTool full implementation

**Files:**
- Modify: `McpChannelVoice/McpTools/RequestApprovalTool.cs`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (register parser)
- Test: `Tests/Unit/McpChannelVoice/McpTools/RequestApprovalToolTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/McpChannelVoice/McpTools/RequestApprovalToolTests.cs
namespace Tests.Unit.McpChannelVoice.McpTools;

using global::McpChannelVoice.McpTools;
using global::McpChannelVoice.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

public class RequestApprovalToolTests
{
    [Fact]
    public async Task Yes_on_first_attempt_returns_approved()
    {
        var approval = Substitute.For<IVoiceApprovalSession>();
        approval.AskAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("yes");
        RequestApprovalTool.ApprovalLookup = _ => approval;

        var resp = await RequestApprovalTool.McpRun(
            "kitchen-01", mode: "Request", requests: "[{}]",
            new ApprovalGrammarParser(),
            new VoiceMetricsPublisher(Substitute.For<Domain.Contracts.IMetricsPublisher>()),
            NullLogger<RequestApprovalToolMarker>.Instance);
        System.Text.Json.JsonSerializer.Serialize(resp).Should().Contain("\"approved\":true");
    }

    [Fact]
    public async Task Ambiguous_then_no_declines_after_one_reprompt()
    {
        var approval = Substitute.For<IVoiceApprovalSession>();
        approval.AskAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("uhh", "no");
        RequestApprovalTool.ApprovalLookup = _ => approval;

        var resp = await RequestApprovalTool.McpRun(
            "kitchen-01", mode: "Request", requests: "[{}]",
            new ApprovalGrammarParser(),
            new VoiceMetricsPublisher(Substitute.For<Domain.Contracts.IMetricsPublisher>()),
            NullLogger<RequestApprovalToolMarker>.Instance);
        System.Text.Json.JsonSerializer.Serialize(resp).Should().Contain("\"approved\":false");
        await approval.Received(2).AskAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RequestApprovalToolTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// McpChannelVoice/McpTools/RequestApprovalTool.cs
namespace McpChannelVoice.McpTools;

using System.ComponentModel;
using Domain.DTOs.Metrics.Enums;
using McpChannelVoice.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

[McpServerToolType]
public static class RequestApprovalTool
{
    public static Func<string, IVoiceApprovalSession?> ApprovalLookup { get; set; } =
        sid => SatelliteSession.Find(sid) as IVoiceApprovalSession;

    [McpServerTool(Name = "request_approval")]
    [Description("Ask the user (voice) to approve a tool call.")]
    public static async Task<object?> McpRun(
        string conversationId,
        string mode,
        string requests,
        ApprovalGrammarParser parser,
        VoiceMetricsPublisher metrics,
        ILogger<RequestApprovalToolMarker> logger)
    {
        var session = ApprovalLookup(conversationId);
        if (session is null) return new { approved = false, reason = "session-missing" };

        var first = await session.AskAsync("Approve this action?", CancellationToken.None);
        var decision = parser.Parse(first);
        if (decision == ApprovalDecision.Ambiguous)
        {
            var second = await session.AskAsync("I didn't catch that. Yes or no?", CancellationToken.None);
            decision = parser.Parse(second);
        }
        await metrics.PublishAsync(VoiceMetric.ApprovalResolved, 1, new Dictionary<VoiceDimension, string>
        {
            [VoiceDimension.SatelliteId] = conversationId,
            [VoiceDimension.Outcome] = decision.ToString()
        }, ct: CancellationToken.None);
        return new { approved = decision == ApprovalDecision.Yes, reason = decision.ToString() };
    }
}

public sealed class RequestApprovalToolMarker;
```

Register the parser in `ConfigModule.ConfigureChannel`:

```csharp
services.AddSingleton<ApprovalGrammarParser>();
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RequestApprovalToolTests"`
Expected: all passing.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice Tests/Unit/McpChannelVoice/McpTools/RequestApprovalToolTests.cs
git commit -m "feat(voice): request_approval with re-prompt + metric"
```

---

### Task 5.4: Button-press fallback

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs` (handle `button-press` Wyoming event)
- Test: `Tests/Unit/McpChannelVoice/Services/SatelliteSessionButtonTests.cs`

> Single-press → "yes" (when an approval capture is active), double-press → "no".

- [ ] **Step 1: Write failing test**

```csharp
// Tests/Unit/McpChannelVoice/Services/SatelliteSessionButtonTests.cs
namespace Tests.Unit.McpChannelVoice.Services;

using FluentAssertions;
using Xunit;

public class SatelliteSessionButtonTests
{
    [Fact]
    public async Task Single_button_press_resolves_yes_during_approval()
    {
        // Build session against a fake TcpListener, start RunAsync.
        // Trigger approvalCapture via reflection or by calling AskAsync (after mocking TTS).
        // From the fake client, write a button-press event with double=false.
        // Assert AskAsync returns "yes".
    }
}
```

(Fully realise this using the TcpListener pattern from `SatelliteSessionPlaybackTests` and an `NSubstitute` `ITextToSpeech`.)

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionButtonTests"`
Expected: FAIL.

- [ ] **Step 3: Handle the event in RunAsync's main switch**

```csharp
case "button-press":
    if (approvalCapture is { } cap)
    {
        var dbl = first.Data.RootElement.TryGetProperty("double", out var d) && d.GetBoolean();
        cap.TrySetResult(dbl ? "no" : "yes");
    }
    break;
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionButtonTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSession.cs Tests/Unit/McpChannelVoice/Services/SatelliteSessionButtonTests.cs
git commit -m "feat(voice): button-press fallback for approvals"
```

---

### Task 5.5: Approval outcomes chart

**Files:**
- Modify: `Dashboard.Client/Pages/Voice.razor`

- [ ] **Step 1: Add chart**

```razor
<DynamicChart Title="Approval outcomes"
              Group="@VoiceDimension.Outcome"
              Metric="@VoiceMetric.ApprovalResolved"
              Loader="LoadGrouped" />
```

- [ ] **Step 2: Build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: success.

- [ ] **Step 3: Commit**

```bash
git add Dashboard.Client/Pages/Voice.razor
git commit -m "feat(voice): approval outcomes chart"
```

---

# Slice 6 — Cloud STT/TTS adapters

End state: switching `Voice.Stt.Provider` (and `Voice.Tts.Provider`) to `OpenAi` works without code changes elsewhere.

### Task 6.1: OpenAiSpeechToText

**Files:**
- Create: `Infrastructure/Clients/Voice/OpenAiSpeechToText.cs`
- Test: `Tests/Unit/McpChannelVoice/Infrastructure/OpenAiSpeechToTextTests.cs`

- [ ] **Step 1: Write failing test (stubbed `HttpMessageHandler`)**

```csharp
// Tests/Unit/McpChannelVoice/Infrastructure/OpenAiSpeechToTextTests.cs
namespace Tests.Unit.McpChannelVoice.Infrastructure;

using System.Net;
using System.Net.Http;
using global::Domain.DTOs.Voice;
using global::Infrastructure.Clients.Voice;
using FluentAssertions;
using Xunit;

public class OpenAiSpeechToTextTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c)
        { Last = r; return Task.FromResult(response); }
    }

    [Fact]
    public async Task Posts_multipart_to_openai_and_returns_text()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"text":"hola","language":"es"}""", System.Text.Encoding.UTF8, "application/json")
        };
        var stub = new StubHandler(resp);
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.openai.com/") };
        var stt = new OpenAiSpeechToText(http, "whisper-1", "k");

        async IAsyncEnumerable<AudioChunk> Frames()
        {
            yield return new AudioChunk(new byte[3200], 16000, 1, 16);
            await Task.CompletedTask;
        }
        var result = await stt.TranscribeAsync(Frames(), new TranscriptionOptions("es"), CancellationToken.None);
        result.Text.Should().Be("hola");
        stub.Last!.RequestUri!.AbsolutePath.Should().Be("/v1/audio/transcriptions");
        stub.Last.Headers.Authorization!.Parameter.Should().Be("k");
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiSpeechToTextTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// Infrastructure/Clients/Voice/OpenAiSpeechToText.cs
namespace Infrastructure.Clients.Voice;

using System.Net.Http.Headers;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;

public class OpenAiSpeechToText(HttpClient http, string model, string apiKey) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        var wav = await ToWavAsync(audio, ct);
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wav);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(fileContent, "file", "utterance.wav");
        form.Add(new StringContent(options.Model ?? model), "model");
        if (options.Language is { } lang) form.Add(new StringContent(lang), "language");
        form.Add(new StringContent("verbose_json"), "response_format");

        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/transcriptions") { Content = form };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var text = doc.RootElement.GetProperty("text").GetString() ?? "";
        var lang2 = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() : null;
        return new TranscriptionResult(text, lang2, Confidence: 1.0);
    }

    private static async Task<byte[]> ToWavAsync(IAsyncEnumerable<AudioChunk> chunks, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[44], 0, 44);
        var sampleRate = 16000; var channels = 1; var bits = 16;
        await foreach (var c in chunks.WithCancellation(ct))
        {
            sampleRate = c.SampleRate; channels = c.Channels; bits = c.BitsPerSample;
            await ms.WriteAsync(c.Data, ct);
        }
        WriteWavHeader(ms, sampleRate, channels, bits);
        return ms.ToArray();
    }

    private static void WriteWavHeader(MemoryStream ms, int sampleRate, int channels, int bits)
    {
        var dataLen = (int)ms.Length - 44;
        var bw = new BinaryWriter(new MemoryStream(ms.GetBuffer(), 0, 44, writable: true));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataLen);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * bits / 8);
        bw.Write((short)(channels * bits / 8));
        bw.Write((short)bits);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataLen);
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiSpeechToTextTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Voice/OpenAiSpeechToText.cs Tests/Unit/McpChannelVoice/Infrastructure/OpenAiSpeechToTextTests.cs
git commit -m "feat(voice): OpenAI STT adapter"
```

---

### Task 6.2: OpenAiTextToSpeech

**Files:**
- Create: `Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs`
- Test: `Tests/Unit/McpChannelVoice/Infrastructure/OpenAiTextToSpeechTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// Tests/Unit/McpChannelVoice/Infrastructure/OpenAiTextToSpeechTests.cs
namespace Tests.Unit.McpChannelVoice.Infrastructure;

using System.Net;
using System.Net.Http;
using global::Domain.DTOs.Voice;
using global::Infrastructure.Clients.Voice;
using FluentAssertions;
using Xunit;

public class OpenAiTextToSpeechTests
{
    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken c) => Task.FromResult(response);
    }

    [Fact]
    public async Task Streams_pcm_response_as_chunks()
    {
        var pcm = new byte[6400];
        new Random(42).NextBytes(pcm);
        var resp = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pcm) };
        var http = new HttpClient(new StubHandler(resp)) { BaseAddress = new Uri("https://api.openai.com/") };
        var tts = new OpenAiTextToSpeech(http, "tts-1", "alloy", "k");
        var got = new List<byte>();
        await foreach (var c in tts.SynthesizeAsync("hello", new SynthesisOptions(), CancellationToken.None))
            got.AddRange(c.Data);
        got.Should().NotBeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiTextToSpeechTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs
namespace Infrastructure.Clients.Voice;

using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;

public class OpenAiTextToSpeech(HttpClient http, string model, string voice, string apiKey) : ITextToSpeech
{
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model,
            input = text,
            voice = options.Voice ?? voice,
            response_format = "pcm"
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/speech")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        var buf = new byte[4096];
        while (true)
        {
            var n = await s.ReadAsync(buf, ct);
            if (n == 0) break;
            yield return new AudioChunk(buf.AsMemory(0, n).ToArray(), 24000, 1, 16);
        }
    }
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiTextToSpeechTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs Tests/Unit/McpChannelVoice/Infrastructure/OpenAiTextToSpeechTests.cs
git commit -m "feat(voice): OpenAI TTS adapter"
```

---

### Task 6.3: VoiceModule — provider switch

**Files:**
- Create: `McpChannelVoice/Modules/VoiceModule.cs`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (delegate STT/TTS registration)
- Test: `Tests/Unit/McpChannelVoice/Modules/VoiceModuleTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// Tests/Unit/McpChannelVoice/Modules/VoiceModuleTests.cs
namespace Tests.Unit.McpChannelVoice.Modules;

using global::Domain.Contracts;
using global::Infrastructure.Clients.Voice;
using global::McpChannelVoice.Modules;
using global::McpChannelVoice.Settings;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

public class VoiceModuleTests
{
    [Fact]
    public void Wyoming_provider_resolves_WyomingSpeechToText()
    {
        var services = new ServiceCollection();
        services.AddHttpClient();
        var settings = new VoiceSettings
        {
            Stt = new SttSettings { Provider = "Wyoming", Wyoming = new WyomingBackendSettings { Host = "h", Port = 1, Model = "base" } },
            Tts = new TtsSettings { Provider = "Wyoming", Wyoming = new WyomingBackendSettings { Host = "h", Port = 2, Voice = "v" } }
        };
        VoiceModule.RegisterAudio(services, settings);
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISpeechToText>().Should().BeOfType<WyomingSpeechToText>();
        sp.GetRequiredService<ITextToSpeech>().Should().BeOfType<WyomingTextToSpeech>();
    }

    [Fact]
    public void OpenAi_provider_resolves_OpenAi_adapters()
    {
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "k");
        var services = new ServiceCollection();
        services.AddHttpClient();
        var settings = new VoiceSettings
        {
            Stt = new SttSettings { Provider = "OpenAi", OpenAi = new OpenAiBackendSettings { Model = "whisper-1" } },
            Tts = new TtsSettings { Provider = "OpenAi", OpenAi = new OpenAiBackendSettings { Model = "tts-1", Voice = "alloy" } }
        };
        VoiceModule.RegisterAudio(services, settings);
        var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ISpeechToText>().Should().BeOfType<OpenAiSpeechToText>();
        sp.GetRequiredService<ITextToSpeech>().Should().BeOfType<OpenAiTextToSpeech>();
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceModuleTests"`
Expected: FAIL.

- [ ] **Step 3: Implement**

```csharp
// McpChannelVoice/Modules/VoiceModule.cs
namespace McpChannelVoice.Modules;

using Domain.Contracts;
using Infrastructure.Clients.Voice;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;

public static class VoiceModule
{
    public static void RegisterAudio(IServiceCollection services, VoiceSettings settings)
    {
        services.AddHttpClient("openai", c => c.BaseAddress = new Uri("https://api.openai.com/"));

        services.AddSingleton<ISpeechToText>(sp => settings.Stt.Provider switch
        {
            "OpenAi" => new OpenAiSpeechToText(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai"),
                settings.Stt.OpenAi!.Model,
                Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY missing")),
            _ => new WyomingSpeechToText(
                settings.Stt.Wyoming!.Host,
                settings.Stt.Wyoming!.Port,
                settings.Stt.Wyoming!.Model)
        });

        services.AddSingleton<ITextToSpeech>(sp => settings.Tts.Provider switch
        {
            "OpenAi" => new OpenAiTextToSpeech(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai"),
                settings.Tts.OpenAi!.Model,
                settings.Tts.OpenAi!.Voice ?? "alloy",
                Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY missing")),
            _ => new WyomingTextToSpeech(
                settings.Tts.Wyoming!.Host,
                settings.Tts.Wyoming!.Port,
                settings.Tts.Wyoming!.Voice)
        });
    }
}
```

In `ConfigModule.ConfigureChannel`, replace the explicit STT/TTS registrations with:

```csharp
VoiceModule.RegisterAudio(services, settings);
```

- [ ] **Step 4: Run tests**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceModuleTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice Tests/Unit/McpChannelVoice/Modules
git commit -m "feat(voice): provider switch for STT/TTS"
```

---

### Task 6.4: Tag cloud cost events with Origin=voice

**Files:**
- Modify: `Domain/DTOs/Metrics/TokenUsageEvent.cs` (add `Origin` property if absent)
- Modify: `Infrastructure/Clients/Voice/OpenAiSpeechToText.cs` and `OpenAiTextToSpeech.cs` (publish `TokenUsageEvent` with `Origin="voice"`)

> Open `Domain/DTOs/Metrics/TokenUsageEvent.cs` first. If it has no `Origin` property, add one (`string? Origin { get; init; }`) and verify `Tokens.razor` keeps working (it should — its grouping is dimension-based).

- [ ] **Step 1: Add `Origin` to TokenUsageEvent (if missing)**

```csharp
// Domain/DTOs/Metrics/TokenUsageEvent.cs
public record TokenUsageEvent : MetricEvent
{
    // existing properties …
    public string? Origin { get; init; }
}
```

- [ ] **Step 2: Inject `IMetricsPublisher` and publish on success**

Update the OpenAI adapter constructors to accept `IMetricsPublisher metrics`, then after parsing a successful response, publish:

```csharp
await metrics.PublishAsync(new TokenUsageEvent
{
    Timestamp = DateTimeOffset.UtcNow,
    Model = model,
    InputTokens = 0,   // OpenAI Whisper doesn't report tokens; leave 0 or compute audio seconds
    OutputTokens = 0,
    Origin = "voice"
}, ct);
```

(Adapt fields to the actual `TokenUsageEvent` shape after reading the file.)

Update `VoiceModule.RegisterAudio` to pass the `IMetricsPublisher` from DI into the adapter constructors.

- [ ] **Step 3: Build**

Run: `dotnet build`
Expected: success.

- [ ] **Step 4: Commit**

```bash
git add Domain Infrastructure McpChannelVoice
git commit -m "feat(voice): tag cloud STT/TTS cost with Origin=voice"
```

---

### Task 6.5: End-to-end provider switch verification

**Files:**
- (manual only)

- [ ] **Step 1: Temporarily switch to OpenAI in dev settings**

Edit `McpChannelVoice/appsettings.Development.json`:

```json
{ "Voice": { "Stt": { "Provider": "OpenAi" }, "Tts": { "Provider": "OpenAi" } } }
```

- [ ] **Step 2: Restart and exercise**

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build mcp-channel-voice
```

Speak into a satellite or replay a WAV via the fake-client harness. Confirm transcript still flows and reply is spoken back.

- [ ] **Step 3: Revert dev settings**

```bash
git checkout McpChannelVoice/appsettings.Development.json
```

---

# Satellite provisioning script

### Task 7.1: provision-satellite.sh

**Files:**
- Create: `scripts/provision-satellite.sh`

- [ ] **Step 1: Write the script**

```bash
#!/usr/bin/env bash
# scripts/provision-satellite.sh
# One-shot satellite provisioning for Raspberry Pi Zero 2 W.
# Usage: sudo ./provision-satellite.sh <satellite-id> <hub-host> <wake-word> [mic-device] [button-gpio]
set -euo pipefail

SATELLITE_ID="${1:?satellite-id required}"
HUB_HOST="${2:?hub-host required}"
WAKE_WORD="${3:-hey_jarvis}"
MIC_DEVICE="${4:-plughw:CARD=seeed2micvoicec,DEV=0}"

apt-get update
apt-get install -y python3-pip python3-venv python3-spidev libportaudio2 alsa-utils pipx
sudo -u pi pipx install wyoming-satellite
sudo -u pi pipx install wyoming-openwakeword

cat > /etc/systemd/system/wyoming-openwakeword.service <<EOF
[Unit]
Description=openWakeWord ($SATELLITE_ID)
After=network-online.target

[Service]
User=pi
ExecStart=/home/pi/.local/bin/wyoming-openwakeword --uri tcp://0.0.0.0:10400 --preload-model $WAKE_WORD
Restart=always

[Install]
WantedBy=multi-user.target
EOF

cat > /etc/systemd/system/wyoming-satellite.service <<EOF
[Unit]
Description=Wyoming Satellite ($SATELLITE_ID)
After=network-online.target wyoming-openwakeword.service
Wants=network-online.target

[Service]
User=pi
ExecStart=/home/pi/.local/bin/wyoming-satellite \\
  --uri tcp://0.0.0.0:10700 \\
  --name $SATELLITE_ID \\
  --mic-command "arecord -r 16000 -c 1 -f S16_LE -D $MIC_DEVICE -t raw" \\
  --snd-command "aplay -r 22050 -c 1 -f S16_LE -t raw" \\
  --wake-uri tcp://127.0.0.1:10400 \\
  --wake-word-name $WAKE_WORD \\
  --event-uri tcp://$HUB_HOST:10700
Restart=always

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now wyoming-openwakeword.service wyoming-satellite.service
echo "Provisioned $SATELLITE_ID pointing at hub $HUB_HOST."
```

- [ ] **Step 2: Make executable**

```bash
chmod +x scripts/provision-satellite.sh
```

- [ ] **Step 3: Commit**

```bash
git add scripts/provision-satellite.sh
git commit -m "chore(voice): satellite provisioning script"
```

---

# Final verification

- [ ] All unit tests pass: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~McpChannelVoice"`
- [ ] Full stack boots: `docker compose -p jackbot up -d --build` (with the OS-specific override file).
- [ ] Agent log mentions the voice channel is connected.
- [ ] `curl` to `/api/voice/announce` with the dev token returns `202`/`offline` (no satellites yet).
- [ ] Dashboard `/voice` page renders without errors and shows the charts.
- [ ] Provision one Pi Zero 2 W via `scripts/provision-satellite.sh`, say the wake word, speak; agent receives the transcript and replies aloud.

---

# Spec coverage notes

- §Goal — Slices 1–3 (round-trip), Slice 4 (announce ownership).
- §Constraints — Pi/wake-word/identity/STT/TTS pluggability all covered.
- §Architecture diagram — implemented across Slices 1–4.
- §Components — every new file in the spec's "New components" table appears in this plan.
- §Data flow / utterance round-trip — Slices 2, 3.
- §Approval flow — Slice 5.
- §External announcement — Slice 4 (auth, priority, queue, HA reference). `Voice.Announce.BindToLoopbackOnly` is supported via the settings record; when enabled, set `ASPNETCORE_URLS=http://127.0.0.1:5010` on the channel container (config-only, no code task).
- §Configuration — `appsettings.json`, `.env`, `docker-compose.yml`, `appsettings.Development.json` updated together (Tasks 1.2, 1.8, 2.7, 3.4).
- §Identity and threading — `SatelliteRegistry` + emitter dispatch identity as `sender`, room in metadata.
- §Observability — Voice metrics emitted; dashboard page + Overview KPI cards; cloud cost tagged with `Origin=voice` (Task 6.4).
- §Dashboard changes — Voice.razor (Task 2.9), Overview KPI (Task 3.5), nav entry (Task 2.9), Tokens.razor inherits the new `Origin` dimension automatically (Task 6.4). Errors.razor and HealthGrid.razor already aggregate by event-type/source; verify during final verification.
- §Error handling — confidence gate (Task 2.4), STT error metric (Task 2.5), preempt metrics (Task 4.3), 401/503 (Task 4.5).
- §Testing — unit tests under each task, integration scaffolds in Tasks 2.8 and 3.6, manual E2E in Final Verification.
- §Out of scope — respected throughout.
- §Style and layering — primary constructors, records, file-scoped namespaces used everywhere; Domain has no references to Infrastructure or McpChannelVoice (Wyoming protocol lives under `Infrastructure/Clients/Voice/Wyoming/` if Task 2.3's circular-reference note triggers); new env vars added to all four infra files in the same task.




