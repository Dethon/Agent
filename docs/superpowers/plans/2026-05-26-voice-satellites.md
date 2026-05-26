# Voice Satellites Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an Alexa-like voice interface (wake-word → STT → agent → TTS) via a new `McpChannelVoice` server that bridges Wyoming-protocol Pi Zero 2 W satellites to the existing MCP channel pipeline, with a push-to-speaker HTTP announce endpoint for Home Assistant.

**Architecture:** Stock satellite stack (`wyoming-satellite` + openWakeWord on Pi Zero) streams audio over the Wyoming protocol to a new `McpChannelVoice` .NET service. The channel forwards audio to `wyoming-faster-whisper` (pluggable behind `ISpeechToText`), dispatches the transcript to the agent as a standard `channel/message` notification, then synthesises the agent reply via `wyoming-piper` (pluggable behind `ITextToSpeech`) and streams it back to the originating satellite. A side `POST /api/voice/announce` endpoint reuses the same TTS + playback path so Home Assistant can push announcements without fighting the agent for audio ownership.

**Tech Stack:** .NET 10 (ASP.NET Core), MCP HTTP transport (`ModelContextProtocol.AspNetCore`), Wyoming protocol (custom minimal client/server over TCP), `wyoming-faster-whisper` / `wyoming-piper` (Rhasspy Docker images), `wyoming-satellite` + `wyoming-openwakeword` (Python, on the Pi via `pipx`), `IMetricsPublisher` → Redis Pub/Sub for observability, Blazor WebAssembly dashboard for the new Voice page.

**Slices** (each ends in a green commit):
1. `McpChannelVoice` skeleton — project, MCP transport, dummy tools, registry, heartbeat.
2. STT path — Wyoming server + client, `ISpeechToText`, satellite → agent transcripts.
3. TTS path — `ITextToSpeech`, `send_reply` round-trip, Voice dashboard page.
4. Announce endpoint — `POST /api/voice/announce`, priority queue, HA reference snippet.
5. Approval over voice — grammar parser, re-prompt, button fallback.
6. Cloud STT/TTS adapters — OpenAI provider, configuration switch.

---

## File Structure

### New project — `McpChannelVoice/`

| File | Responsibility |
|------|----------------|
| `McpChannelVoice.csproj` | .NET 10 Web SDK, references `Domain`, `Infrastructure` |
| `Program.cs` | Bootstrap — settings, DI, `MapMcp("/mcp")`, announce endpoint mapping |
| `Dockerfile` | Multi-stage build matching the Telegram/SignalR pattern |
| `appsettings.json` | `Voice` section skeleton with placeholders |
| `appsettings.Development.json` | Development overrides |
| `McpTools/SendReplyTool.cs` | `send_reply` MCP tool — routes content to `SatelliteSession` |
| `McpTools/RequestApprovalTool.cs` | `request_approval` MCP tool — speaks prompt, awaits voice confirmation |
| `McpTools/RegisterAgentsTool.cs` | `register_agents` — populates `MutableAgentCatalog` |
| `Modules/ConfigModule.cs` | Settings load, DI wiring, MCP server configuration |
| `Services/WyomingProtocol/WyomingEvent.cs` | One JSON-line + optional payload record |
| `Services/WyomingProtocol/WyomingReader.cs` | Parses framed events off a `Stream` |
| `Services/WyomingProtocol/WyomingWriter.cs` | Writes framed events to a `Stream` (thread-safe) |
| `Services/WyomingProtocol/WyomingClient.cs` | TCP client wrapper used to talk to Whisper/Piper |
| `Services/WyomingServer.cs` | TCP listener; accepts satellite connections, parses `info` → spawns `SatelliteSession` |
| `Services/SatelliteSession.cs` | One wake-to-reply session, owns playback queue + cancellation |
| `Services/SatelliteSessionRegistry.cs` | `conversationId` → live `SatelliteSession` lookup |
| `Services/SatelliteRegistry.cs` | Config-backed `id → identity/room/wakeWord/overrides` lookups (incl. reverse-by-room + all) |
| `Services/ApprovalGrammarParser.cs` | yes/no/sí/no/cancel/confirm/ok parsing — pure function |
| `Services/ChannelNotificationEmitter.cs` | Holds active MCP sessions; emits `notifications/channel/message` |
| `Services/AnnouncementService.cs` | Resolves target → synthesises → enqueues per satellite |
| `Services/AnnounceEndpoint.cs` | Maps `POST /api/voice/announce`, token auth filter |
| `Services/VoiceHeartbeatHostedService.cs` | Wraps `HeartbeatService` for the channel itself + downstream Wyoming probes |
| `Settings/VoiceSettings.cs` | Root settings record |
| `Settings/WyomingServerSettings.cs` | Wyoming inbound TCP config |
| `Settings/SttSettings.cs` | STT provider + per-provider config |
| `Settings/TtsSettings.cs` | TTS provider + per-provider config |
| `Settings/AnnounceSettings.cs` | Announce endpoint config (token, bind, queue depth, default priority) |
| `Settings/SatelliteConfig.cs` | Per-satellite mapping record |

### New files in `Domain/`

| File | Responsibility |
|------|----------------|
| `Domain/Contracts/ISpeechToText.cs` | STT contract |
| `Domain/Contracts/ITextToSpeech.cs` | TTS contract |
| `Domain/DTOs/Voice/AudioChunk.cs` | PCM payload + format metadata |
| `Domain/DTOs/Voice/AudioFormat.cs` | Sample rate / width / channels |
| `Domain/DTOs/Voice/TranscriptionOptions.cs` | Language, model hint, timeout |
| `Domain/DTOs/Voice/TranscriptionResult.cs` | Text + language + confidence |
| `Domain/DTOs/Voice/SynthesisOptions.cs` | Voice id + format override |
| `Domain/DTOs/Voice/AnnouncePriority.cs` | `Low` / `Normal` / `High` |
| `Domain/DTOs/Voice/AnnounceTarget.cs` | One of `SatelliteId` / `Room` / `All` |
| `Domain/DTOs/Voice/AnnounceRequest.cs` | Endpoint request DTO |
| `Domain/DTOs/Voice/AnnounceResponse.cs` | Endpoint response DTO |
| `Domain/DTOs/Voice/AnnouncementOutcome.cs` | Per-satellite outcome row |
| `Domain/DTOs/Metrics/Enums/VoiceDimension.cs` | `SatelliteId, Room, Identity, WakeWord, Language, SttProvider, SttModel, TtsProvider, TtsVoice, Outcome, Source, Priority` |
| `Domain/DTOs/Metrics/Enums/VoiceMetric.cs` | `WakeTriggered, UtteranceTranscribed, …` (full list in spec) |
| `Domain/DTOs/Metrics/VoiceEvent.cs` | `MetricEvent` derived voice event |

### New files in `Infrastructure/`

| File | Responsibility |
|------|----------------|
| `Infrastructure/Clients/Voice/WyomingSpeechToText.cs` | `ISpeechToText` via `wyoming-faster-whisper` |
| `Infrastructure/Clients/Voice/WyomingTextToSpeech.cs` | `ITextToSpeech` via `wyoming-piper` |
| `Infrastructure/Clients/Voice/OpenAiSpeechToText.cs` | Slice 6 — `audio/transcriptions` |
| `Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs` | Slice 6 — `audio/speech` |

### Tests

| File | Responsibility |
|------|----------------|
| `Tests/Unit/McpChannelVoice/SatelliteRegistryTests.cs` | id resolution + reverse lookups + overrides |
| `Tests/Unit/McpChannelVoice/SatelliteSessionRegistryTests.cs` | conversation/session bookkeeping |
| `Tests/Unit/McpChannelVoice/ApprovalGrammarParserTests.cs` | yes/no/sí/no |
| `Tests/Unit/McpChannelVoice/ConfidenceGateTests.cs` | low-confidence drops |
| `Tests/Unit/McpChannelVoice/AnnouncementServiceTests.cs` | resolution + priority + preempt |
| `Tests/Unit/McpChannelVoice/AnnounceEndpointAuthTests.cs` | token auth filter |
| `Tests/Unit/McpChannelVoice/Wyoming/WyomingReaderTests.cs` | event framing |
| `Tests/Unit/McpChannelVoice/Wyoming/WyomingWriterTests.cs` | event framing |
| `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs` | content routing |
| `Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs` | dispatch + parsing path |
| `Tests/Integration/McpChannelVoice/WyomingEndToEndTests.cs` | fake satellite → real whisper-tiny |
| `Tests/Integration/McpChannelVoice/SendReplyRoundTripTests.cs` | tool → audio back |
| `Tests/Integration/McpChannelVoice/SttProviderSwitchTests.cs` | swap provider via config |
| `Tests/Integration/McpChannelVoice/AnnounceEndToEndTests.cs` | HTTP POST → audio out |
| `Tests/Integration/Fixtures/McpChannelVoiceFixture.cs` | Shared compose + fake satellite |
| `Tests/Integration/Fixtures/FakeWyomingSatelliteClient.cs` | Test double |

### Dashboard

| File | Responsibility |
|------|----------------|
| `Dashboard.Client/Pages/Voice.razor` | New voice page (KPIs + charts) |
| `Dashboard.Client/State/Voice/VoiceState.cs` | Voice page state |
| `Dashboard.Client/State/Voice/VoiceStore.cs` | Store + actions |
| `Dashboard.Client/Pages/Overview.razor` | (modify) Add latency + utterances KPIs |
| `Dashboard.Client/Pages/Errors.razor` | (modify) Voice tab/source |
| `Dashboard.Client/Components/HealthGrid.razor` | (modify) Add voice services |
| `Observability/Services/MetricsQueryService.cs` | (modify) Voice grouping queries |
| `Observability/MetricsApiEndpoints.cs` | (modify) `/api/metrics/voice/*` endpoints |

### Infrastructure files (touched in every slice that introduces new env vars)

- `DockerCompose/docker-compose.yml` — add `mcp-channel-voice`, `wyoming-whisper`, `wyoming-piper` services.
- `DockerCompose/.env` — placeholders for `ANNOUNCE_TOKEN`, `OPENAI_API_KEY`.
- `Agent/appsettings.json` — add `mcp-channel-voice` to `channelEndpoints`.
- `scripts/provision-satellite.sh` — one-time Pi Zero provisioning script.
- `agent.sln` — register the new project.

---

## Slice 1 — `McpChannelVoice` skeleton

### Task 1.1: Add `VoiceDimension` and `VoiceMetric` enums

**Files:**
- Create: `Domain/DTOs/Metrics/Enums/VoiceDimension.cs`
- Create: `Domain/DTOs/Metrics/Enums/VoiceMetric.cs`
- Test: `Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics.Enums;

public class VoiceEnumsTests
{
    [Fact]
    public void VoiceDimension_HasExpectedMembers()
    {
        var names = Enum.GetNames<VoiceDimension>();
        names.ShouldContain(nameof(VoiceDimension.SatelliteId));
        names.ShouldContain(nameof(VoiceDimension.Room));
        names.ShouldContain(nameof(VoiceDimension.Identity));
        names.ShouldContain(nameof(VoiceDimension.WakeWord));
        names.ShouldContain(nameof(VoiceDimension.Language));
        names.ShouldContain(nameof(VoiceDimension.SttProvider));
        names.ShouldContain(nameof(VoiceDimension.SttModel));
        names.ShouldContain(nameof(VoiceDimension.TtsProvider));
        names.ShouldContain(nameof(VoiceDimension.TtsVoice));
        names.ShouldContain(nameof(VoiceDimension.Outcome));
        names.ShouldContain(nameof(VoiceDimension.Source));
        names.ShouldContain(nameof(VoiceDimension.Priority));
    }

    [Fact]
    public void VoiceMetric_HasExpectedMembers()
    {
        var names = Enum.GetNames<VoiceMetric>();
        names.ShouldContain(nameof(VoiceMetric.WakeTriggered));
        names.ShouldContain(nameof(VoiceMetric.UtteranceTranscribed));
        names.ShouldContain(nameof(VoiceMetric.AudioSeconds));
        names.ShouldContain(nameof(VoiceMetric.SttLatencyMs));
        names.ShouldContain(nameof(VoiceMetric.TtsLatencyMs));
        names.ShouldContain(nameof(VoiceMetric.WakeToFirstAudioMs));
        names.ShouldContain(nameof(VoiceMetric.ApprovalResolved));
        names.ShouldContain(nameof(VoiceMetric.SttError));
        names.ShouldContain(nameof(VoiceMetric.TtsError));
        names.ShouldContain(nameof(VoiceMetric.AnnouncePlayed));
        names.ShouldContain(nameof(VoiceMetric.AnnounceQueued));
        names.ShouldContain(nameof(VoiceMetric.AnnounceError));
        names.ShouldContain(nameof(VoiceMetric.AnnouncePreemptedReply));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests" --no-restore`
Expected: FAIL with `CS0246: The type or namespace name 'VoiceDimension' could not be found`.

- [ ] **Step 3: Create the enums**

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

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEnumsTests" --no-restore`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Metrics/Enums/VoiceDimension.cs \
        Domain/DTOs/Metrics/Enums/VoiceMetric.cs \
        Tests/Unit/Domain/DTOs/Metrics/Enums/VoiceEnumsTests.cs
git commit -m "feat(metrics): add voice dimension and metric enums"
```

---

### Task 1.2: Add `VoiceEvent` metric event

**Files:**
- Create: `Domain/DTOs/Metrics/VoiceEvent.cs`
- Modify: `Domain/DTOs/Metrics/MetricEvent.cs` (add `JsonDerivedType`)
- Test: `Tests/Unit/Domain/DTOs/Metrics/VoiceEventTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/Domain/DTOs/Metrics/VoiceEventTests.cs
using System.Text.Json;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Metrics;

public class VoiceEventTests
{
    [Fact]
    public void VoiceEvent_SerializesWithTypeDiscriminator()
    {
        MetricEvent evt = new VoiceEvent
        {
            Metric = VoiceMetric.WakeTriggered,
            SatelliteId = "kitchen-01",
            Room = "Kitchen",
            Identity = "household",
            WakeWord = "hey_jarvis"
        };

        var json = JsonSerializer.Serialize(evt);

        json.ShouldContain("\"type\":\"voice\"");
        json.ShouldContain("\"metric\":\"WakeTriggered\"");
        json.ShouldContain("\"satelliteId\":\"kitchen-01\"");
    }

    [Fact]
    public void VoiceEvent_RoundTripsThroughBaseType()
    {
        MetricEvent evt = new VoiceEvent
        {
            Metric = VoiceMetric.SttLatencyMs,
            SttProvider = "Wyoming",
            SttModel = "base",
            DurationMs = 320
        };

        var json = JsonSerializer.Serialize(evt);
        var decoded = JsonSerializer.Deserialize<MetricEvent>(json);

        decoded.ShouldBeOfType<VoiceEvent>();
        ((VoiceEvent)decoded!).Metric.ShouldBe(VoiceMetric.SttLatencyMs);
        ((VoiceEvent)decoded!).DurationMs.ShouldBe(320);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEventTests" --no-restore`
Expected: FAIL with `'VoiceEvent' could not be found`.

- [ ] **Step 3: Create the event record**

```csharp
// Domain/DTOs/Metrics/VoiceEvent.cs
using Domain.DTOs.Metrics.Enums;

namespace Domain.DTOs.Metrics;

public record VoiceEvent : MetricEvent
{
    public required VoiceMetric Metric { get; init; }
    public string? SatelliteId { get; init; }
    public string? Room { get; init; }
    public string? Identity { get; init; }
    public string? WakeWord { get; init; }
    public string? Language { get; init; }
    public string? SttProvider { get; init; }
    public string? SttModel { get; init; }
    public string? TtsProvider { get; init; }
    public string? TtsVoice { get; init; }
    public string? Outcome { get; init; }
    public string? Source { get; init; }
    public string? Priority { get; init; }
    public long? DurationMs { get; init; }
    public double? AudioSeconds { get; init; }
    public double? Confidence { get; init; }
    public string? Error { get; init; }
}
```

- [ ] **Step 4: Wire the polymorphism**

Edit `Domain/DTOs/Metrics/MetricEvent.cs`. Add this line beneath the existing `[JsonDerivedType(typeof(LatencyEvent), "latency")]`:

```csharp
[JsonDerivedType(typeof(VoiceEvent), "voice")]
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceEventTests" --no-restore`
Expected: 2 passed.

- [ ] **Step 6: Commit**

```bash
git add Domain/DTOs/Metrics/VoiceEvent.cs \
        Domain/DTOs/Metrics/MetricEvent.cs \
        Tests/Unit/Domain/DTOs/Metrics/VoiceEventTests.cs
git commit -m "feat(metrics): add VoiceEvent metric type"
```

---

### Task 1.3: Define voice DTOs (`AudioChunk`, `AudioFormat`, options, results)

**Files:**
- Create: `Domain/DTOs/Voice/AudioFormat.cs`
- Create: `Domain/DTOs/Voice/AudioChunk.cs`
- Create: `Domain/DTOs/Voice/TranscriptionOptions.cs`
- Create: `Domain/DTOs/Voice/TranscriptionResult.cs`
- Create: `Domain/DTOs/Voice/SynthesisOptions.cs`
- Test: `Tests/Unit/Domain/DTOs/Voice/VoiceDtoTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/Domain/DTOs/Voice/VoiceDtoTests.cs
using Domain.DTOs.Voice;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Voice;

public class VoiceDtoTests
{
    [Fact]
    public void AudioFormat_DefaultsToWyomingStandard()
    {
        var fmt = AudioFormat.WyomingStandard;
        fmt.SampleRateHz.ShouldBe(16_000);
        fmt.SampleWidthBytes.ShouldBe(2);
        fmt.Channels.ShouldBe(1);
    }

    [Fact]
    public void AudioChunk_Constructs()
    {
        var chunk = new AudioChunk
        {
            Data = new byte[] { 0, 1, 2, 3 },
            Format = AudioFormat.WyomingStandard,
            Timestamp = TimeSpan.FromMilliseconds(100)
        };
        chunk.Data.Length.ShouldBe(4);
        chunk.Format.SampleRateHz.ShouldBe(16_000);
    }

    [Fact]
    public void TranscriptionResult_Constructs()
    {
        var result = new TranscriptionResult
        {
            Text = "hello world",
            Language = "en",
            Confidence = 0.92
        };
        result.Text.ShouldBe("hello world");
        result.Confidence.ShouldBe(0.92);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceDtoTests" --no-restore`
Expected: FAIL — types missing.

- [ ] **Step 3: Create the DTOs**

```csharp
// Domain/DTOs/Voice/AudioFormat.cs
namespace Domain.DTOs.Voice;

public record AudioFormat
{
    public required int SampleRateHz { get; init; }
    public required int SampleWidthBytes { get; init; }
    public required int Channels { get; init; }

    public static AudioFormat WyomingStandard { get; } = new()
    {
        SampleRateHz = 16_000,
        SampleWidthBytes = 2,
        Channels = 1
    };
}
```

```csharp
// Domain/DTOs/Voice/AudioChunk.cs
namespace Domain.DTOs.Voice;

public record AudioChunk
{
    public required ReadOnlyMemory<byte> Data { get; init; }
    public required AudioFormat Format { get; init; }
    public TimeSpan Timestamp { get; init; }
}
```

```csharp
// Domain/DTOs/Voice/TranscriptionOptions.cs
namespace Domain.DTOs.Voice;

public record TranscriptionOptions
{
    public string? Language { get; init; }
    public string? ModelHint { get; init; }
    public TimeSpan? Timeout { get; init; }
}
```

```csharp
// Domain/DTOs/Voice/TranscriptionResult.cs
namespace Domain.DTOs.Voice;

public record TranscriptionResult
{
    public required string Text { get; init; }
    public string? Language { get; init; }
    public double? Confidence { get; init; }
}
```

```csharp
// Domain/DTOs/Voice/SynthesisOptions.cs
namespace Domain.DTOs.Voice;

public record SynthesisOptions
{
    public string? Voice { get; init; }
    public string? Language { get; init; }
    public AudioFormat? Format { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceDtoTests" --no-restore`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Voice/ Tests/Unit/Domain/DTOs/Voice/VoiceDtoTests.cs
git commit -m "feat(voice): add audio + transcription/synthesis DTOs"
```

---

### Task 1.4: Define `ISpeechToText` and `ITextToSpeech` contracts

**Files:**
- Create: `Domain/Contracts/ISpeechToText.cs`
- Create: `Domain/Contracts/ITextToSpeech.cs`
- Test: `Tests/Unit/Domain/Contracts/VoiceContractsTests.cs`

- [ ] **Step 1: Write the failing test (contract shape)**

```csharp
// Tests/Unit/Domain/Contracts/VoiceContractsTests.cs
using Domain.Contracts;
using Domain.DTOs.Voice;
using Shouldly;

namespace Tests.Unit.Domain.Contracts;

public class VoiceContractsTests
{
    [Fact]
    public void ISpeechToText_HasTranscribeAsync()
    {
        var method = typeof(ISpeechToText).GetMethod("TranscribeAsync");
        method.ShouldNotBeNull();
        method!.ReturnType.ShouldBe(typeof(Task<TranscriptionResult>));
    }

    [Fact]
    public void ITextToSpeech_HasSynthesizeAsync()
    {
        var method = typeof(ITextToSpeech).GetMethod("SynthesizeAsync");
        method.ShouldNotBeNull();
        method!.ReturnType.ShouldBe(typeof(IAsyncEnumerable<AudioChunk>));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceContractsTests" --no-restore`
Expected: FAIL — interfaces missing.

- [ ] **Step 3: Create the contracts**

```csharp
// Domain/Contracts/ISpeechToText.cs
using Domain.DTOs.Voice;

namespace Domain.Contracts;

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
using Domain.DTOs.Voice;

namespace Domain.Contracts;

public interface ITextToSpeech
{
    IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        CancellationToken ct);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceContractsTests" --no-restore`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Contracts/ISpeechToText.cs Domain/Contracts/ITextToSpeech.cs \
        Tests/Unit/Domain/Contracts/VoiceContractsTests.cs
git commit -m "feat(voice): add ISpeechToText and ITextToSpeech contracts"
```

---

### Task 1.5: Create the `McpChannelVoice` project skeleton

**Files:**
- Create: `McpChannelVoice/McpChannelVoice.csproj`
- Create: `McpChannelVoice/Program.cs`
- Create: `McpChannelVoice/appsettings.json`
- Create: `McpChannelVoice/appsettings.Development.json`
- Create: `McpChannelVoice/Dockerfile`
- Modify: `agent.sln` (add project)

- [ ] **Step 1: Create the csproj**

```xml
<!-- McpChannelVoice/McpChannelVoice.csproj -->
<Project Sdk="Microsoft.NET.Sdk.Web">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <LangVersion>14</LangVersion>
    <UserSecretsId>b2c5d8e1-9f44-4a72-9eaf-2c8f3d6b1a07</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="..\.dockerignore">
      <Link>.dockerignore</Link>
    </Content>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.3.0" />
    <PackageReference Include="StackExchange.Redis" Version="2.13.1" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Domain\Domain.csproj" />
    <ProjectReference Include="..\Infrastructure\Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="Tests" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
    <None Update="appsettings.Development.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create a placeholder `Program.cs`**

```csharp
// McpChannelVoice/Program.cs
var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

app.MapGet("/", () => "McpChannelVoice");

await app.RunAsync();
```

- [ ] **Step 3: Create `appsettings.json`**

```json
{
  "Voice": {
    "WyomingServer": { "Host": "0.0.0.0", "Port": 10700 },
    "Stt": {
      "Provider": "Wyoming",
      "Wyoming": { "Host": "wyoming-whisper", "Port": 10300, "Model": "base", "Language": "es" },
      "OpenAi":  { "Model": "whisper-1" }
    },
    "Tts": {
      "Provider": "Wyoming",
      "Wyoming": { "Host": "wyoming-piper", "Port": 10200, "Voice": "es_ES-davefx-medium" },
      "OpenAi":  { "Model": "tts-1", "Voice": "alloy" }
    },
    "ConfidenceThreshold": 0.4,
    "Announce": {
      "Enabled": true,
      "Token": "",
      "BindToLoopbackOnly": false,
      "QueueMaxDepth": 8,
      "DefaultPriority": "Normal"
    },
    "Satellites": {}
  },
  "Redis": {
    "ConnectionString": "redis:6379"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

- [ ] **Step 4: Create `appsettings.Development.json`**

```json
{
  "Voice": {
    "Satellites": {
      "kitchen-01":     { "Identity": "household", "Room": "Kitchen",     "WakeWord": "hey_jarvis" },
      "living-room-01": { "Identity": "household", "Room": "Living Room", "WakeWord": "hey_jarvis" },
      "bedroom-01":     { "Identity": "francisco", "Room": "Bedroom",     "WakeWord": "hey_jarvis" }
    }
  }
}
```

- [ ] **Step 5: Create the `Dockerfile`**

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpChannelVoice/McpChannelVoice.csproj", "McpChannelVoice/"]
RUN dotnet restore "McpChannelVoice/McpChannelVoice.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpChannelVoice/", "McpChannelVoice/"]
WORKDIR "/src/McpChannelVoice"
RUN dotnet publish "./McpChannelVoice.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "McpChannelVoice.dll"]
```

- [ ] **Step 6: Register the project in `agent.sln`**

Run from repo root:

```bash
dotnet sln agent.sln add McpChannelVoice/McpChannelVoice.csproj
```

- [ ] **Step 7: Verify the solution builds**

Run: `dotnet build agent.sln -c Debug --nologo /p:TreatWarningsAsErrors=false`
Expected: Build succeeds; `McpChannelVoice -> .../McpChannelVoice.dll` appears in the output.

- [ ] **Step 8: Commit**

```bash
git add McpChannelVoice/ agent.sln
git commit -m "feat(voice): scaffold McpChannelVoice project"
```

---

### Task 1.6: Voice settings records

**Files:**
- Create: `McpChannelVoice/Settings/VoiceSettings.cs`
- Create: `McpChannelVoice/Settings/WyomingServerSettings.cs`
- Create: `McpChannelVoice/Settings/SttSettings.cs`
- Create: `McpChannelVoice/Settings/TtsSettings.cs`
- Create: `McpChannelVoice/Settings/AnnounceSettings.cs`
- Create: `McpChannelVoice/Settings/SatelliteConfig.cs`
- Test: `Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs
using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceSettingsBindingTests
{
    [Fact]
    public void VoiceSettings_BindsFromJson()
    {
        var json = """
        {
          "Voice": {
            "WyomingServer": { "Host": "0.0.0.0", "Port": 10700 },
            "Stt": {
              "Provider": "Wyoming",
              "Wyoming": { "Host": "wyoming-whisper", "Port": 10300, "Model": "base", "Language": "es" }
            },
            "Tts": {
              "Provider": "Wyoming",
              "Wyoming": { "Host": "wyoming-piper", "Port": 10200, "Voice": "es_ES-davefx-medium" }
            },
            "ConfidenceThreshold": 0.4,
            "Announce": {
              "Enabled": true,
              "Token": "secret",
              "BindToLoopbackOnly": false,
              "QueueMaxDepth": 8,
              "DefaultPriority": "Normal"
            },
            "Satellites": {
              "kitchen-01": { "Identity": "household", "Room": "Kitchen", "WakeWord": "hey_jarvis" }
            }
          }
        }
        """;

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();

        var settings = config.GetSection("Voice").Get<VoiceSettings>();

        settings.ShouldNotBeNull();
        settings!.WyomingServer.Port.ShouldBe(10700);
        settings.Stt.Provider.ShouldBe("Wyoming");
        settings.Stt.Wyoming!.Model.ShouldBe("base");
        settings.Tts.Wyoming!.Voice.ShouldBe("es_ES-davefx-medium");
        settings.ConfidenceThreshold.ShouldBe(0.4);
        settings.Announce.Token.ShouldBe("secret");
        settings.Announce.DefaultPriority.ShouldBe(AnnouncePriorityDefault.Normal);
        settings.Satellites.Count.ShouldBe(1);
        settings.Satellites["kitchen-01"].Identity.ShouldBe("household");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSettingsBindingTests" --no-restore`
Expected: FAIL — settings types missing.

- [ ] **Step 3: Create the settings records**

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
    public Dictionary<string, SatelliteConfig> Satellites { get; init; } = new();
}
```

```csharp
// McpChannelVoice/Settings/WyomingServerSettings.cs
namespace McpChannelVoice.Settings;

public record WyomingServerSettings
{
    public string Host { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 10700;
}
```

```csharp
// McpChannelVoice/Settings/SttSettings.cs
namespace McpChannelVoice.Settings;

public record SttSettings
{
    public string Provider { get; init; } = "Wyoming";
    public WyomingSttConfig? Wyoming { get; init; }
    public OpenAiSttConfig? OpenAi { get; init; }
}

public record WyomingSttConfig
{
    public string Host { get; init; } = "wyoming-whisper";
    public int Port { get; init; } = 10300;
    public string? Model { get; init; }
    public string? Language { get; init; }
}

public record OpenAiSttConfig
{
    public string Model { get; init; } = "whisper-1";
}
```

```csharp
// McpChannelVoice/Settings/TtsSettings.cs
namespace McpChannelVoice.Settings;

public record TtsSettings
{
    public string Provider { get; init; } = "Wyoming";
    public WyomingTtsConfig? Wyoming { get; init; }
    public OpenAiTtsConfig? OpenAi { get; init; }
}

public record WyomingTtsConfig
{
    public string Host { get; init; } = "wyoming-piper";
    public int Port { get; init; } = 10200;
    public string? Voice { get; init; }
}

public record OpenAiTtsConfig
{
    public string Model { get; init; } = "tts-1";
    public string Voice { get; init; } = "alloy";
}
```

```csharp
// McpChannelVoice/Settings/AnnounceSettings.cs
namespace McpChannelVoice.Settings;

public record AnnounceSettings
{
    public bool Enabled { get; init; } = true;
    public string Token { get; init; } = "";
    public bool BindToLoopbackOnly { get; init; }
    public int QueueMaxDepth { get; init; } = 8;
    public AnnouncePriorityDefault DefaultPriority { get; init; } = AnnouncePriorityDefault.Normal;
}

public enum AnnouncePriorityDefault { Low, Normal, High }
```

```csharp
// McpChannelVoice/Settings/SatelliteConfig.cs
namespace McpChannelVoice.Settings;

public record SatelliteConfig
{
    public required string Identity { get; init; }
    public required string Room { get; init; }
    public string? WakeWord { get; init; }
    public SttSettings? Stt { get; init; }
    public TtsSettings? Tts { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSettingsBindingTests" --no-restore`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Settings/ Tests/Unit/McpChannelVoice/VoiceSettingsBindingTests.cs
git commit -m "feat(voice): voice settings records"
```

---

### Task 1.7: `SatelliteRegistry` with id / room / all-satellites resolution

**Files:**
- Create: `McpChannelVoice/Services/SatelliteRegistry.cs`
- Test: `Tests/Unit/McpChannelVoice/SatelliteRegistryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/SatelliteRegistryTests.cs
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteRegistryTests
{
    private static readonly Dictionary<string, SatelliteConfig> _sample = new()
    {
        ["kitchen-01"]     = new() { Identity = "household", Room = "Kitchen",     WakeWord = "hey_jarvis" },
        ["living-room-01"] = new() { Identity = "household", Room = "Living Room", WakeWord = "hey_jarvis" },
        ["bedroom-01"]     = new() { Identity = "francisco", Room = "Bedroom",     WakeWord = "hey_jarvis" }
    };

    [Fact]
    public void GetById_KnownSatellite_ReturnsConfig()
    {
        var registry = new SatelliteRegistry(_sample);
        var sat = registry.GetById("kitchen-01");
        sat.ShouldNotBeNull();
        sat!.Identity.ShouldBe("household");
        sat.Room.ShouldBe("Kitchen");
    }

    [Fact]
    public void GetById_UnknownSatellite_ReturnsNull()
    {
        var registry = new SatelliteRegistry(_sample);
        registry.GetById("ghost-01").ShouldBeNull();
    }

    [Fact]
    public void GetIdsByRoom_MatchesCaseInsensitive()
    {
        var registry = new SatelliteRegistry(_sample);
        var ids = registry.GetIdsByRoom("kitchen");
        ids.ShouldBe(["kitchen-01"]);
    }

    [Fact]
    public void GetIdsByRoom_UnknownRoom_ReturnsEmpty()
    {
        var registry = new SatelliteRegistry(_sample);
        registry.GetIdsByRoom("Basement").ShouldBeEmpty();
    }

    [Fact]
    public void GetAllIds_ReturnsEverySatellite()
    {
        var registry = new SatelliteRegistry(_sample);
        registry.GetAllIds().ShouldBe(["kitchen-01", "living-room-01", "bedroom-01"], ignoreOrder: true);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteRegistryTests" --no-restore`
Expected: FAIL — `SatelliteRegistry` missing.

- [ ] **Step 3: Implement `SatelliteRegistry`**

```csharp
// McpChannelVoice/Services/SatelliteRegistry.cs
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class SatelliteRegistry
{
    private readonly IReadOnlyDictionary<string, SatelliteConfig> _byId;
    private readonly ILookup<string, string> _idsByRoom;

    public SatelliteRegistry(IReadOnlyDictionary<string, SatelliteConfig> satellites)
    {
        _byId = satellites;
        _idsByRoom = satellites
            .ToLookup(kv => kv.Value.Room, kv => kv.Key, StringComparer.OrdinalIgnoreCase);
    }

    public SatelliteConfig? GetById(string satelliteId) =>
        _byId.TryGetValue(satelliteId, out var cfg) ? cfg : null;

    public IReadOnlyList<string> GetIdsByRoom(string room) =>
        _idsByRoom[room].ToList();

    public IReadOnlyList<string> GetAllIds() =>
        _byId.Keys.ToList();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteRegistryTests" --no-restore`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteRegistry.cs \
        Tests/Unit/McpChannelVoice/SatelliteRegistryTests.cs
git commit -m "feat(voice): SatelliteRegistry with id/room/all lookups"
```

---

### Task 1.8: `ChannelNotificationEmitter` (clone of Telegram's, voice-specific logger)

**Files:**
- Create: `McpChannelVoice/Services/ChannelNotificationEmitter.cs`
- Test: `Tests/Unit/McpChannelVoice/ChannelNotificationEmitterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/ChannelNotificationEmitterTests.cs
using McpChannelVoice.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ChannelNotificationEmitterTests
{
    [Fact]
    public void HasActiveSessions_InitiallyFalse()
    {
        var emitter = new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance);
        emitter.HasActiveSessions.ShouldBeFalse();
    }

    [Fact]
    public void UnregisterSession_OnUnknownId_DoesNotThrow()
    {
        var emitter = new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance);
        Should.NotThrow(() => emitter.UnregisterSession("nope"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelNotificationEmitterTests"`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement emitter**

```csharp
// McpChannelVoice/Services/ChannelNotificationEmitter.cs
using System.Collections.Concurrent;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelVoice.Services;

public sealed class ChannelNotificationEmitter(ILogger<ChannelNotificationEmitter> logger)
{
    private readonly ConcurrentDictionary<string, McpServer> _activeSessions = new();

    public void RegisterSession(string sessionId, McpServer server)
    {
        _activeSessions[sessionId] = server;
        logger.LogInformation("MCP session registered: {SessionId}", sessionId);
    }

    public void UnregisterSession(string sessionId)
    {
        _activeSessions.TryRemove(sessionId, out _);
        logger.LogInformation("MCP session unregistered: {SessionId}", sessionId);
    }

    public bool HasActiveSessions => !_activeSessions.IsEmpty;

    public async Task EmitMessageNotificationAsync(
        ChannelMessageNotification payload,
        CancellationToken cancellationToken = default)
    {
        var tasks = _activeSessions.Values.Select(async server =>
        {
            try
            {
                await server.SendNotificationAsync(
                    ChannelProtocol.MessageNotification,
                    payload,
                    ChannelProtocol.SerializerOptions,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to emit channel/message notification");
            }
        });

        await Task.WhenAll(tasks);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ChannelNotificationEmitterTests"`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/ChannelNotificationEmitter.cs \
        Tests/Unit/McpChannelVoice/ChannelNotificationEmitterTests.cs
git commit -m "feat(voice): channel notification emitter"
```

---

### Task 1.9: Dummy `SendReplyTool`, `RequestApprovalTool`, `RegisterAgentsTool`

**Files:**
- Create: `McpChannelVoice/McpTools/SendReplyTool.cs`
- Create: `McpChannelVoice/McpTools/RequestApprovalTool.cs`
- Create: `McpChannelVoice/McpTools/RegisterAgentsTool.cs`
- Test: `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs`

These are no-op placeholders that log and return "ok" — real implementations land in Slices 3 and 5. The intent is that the channel speaks the MCP protocol from day one so the agent connects cleanly.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/SendReplyToolTests.cs
using Domain.DTOs;
using McpChannelVoice.McpTools;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SendReplyToolTests
{
    [Fact]
    public async Task McpRun_ReturnsOkPlaceholder()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var result = await SendReplyTool.McpRun(
            "kitchen-01",
            "hello",
            ReplyContentType.Text,
            true,
            null,
            services);

        result.ShouldBe("ok");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SendReplyToolTests" --no-restore`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement placeholder tools**

```csharp
// McpChannelVoice/McpTools/SendReplyTool.cs
using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Speak a response chunk on the originating voice satellite")]
    public static Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services)
    {
        var logger = services.GetService<ILogger<SendReplyTool>>();
        logger?.LogInformation(
            "send_reply (placeholder) conversation={ConversationId} type={ContentType} complete={IsComplete}",
            conversationId, contentType, isComplete);
        return Task.FromResult("ok");
    }
}
```

```csharp
// McpChannelVoice/McpTools/RequestApprovalTool.cs
using System.ComponentModel;
using Domain.DTOs;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request user approval (placeholder, returns 'decline')")]
    public static Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Whether to ask the user or just notify them")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services)
    {
        var logger = services.GetService<ILogger<RequestApprovalTool>>();
        logger?.LogInformation(
            "request_approval (placeholder) conversation={ConversationId} mode={Mode} requests={Count}",
            conversationId, mode, requests.Count);

        return Task.FromResult(mode == ApprovalMode.Notify ? "notified" : "declined");
    }
}
```

```csharp
// McpChannelVoice/McpTools/RegisterAgentsTool.cs
using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RegisterAgentsTool(IMutableAgentCatalog catalog)
{
    [McpServerTool(Name = ChannelProtocol.RegisterAgentsTool)]
    [Description("Register the agents that voice satellites may target (replaces any previously registered set)")]
    public string McpRun([Description("Agents available to voice")] IReadOnlyList<AgentCatalogEntry> agents)
    {
        catalog.Replace(agents);
        return $"registered {agents.Count} agents";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SendReplyToolTests" --no-restore`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/McpTools/ Tests/Unit/McpChannelVoice/SendReplyToolTests.cs
git commit -m "feat(voice): placeholder send_reply / request_approval / register_agents tools"
```

---

### Task 1.10: `ConfigModule` and real `Program.cs` (MCP HTTP transport + heartbeat)

**Files:**
- Create: `McpChannelVoice/Modules/ConfigModule.cs`
- Modify: `McpChannelVoice/Program.cs`

- [ ] **Step 1: Replace `Program.cs`**

```csharp
// McpChannelVoice/Program.cs
using McpChannelVoice.Modules;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetVoiceSettings();
builder.Services.ConfigureVoiceChannel(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();
```

- [ ] **Step 2: Create `Modules/ConfigModule.cs`**

```csharp
// McpChannelVoice/Modules/ConfigModule.cs
using Domain.Agents;
using Domain.Contracts;
using Infrastructure.Metrics;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using ModelContextProtocol.Protocol;
using StackExchange.Redis;

namespace McpChannelVoice.Modules;

public static class ConfigModule
{
    public static VoiceSettings GetVoiceSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.GetSection("Voice").Get<VoiceSettings>()
                       ?? throw new InvalidOperationException("Voice settings not found");
        return settings;
    }

    public static IServiceCollection ConfigureVoiceChannel(
        this IServiceCollection services,
        VoiceSettings settings)
    {
        var redisConnection = Environment.GetEnvironmentVariable("REDIS__CONNECTIONSTRING")
                              ?? "redis:6379";

        var emitter = new ChannelNotificationEmitter(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChannelNotificationEmitter>());

        services
            .AddSingleton(settings)
            .AddSingleton(emitter)
            .AddSingleton(new SatelliteRegistry(settings.Satellites))
            .AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisConnection))
            .AddSingleton<IMetricsPublisher, RedisMetricsPublisher>()
            .AddSingleton<MutableAgentCatalog>()
            .AddSingleton<IAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddSingleton<IMutableAgentCatalog>(sp => sp.GetRequiredService<MutableAgentCatalog>())
            .AddHostedService(sp =>
                new HeartbeatService(sp.GetRequiredService<IMetricsPublisher>(), "mcp-channel-voice"));

        services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
#pragma warning disable MCPEXP002
                options.RunSessionHandler = async (_, server, ct) =>
                {
                    var sessionId = server.SessionId ?? Guid.NewGuid().ToString();
                    emitter.RegisterSession(sessionId, server);
                    try
                    {
                        await server.RunAsync(ct);
                    }
                    finally
                    {
                        emitter.UnregisterSession(sessionId);
                    }
                };
#pragma warning restore MCPEXP002
            })
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    var logger = context.Services?.GetRequiredService<ILogger<Program>>();
                    logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
                    return new CallToolResult
                    {
                        IsError = true,
                        Content = [new TextContentBlock { Text = ex.Message }]
                    };
                }
            }));

        return services;
    }
}
```

- [ ] **Step 3: Verify the project still builds**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add McpChannelVoice/Modules/ConfigModule.cs McpChannelVoice/Program.cs
git commit -m "feat(voice): wire MCP HTTP transport + heartbeat in ConfigModule"
```

---

### Task 1.11: Docker Compose entry for `mcp-channel-voice` and `channelEndpoints` update

**Files:**
- Modify: `DockerCompose/docker-compose.yml`
- Modify: `DockerCompose/.env`
- Modify: `Agent/appsettings.json`

- [ ] **Step 1: Add the service to `docker-compose.yml`**

In `DockerCompose/docker-compose.yml`, immediately after the `mcp-scheduling` block (around line 468), insert:

```yaml
  mcp-channel-voice:
    image: mcp-channel-voice:latest
    logging:
      options:
        max-size: "5m"
        max-file: "3"
    container_name: mcp-channel-voice
    ports:
      - "6014:8080"
      - "10700:10700"
    build:
      context: ${REPOSITORY_PATH}
      dockerfile: McpChannelVoice/Dockerfile
      cache_from:
        - mcp-channel-voice:latest
      args:
        - BUILDKIT_INLINE_CACHE=1
    restart: unless-stopped
    environment:
      - VOICE__ANNOUNCE__TOKEN=${ANNOUNCE_TOKEN}
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

Then add `mcp-channel-voice` to the `agent` service's `depends_on` block (after the `mcp-channel-servicebus` entry):

```yaml
      mcp-channel-voice:
        condition: service_started
```

- [ ] **Step 2: Add `ANNOUNCE_TOKEN` placeholder to `DockerCompose/.env`**

Append to `DockerCompose/.env`:

```bash
# Voice channel secrets
ANNOUNCE_TOKEN=
```

- [ ] **Step 3: Add the channel to the agent's `channelEndpoints`**

Edit `Agent/appsettings.json`. Inside the `channelEndpoints` array (currently ends with `scheduling`), add a trailing entry:

```json
        {
            "channelId": "voice",
            "endpoint": "http://mcp-channel-voice:8080/mcp"
        }
```

- [ ] **Step 4: Smoke-build the new image**

Run from repo root:

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot build mcp-channel-voice
```

Expected: Image builds successfully; the final line contains `naming to docker.io/library/mcp-channel-voice:latest`.

- [ ] **Step 5: Commit**

```bash
git add DockerCompose/docker-compose.yml DockerCompose/.env Agent/appsettings.json
git commit -m "infra(voice): mcp-channel-voice compose service + agent endpoint"
```

---

### Task 1.12: Slice 1 wrap-up — manual smoke test

- [ ] **Step 1: Boot the new channel alongside Redis and the agent**

Run:

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build redis mcp-channel-voice agent
```

- [ ] **Step 2: Confirm the agent registered with the voice channel**

Run:

```bash
docker logs --tail 100 mcp-channel-voice 2>&1 | grep -E "MCP session registered|registered .* agents"
```

Expected: A line `MCP session registered: ...` and a `registered N agents` log entry within ~15 s of agent boot.

- [ ] **Step 3: Confirm the heartbeat is published**

Run:

```bash
docker exec redis redis-cli --csv subscribe metrics:events &
sleep 35
kill %1
```

Expected: At least one `heartbeat` payload with `"service":"mcp-channel-voice"` in the captured output.

- [ ] **Step 4: Tear down the test stack**

Run: `docker compose -f DockerCompose/docker-compose.yml -p jackbot down`

**Slice 1 done when:** Build is green, the agent connects, the channel emits heartbeats, and tools list (`SendReplyTool`, `RequestApprovalTool`, `RegisterAgentsTool`) is visible via the agent's tool registry logs.

---

## Slice 2 — STT path (Wyoming inbound + outbound, transcripts to the agent)

### Task 2.1: `WyomingEvent` record (one event = one JSON header + optional binary payload)

The Wyoming protocol frames each event as a **single line of JSON** terminated by `\n`. The JSON object always has a `type` field; when binary data follows, the JSON also has a `payload_length` field set to the byte count of the payload that follows immediately after the newline.

**Files:**
- Create: `McpChannelVoice/Services/WyomingProtocol/WyomingEvent.cs`
- Test: `Tests/Unit/McpChannelVoice/Wyoming/WyomingEventTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Wyoming/WyomingEventTests.cs
using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class WyomingEventTests
{
    [Fact]
    public void Create_WithDataOnly_HasNoPayload()
    {
        var data = new JsonObject { ["text"] = "hello" };
        var evt = new WyomingEvent("transcript", data, ReadOnlyMemory<byte>.Empty);
        evt.Type.ShouldBe("transcript");
        evt.Payload.Length.ShouldBe(0);
    }

    [Fact]
    public void Create_WithPayload_PreservesBytes()
    {
        var bytes = new byte[] { 1, 2, 3 };
        var evt = new WyomingEvent("audio-chunk", new JsonObject(), bytes);
        evt.Payload.ToArray().ShouldBe(bytes);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingEventTests" --no-restore`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement `WyomingEvent`**

```csharp
// McpChannelVoice/Services/WyomingProtocol/WyomingEvent.cs
using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed record WyomingEvent(
    string Type,
    JsonObject Data,
    ReadOnlyMemory<byte> Payload)
{
    public static WyomingEvent Header(string type, JsonObject data) =>
        new(type, data, ReadOnlyMemory<byte>.Empty);

    public static WyomingEvent WithPayload(string type, JsonObject data, ReadOnlyMemory<byte> payload) =>
        new(type, data, payload);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingEventTests" --no-restore`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/WyomingEvent.cs \
        Tests/Unit/McpChannelVoice/Wyoming/WyomingEventTests.cs
git commit -m "feat(voice): WyomingEvent record"
```

---

### Task 2.2: `WyomingWriter` (frame events onto a stream, thread-safe)

**Files:**
- Create: `McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs`
- Test: `Tests/Unit/McpChannelVoice/Wyoming/WyomingWriterTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Wyoming/WyomingWriterTests.cs
using System.Text;
using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class WyomingWriterTests
{
    [Fact]
    public async Task WriteAsync_HeaderOnly_WritesJsonLine()
    {
        await using var ms = new MemoryStream();
        var writer = new WyomingWriter(ms);

        await writer.WriteAsync(
            WyomingEvent.Header("describe", new JsonObject()),
            CancellationToken.None);

        var output = Encoding.UTF8.GetString(ms.ToArray());
        output.ShouldEndWith("\n");
        output.ShouldContain("\"type\":\"describe\"");
    }

    [Fact]
    public async Task WriteAsync_WithPayload_AppendsBytesAfterNewline()
    {
        await using var ms = new MemoryStream();
        var writer = new WyomingWriter(ms);
        var payload = new byte[] { 1, 2, 3, 4 };
        var data = new JsonObject { ["rate"] = 16_000, ["width"] = 2, ["channels"] = 1 };

        await writer.WriteAsync(
            WyomingEvent.WithPayload("audio-chunk", data, payload),
            CancellationToken.None);

        var bytes = ms.ToArray();
        var newlineIndex = Array.IndexOf(bytes, (byte)'\n');
        newlineIndex.ShouldBeGreaterThan(0);
        var header = Encoding.UTF8.GetString(bytes, 0, newlineIndex);
        header.ShouldContain("\"payload_length\":4");
        bytes[(newlineIndex + 1)..].ShouldBe(payload);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingWriterTests" --no-restore`
Expected: FAIL.

- [ ] **Step 3: Implement `WyomingWriter`**

```csharp
// McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed class WyomingWriter(Stream stream)
{
    private static readonly byte[] _newline = "\n"u8.ToArray();
    private static readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task WriteAsync(WyomingEvent evt, CancellationToken ct)
    {
        var header = new JsonObject(evt.Data.ToDictionary(kv => kv.Key, kv => kv.Value?.DeepClone()))
        {
            ["type"] = evt.Type
        };
        if (evt.Payload.Length > 0)
        {
            header["payload_length"] = evt.Payload.Length;
        }

        var bytes = Encoding.UTF8.GetBytes(header.ToJsonString(_serializerOptions));

        await _lock.WaitAsync(ct);
        try
        {
            await stream.WriteAsync(bytes, ct);
            await stream.WriteAsync(_newline, ct);
            if (evt.Payload.Length > 0)
            {
                await stream.WriteAsync(evt.Payload, ct);
            }
            await stream.FlushAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingWriterTests" --no-restore`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/WyomingWriter.cs \
        Tests/Unit/McpChannelVoice/Wyoming/WyomingWriterTests.cs
git commit -m "feat(voice): WyomingWriter frames events on a stream"
```

---

### Task 2.3: `WyomingReader` (parse framed events off a stream as an async sequence)

**Files:**
- Create: `McpChannelVoice/Services/WyomingProtocol/WyomingReader.cs`
- Test: `Tests/Unit/McpChannelVoice/Wyoming/WyomingReaderTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Wyoming/WyomingReaderTests.cs
using System.Text;
using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class WyomingReaderTests
{
    [Fact]
    public async Task ReadAllAsync_HeaderOnly_YieldsEvent()
    {
        var bytes = Encoding.UTF8.GetBytes("{\"type\":\"describe\"}\n");
        await using var ms = new MemoryStream(bytes);
        var reader = new WyomingReader(ms);

        var events = new List<WyomingEvent>();
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
        {
            events.Add(evt);
        }

        events.Count.ShouldBe(1);
        events[0].Type.ShouldBe("describe");
        events[0].Payload.Length.ShouldBe(0);
    }

    [Fact]
    public async Task ReadAllAsync_WithPayload_ReadsExactBytes()
    {
        var header = "{\"type\":\"audio-chunk\",\"payload_length\":3}\n";
        var payload = new byte[] { 9, 8, 7 };
        var combined = Encoding.UTF8.GetBytes(header).Concat(payload).ToArray();
        await using var ms = new MemoryStream(combined);

        var reader = new WyomingReader(ms);
        var events = new List<WyomingEvent>();
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
        {
            events.Add(evt);
        }

        events.Count.ShouldBe(1);
        events[0].Type.ShouldBe("audio-chunk");
        events[0].Payload.ToArray().ShouldBe(payload);
    }

    [Fact]
    public async Task ReadAllAsync_MultipleEvents_YieldsInOrder()
    {
        var s = "{\"type\":\"a\"}\n{\"type\":\"b\",\"payload_length\":1}\n" + (char)42 + "{\"type\":\"c\"}\n";
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(s));
        var reader = new WyomingReader(ms);

        var types = new List<string>();
        await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
        {
            types.Add(evt.Type);
        }

        types.ShouldBe(["a", "b", "c"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingReaderTests" --no-restore`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement `WyomingReader`**

```csharp
// McpChannelVoice/Services/WyomingProtocol/WyomingReader.cs
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed class WyomingReader(Stream stream)
{
    public async IAsyncEnumerable<WyomingEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var oneByte = new byte[1];

        while (!ct.IsCancellationRequested)
        {
            buffer.SetLength(0);
            while (true)
            {
                var read = await stream.ReadAsync(oneByte.AsMemory(0, 1), ct);
                if (read == 0)
                {
                    yield break;
                }
                if (oneByte[0] == (byte)'\n')
                {
                    break;
                }
                buffer.WriteByte(oneByte[0]);
            }

            if (buffer.Length == 0)
            {
                continue;
            }

            var headerJson = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            var node = JsonNode.Parse(headerJson)?.AsObject()
                       ?? throw new InvalidDataException("Wyoming header is not a JSON object");

            var type = node["type"]?.GetValue<string>()
                       ?? throw new InvalidDataException("Wyoming header missing 'type'");

            ReadOnlyMemory<byte> payload = ReadOnlyMemory<byte>.Empty;
            if (node["payload_length"]?.GetValue<int>() is int payloadLength && payloadLength > 0)
            {
                var payloadBuf = new byte[payloadLength];
                var totalRead = 0;
                while (totalRead < payloadLength)
                {
                    var read = await stream.ReadAsync(
                        payloadBuf.AsMemory(totalRead, payloadLength - totalRead), ct);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("Stream closed mid-payload");
                    }
                    totalRead += read;
                }
                payload = payloadBuf;
                node.Remove("payload_length");
            }
            node.Remove("type");

            yield return new WyomingEvent(type, node, payload);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingReaderTests" --no-restore`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/WyomingReader.cs \
        Tests/Unit/McpChannelVoice/Wyoming/WyomingReaderTests.cs
git commit -m "feat(voice): WyomingReader parses framed events"
```

---

### Task 2.4: `WyomingClient` (outbound TCP client used by adapters)

**Files:**
- Create: `McpChannelVoice/Services/WyomingProtocol/WyomingClient.cs`
- Test: `Tests/Unit/McpChannelVoice/Wyoming/WyomingClientTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Wyoming/WyomingClientTests.cs
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using McpChannelVoice.Services.WyomingProtocol;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Wyoming;

public class WyomingClientTests
{
    [Fact]
    public async Task ConnectAsync_RoundTripsAnEvent()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            await using var stream = server.GetStream();
            var reader = new WyomingReader(stream);
            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "describe")
                {
                    var writer = new WyomingWriter(stream);
                    await writer.WriteAsync(
                        WyomingEvent.Header("info", new JsonObject { ["foo"] = "bar" }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        await using var client = new WyomingClient();
        await client.ConnectAsync("127.0.0.1", port, CancellationToken.None);
        await client.WriteAsync(
            WyomingEvent.Header("describe", new JsonObject()),
            CancellationToken.None);

        WyomingEvent? received = null;
        await foreach (var evt in client.ReadAllAsync(CancellationToken.None))
        {
            received = evt;
            break;
        }

        await serverTask;
        listener.Stop();

        received.ShouldNotBeNull();
        received!.Type.ShouldBe("info");
        received.Data["foo"]!.GetValue<string>().ShouldBe("bar");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingClientTests" --no-restore`
Expected: FAIL.

- [ ] **Step 3: Implement `WyomingClient`**

```csharp
// McpChannelVoice/Services/WyomingProtocol/WyomingClient.cs
using System.Net.Sockets;

namespace McpChannelVoice.Services.WyomingProtocol;

public sealed class WyomingClient : IAsyncDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private WyomingWriter? _writer;
    private WyomingReader? _reader;

    public async Task ConnectAsync(string host, int port, CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
        _writer = new WyomingWriter(_stream);
        _reader = new WyomingReader(_stream);
    }

    public Task WriteAsync(WyomingEvent evt, CancellationToken ct) =>
        (_writer ?? throw new InvalidOperationException("Not connected")).WriteAsync(evt, ct);

    public IAsyncEnumerable<WyomingEvent> ReadAllAsync(CancellationToken ct) =>
        (_reader ?? throw new InvalidOperationException("Not connected")).ReadAllAsync(ct);

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null)
        {
            await _stream.DisposeAsync();
        }
        _tcp?.Dispose();
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingClientTests" --no-restore`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingProtocol/WyomingClient.cs \
        Tests/Unit/McpChannelVoice/Wyoming/WyomingClientTests.cs
git commit -m "feat(voice): WyomingClient TCP wrapper"
```

---

### Task 2.5: `SatelliteSession` (per-connection session state, owns playback queue stub)

The session is the per-satellite state machine — it holds the open Wyoming stream back to the satellite, exposes a `ConversationId` (= satellite id), routes inbound audio chunks to whoever is currently transcribing, and (later, in Slice 3) owns the playback queue. For now it stores the inbound audio channel and a stub playback hook.

**Files:**
- Create: `McpChannelVoice/Services/SatelliteSession.cs`
- Test: `Tests/Unit/McpChannelVoice/SatelliteSessionTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/SatelliteSessionTests.cs
using System.Threading.Channels;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionTests
{
    [Fact]
    public async Task InboundAudio_CompletesWhenSessionClosed()
    {
        var session = new SatelliteSession(
            satelliteId: "kitchen-01",
            config: new SatelliteConfig { Identity = "household", Room = "Kitchen" });

        await session.PublishAudioAsync(new byte[] { 1, 2, 3 }, CancellationToken.None);
        session.CompleteInboundAudio();

        var bytes = new List<byte>();
        await foreach (var chunk in session.ReadInboundAudioAsync(CancellationToken.None))
        {
            bytes.AddRange(chunk.ToArray());
        }
        bytes.ShouldBe([1, 2, 3]);
    }

    [Fact]
    public void ConversationId_EqualsSatelliteId()
    {
        var session = new SatelliteSession(
            satelliteId: "bedroom-01",
            config: new SatelliteConfig { Identity = "francisco", Room = "Bedroom" });

        session.ConversationId.ShouldBe("bedroom-01");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionTests" --no-restore`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement `SatelliteSession`**

```csharp
// McpChannelVoice/Services/SatelliteSession.cs
using System.Threading.Channels;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class SatelliteSession
{
    private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

    public SatelliteSession(string satelliteId, SatelliteConfig config)
    {
        SatelliteId = satelliteId;
        Config = config;
    }

    public string SatelliteId { get; }
    public string ConversationId => SatelliteId;
    public SatelliteConfig Config { get; }

    public ValueTask PublishAudioAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        _inbound.Writer.WriteAsync(bytes, ct);

    public void CompleteInboundAudio() => _inbound.Writer.TryComplete();

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadInboundAudioAsync(CancellationToken ct) =>
        _inbound.Reader.ReadAllAsync(ct);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionTests" --no-restore`
Expected: 2 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSession.cs \
        Tests/Unit/McpChannelVoice/SatelliteSessionTests.cs
git commit -m "feat(voice): SatelliteSession with inbound audio channel"
```

---

### Task 2.6: `SatelliteSessionRegistry` — id → live session lookup

**Files:**
- Create: `McpChannelVoice/Services/SatelliteSessionRegistry.cs`
- Test: `Tests/Unit/McpChannelVoice/SatelliteSessionRegistryTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/SatelliteSessionRegistryTests.cs
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionRegistryTests
{
    [Fact]
    public void RegisterAndGet_RoundTrips()
    {
        var registry = new SatelliteSessionRegistry();
        var session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });

        registry.Register(session);

        registry.Get("kitchen-01").ShouldBe(session);
    }

    [Fact]
    public void Get_Unknown_ReturnsNull()
    {
        var registry = new SatelliteSessionRegistry();
        registry.Get("ghost-01").ShouldBeNull();
    }

    [Fact]
    public void Unregister_RemovesEntry()
    {
        var registry = new SatelliteSessionRegistry();
        var session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        registry.Register(session);

        registry.Unregister("kitchen-01");

        registry.Get("kitchen-01").ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionRegistryTests" --no-restore`
Expected: FAIL.

- [ ] **Step 3: Implement the registry**

```csharp
// McpChannelVoice/Services/SatelliteSessionRegistry.cs
using System.Collections.Concurrent;

namespace McpChannelVoice.Services;

public sealed class SatelliteSessionRegistry
{
    private readonly ConcurrentDictionary<string, SatelliteSession> _sessions = new();

    public void Register(SatelliteSession session) => _sessions[session.SatelliteId] = session;

    public void Unregister(string satelliteId) => _sessions.TryRemove(satelliteId, out _);

    public SatelliteSession? Get(string satelliteId) =>
        _sessions.TryGetValue(satelliteId, out var s) ? s : null;

    public IReadOnlyList<SatelliteSession> All() => _sessions.Values.ToList();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionRegistryTests" --no-restore`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/SatelliteSessionRegistry.cs \
        Tests/Unit/McpChannelVoice/SatelliteSessionRegistryTests.cs
git commit -m "feat(voice): SatelliteSessionRegistry"
```

---

### Task 2.7: `WyomingSpeechToText` adapter (calls `wyoming-faster-whisper`)

The Whisper Wyoming protocol flow is:
1. Client → `{"type":"transcribe","language":"en"}`
2. Client → `{"type":"audio-start","rate":16000,"width":2,"channels":1,"timestamp":0}`
3. Client → repeated `{"type":"audio-chunk","rate":...,"timestamp":...,"payload_length":N}` + raw PCM payload
4. Client → `{"type":"audio-stop","timestamp":...}`
5. Server → `{"type":"transcript","text":"hello","language":"en"}` (and may include `score` for confidence)

**Files:**
- Create: `Infrastructure/Clients/Voice/WyomingSpeechToText.cs`
- Test: `Tests/Unit/Infrastructure/Clients/Voice/WyomingSpeechToTextTests.cs`
- Modify: `Infrastructure/Infrastructure.csproj` (verify no new pkg needed; `WyomingClient` lives in the channel project)

Because `WyomingSpeechToText` calls into `WyomingClient` which lives in `McpChannelVoice`, we instead keep the adapter **inside `McpChannelVoice`** (per the layering rule, Infrastructure does not depend on a channel project). Confirm via the test: the adapter belongs in `McpChannelVoice.Services.Stt`.

**Updated files:**
- Create: `McpChannelVoice/Services/Stt/WyomingSpeechToText.cs`
- Test: `Tests/Unit/McpChannelVoice/Stt/WyomingSpeechToTextTests.cs`

- [ ] **Step 1: Write the failing test (loopback Wyoming server playing the role of Whisper)**

```csharp
// Tests/Unit/McpChannelVoice/Stt/WyomingSpeechToTextTests.cs
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Stt;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Stt;

public class WyomingSpeechToTextTests
{
    [Fact]
    public async Task TranscribeAsync_StreamsAudioAndReturnsTranscript()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var seenChunks = 0;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type == "audio-chunk") seenChunks++;
                if (evt.Type == "audio-stop")
                {
                    await writer.WriteAsync(
                        WyomingEvent.Header("transcript", new JsonObject
                        {
                            ["text"] = "hola mundo",
                            ["language"] = "es"
                        }),
                        CancellationToken.None);
                    return;
                }
            }
        });

        var sut = new WyomingSpeechToText(
            new WyomingSttConfig { Host = "127.0.0.1", Port = port, Language = "es" },
            NullLogger<WyomingSpeechToText>.Instance);

        async IAsyncEnumerable<AudioChunk> Audio()
        {
            for (var i = 0; i < 3; i++)
            {
                yield return new AudioChunk
                {
                    Data = new byte[16],
                    Format = AudioFormat.WyomingStandard,
                    Timestamp = TimeSpan.FromMilliseconds(i * 10)
                };
                await Task.Yield();
            }
        }

        var result = await sut.TranscribeAsync(Audio(), new TranscriptionOptions(), CancellationToken.None);

        await serverTask;
        listener.Stop();

        seenChunks.ShouldBe(3);
        result.Text.ShouldBe("hola mundo");
        result.Language.ShouldBe("es");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingSpeechToTextTests" --no-restore`
Expected: FAIL — adapter missing.

- [ ] **Step 3: Implement the adapter**

```csharp
// McpChannelVoice/Services/Stt/WyomingSpeechToText.cs
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Stt;

public sealed class WyomingSpeechToText(
    WyomingSttConfig config,
    ILogger<WyomingSpeechToText> logger) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        await using var client = new WyomingClient();
        await client.ConnectAsync(config.Host, config.Port, ct);

        var language = options.Language ?? config.Language;
        var transcribeData = new JsonObject();
        if (language is not null) transcribeData["language"] = language;
        if (config.Model is not null) transcribeData["name"] = config.Model;
        await client.WriteAsync(WyomingEvent.Header("transcribe", transcribeData), ct);

        var fmt = AudioFormat.WyomingStandard;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await client.WriteAsync(
            WyomingEvent.Header("audio-start", new JsonObject
            {
                ["rate"] = fmt.SampleRateHz,
                ["width"] = fmt.SampleWidthBytes,
                ["channels"] = fmt.Channels,
                ["timestamp"] = 0
            }), ct);

        await foreach (var chunk in audio.WithCancellation(ct))
        {
            await client.WriteAsync(
                WyomingEvent.WithPayload(
                    "audio-chunk",
                    new JsonObject
                    {
                        ["rate"] = chunk.Format.SampleRateHz,
                        ["width"] = chunk.Format.SampleWidthBytes,
                        ["channels"] = chunk.Format.Channels,
                        ["timestamp"] = (long)chunk.Timestamp.TotalMilliseconds
                    },
                    chunk.Data),
                ct);
        }

        await client.WriteAsync(
            WyomingEvent.Header("audio-stop", new JsonObject
            {
                ["timestamp"] = sw.ElapsedMilliseconds
            }), ct);

        await foreach (var evt in client.ReadAllAsync(ct))
        {
            if (evt.Type != "transcript") continue;

            var text = evt.Data["text"]?.GetValue<string>() ?? string.Empty;
            var lang = evt.Data["language"]?.GetValue<string>();
            double? score = null;
            if (evt.Data["score"] is JsonNode s)
            {
                score = s.GetValue<double>();
            }

            logger.LogInformation("Wyoming transcript: text={Text} lang={Lang}", text, lang);
            return new TranscriptionResult { Text = text, Language = lang, Confidence = score };
        }

        return new TranscriptionResult { Text = "" };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingSpeechToTextTests" --no-restore`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Stt/WyomingSpeechToText.cs \
        Tests/Unit/McpChannelVoice/Stt/WyomingSpeechToTextTests.cs
git commit -m "feat(voice): WyomingSpeechToText adapter"
```

---

### Task 2.8: Confidence gate + identity/room dispatch helper

**Files:**
- Create: `McpChannelVoice/Services/TranscriptDispatcher.cs`
- Test: `Tests/Unit/McpChannelVoice/ConfidenceGateTests.cs`

The dispatcher takes a `TranscriptionResult`, the originating `SatelliteSession`, the configured threshold, the emitter, and the metrics publisher; it drops empty/low-confidence transcripts (publishing the right metric event) and emits `channel/message` otherwise.

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/ConfidenceGateTests.cs
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ConfidenceGateTests
{
    private static SatelliteSession MakeSession() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    private sealed class CapturingEmitter : ChannelNotificationEmitter
    {
        public List<ChannelMessageNotification> Captured { get; } = new();
        public CapturingEmitter() : base(NullLogger<ChannelNotificationEmitter>.Instance) { }
        public new Task EmitMessageNotificationAsync(ChannelMessageNotification p, CancellationToken ct = default)
        {
            Captured.Add(p);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Dispatch_EmptyText_DropsAndPublishesMetric()
    {
        var emitter = new CapturingEmitter();
        var publisher = new Mock<IMetricsPublisher>();
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, confidenceThreshold: 0.4,
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await dispatcher.DispatchAsync(
            MakeSession(),
            new TranscriptionResult { Text = "", Confidence = 0.9, Language = "en" },
            agentId: "jonas",
            CancellationToken.None);

        ok.ShouldBeFalse();
        emitter.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_LowConfidence_DropsAndPublishesMetric()
    {
        var emitter = new CapturingEmitter();
        var publisher = new Mock<IMetricsPublisher>();
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, confidenceThreshold: 0.4,
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await dispatcher.DispatchAsync(
            MakeSession(),
            new TranscriptionResult { Text = "what?", Confidence = 0.2, Language = "en" },
            agentId: "jonas",
            CancellationToken.None);

        ok.ShouldBeFalse();
        emitter.Captured.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_GoodTranscript_EmitsAndPublishes()
    {
        var emitter = new CapturingEmitter();
        var publisher = new Mock<IMetricsPublisher>();
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, confidenceThreshold: 0.4,
            NullLogger<TranscriptDispatcher>.Instance);

        var ok = await dispatcher.DispatchAsync(
            MakeSession(),
            new TranscriptionResult { Text = "qué hora es", Confidence = 0.9, Language = "es" },
            agentId: "jonas",
            CancellationToken.None);

        ok.ShouldBeTrue();
        emitter.Captured.Count.ShouldBe(1);
        emitter.Captured[0].Content.ShouldBe("qué hora es");
        emitter.Captured[0].Sender.ShouldBe("household");
        emitter.Captured[0].ConversationId.ShouldBe("kitchen-01");
        emitter.Captured[0].AgentId.ShouldBe("jonas");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConfidenceGateTests" --no-restore`
Expected: FAIL — type missing.

- [ ] **Step 3: Implement the dispatcher**

```csharp
// McpChannelVoice/Services/TranscriptDispatcher.cs
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;

namespace McpChannelVoice.Services;

public sealed class TranscriptDispatcher(
    ChannelNotificationEmitter emitter,
    IMetricsPublisher publisher,
    double confidenceThreshold,
    ILogger<TranscriptDispatcher> logger)
{
    public async Task<bool> DispatchAsync(
        SatelliteSession session,
        TranscriptionResult transcript,
        string? agentId,
        CancellationToken ct)
    {
        var lowConfidence = transcript.Confidence is { } c && c < confidenceThreshold;
        if (string.IsNullOrWhiteSpace(transcript.Text) || lowConfidence)
        {
            logger.LogInformation(
                "Dropping transcript for {Satellite}: empty={Empty} low={Low} confidence={Conf}",
                session.SatelliteId,
                string.IsNullOrWhiteSpace(transcript.Text),
                lowConfidence,
                transcript.Confidence);

            await publisher.PublishAsync(
                new VoiceEvent
                {
                    Metric = VoiceMetric.UtteranceTranscribed,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    Language = transcript.Language,
                    Outcome = "dropped",
                    Confidence = transcript.Confidence,
                    ConversationId = session.ConversationId
                },
                ct);
            return false;
        }

        await emitter.EmitMessageNotificationAsync(
            new ChannelMessageNotification
            {
                ConversationId = session.ConversationId,
                Sender = session.Config.Identity,
                Content = transcript.Text,
                AgentId = agentId,
                Timestamp = DateTimeOffset.UtcNow
            },
            ct);

        await publisher.PublishAsync(
            new VoiceEvent
            {
                Metric = VoiceMetric.UtteranceTranscribed,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                Language = transcript.Language,
                Outcome = "dispatched",
                Confidence = transcript.Confidence,
                ConversationId = session.ConversationId
            },
            ct);
        return true;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ConfidenceGateTests" --no-restore`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/TranscriptDispatcher.cs \
        Tests/Unit/McpChannelVoice/ConfidenceGateTests.cs
git commit -m "feat(voice): TranscriptDispatcher with confidence gate"
```

---

### Task 2.9: `WyomingServer` (TCP listener for inbound satellite connections)

The server accepts a satellite connection, expects a `describe` then `info` exchange, looks the satellite up by id, creates a `SatelliteSession`, and pumps `audio-chunk` payloads through `STT` until `audio-stop`, then dispatches the transcript.

**Files:**
- Create: `McpChannelVoice/Services/WyomingServer.cs`
- Test: `Tests/Integration/McpChannelVoice/WyomingServerInboundTests.cs`

- [ ] **Step 1: Write the failing test (fake-satellite → loopback whisper)**

```csharp
// Tests/Integration/McpChannelVoice/WyomingServerInboundTests.cs
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

public class WyomingServerInboundTests
{
    private sealed class CapturingEmitter : ChannelNotificationEmitter
    {
        public TaskCompletionSource<ChannelMessageNotification> Tcs { get; } = new();
        public CapturingEmitter() : base(NullLogger<ChannelNotificationEmitter>.Instance) { }
        public new Task EmitMessageNotificationAsync(ChannelMessageNotification p, CancellationToken ct = default)
        {
            Tcs.TrySetResult(p);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task FakeSatellite_WakesAndStreamsAudio_ProducesTranscript()
    {
        var stt = new Mock<ISpeechToText>();
        stt.Setup(s => s.TranscribeAsync(It.IsAny<IAsyncEnumerable<AudioChunk>>(),
                                          It.IsAny<TranscriptionOptions>(),
                                          It.IsAny<CancellationToken>()))
            .Returns<IAsyncEnumerable<AudioChunk>, TranscriptionOptions, CancellationToken>(
                async (audio, _, ct) =>
                {
                    await foreach (var _ in audio.WithCancellation(ct)) { }
                    return new TranscriptionResult { Text = "hola", Language = "es", Confidence = 0.9 };
                });

        var emitter = new CapturingEmitter();
        var publisher = new Mock<IMetricsPublisher>();
        var dispatcher = new TranscriptDispatcher(
            emitter, publisher.Object, 0.4, NullLogger<TranscriptDispatcher>.Instance);
        var sessions = new SatelliteSessionRegistry();
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" }
        });

        var server = new WyomingServer(
            new WyomingServerSettings { Host = "127.0.0.1", Port = 0 },
            registry, sessions, stt.Object, dispatcher, publisher.Object,
            NullLogger<WyomingServer>.Instance);

        await server.StartAsync(CancellationToken.None);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, server.BoundPort);
        await using var stream = client.GetStream();
        var writer = new WyomingWriter(stream);

        await writer.WriteAsync(
            WyomingEvent.Header("info", new JsonObject { ["satellite"] = new JsonObject { ["name"] = "kitchen-01" } }),
            CancellationToken.None);

        await writer.WriteAsync(
            WyomingEvent.Header("audio-start", new JsonObject
            {
                ["rate"] = 16000, ["width"] = 2, ["channels"] = 1, ["timestamp"] = 0
            }),
            CancellationToken.None);

        await writer.WriteAsync(
            WyomingEvent.WithPayload("audio-chunk",
                new JsonObject { ["rate"] = 16000, ["width"] = 2, ["channels"] = 1, ["timestamp"] = 10 },
                new byte[64]),
            CancellationToken.None);

        await writer.WriteAsync(
            WyomingEvent.Header("audio-stop", new JsonObject { ["timestamp"] = 30 }),
            CancellationToken.None);

        var msg = await emitter.Tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Content.ShouldBe("hola");
        msg.ConversationId.ShouldBe("kitchen-01");
        msg.Sender.ShouldBe("household");

        await server.StopAsync(CancellationToken.None);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingServerInboundTests" --no-restore`
Expected: FAIL — `WyomingServer` missing.

- [ ] **Step 3: Implement `WyomingServer`**

```csharp
// McpChannelVoice/Services/WyomingServer.cs
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class WyomingServer(
    WyomingServerSettings settings,
    SatelliteRegistry satelliteRegistry,
    SatelliteSessionRegistry sessionRegistry,
    ISpeechToText speechToText,
    TranscriptDispatcher dispatcher,
    IMetricsPublisher metrics,
    ILogger<WyomingServer> logger) : IHostedService
{
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _acceptLoop;

    public int BoundPort => ((IPEndPoint?)_listener?.LocalEndpoint)?.Port ?? 0;

    public Task StartAsync(CancellationToken ct)
    {
        _listener = new TcpListener(IPAddress.Parse(settings.Host), settings.Port);
        _listener.Start();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        logger.LogInformation("Wyoming server listening on {Host}:{Port}", settings.Host, BoundPort);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        _listener?.Stop();
        if (_acceptLoop is not null) await _acceptLoop;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        var reader = new WyomingReader(stream);
        SatelliteSession? session = null;

        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                if (evt.Type == "info")
                {
                    var name = evt.Data["satellite"]?["name"]?.GetValue<string>()
                               ?? throw new InvalidDataException("info missing satellite.name");
                    var cfg = satelliteRegistry.GetById(name)
                              ?? throw new InvalidOperationException($"Unknown satellite '{name}'");
                    session = new SatelliteSession(name, cfg);
                    sessionRegistry.Register(session);
                    logger.LogInformation("Satellite {Id} connected (identity={Identity})", name, cfg.Identity);

                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.WakeTriggered,
                        SatelliteId = session.SatelliteId,
                        Room = cfg.Room,
                        Identity = cfg.Identity,
                        WakeWord = cfg.WakeWord,
                        ConversationId = session.ConversationId
                    }, ct);

                    _ = Task.Run(() => RunTranscriptionAsync(session, ct), ct);
                    continue;
                }

                if (session is null) continue;

                if (evt.Type == "audio-start") continue;

                if (evt.Type == "audio-chunk")
                {
                    await session.PublishAudioAsync(evt.Payload, ct);
                    continue;
                }

                if (evt.Type == "audio-stop")
                {
                    session.CompleteInboundAudio();
                    continue;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling Wyoming client");
        }
        finally
        {
            if (session is not null) sessionRegistry.Unregister(session.SatelliteId);
            client.Dispose();
        }
    }

    private async Task RunTranscriptionAsync(SatelliteSession session, CancellationToken ct)
    {
        try
        {
            async IAsyncEnumerable<AudioChunk> Stream()
            {
                await foreach (var bytes in session.ReadInboundAudioAsync(ct))
                {
                    yield return new AudioChunk
                    {
                        Data = bytes,
                        Format = AudioFormat.WyomingStandard,
                        Timestamp = TimeSpan.Zero
                    };
                }
            }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await speechToText.TranscribeAsync(Stream(), new TranscriptionOptions(), ct);
            sw.Stop();
            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.SttLatencyMs,
                SatelliteId = session.SatelliteId,
                Room = session.Config.Room,
                Identity = session.Config.Identity,
                DurationMs = sw.ElapsedMilliseconds,
                Language = result.Language,
                ConversationId = session.ConversationId
            }, ct);

            await dispatcher.DispatchAsync(session, result, agentId: null, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Transcription failed for {Id}", session.SatelliteId);
            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.SttError,
                SatelliteId = session.SatelliteId,
                Error = ex.Message,
                ConversationId = session.ConversationId
            }, ct);
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingServerInboundTests" --no-restore`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/WyomingServer.cs \
        Tests/Integration/McpChannelVoice/WyomingServerInboundTests.cs
git commit -m "feat(voice): Wyoming inbound server with STT dispatch"
```

---

### Task 2.10: Wire STT + Wyoming server into `ConfigModule`; add `wyoming-whisper` to compose

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`
- Modify: `DockerCompose/docker-compose.yml`

- [ ] **Step 1: Update `ConfigModule.cs`**

Inside `ConfigureVoiceChannel`, before `services.AddMcpServer()`, add the STT and server registrations:

```csharp
        services
            .AddSingleton<SatelliteSessionRegistry>()
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                settings.ConfidenceThreshold,
                sp.GetRequiredService<ILogger<TranscriptDispatcher>>()));

        if (settings.Stt.Provider.Equals("Wyoming", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ISpeechToText>(sp => new McpChannelVoice.Services.Stt.WyomingSpeechToText(
                settings.Stt.Wyoming ?? throw new InvalidOperationException("Stt.Wyoming missing"),
                sp.GetRequiredService<ILogger<McpChannelVoice.Services.Stt.WyomingSpeechToText>>()));
        }

        services.AddHostedService<WyomingServer>();
        services.AddSingleton(settings.WyomingServer);
```

- [ ] **Step 2: Add the Wyoming services to compose**

Append to `DockerCompose/docker-compose.yml` (immediately after the `mcp-channel-voice` service block):

```yaml
  wyoming-whisper:
    image: rhasspy/wyoming-whisper:latest
    container_name: wyoming-whisper
    command: --model base --language es --device cpu --uri tcp://0.0.0.0:10300
    volumes:
      - ./volumes/whisper-data:/data
    restart: unless-stopped
    networks:
      - jackbot
```

Then add `wyoming-whisper` to `mcp-channel-voice`'s `depends_on`:

```yaml
      wyoming-whisper:
        condition: service_started
```

- [ ] **Step 3: Smoke-build the compose stack**

Run:

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot build mcp-channel-voice
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot pull wyoming-whisper
```

Expected: Both succeed.

- [ ] **Step 4: Commit**

```bash
git add McpChannelVoice/Modules/ConfigModule.cs DockerCompose/docker-compose.yml
git commit -m "feat(voice): wire STT pipeline + add wyoming-whisper service"
```

---

### Task 2.11: Provisioning script and `wyoming-satellite` reference systemd unit

**Files:**
- Create: `scripts/provision-satellite.sh`
- Create: `scripts/wyoming-satellite.service.template`

- [ ] **Step 1: Create the provisioning script**

```bash
# scripts/provision-satellite.sh
#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 3 ]]; then
  echo "Usage: $0 <satellite-id> <hub-host> <wake-word> [mic-device] [button-gpio]"
  echo "  e.g.: $0 kitchen-01 hub.local hey_jarvis plughw:CARD=seeed2micvoicec,DEV=0 5"
  exit 64
fi

satellite_id=$1
hub_host=$2
wake_word=$3
mic_device=${4:-plughw:CARD=seeed2micvoicec,DEV=0}
button_gpio=${5:-}

echo ">> Updating apt"
sudo apt-get update
sudo apt-get install -y python3 python3-pip pipx alsa-utils sox libportaudio2

echo ">> Installing Wyoming satellite stack"
pipx ensurepath
pipx install wyoming-satellite || pipx upgrade wyoming-satellite
pipx install wyoming-openwakeword || pipx upgrade wyoming-openwakeword

unit_dir=/etc/systemd/system
sat_unit=$unit_dir/wyoming-satellite.service
ww_unit=$unit_dir/wyoming-openwakeword.service

button_args=""
if [[ -n "$button_gpio" ]]; then
  button_args="--awake-wav /usr/share/sounds/alsa/Front_Center.wav --done-wav /usr/share/sounds/alsa/Side_Right.wav --gpio-button $button_gpio"
fi

sudo tee "$ww_unit" >/dev/null <<EOF
[Unit]
Description=Wyoming openWakeWord
After=network-online.target

[Service]
ExecStart=$(command -v wyoming-openwakeword) --uri tcp://0.0.0.0:10400 --preload-model $wake_word
Restart=always
User=$USER

[Install]
WantedBy=multi-user.target
EOF

sudo tee "$sat_unit" >/dev/null <<EOF
[Unit]
Description=Wyoming Satellite ($satellite_id)
After=wyoming-openwakeword.service
Requires=wyoming-openwakeword.service

[Service]
ExecStart=$(command -v wyoming-satellite) \\
  --name $satellite_id \\
  --uri tcp://0.0.0.0:10700 \\
  --mic-command "arecord -D $mic_device -r 16000 -c 1 -f S16_LE -t raw" \\
  --snd-command "aplay -D $mic_device -r 22050 -c 1 -f S16_LE -t raw" \\
  --wake-uri tcp://127.0.0.1:10400 \\
  --wake-word-name $wake_word \\
  --vad webrtcvad \\
  --event-uri tcp://$hub_host:10700 \\
  $button_args
Restart=always
User=$USER

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable --now wyoming-openwakeword.service wyoming-satellite.service

echo "Satellite '$satellite_id' provisioned. Logs:"
echo "  journalctl -u wyoming-satellite -f"
```

- [ ] **Step 2: Make the script executable**

Run: `chmod +x scripts/provision-satellite.sh`
Expected: no output; `ls -l scripts/provision-satellite.sh` shows mode `-rwxr-xr-x`.

- [ ] **Step 3: Commit**

```bash
git add scripts/provision-satellite.sh
git commit -m "infra(voice): Pi Zero satellite provisioning script"
```

---

### Task 2.12: Slice 2 wrap-up — manual smoke test (desktop satellite)

- [ ] **Step 1: Boot the new stack**

Run:

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build redis wyoming-whisper mcp-channel-voice agent
```

- [ ] **Step 2: Run a desktop satellite against the hub**

On a Linux desktop with a working mic (or in WSL with a USB mic passthrough):

```bash
pipx install wyoming-satellite wyoming-openwakeword || pipx upgrade wyoming-satellite wyoming-openwakeword
wyoming-openwakeword --uri tcp://0.0.0.0:10400 --preload-model hey_jarvis &
wyoming-satellite \
  --name kitchen-01 \
  --uri tcp://0.0.0.0:10700 \
  --mic-command "arecord -r 16000 -c 1 -f S16_LE -t raw" \
  --wake-uri tcp://127.0.0.1:10400 \
  --wake-word-name hey_jarvis \
  --event-uri tcp://127.0.0.1:10700
```

Speak the wake word and a short phrase ("qué hora es").

- [ ] **Step 3: Confirm the transcript reached the agent**

Run:

```bash
docker logs --tail 100 agent 2>&1 | grep -E "channel/message|qué hora|kitchen-01"
```

Expected: a log line containing the spoken text routed to conversation `kitchen-01`.

**Slice 2 done when:** Step 3 shows the transcript in the agent logs, and `docker logs mcp-channel-voice` shows `WakeTriggered` and `UtteranceTranscribed` metric events.

---

## Slice 3 — TTS path (agent replies are spoken on the originating satellite)

### Task 3.1: Playback queue on `SatelliteSession`

The session gains a small queue (default depth = `Voice.Announce.QueueMaxDepth`) and a "currently playing" cancellation token. `EnqueuePlayback` adds work; `RunPlaybackLoopAsync` drains and writes to the live Wyoming stream. The same queue is used by `send_reply` (Slice 3) and by `AnnouncementService` (Slice 4).

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs`
- Test: `Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs
using System.Threading.Channels;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteSessionPlaybackTests
{
    private static SatelliteSession MakeSession() =>
        new("kitchen-01", new SatelliteConfig { Identity = "household", Room = "Kitchen" });

    [Fact]
    public async Task EnqueuePlayback_Normal_RunsAfterCurrent()
    {
        var session = MakeSession();
        var played = new List<string>();

        var first = new PlaybackJob(
            Label: "first",
            Priority: AnnouncePriority.Normal,
            Audio: GenerateAudio("first", count: 2),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        var second = first with { Label = "second", Audio = GenerateAudio("second", count: 1) };

        var pumpTask = session.RunPlaybackLoopAsync(
            async (chunk, ct) =>
            {
                played.Add(System.Text.Encoding.UTF8.GetString(chunk.Data.Span));
                await Task.Yield();
            },
            CancellationToken.None);

        await session.EnqueuePlaybackAsync(first, queueMaxDepth: 4);
        await session.EnqueuePlaybackAsync(second, queueMaxDepth: 4);
        session.CompletePlayback();

        await pumpTask;

        played.ShouldBe(["first", "first", "second"]);
    }

    private static async IAsyncEnumerable<AudioChunk> GenerateAudio(string label, int count)
    {
        for (var i = 0; i < count; i++)
        {
            yield return new AudioChunk
            {
                Data = System.Text.Encoding.UTF8.GetBytes(label),
                Format = AudioFormat.WyomingStandard
            };
            await Task.Yield();
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionPlaybackTests" --no-restore`
Expected: FAIL — `PlaybackJob`, `AnnouncePriority`, etc., missing.

- [ ] **Step 3: Add `AnnouncePriority` enum**

```csharp
// Domain/DTOs/Voice/AnnouncePriority.cs
namespace Domain.DTOs.Voice;

public enum AnnouncePriority { Low, Normal, High }
```

- [ ] **Step 4: Add the `PlaybackJob` record and extend `SatelliteSession`**

Replace the contents of `McpChannelVoice/Services/SatelliteSession.cs` with:

```csharp
// McpChannelVoice/Services/SatelliteSession.cs
using System.Threading.Channels;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed record PlaybackJob(
    string Label,
    AnnouncePriority Priority,
    IAsyncEnumerable<AudioChunk> Audio,
    Func<string, Task> OnStarted,
    Func<string, Task> OnPreempted);

public sealed class SatelliteSession
{
    private readonly Channel<ReadOnlyMemory<byte>> _inbound = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    private readonly Channel<PlaybackJob> _playback = Channel.CreateUnbounded<PlaybackJob>();
    private CancellationTokenSource? _currentPlaybackCts;
    private readonly Lock _gate = new();

    public SatelliteSession(string satelliteId, SatelliteConfig config)
    {
        SatelliteId = satelliteId;
        Config = config;
    }

    public string SatelliteId { get; }
    public string ConversationId => SatelliteId;
    public SatelliteConfig Config { get; }

    public ValueTask PublishAudioAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct) =>
        _inbound.Writer.WriteAsync(bytes, ct);

    public void CompleteInboundAudio() => _inbound.Writer.TryComplete();

    public IAsyncEnumerable<ReadOnlyMemory<byte>> ReadInboundAudioAsync(CancellationToken ct) =>
        _inbound.Reader.ReadAllAsync(ct);

    public async ValueTask<bool> EnqueuePlaybackAsync(PlaybackJob job, int queueMaxDepth)
    {
        if (job.Priority == AnnouncePriority.High)
        {
            PreemptCurrent();
            await _playback.Writer.WriteAsync(job);
            return true;
        }

        if (job.Priority == AnnouncePriority.Low && _playback.Reader.Count > 0)
        {
            return false;
        }

        if (_playback.Reader.Count >= queueMaxDepth)
        {
            return false;
        }
        await _playback.Writer.WriteAsync(job);
        return true;
    }

    public void CompletePlayback() => _playback.Writer.TryComplete();

    public void PreemptCurrent()
    {
        lock (_gate)
        {
            _currentPlaybackCts?.Cancel();
        }
    }

    public async Task RunPlaybackLoopAsync(
        Func<AudioChunk, CancellationToken, Task> writer,
        CancellationToken ct)
    {
        await foreach (var job in _playback.Reader.ReadAllAsync(ct))
        {
            var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate) { _currentPlaybackCts = jobCts; }

            await job.OnStarted(job.Label);

            try
            {
                await foreach (var chunk in job.Audio.WithCancellation(jobCts.Token))
                {
                    await writer(chunk, jobCts.Token);
                }
            }
            catch (OperationCanceledException) when (jobCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                await job.OnPreempted(job.Label);
            }
            finally
            {
                lock (_gate) { _currentPlaybackCts = null; }
                jobCts.Dispose();
            }
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SatelliteSessionPlaybackTests" --no-restore`
Expected: 1 passed.

- [ ] **Step 6: Commit**

```bash
git add Domain/DTOs/Voice/AnnouncePriority.cs \
        McpChannelVoice/Services/SatelliteSession.cs \
        Tests/Unit/McpChannelVoice/SatelliteSessionPlaybackTests.cs
git commit -m "feat(voice): playback queue + priority on SatelliteSession"
```

---

### Task 3.2: `WyomingTextToSpeech` adapter (calls `wyoming-piper`)

Piper's Wyoming flow:
1. Client → `{"type":"synthesize","text":"hola","voice":{"name":"es_ES-davefx-medium"}}`
2. Server → `{"type":"audio-start","rate":22050,"width":2,"channels":1}`
3. Server → repeated `{"type":"audio-chunk","payload_length":N}` + PCM payload
4. Server → `{"type":"audio-stop"}`

**Files:**
- Create: `McpChannelVoice/Services/Tts/WyomingTextToSpeech.cs`
- Test: `Tests/Unit/McpChannelVoice/Tts/WyomingTextToSpeechTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/Tts/WyomingTextToSpeechTests.cs
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.Tts;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice.Tts;

public class WyomingTextToSpeechTests
{
    [Fact]
    public async Task SynthesizeAsync_StreamsChunksBack()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            await using var stream = client.GetStream();
            var reader = new WyomingReader(stream);
            var writer = new WyomingWriter(stream);

            await foreach (var evt in reader.ReadAllAsync(CancellationToken.None))
            {
                if (evt.Type != "synthesize") continue;

                await writer.WriteAsync(WyomingEvent.Header("audio-start",
                    new JsonObject { ["rate"] = 22050, ["width"] = 2, ["channels"] = 1 }),
                    CancellationToken.None);
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk",
                    new JsonObject { ["rate"] = 22050, ["width"] = 2, ["channels"] = 1 },
                    new byte[] { 1, 2, 3, 4 }),
                    CancellationToken.None);
                await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk",
                    new JsonObject { ["rate"] = 22050, ["width"] = 2, ["channels"] = 1 },
                    new byte[] { 5, 6, 7, 8 }),
                    CancellationToken.None);
                await writer.WriteAsync(WyomingEvent.Header("audio-stop", new JsonObject()),
                    CancellationToken.None);
                return;
            }
        });

        var sut = new WyomingTextToSpeech(
            new WyomingTtsConfig { Host = "127.0.0.1", Port = port, Voice = "es_ES-davefx-medium" },
            NullLogger<WyomingTextToSpeech>.Instance);

        var collected = new List<byte>();
        await foreach (var chunk in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
        {
            collected.AddRange(chunk.Data.ToArray());
        }
        await serverTask;
        listener.Stop();

        collected.ShouldBe([1, 2, 3, 4, 5, 6, 7, 8]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingTextToSpeechTests" --no-restore`
Expected: FAIL.

- [ ] **Step 3: Implement adapter**

```csharp
// McpChannelVoice/Services/Tts/WyomingTextToSpeech.cs
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services.Tts;

public sealed class WyomingTextToSpeech(
    WyomingTtsConfig config,
    ILogger<WyomingTextToSpeech> logger) : ITextToSpeech
{
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await using var client = new WyomingClient();
        await client.ConnectAsync(config.Host, config.Port, ct);

        var voice = options.Voice ?? config.Voice;
        var data = new JsonObject { ["text"] = text };
        if (voice is not null)
        {
            data["voice"] = new JsonObject { ["name"] = voice };
        }
        await client.WriteAsync(WyomingEvent.Header("synthesize", data), ct);

        AudioFormat? format = null;

        await foreach (var evt in client.ReadAllAsync(ct))
        {
            if (evt.Type == "audio-start")
            {
                format = new AudioFormat
                {
                    SampleRateHz = evt.Data["rate"]?.GetValue<int>() ?? 22050,
                    SampleWidthBytes = evt.Data["width"]?.GetValue<int>() ?? 2,
                    Channels = evt.Data["channels"]?.GetValue<int>() ?? 1
                };
                continue;
            }
            if (evt.Type == "audio-chunk" && evt.Payload.Length > 0)
            {
                yield return new AudioChunk
                {
                    Data = evt.Payload,
                    Format = format ?? AudioFormat.WyomingStandard
                };
                continue;
            }
            if (evt.Type == "audio-stop")
            {
                logger.LogDebug("Piper synthesis complete");
                yield break;
            }
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WyomingTextToSpeechTests" --no-restore`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/Tts/WyomingTextToSpeech.cs \
        Tests/Unit/McpChannelVoice/Tts/WyomingTextToSpeechTests.cs
git commit -m "feat(voice): WyomingTextToSpeech adapter"
```

---

### Task 3.3: `SendReplyTool` rewrite — synthesise and enqueue

`SendReplyTool` now resolves the active `SatelliteSession` by conversation id and enqueues a TTS playback job. Non-text content types (`Reasoning`, `ToolCall`) are ignored; `Error` is spoken as a short prefixed message; `StreamComplete` is a no-op (audio drains naturally). The text accumulator is per-message; `IsComplete=true` triggers the actual synthesis.

**Files:**
- Modify: `McpChannelVoice/McpTools/SendReplyTool.cs`
- Create: `McpChannelVoice/Services/ReplyTextAccumulator.cs`
- Test: `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs`

- [ ] **Step 1: Update the failing test**

Replace the entire content of `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs` with:

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Voice;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SendReplyToolTests
{
    private readonly SatelliteSession _session;
    private readonly SatelliteSessionRegistry _sessions = new();
    private readonly ReplyTextAccumulator _accumulator = new();
    private readonly Mock<ITextToSpeech> _tts = new();
    private readonly IServiceProvider _services;

    public SendReplyToolTests()
    {
        _session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        _sessions.Register(_session);

        _tts.Setup(t => t.SynthesizeAsync(
                It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((text, _, _) => EmptyAudio(text));

        _services = new ServiceCollection()
            .AddSingleton(_sessions)
            .AddSingleton(_accumulator)
            .AddSingleton(_tts.Object)
            .AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>())
            .AddSingleton(new VoiceSettings())
            .AddSingleton<ILogger<SendReplyTool>>(NullLogger<SendReplyTool>.Instance)
            .BuildServiceProvider();
    }

    private static async IAsyncEnumerable<AudioChunk> EmptyAudio(string label)
    {
        yield return new AudioChunk
        {
            Data = System.Text.Encoding.UTF8.GetBytes(label),
            Format = AudioFormat.WyomingStandard
        };
        await Task.Yield();
    }

    [Fact]
    public async Task McpRun_Text_NotComplete_AccumulatesNoSynthesis()
    {
        var result = await SendReplyTool.McpRun("kitchen-01", "hola ", ReplyContentType.Text, false, "m-1", _services);

        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpRun_Text_Complete_SynthesisesAccumulatedText()
    {
        await SendReplyTool.McpRun("kitchen-01", "hola ", ReplyContentType.Text, false, "m-1", _services);
        await SendReplyTool.McpRun("kitchen-01", "mundo", ReplyContentType.Text, true, "m-1", _services);

        _tts.Verify(t => t.SynthesizeAsync("hola mundo", It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_Error_SpeaksErrorPrefix()
    {
        await SendReplyTool.McpRun("kitchen-01", "boom", ReplyContentType.Error, true, "m-1", _services);
        _tts.Verify(t => t.SynthesizeAsync(
            It.Is<string>(s => s.Contains("boom")), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task McpRun_Reasoning_DoesNothing()
    {
        var result = await SendReplyTool.McpRun("kitchen-01", "thinking", ReplyContentType.Reasoning, false, null, _services);
        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task McpRun_UnknownConversation_ReturnsOk()
    {
        // No session for 'ghost-01' — tool gracefully no-ops, doesn't throw.
        var result = await SendReplyTool.McpRun("ghost-01", "hi", ReplyContentType.Text, true, "m-1", _services);
        result.ShouldBe("ok");
        _tts.VerifyNoOtherCalls();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SendReplyToolTests" --no-restore`
Expected: FAIL — `ReplyTextAccumulator` missing, behavior wrong.

- [ ] **Step 3: Create the accumulator**

```csharp
// McpChannelVoice/Services/ReplyTextAccumulator.cs
using System.Collections.Concurrent;
using System.Text;

namespace McpChannelVoice.Services;

public sealed class ReplyTextAccumulator
{
    private readonly ConcurrentDictionary<string, StringBuilder> _buffers = new();

    public void Append(string conversationId, string messageId, string text)
    {
        var key = $"{conversationId}|{messageId}";
        _buffers.AddOrUpdate(key,
            _ => new StringBuilder(text),
            (_, sb) => sb.Append(text));
    }

    public string Flush(string conversationId, string messageId)
    {
        var key = $"{conversationId}|{messageId}";
        return _buffers.TryRemove(key, out var sb) ? sb.ToString() : string.Empty;
    }
}
```

- [ ] **Step 4: Replace `SendReplyTool`**

```csharp
// McpChannelVoice/McpTools/SendReplyTool.cs
using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = ChannelProtocol.SendReplyTool)]
    [Description("Speak a response chunk on the originating voice satellite")]
    public static async Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services)
    {
        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var accumulator = services.GetRequiredService<ReplyTextAccumulator>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var settings = services.GetRequiredService<VoiceSettings>();
        var metrics = services.GetRequiredService<IMetricsPublisher>();

        var session = sessions.Get(conversationId);
        if (session is null) return "ok";

        switch (contentType)
        {
            case ReplyContentType.Reasoning:
            case ReplyContentType.ToolCall:
            case ReplyContentType.StreamComplete:
                return "ok";

            case ReplyContentType.Error:
                await SpeakAsync(session, $"Hubo un error: {content}", tts, settings, metrics, default);
                return "ok";

            default:
                accumulator.Append(conversationId, messageId ?? "_default", content);
                if (isComplete)
                {
                    var text = accumulator.Flush(conversationId, messageId ?? "_default");
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        await SpeakAsync(session, text, tts, settings, metrics, default);
                    }
                }
                return "ok";
        }
    }

    private static async Task SpeakAsync(
        SatelliteSession session,
        string text,
        ITextToSpeech tts,
        VoiceSettings settings,
        IMetricsPublisher metrics,
        CancellationToken ct)
    {
        var voice = session.Config.Tts?.Wyoming?.Voice ?? settings.Tts.Wyoming?.Voice;
        var options = new SynthesisOptions { Voice = voice };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var job = new PlaybackJob(
            Label: $"reply:{session.SatelliteId}",
            Priority: AnnouncePriority.Normal,
            Audio: tts.SynthesizeAsync(text, options, ct),
            OnStarted: async _ =>
            {
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.WakeToFirstAudioMs,
                    SatelliteId = session.SatelliteId,
                    Room = session.Config.Room,
                    Identity = session.Config.Identity,
                    DurationMs = sw.ElapsedMilliseconds,
                    ConversationId = session.ConversationId
                });
            },
            OnPreempted: async _ =>
            {
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.AnnouncePreemptedReply,
                    SatelliteId = session.SatelliteId,
                    ConversationId = session.ConversationId
                });
            });

        await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);

        await metrics.PublishAsync(new VoiceEvent
        {
            Metric = VoiceMetric.TtsLatencyMs,
            SatelliteId = session.SatelliteId,
            Room = session.Config.Room,
            Identity = session.Config.Identity,
            DurationMs = sw.ElapsedMilliseconds,
            ConversationId = session.ConversationId
        });
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SendReplyToolTests" --no-restore`
Expected: 5 passed.

- [ ] **Step 6: Commit**

```bash
git add McpChannelVoice/McpTools/SendReplyTool.cs \
        McpChannelVoice/Services/ReplyTextAccumulator.cs \
        Tests/Unit/McpChannelVoice/SendReplyToolTests.cs
git commit -m "feat(voice): SendReplyTool synthesises and enqueues playback"
```

---

### Task 3.4: Wire playback writeback into `WyomingServer`

`WyomingServer` must, for each connected satellite, run the playback loop and write each chunk as Wyoming `audio-start`/`audio-chunk`/`audio-stop` frames on the open satellite stream.

**Files:**
- Modify: `McpChannelVoice/Services/WyomingServer.cs`

- [ ] **Step 1: Update `HandleClientAsync` to also pump playback**

In `WyomingServer.HandleClientAsync`, after creating the `session`, kick off a playback writer task. Replace the relevant section so the method becomes:

```csharp
    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        await using var stream = client.GetStream();
        var reader = new WyomingReader(stream);
        var writer = new WyomingWriter(stream);
        SatelliteSession? session = null;
        Task? playbackTask = null;

        try
        {
            await foreach (var evt in reader.ReadAllAsync(ct))
            {
                if (evt.Type == "info")
                {
                    var name = evt.Data["satellite"]?["name"]?.GetValue<string>()
                               ?? throw new InvalidDataException("info missing satellite.name");
                    var cfg = satelliteRegistry.GetById(name)
                              ?? throw new InvalidOperationException($"Unknown satellite '{name}'");
                    session = new SatelliteSession(name, cfg);
                    sessionRegistry.Register(session);
                    logger.LogInformation("Satellite {Id} connected (identity={Identity})", name, cfg.Identity);

                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.WakeTriggered,
                        SatelliteId = session.SatelliteId,
                        Room = cfg.Room,
                        Identity = cfg.Identity,
                        WakeWord = cfg.WakeWord,
                        ConversationId = session.ConversationId
                    }, ct);

                    var capturedSession = session;
                    var capturedWriter = writer;
                    playbackTask = Task.Run(() => capturedSession.RunPlaybackLoopAsync(
                        async (chunk, jct) => await WritePlaybackFrameAsync(capturedWriter, chunk, jct), ct), ct);

                    _ = Task.Run(() => RunTranscriptionAsync(capturedSession, ct), ct);
                    continue;
                }

                if (session is null) continue;

                if (evt.Type == "audio-start") continue;
                if (evt.Type == "audio-chunk") { await session.PublishAudioAsync(evt.Payload, ct); continue; }
                if (evt.Type == "audio-stop")  { session.CompleteInboundAudio(); continue; }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling Wyoming client");
        }
        finally
        {
            if (session is not null)
            {
                session.CompletePlayback();
                if (playbackTask is not null) try { await playbackTask; } catch { /* ignore */ }
                sessionRegistry.Unregister(session.SatelliteId);
            }
            client.Dispose();
        }
    }

    private static async Task WritePlaybackFrameAsync(WyomingWriter writer, AudioChunk chunk, CancellationToken ct)
    {
        var data = new System.Text.Json.Nodes.JsonObject
        {
            ["rate"] = chunk.Format.SampleRateHz,
            ["width"] = chunk.Format.SampleWidthBytes,
            ["channels"] = chunk.Format.Channels
        };
        await writer.WriteAsync(WyomingEvent.WithPayload("audio-chunk", data, chunk.Data), ct);
    }
```

(The `using System.Text.Json.Nodes;` import already exists.)

- [ ] **Step 2: Add the missing `using` line at the top of the file**

Add `using Domain.DTOs.Voice;` at the top of `McpChannelVoice/Services/WyomingServer.cs` if not already present.

- [ ] **Step 3: Verify the project still builds**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add McpChannelVoice/Services/WyomingServer.cs
git commit -m "feat(voice): pipe playback queue back through Wyoming writer"
```

---

### Task 3.5: Add `ITextToSpeech` + Wyoming TTS service to `ConfigModule`; compose adds `wyoming-piper`

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`
- Modify: `DockerCompose/docker-compose.yml`

- [ ] **Step 1: Add TTS registration in `ConfigModule`**

Inside `ConfigureVoiceChannel`, after the STT registration block, add:

```csharp
        services.AddSingleton<ReplyTextAccumulator>();

        if (settings.Tts.Provider.Equals("Wyoming", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITextToSpeech>(sp => new McpChannelVoice.Services.Tts.WyomingTextToSpeech(
                settings.Tts.Wyoming ?? throw new InvalidOperationException("Tts.Wyoming missing"),
                sp.GetRequiredService<ILogger<McpChannelVoice.Services.Tts.WyomingTextToSpeech>>()));
        }
```

- [ ] **Step 2: Add `wyoming-piper` to `docker-compose.yml`**

After the `wyoming-whisper` block:

```yaml
  wyoming-piper:
    image: rhasspy/wyoming-piper:latest
    container_name: wyoming-piper
    command: --voice es_ES-davefx-medium --uri tcp://0.0.0.0:10200
    volumes:
      - ./volumes/piper-data:/data
    restart: unless-stopped
    networks:
      - jackbot
```

Add `wyoming-piper` to `mcp-channel-voice`'s `depends_on`:

```yaml
      wyoming-piper:
        condition: service_started
```

- [ ] **Step 3: Smoke-build**

Run:

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot build mcp-channel-voice
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot pull wyoming-piper
```

Expected: success.

- [ ] **Step 4: Commit**

```bash
git add McpChannelVoice/Modules/ConfigModule.cs DockerCompose/docker-compose.yml
git commit -m "feat(voice): wire TTS pipeline + add wyoming-piper service"
```

---

### Task 3.6: `MetricsApiEndpoints` — `/api/metrics/voice/*` endpoints

**Files:**
- Modify: `Observability/MetricsApiEndpoints.cs`
- Modify: `Observability/Services/MetricsQueryService.cs`
- Test: `Tests/Unit/Observability/VoiceMetricsQueryTests.cs`

- [ ] **Step 1: Write a failing test**

```csharp
// Tests/Unit/Observability/VoiceMetricsQueryTests.cs
using System.Reflection;
using Domain.DTOs.Metrics.Enums;
using Observability.Services;
using Shouldly;

namespace Tests.Unit.Observability;

public class VoiceMetricsQueryTests
{
    [Fact]
    public void MetricsQueryService_HasGetVoiceGroupedAsync()
    {
        var method = typeof(MetricsQueryService).GetMethod(
            "GetVoiceGroupedAsync",
            BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull();

        var parameters = method!.GetParameters().Select(p => p.ParameterType).ToArray();
        parameters.ShouldContain(typeof(VoiceDimension));
        parameters.ShouldContain(typeof(VoiceMetric));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceMetricsQueryTests" --no-restore`
Expected: FAIL.

- [ ] **Step 3: Add the query method**

In `Observability/Services/MetricsQueryService.cs`, append next to the existing `GetMemoryGroupedAsync` / `GetLatencyGroupedAsync` methods:

```csharp
    public async Task<Dictionary<string, decimal>> GetVoiceGroupedAsync(
        VoiceDimension dimension,
        VoiceMetric metric,
        DateOnly from,
        DateOnly to)
    {
        var events = await GetEventsAsync<VoiceEvent>("metrics:voice:", from, to);
        IEnumerable<VoiceEvent> scoped = events.Where(e => e.Metric == metric);

        Func<VoiceEvent, string?> selector = dimension switch
        {
            VoiceDimension.SatelliteId  => e => e.SatelliteId,
            VoiceDimension.Room         => e => e.Room,
            VoiceDimension.Identity     => e => e.Identity,
            VoiceDimension.WakeWord     => e => e.WakeWord,
            VoiceDimension.Language     => e => e.Language,
            VoiceDimension.SttProvider  => e => e.SttProvider,
            VoiceDimension.SttModel     => e => e.SttModel,
            VoiceDimension.TtsProvider  => e => e.TtsProvider,
            VoiceDimension.TtsVoice     => e => e.TtsVoice,
            VoiceDimension.Outcome      => e => e.Outcome,
            VoiceDimension.Source       => e => e.Source,
            VoiceDimension.Priority     => e => e.Priority,
            _ => e => e.SatelliteId
        };

        return scoped
            .GroupBy(e => selector(e) ?? "(unknown)")
            .ToDictionary(
                g => g.Key,
                g => metric switch
                {
                    VoiceMetric.SttLatencyMs       => (decimal)(g.Average(e => e.DurationMs ?? 0)),
                    VoiceMetric.TtsLatencyMs       => (decimal)(g.Average(e => e.DurationMs ?? 0)),
                    VoiceMetric.WakeToFirstAudioMs => (decimal)(g.Average(e => e.DurationMs ?? 0)),
                    VoiceMetric.AudioSeconds       => (decimal)(g.Sum(e => e.AudioSeconds ?? 0)),
                    _ => (decimal)g.Count()
                });
    }
```

- [ ] **Step 4: Add the API endpoints**

In `Observability/MetricsApiEndpoints.cs`, after `latency/trend` mapping (before the closing brace of `MapMetricsApi`), append:

```csharp
        api.MapGet("/voice", async (
            MetricsQueryService query,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetEventsAsync<VoiceEvent>("metrics:voice:", fromDate, toDate);
        });

        api.MapGet("/voice/by/{dimension}", async (
            MetricsQueryService query,
            VoiceDimension dimension,
            VoiceMetric metric,
            DateOnly? from,
            DateOnly? to) =>
        {
            var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
            return await query.GetVoiceGroupedAsync(dimension, metric, fromDate, toDate);
        });
```

- [ ] **Step 5: Update the metrics-events sink to route voice events to `metrics:voice:*`**

Find `MetricsCollectorService` (the subscriber that maps event type → Redis key prefix). Add a case for `VoiceEvent`:

```bash
grep -nE "case .*Event(:| =>)" Observability/Services/MetricsCollectorService.cs
```

Inside its event-routing switch (analogous to the existing `ToolCallEvent → metrics:tools:` mapping), add:

```csharp
            case VoiceEvent voice:
                await StoreEventAsync("metrics:voice:", voice, ct);
                break;
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceMetricsQueryTests" --no-restore`
Expected: 1 passed.

- [ ] **Step 7: Commit**

```bash
git add Observability/MetricsApiEndpoints.cs \
        Observability/Services/MetricsQueryService.cs \
        Observability/Services/MetricsCollectorService.cs \
        Tests/Unit/Observability/VoiceMetricsQueryTests.cs
git commit -m "feat(observability): voice metrics endpoints + collector routing"
```

---

### Task 3.7: Dashboard — `Voice.razor` page with KPIs and charts

**Files:**
- Create: `Dashboard.Client/Pages/Voice.razor`
- Create: `Dashboard.Client/State/Voice/VoiceState.cs`
- Create: `Dashboard.Client/State/Voice/VoiceStore.cs`
- Create: `Dashboard.Client/Services/VoiceApiMethods.cs` (extension on `MetricsApiService`)
- Modify: `Dashboard.Client/Components/MainNav.razor` (or whatever the nav file is) — add "Voice" entry

- [ ] **Step 1: Locate the nav component**

Run: `grep -lE "Tools.razor|/tools" Dashboard.Client/Components/*.razor Dashboard.Client/Layout/*.razor 2>/dev/null`
Expected: 1 file. Open it for the next step.

- [ ] **Step 2: Add the state and store (clone of `ToolsState`/`ToolsStore`)**

```csharp
// Dashboard.Client/State/Voice/VoiceState.cs
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Voice;

public record VoiceState
{
    public IReadOnlyList<VoiceEvent> Events { get; init; } = [];
    public VoiceDimension GroupBy { get; init; } = VoiceDimension.SatelliteId;
    public VoiceMetric Metric { get; init; } = VoiceMetric.UtteranceTranscribed;
    public Dictionary<string, decimal> Breakdown { get; init; } = [];
    public DateOnly From { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
    public DateOnly To { get; init; } = DateOnly.FromDateTime(DateTime.UtcNow);
}
```

```csharp
// Dashboard.Client/State/Voice/VoiceStore.cs
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;

namespace Dashboard.Client.State.Voice;

public sealed class VoiceStore
{
    private readonly BehaviorSubject<VoiceState> _state = new(new VoiceState());

    public VoiceState State => _state.Value;
    public IObservable<VoiceState> StateObservable => _state.AsObservable();

    public void SetEvents(IReadOnlyList<VoiceEvent> events) =>
        _state.OnNext(_state.Value with { Events = events });
    public void SetGroupBy(VoiceDimension dim) =>
        _state.OnNext(_state.Value with { GroupBy = dim });
    public void SetMetric(VoiceMetric metric) =>
        _state.OnNext(_state.Value with { Metric = metric });
    public void SetBreakdown(Dictionary<string, decimal> b) =>
        _state.OnNext(_state.Value with { Breakdown = b });
    public void SetDateRange(DateOnly from, DateOnly to) =>
        _state.OnNext(_state.Value with { From = from, To = to });
}
```

- [ ] **Step 3: Extend `MetricsApiService`**

Find the existing `MetricsApiService` (likely `Dashboard.Client/Services/MetricsApiService.cs`). Add:

```csharp
    public async Task<IReadOnlyList<VoiceEvent>> GetVoiceEventsAsync(DateOnly from, DateOnly to)
    {
        var url = $"/api/metrics/voice?from={from:O}&to={to:O}";
        return await Http.GetFromJsonAsync<List<VoiceEvent>>(url) ?? [];
    }

    public async Task<Dictionary<string, decimal>?> GetVoiceGroupedAsync(
        VoiceDimension dim, VoiceMetric metric, DateOnly from, DateOnly to)
    {
        var url = $"/api/metrics/voice/by/{dim}?metric={metric}&from={from:O}&to={to:O}";
        return await Http.GetFromJsonAsync<Dictionary<string, decimal>>(url);
    }
```

(Adjust to match the conventions used by `GetToolGroupedAsync` in the same file.)

- [ ] **Step 4: Create the page**

```razor
@* Dashboard.Client/Pages/Voice.razor *@
@page "/voice"
@using Dashboard.Client.State.Voice
@using Domain.DTOs.Metrics
@using Domain.DTOs.Metrics.Enums
@using static Dashboard.Client.Components.PillSelector
@implements IDisposable
@inject VoiceStore Store
@inject MetricsApiService Api
@inject LocalStorageService Storage

<div class="voice-page">
    <header class="page-header">
        <h2>Voice</h2>
        <div class="controls">
            <PillSelector Label="Group by" Options="DimensionOptions" Value="@_state.GroupBy.ToString()" OnChanged="OnDimensionChanged" />
            <PillSelector Label="Metric"   Options="MetricOptions"    Value="@_state.Metric.ToString()"  OnChanged="OnMetricChanged" />
            <PillSelector Label="Time"     Options="TimeOptions"      Value="@_selectedDays.ToString()"  OnChanged="OnTimeChanged" />
        </div>
    </header>

    <section class="kpi-row">
        <KpiCard Label="Utterances (24h)" Value="@_utterances.ToString("N0")" Color="var(--accent-blue)" />
        <KpiCard Label="Median wake → audio" Value="@($"{_medianLatency:F0}ms")" Color="var(--accent-yellow)" />
        <KpiCard Label="STT errors (24h)" Value="@_sttErrors.ToString("N0")" Color="var(--accent-red)" />
        <KpiCard Label="TTS errors (24h)" Value="@_ttsErrors.ToString("N0")" Color="var(--accent-red)" />
    </section>

    <section class="section">
        <DynamicChart Data="_state.Breakdown" ChartType="DynamicChart.ChartMode.HorizontalBar"
                      MetricLabel="@_state.Metric.ToString()" Unit="@GetMetricUnit()" />
    </section>
</div>

@code {
    private VoiceState _state = new();
    private int _selectedDays = 1;
    private DateOnly _from = DateOnly.FromDateTime(DateTime.UtcNow);
    private DateOnly _to   = DateOnly.FromDateTime(DateTime.UtcNow);
    private long _utterances;
    private double _medianLatency;
    private long _sttErrors;
    private long _ttsErrors;
    private IDisposable? _sub;

    private static readonly IReadOnlyList<PillOption> DimensionOptions =
    [
        new("Satellite", nameof(VoiceDimension.SatelliteId)),
        new("Room",      nameof(VoiceDimension.Room)),
        new("Identity",  nameof(VoiceDimension.Identity)),
        new("WakeWord",  nameof(VoiceDimension.WakeWord)),
        new("Language",  nameof(VoiceDimension.Language)),
        new("Source",    nameof(VoiceDimension.Source)),
    ];

    private static readonly IReadOnlyList<PillOption> MetricOptions =
    [
        new("Utterances",        nameof(VoiceMetric.UtteranceTranscribed)),
        new("Wake → audio (ms)", nameof(VoiceMetric.WakeToFirstAudioMs)),
        new("STT latency (ms)",  nameof(VoiceMetric.SttLatencyMs)),
        new("TTS latency (ms)",  nameof(VoiceMetric.TtsLatencyMs)),
        new("Announcements",     nameof(VoiceMetric.AnnouncePlayed)),
        new("STT errors",        nameof(VoiceMetric.SttError)),
    ];

    private static readonly IReadOnlyList<PillOption> TimeOptions =
    [
        new("Today", "1"),
        new("7d",    "7"),
        new("30d",   "30"),
    ];

    protected override async Task OnInitializedAsync()
    {
        _sub = Store.StateObservable.Subscribe(s =>
        {
            _state = s;
            _utterances = s.Events.Count(e => e.Metric == VoiceMetric.UtteranceTranscribed && e.Outcome == "dispatched");
            var lat = s.Events.Where(e => e.Metric == VoiceMetric.WakeToFirstAudioMs && e.DurationMs is not null)
                              .Select(e => e.DurationMs!.Value).OrderBy(x => x).ToList();
            _medianLatency = lat.Count > 0 ? lat[lat.Count / 2] : 0;
            _sttErrors = s.Events.Count(e => e.Metric == VoiceMetric.SttError);
            _ttsErrors = s.Events.Count(e => e.Metric == VoiceMetric.TtsError);
            InvokeAsync(StateHasChanged);
        });

        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to.AddDays(-(_selectedDays - 1));
        Store.SetDateRange(_from, _to);
        await ReloadAll();
    }

    private async Task ReloadAll()
    {
        var events = await Api.GetVoiceEventsAsync(_from, _to);
        Store.SetEvents(events);
        await ReloadBreakdown();
    }

    private async Task ReloadBreakdown()
    {
        var breakdown = await Api.GetVoiceGroupedAsync(Store.State.GroupBy, Store.State.Metric, _from, _to);
        Store.SetBreakdown(breakdown ?? []);
    }

    private async Task OnDimensionChanged(string v) { Store.SetGroupBy(Enum.Parse<VoiceDimension>(v)); await ReloadBreakdown(); }
    private async Task OnMetricChanged(string v)    { Store.SetMetric(Enum.Parse<VoiceMetric>(v));     await ReloadBreakdown(); }
    private async Task OnTimeChanged(string v)
    {
        _selectedDays = int.Parse(v);
        _to = DateOnly.FromDateTime(DateTime.UtcNow);
        _from = _to.AddDays(-(_selectedDays - 1));
        Store.SetDateRange(_from, _to);
        await ReloadAll();
    }

    private string GetMetricUnit() => _state.Metric switch
    {
        VoiceMetric.WakeToFirstAudioMs => "ms",
        VoiceMetric.SttLatencyMs       => "ms",
        VoiceMetric.TtsLatencyMs       => "ms",
        _ => ""
    };

    public void Dispose() => _sub?.Dispose();
}

<style>
    .voice-page { display: flex; flex-direction: column; gap: 1.5rem; }
    .page-header { display: flex; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; gap: 1rem; }
    .page-header h2 { font-size: 1.4rem; font-weight: 600; }
    .controls { display: flex; gap: 1.5rem; flex-wrap: wrap; align-items: flex-end; }
    .kpi-row { display: flex; gap: 1rem; flex-wrap: wrap; }
</style>
```

- [ ] **Step 5: Register `VoiceStore` in DI**

Find the dashboard's Program.cs / DI module (`grep -l "ToolsStore" Dashboard.Client/`) and add `services.AddSingleton<VoiceStore>();` alongside the existing store registrations.

- [ ] **Step 6: Add the nav entry**

In the nav file located in Step 1, add a `<NavLink href="voice">Voice</NavLink>` entry alongside the existing `Tools` link (copy whatever icon convention is in use).

- [ ] **Step 7: Build the dashboard**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 8: Commit**

```bash
git add Dashboard.Client/Pages/Voice.razor \
        Dashboard.Client/State/Voice/ \
        Dashboard.Client/Services/MetricsApiService.cs \
        Dashboard.Client/Components/*.razor \
        Dashboard.Client/Layout/*.razor
git commit -m "feat(dashboard): Voice page with KPIs and breakdown chart"
```

---

### Task 3.8: Overview KPIs + HealthGrid additions

**Files:**
- Modify: `Dashboard.Client/Pages/Overview.razor`
- Modify: `Dashboard.Client/Components/HealthGrid.razor`

- [ ] **Step 1: Read the current Overview file to find the KPI list**

Run: `grep -n "KpiCard" Dashboard.Client/Pages/Overview.razor | head -10`
Expected: a block of `KpiCard` lines.

- [ ] **Step 2: Add two voice KPIs**

In `Overview.razor`, append two more `KpiCard` instances next to the existing ones:

```razor
<KpiCard Label="Utterances (24h)" Value="@_voiceUtterances.ToString("N0")" Color="var(--accent-blue)" />
<KpiCard Label="Median voice latency" Value="@($"{_voiceLatency:F0}ms")" Color="var(--accent-yellow)" />
```

Then bind these in `@code` by fetching `VoiceEvent`s from the API on init (mirror the existing pattern that loads `ToolCallEvent`s).

- [ ] **Step 3: Add voice services to `HealthGrid.razor`**

Find the list of monitored services in `HealthGrid.razor` and add:

- `mcp-channel-voice`
- `wyoming-whisper`
- `wyoming-piper`

If `HealthGrid` is auto-populated from heartbeats, the channel's `HeartbeatService` already publishes for `mcp-channel-voice`. For `wyoming-whisper`/`wyoming-piper`, the channel needs to publish heartbeats on their behalf (Task 3.9).

- [ ] **Step 4: Verify build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add Dashboard.Client/Pages/Overview.razor Dashboard.Client/Components/HealthGrid.razor
git commit -m "feat(dashboard): Overview voice KPIs + HealthGrid voice services"
```

---

### Task 3.9: Wyoming backend health probe (channel emits heartbeats for whisper/piper)

**Files:**
- Create: `McpChannelVoice/Services/WyomingHealthProbeService.cs`
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`

- [ ] **Step 1: Implement the probe**

```csharp
// McpChannelVoice/Services/WyomingHealthProbeService.cs
using Domain.Contracts;
using Domain.DTOs.Metrics;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class WyomingHealthProbeService(
    VoiceSettings settings,
    IMetricsPublisher publisher,
    ILogger<WyomingHealthProbeService> logger) : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var targets = new List<(string Service, string Host, int Port)>();
        if (settings.Stt.Wyoming is { } stt) targets.Add(("wyoming-whisper", stt.Host, stt.Port));
        if (settings.Tts.Wyoming is { } tts) targets.Add(("wyoming-piper",   tts.Host, tts.Port));

        while (!stoppingToken.IsCancellationRequested)
        {
            foreach (var (service, host, port) in targets)
            {
                try
                {
                    using var tcp = new System.Net.Sockets.TcpClient();
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    cts.CancelAfter(TimeSpan.FromSeconds(5));
                    await tcp.ConnectAsync(host, port, cts.Token);
                    await publisher.PublishAsync(new HeartbeatEvent { Service = service }, stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Wyoming probe failed: {Service}@{Host}:{Port}", service, host, port);
                }
            }
            await Task.Delay(_interval, stoppingToken);
        }
    }
}
```

- [ ] **Step 2: Register it in `ConfigModule`**

Inside `ConfigureVoiceChannel`, add:

```csharp
        services.AddHostedService<WyomingHealthProbeService>();
```

- [ ] **Step 3: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add McpChannelVoice/Services/WyomingHealthProbeService.cs \
        McpChannelVoice/Modules/ConfigModule.cs
git commit -m "feat(voice): probe whisper/piper TCP and emit heartbeats"
```

---

### Task 3.10: Slice 3 wrap-up — manual E2E test

- [ ] **Step 1: Boot the full voice stack**

Run:

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build redis wyoming-whisper wyoming-piper mcp-channel-voice agent
```

- [ ] **Step 2: Run a desktop satellite as in Slice 2 Task 2.12**

Speak wake word + a question ("dime la hora").

- [ ] **Step 3: Confirm spoken reply**

Expected: the satellite speaker plays the agent's spoken answer. `docker logs mcp-channel-voice` shows `WakeToFirstAudioMs` and `TtsLatencyMs` events.

- [ ] **Step 4: Inspect the dashboard**

Browse `https://assistants.herfluffness.com/dashboard/voice`. Expected: KPI row populated; "Utterances" chart shows the satellite.

**Slice 3 done when:** Step 3 produces a spoken reply, the Voice dashboard page shows utterance/latency data, and HealthGrid shows the three new services green.

---

## Slice 4 — Announce HTTP endpoint

### Task 4.1: Announce DTOs

**Files:**
- Create: `Domain/DTOs/Voice/AnnounceTarget.cs`
- Create: `Domain/DTOs/Voice/AnnounceRequest.cs`
- Create: `Domain/DTOs/Voice/AnnouncementOutcome.cs`
- Create: `Domain/DTOs/Voice/AnnounceResponse.cs`
- Test: `Tests/Unit/Domain/DTOs/Voice/AnnounceDtoTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/Domain/DTOs/Voice/AnnounceDtoTests.cs
using System.Text.Json;
using Domain.DTOs.Voice;
using Shouldly;

namespace Tests.Unit.Domain.DTOs.Voice;

public class AnnounceDtoTests
{
    [Fact]
    public void AnnounceRequest_RoundTrips_WithSatelliteIdTarget()
    {
        var json = """{"target":{"satelliteId":"kitchen-01"},"text":"hi","priority":"High"}""";
        var req = JsonSerializer.Deserialize<AnnounceRequest>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });

        req.ShouldNotBeNull();
        req!.Target.SatelliteId.ShouldBe("kitchen-01");
        req.Text.ShouldBe("hi");
        req.Priority.ShouldBe(AnnouncePriority.High);
    }

    [Fact]
    public void AnnounceRequest_RoundTrips_WithRoomTarget()
    {
        var json = """{"target":{"room":"Kitchen"},"text":"hi"}""";
        var req = JsonSerializer.Deserialize<AnnounceRequest>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        req.ShouldNotBeNull();
        req!.Target.Room.ShouldBe("Kitchen");
    }

    [Fact]
    public void AnnounceRequest_RoundTrips_WithAllTarget()
    {
        var json = """{"target":{"all":true},"text":"hi"}""";
        var req = JsonSerializer.Deserialize<AnnounceRequest>(json,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        req.ShouldNotBeNull();
        req!.Target.All.ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnounceDtoTests" --no-restore`
Expected: FAIL.

- [ ] **Step 3: Implement the DTOs**

```csharp
// Domain/DTOs/Voice/AnnounceTarget.cs
namespace Domain.DTOs.Voice;

public record AnnounceTarget
{
    public string? SatelliteId { get; init; }
    public string? Room { get; init; }
    public bool? All { get; init; }
}
```

```csharp
// Domain/DTOs/Voice/AnnounceRequest.cs
namespace Domain.DTOs.Voice;

public record AnnounceRequest
{
    public required AnnounceTarget Target { get; init; }
    public required string Text { get; init; }
    public string? Voice { get; init; }
    public AnnouncePriority Priority { get; init; } = AnnouncePriority.Normal;
}
```

```csharp
// Domain/DTOs/Voice/AnnouncementOutcome.cs
namespace Domain.DTOs.Voice;

public record AnnouncementOutcome
{
    public required string Id { get; init; }
    public required string Status { get; init; }
}
```

```csharp
// Domain/DTOs/Voice/AnnounceResponse.cs
namespace Domain.DTOs.Voice;

public record AnnounceResponse
{
    public required string AnnouncementId { get; init; }
    public required IReadOnlyList<AnnouncementOutcome> Satellites { get; init; }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnounceDtoTests" --no-restore`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Voice/ Tests/Unit/Domain/DTOs/Voice/AnnounceDtoTests.cs
git commit -m "feat(voice): announce DTOs"
```

---

### Task 4.2: `AnnouncementService` — target resolution + per-satellite enqueue with priority

**Files:**
- Create: `McpChannelVoice/Services/AnnouncementService.cs`
- Test: `Tests/Unit/McpChannelVoice/AnnouncementServiceTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/AnnouncementServiceTests.cs
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class AnnouncementServiceTests
{
    private static SatelliteSession MakeSession(string id, string room) =>
        new(id, new SatelliteConfig { Identity = "household", Room = room });

    private static async IAsyncEnumerable<AudioChunk> FakeAudio()
    {
        yield return new AudioChunk
        {
            Data = new byte[16],
            Format = AudioFormat.WyomingStandard
        };
        await Task.Yield();
    }

    private (AnnouncementService Sut, SatelliteSessionRegistry SessionReg) BuildSut(params (string Id, string Room)[] sats)
    {
        var sessions = new SatelliteSessionRegistry();
        foreach (var (id, room) in sats) sessions.Register(MakeSession(id, room));

        var registry = new SatelliteRegistry(sats.ToDictionary(
            s => s.Id,
            s => new SatelliteConfig { Identity = "household", Room = s.Room }));

        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(FakeAudio());

        var settings = new VoiceSettings();
        var publisher = new Mock<IMetricsPublisher>();
        var sut = new AnnouncementService(registry, sessions, tts.Object, settings, publisher.Object,
            NullLogger<AnnouncementService>.Instance);
        return (sut, sessions);
    }

    [Fact]
    public async Task Announce_BySatelliteId_TargetsOne()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"), ("bedroom-01", "Bedroom"));

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" },
            source: "ha", CancellationToken.None);

        response.Satellites.Count.ShouldBe(1);
        response.Satellites[0].Id.ShouldBe("kitchen-01");
        response.Satellites[0].Status.ShouldBe("queued");
    }

    [Fact]
    public async Task Announce_ByRoom_TargetsAllInRoom()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"), ("kitchen-02", "Kitchen"), ("bedroom-01", "Bedroom"));

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { Room = "Kitchen" }, Text = "hi" },
            source: "ha", CancellationToken.None);

        response.Satellites.Select(s => s.Id).ShouldBe(["kitchen-01", "kitchen-02"], ignoreOrder: true);
    }

    [Fact]
    public async Task Announce_All_TargetsEverySession()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"), ("bedroom-01", "Bedroom"));

        var response = await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { All = true }, Text = "hi" },
            source: "ha", CancellationToken.None);

        response.Satellites.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Announce_UnknownTarget_Throws404Equivalent()
    {
        var (sut, _) = BuildSut(("kitchen-01", "Kitchen"));

        await Should.ThrowAsync<AnnounceTargetNotFoundException>(
            () => sut.AnnounceAsync(
                new AnnounceRequest { Target = new() { SatelliteId = "ghost" }, Text = "hi" },
                source: "ha", CancellationToken.None));
    }

    [Fact]
    public async Task Announce_HighPriority_PreemptsCurrentReply()
    {
        var (sut, sessions) = BuildSut(("kitchen-01", "Kitchen"));
        // Pre-load a long-running playback to be preempted.
        var session = sessions.Get("kitchen-01")!;
        var pump = session.RunPlaybackLoopAsync((c, ct) => Task.Delay(5_000, ct), CancellationToken.None);
        await session.EnqueuePlaybackAsync(
            new PlaybackJob("ongoing", AnnouncePriority.Normal,
                NeverEnding(), _ => Task.CompletedTask, _ => Task.CompletedTask),
            queueMaxDepth: 4);

        await sut.AnnounceAsync(
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "alert", Priority = AnnouncePriority.High },
            source: "ha", CancellationToken.None);

        // The 'ongoing' job's cancellation token should have been triggered.
        // (Indirect check: completing playback closes the loop quickly.)
        session.CompletePlayback();
        var completed = await Task.WhenAny(pump, Task.Delay(2_000));
        completed.ShouldBe(pump);
    }

    private static async IAsyncEnumerable<AudioChunk> NeverEnding()
    {
        while (true)
        {
            yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
            await Task.Delay(10);
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnouncementServiceTests" --no-restore`
Expected: FAIL.

- [ ] **Step 3: Implement `AnnouncementService`**

```csharp
// McpChannelVoice/Services/AnnouncementService.cs
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class AnnounceTargetNotFoundException(string message) : Exception(message);

public sealed class AnnouncementService(
    SatelliteRegistry registry,
    SatelliteSessionRegistry sessions,
    ITextToSpeech tts,
    VoiceSettings settings,
    IMetricsPublisher metrics,
    ILogger<AnnouncementService> logger)
{
    public async Task<AnnounceResponse> AnnounceAsync(
        AnnounceRequest request,
        string source,
        CancellationToken ct)
    {
        var targetIds = ResolveTargets(request.Target);
        if (targetIds.Count == 0)
        {
            throw new AnnounceTargetNotFoundException(
                $"No matching satellites for target: id={request.Target.SatelliteId} room={request.Target.Room} all={request.Target.All}");
        }

        var announcementId = Guid.NewGuid().ToString("N");
        var outcomes = new List<AnnouncementOutcome>();

        foreach (var id in targetIds)
        {
            var session = sessions.Get(id);
            if (session is null)
            {
                outcomes.Add(new AnnouncementOutcome { Id = id, Status = "offline" });
                await metrics.PublishAsync(new VoiceEvent
                {
                    Metric = VoiceMetric.AnnounceError,
                    SatelliteId = id,
                    Source = source,
                    Priority = request.Priority.ToString(),
                    Outcome = "offline"
                }, ct);
                continue;
            }

            var voice = request.Voice
                        ?? session.Config.Tts?.Wyoming?.Voice
                        ?? settings.Tts.Wyoming?.Voice;
            var options = new SynthesisOptions { Voice = voice };

            var job = new PlaybackJob(
                Label: $"announce:{announcementId}",
                Priority: request.Priority,
                Audio: tts.SynthesizeAsync(request.Text, options, ct),
                OnStarted: async _ =>
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.AnnouncePlayed,
                        SatelliteId = id,
                        Room = session.Config.Room,
                        Source = source,
                        Priority = request.Priority.ToString()
                    }, ct);
                },
                OnPreempted: async _ =>
                {
                    await metrics.PublishAsync(new VoiceEvent
                    {
                        Metric = VoiceMetric.AnnouncePreemptedReply,
                        SatelliteId = id,
                        Source = source
                    }, ct);
                });

            var accepted = await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
            outcomes.Add(new AnnouncementOutcome { Id = id, Status = accepted ? "queued" : "dropped" });

            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = accepted ? VoiceMetric.AnnounceQueued : VoiceMetric.AnnounceError,
                SatelliteId = id,
                Room = session.Config.Room,
                Source = source,
                Priority = request.Priority.ToString(),
                Outcome = accepted ? "queued" : "dropped"
            }, ct);
        }

        logger.LogInformation("Announce {Id} → {N} targets ({Status})",
            announcementId, outcomes.Count,
            string.Join(",", outcomes.Select(o => $"{o.Id}={o.Status}")));

        return new AnnounceResponse { AnnouncementId = announcementId, Satellites = outcomes };
    }

    private IReadOnlyList<string> ResolveTargets(AnnounceTarget target)
    {
        if (target.SatelliteId is not null)
        {
            return registry.GetById(target.SatelliteId) is null
                ? []
                : [target.SatelliteId];
        }
        if (target.Room is not null) return registry.GetIdsByRoom(target.Room);
        if (target.All == true) return registry.GetAllIds();
        return [];
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnouncementServiceTests" --no-restore`
Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/AnnouncementService.cs \
        Tests/Unit/McpChannelVoice/AnnouncementServiceTests.cs
git commit -m "feat(voice): AnnouncementService with priority and target resolution"
```

---

### Task 4.3: `AnnounceEndpoint` with token auth

**Files:**
- Create: `McpChannelVoice/Services/AnnounceEndpoint.cs`
- Test: `Tests/Unit/McpChannelVoice/AnnounceEndpointAuthTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/AnnounceEndpointAuthTests.cs
using System.Net;
using System.Net.Http.Json;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class AnnounceEndpointAuthTests
{
    private static async Task<HttpClient> BuildClientAsync(AnnounceSettings announce, AnnouncementService? svc = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services
            .AddSingleton(announce)
            .AddSingleton(svc ?? new Mock<AnnouncementService>(MockBehavior.Loose,
                new SatelliteRegistry(new Dictionary<string, SatelliteConfig>()),
                new SatelliteSessionRegistry(),
                Mock.Of<Domain.Contracts.ITextToSpeech>(),
                new VoiceSettings(),
                Mock.Of<Domain.Contracts.IMetricsPublisher>(),
                NullLogger<AnnouncementService>.Instance).Object);

        var app = builder.Build();
        AnnounceEndpoint.Map(app);
        await app.StartAsync();
        return app.GetTestClient();
    }

    [Fact]
    public async Task NoToken_Returns401()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" });
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task WrongToken_Returns401()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = true, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "wrong");
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" });
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Disabled_Returns503()
    {
        using var client = await BuildClientAsync(new AnnounceSettings { Enabled = false, Token = "expected" });
        client.DefaultRequestHeaders.Add("X-Announce-Token", "expected");
        var response = await client.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest { Target = new() { SatelliteId = "kitchen-01" }, Text = "hi" });
        response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnounceEndpointAuthTests" --no-restore`
Expected: FAIL.

- [ ] **Step 3: Implement the endpoint mapping**

```csharp
// McpChannelVoice/Services/AnnounceEndpoint.cs
using Domain.DTOs.Voice;
using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace McpChannelVoice.Services;

public static class AnnounceEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/voice/announce", async (
            AnnounceRequest body,
            HttpContext ctx,
            AnnounceSettings settings,
            AnnouncementService announcer,
            CancellationToken ct) =>
        {
            if (!settings.Enabled)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }

            var token = ctx.Request.Headers["X-Announce-Token"].FirstOrDefault();
            if (string.IsNullOrEmpty(settings.Token) || token != settings.Token)
            {
                return Results.Unauthorized();
            }

            var source = ctx.Request.Headers["X-Announce-Source"].FirstOrDefault() ?? "unknown";

            try
            {
                var response = await announcer.AnnounceAsync(body, source, ct);
                return Results.Accepted(value: response);
            }
            catch (AnnounceTargetNotFoundException ex)
            {
                return Results.NotFound(new { error = ex.Message });
            }
        });
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnounceEndpointAuthTests" --no-restore`
Expected: 3 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/AnnounceEndpoint.cs \
        Tests/Unit/McpChannelVoice/AnnounceEndpointAuthTests.cs
git commit -m "feat(voice): announce HTTP endpoint with token auth"
```

---

### Task 4.4: Wire `AnnouncementService` + endpoint into `ConfigModule` and `Program.cs`

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`
- Modify: `McpChannelVoice/Program.cs`

- [ ] **Step 1: Register the service**

Inside `ConfigureVoiceChannel`, after `services.AddHostedService<WyomingHealthProbeService>();`, add:

```csharp
        services.AddSingleton(settings.Announce);
        services.AddSingleton<AnnouncementService>();
```

- [ ] **Step 2: Map the endpoint in `Program.cs`**

Replace `Program.cs` with:

```csharp
// McpChannelVoice/Program.cs
using McpChannelVoice.Modules;
using McpChannelVoice.Services;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetVoiceSettings();
builder.Services.ConfigureVoiceChannel(settings);

if (settings.Announce.BindToLoopbackOnly)
{
    builder.WebHost.UseKestrel(options =>
        options.Listen(System.Net.IPAddress.Loopback, 8080));
}

var app = builder.Build();
app.MapMcp("/mcp");
AnnounceEndpoint.Map(app);

await app.RunAsync();
```

- [ ] **Step 3: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add McpChannelVoice/Modules/ConfigModule.cs McpChannelVoice/Program.cs
git commit -m "feat(voice): wire announce service + endpoint mapping"
```

---

### Task 4.5: Integration test — announce E2E

**Files:**
- Create: `Tests/Integration/McpChannelVoice/AnnounceEndToEndTests.cs`

- [ ] **Step 1: Write the test**

```csharp
// Tests/Integration/McpChannelVoice/AnnounceEndToEndTests.cs
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs.Voice;
using McpChannelVoice.Modules;
using McpChannelVoice.Services;
using McpChannelVoice.Services.WyomingProtocol;
using McpChannelVoice.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

public class AnnounceEndToEndTests
{
    [Fact]
    public async Task PostAnnounce_PushesAudioToConnectedSatellite()
    {
        var settings = new VoiceSettings
        {
            WyomingServer = new() { Host = "127.0.0.1", Port = 0 },
            Announce = new() { Enabled = true, Token = "secret", QueueMaxDepth = 4 },
            Satellites = new()
            {
                ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" }
            }
        };

        var port = GetFreePort();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(opts => opts.Listen(IPAddress.Loopback, port));
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton(settings.Announce);
        builder.Services.AddSingleton(settings.WyomingServer);
        builder.Services.AddSingleton(new SatelliteRegistry(settings.Satellites));
        builder.Services.AddSingleton<SatelliteSessionRegistry>();
        builder.Services.AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>());

        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns<string, SynthesisOptions, CancellationToken>((_, _, _) => FakeTtsAudio());
        builder.Services.AddSingleton(tts.Object);
        builder.Services.AddSingleton<TranscriptDispatcher>(_ => null!);

        var stt = new Mock<ISpeechToText>();
        builder.Services.AddSingleton(stt.Object);

        builder.Services.AddSingleton<AnnouncementService>();
        builder.Services.AddHostedService<WyomingServer>();

        var app = builder.Build();
        AnnounceEndpoint.Map(app);
        await app.StartAsync();

        var wyomingServer = app.Services.GetServices<IHostedService>().OfType<WyomingServer>().Single();

        using var satellite = new TcpClient();
        await satellite.ConnectAsync(IPAddress.Loopback, wyomingServer.BoundPort);
        await using var satStream = satellite.GetStream();
        var satWriter = new WyomingWriter(satStream);
        var satReader = new WyomingReader(satStream);

        await satWriter.WriteAsync(WyomingEvent.Header("info",
            new JsonObject { ["satellite"] = new JsonObject { ["name"] = "kitchen-01" } }),
            CancellationToken.None);

        await Task.Delay(150); // let session register

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
        http.DefaultRequestHeaders.Add("X-Announce-Token", "secret");

        var response = await http.PostAsJsonAsync("/api/voice/announce",
            new AnnounceRequest
            {
                Target = new() { SatelliteId = "kitchen-01" },
                Text = "hello",
                Priority = AnnouncePriority.Normal
            });

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Expect a Wyoming audio-chunk back on the satellite stream within 1 s.
        var sawAudio = false;
        var ctsRead = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await foreach (var evt in satReader.ReadAllAsync(ctsRead.Token))
            {
                if (evt.Type == "audio-chunk") { sawAudio = true; break; }
            }
        }
        catch (OperationCanceledException) { /* ignore */ }

        sawAudio.ShouldBeTrue();

        await app.StopAsync();
    }

    private static async IAsyncEnumerable<AudioChunk> FakeTtsAudio()
    {
        yield return new AudioChunk { Data = new byte[32], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    private static int GetFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~AnnounceEndToEndTests" --no-restore`
Expected: 1 passed.

- [ ] **Step 3: Commit**

```bash
git add Tests/Integration/McpChannelVoice/AnnounceEndToEndTests.cs
git commit -m "test(voice): announce end-to-end integration"
```

---

### Task 4.6: Add Home Assistant reference snippet to the spec README, finalize HA documentation

**Files:**
- Modify: `docs/superpowers/specs/2026-05-08-voice-satellites-design.md` (no change required — already includes the HA snippet)
- Create: `docs/homeassistant-voice-announce.md` (short reference doc with the YAML + token rotation note)

- [ ] **Step 1: Create the reference doc**

```markdown
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
```

- [ ] **Step 2: Commit**

```bash
git add docs/homeassistant-voice-announce.md
git commit -m "docs(voice): Home Assistant announce reference"
```

---

### Task 4.7: Slice 4 wrap-up — manual smoke test

- [ ] **Step 1: Set the announce token**

Edit `DockerCompose/.env`:

```bash
ANNOUNCE_TOKEN=test-token-please-change
```

- [ ] **Step 2: Rebuild and restart**

Run:

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build mcp-channel-voice
```

- [ ] **Step 3: Boot a desktop satellite (as in Slice 2 Task 2.12)**

- [ ] **Step 4: POST an announcement**

Run:

```bash
curl -s -X POST -H "Content-Type: application/json" \
     -H "X-Announce-Token: test-token-please-change" \
     -d '{"target":{"satelliteId":"kitchen-01"},"text":"hola, esto es una prueba","priority":"Normal"}' \
     http://localhost:6014/api/voice/announce | jq
```

Expected: `202` JSON body; the satellite speaker plays "hola, esto es una prueba".

**Slice 4 done when:** Step 4 plays audio on the targeted satellite and `docker logs mcp-channel-voice` shows `AnnounceQueued` and `AnnouncePlayed` events with `Source=unknown` (or whatever `X-Announce-Source` was set to).

---

## Slice 5 — Approval over voice

### Task 5.1: `ApprovalGrammarParser` (yes / no / sí / no / cancel / confirm / ok)

**Files:**
- Create: `McpChannelVoice/Services/ApprovalGrammarParser.cs`
- Test: `Tests/Unit/McpChannelVoice/ApprovalGrammarParserTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/ApprovalGrammarParserTests.cs
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ApprovalGrammarParserTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("Yes please")]
    [InlineData("sí")]
    [InlineData("si por favor")]
    [InlineData("confirm")]
    [InlineData("ok")]
    [InlineData("okay")]
    [InlineData("vale")]
    public void Parse_Affirmative(string text)
    {
        ApprovalGrammarParser.Parse(text).ShouldBe(ApprovalResponse.Approved);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("No thanks")]
    [InlineData("cancel")]
    [InlineData("cancelar")]
    [InlineData("nope")]
    public void Parse_Negative(string text)
    {
        ApprovalGrammarParser.Parse(text).ShouldBe(ApprovalResponse.Declined);
    }

    [Theory]
    [InlineData("yes please cancel that")]
    [InlineData("maybe")]
    [InlineData("")]
    public void Parse_Ambiguous(string text)
    {
        ApprovalGrammarParser.Parse(text).ShouldBe(ApprovalResponse.Ambiguous);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ApprovalGrammarParserTests" --no-restore`
Expected: FAIL.

- [ ] **Step 3: Implement the parser**

```csharp
// McpChannelVoice/Services/ApprovalGrammarParser.cs
namespace McpChannelVoice.Services;

public enum ApprovalResponse { Approved, Declined, Ambiguous }

public static class ApprovalGrammarParser
{
    private static readonly HashSet<string> _affirmative = new(StringComparer.OrdinalIgnoreCase)
    {
        "yes", "yeah", "yep", "sure", "okay", "ok", "confirm", "confirmed",
        "sí", "si", "vale", "claro", "afirmativo"
    };

    private static readonly HashSet<string> _negative = new(StringComparer.OrdinalIgnoreCase)
    {
        "no", "nope", "nah", "cancel", "cancelar", "negativo", "abort", "stop"
    };

    public static ApprovalResponse Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ApprovalResponse.Ambiguous;

        var tokens = text
            .ToLowerInvariant()
            .Split([' ', ',', '.', '!', '?', ';', ':'], StringSplitOptions.RemoveEmptyEntries);
        var hasYes = tokens.Any(_affirmative.Contains);
        var hasNo = tokens.Any(_negative.Contains);

        return (hasYes, hasNo) switch
        {
            (true, false) => ApprovalResponse.Approved,
            (false, true) => ApprovalResponse.Declined,
            _ => ApprovalResponse.Ambiguous
        };
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ApprovalGrammarParserTests" --no-restore`
Expected: 16 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Services/ApprovalGrammarParser.cs \
        Tests/Unit/McpChannelVoice/ApprovalGrammarParserTests.cs
git commit -m "feat(voice): approval grammar parser (en/es)"
```

---

### Task 5.2: `RequestApprovalTool` — speak prompt, capture answer, re-prompt once

The flow:
1. Find the live session for `conversationId`.
2. Build a Spanish/English prompt (one sentence summarising the tool requests).
3. Synthesise + enqueue the prompt via the playback queue.
4. Wait for the next utterance from the satellite (capture window of ~10 s).
5. Run STT, then `ApprovalGrammarParser.Parse`.
6. On `Ambiguous`, re-prompt once. Second `Ambiguous` → `declined`.

The approval tool needs a way to **synchronously await the next transcribed utterance** for a given session. We add a small `ApprovalCaptureBroker` that the dispatcher consults: if a session has a pending approval listener, the transcript goes there instead of to `EmitMessageNotificationAsync`.

**Files:**
- Create: `McpChannelVoice/Services/ApprovalCaptureBroker.cs`
- Modify: `McpChannelVoice/Services/TranscriptDispatcher.cs`
- Modify: `McpChannelVoice/McpTools/RequestApprovalTool.cs`
- Test: `Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Voice;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class RequestApprovalToolTests
{
    private static IServiceProvider BuildServices(out SatelliteSession session, out ApprovalCaptureBroker broker, ITextToSpeech tts)
    {
        session = new SatelliteSession("kitchen-01",
            new SatelliteConfig { Identity = "household", Room = "Kitchen" });
        var sessions = new SatelliteSessionRegistry();
        sessions.Register(session);

        broker = new ApprovalCaptureBroker();

        return new ServiceCollection()
            .AddSingleton(sessions)
            .AddSingleton(broker)
            .AddSingleton(tts)
            .AddSingleton(new VoiceSettings())
            .AddSingleton<IMetricsPublisher>(Mock.Of<IMetricsPublisher>())
            .AddSingleton<ILogger<RequestApprovalTool>>(NullLogger<RequestApprovalTool>.Instance)
            .BuildServiceProvider();
    }

    private static async IAsyncEnumerable<AudioChunk> Audio()
    {
        yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
        await Task.Yield();
    }

    [Fact]
    public async Task NotifyMode_DoesNotWaitForResponse()
    {
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        var services = BuildServices(out _, out _, tts.Object);

        var result = await RequestApprovalTool.McpRun(
            "kitchen-01", ApprovalMode.Notify,
            [new ToolApprovalRequest { ToolName = "mcp__lib__download", Arguments = new Dictionary<string, object?>() }],
            services);

        result.ShouldBe("notified");
    }

    [Fact]
    public async Task RequestMode_PositiveAnswer_ReturnsApproved()
    {
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        var services = BuildServices(out var session, out var broker, tts.Object);

        var run = RequestApprovalTool.McpRun(
            "kitchen-01", ApprovalMode.Request,
            [new ToolApprovalRequest { ToolName = "mcp__lib__download", Arguments = new Dictionary<string, object?>() }],
            services);

        await Task.Delay(50);
        broker.SubmitUtterance("kitchen-01", "sí, claro");

        var result = await run;
        result.ShouldBe("approved");
    }

    [Fact]
    public async Task RequestMode_AmbiguousThenNegative_ReturnsDeclined()
    {
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        var services = BuildServices(out _, out var broker, tts.Object);

        var run = RequestApprovalTool.McpRun(
            "kitchen-01", ApprovalMode.Request,
            [new ToolApprovalRequest { ToolName = "mcp__lib__download", Arguments = new Dictionary<string, object?>() }],
            services);

        await Task.Delay(50);
        broker.SubmitUtterance("kitchen-01", "maybe");
        await Task.Delay(50);
        broker.SubmitUtterance("kitchen-01", "no thanks");

        var result = await run;
        result.ShouldBe("declined");
    }

    [Fact]
    public async Task RequestMode_TwoAmbiguous_DeclinesByDefault()
    {
        var tts = new Mock<ITextToSpeech>();
        tts.Setup(t => t.SynthesizeAsync(It.IsAny<string>(), It.IsAny<SynthesisOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Audio());

        var services = BuildServices(out _, out var broker, tts.Object);

        var run = RequestApprovalTool.McpRun(
            "kitchen-01", ApprovalMode.Request,
            [new ToolApprovalRequest { ToolName = "mcp__lib__download", Arguments = new Dictionary<string, object?>() }],
            services);

        await Task.Delay(50);
        broker.SubmitUtterance("kitchen-01", "maybe");
        await Task.Delay(50);
        broker.SubmitUtterance("kitchen-01", "hmm");

        var result = await run;
        result.ShouldBe("declined");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RequestApprovalToolTests" --no-restore`
Expected: FAIL — broker missing.

- [ ] **Step 3: Implement the broker**

```csharp
// McpChannelVoice/Services/ApprovalCaptureBroker.cs
using System.Collections.Concurrent;

namespace McpChannelVoice.Services;

public sealed class ApprovalCaptureBroker
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _pending = new();

    public bool HasListener(string satelliteId) => _pending.ContainsKey(satelliteId);

    public Task<string> WaitForUtteranceAsync(string satelliteId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[satelliteId] = tcs;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        cts.Token.Register(() =>
        {
            if (_pending.TryRemove(satelliteId, out var pending) && pending == tcs)
            {
                tcs.TrySetResult("");
            }
        });

        return tcs.Task;
    }

    public bool SubmitUtterance(string satelliteId, string text)
    {
        if (_pending.TryRemove(satelliteId, out var tcs))
        {
            tcs.TrySetResult(text);
            return true;
        }
        return false;
    }
}
```

- [ ] **Step 4: Hook the broker into the dispatcher**

In `TranscriptDispatcher.DispatchAsync`, before the `EmitMessageNotificationAsync` call, check the broker. Update the constructor to take `ApprovalCaptureBroker broker`, then:

```csharp
        if (broker.SubmitUtterance(session.SatelliteId, transcript.Text))
        {
            logger.LogInformation("Transcript routed to pending approval for {Id}", session.SatelliteId);
            return true;
        }
```

- [ ] **Step 5: Implement `RequestApprovalTool`**

Replace `McpChannelVoice/McpTools/RequestApprovalTool.cs`:

```csharp
// McpChannelVoice/McpTools/RequestApprovalTool.cs
using System.ComponentModel;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Channel;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    private static readonly TimeSpan _captureWindow = TimeSpan.FromSeconds(10);

    [McpServerTool(Name = ChannelProtocol.RequestApprovalTool)]
    [Description("Request user approval via voice")]
    public static async Task<string> McpRun(
        [Description("Satellite ID owning the conversation")] string conversationId,
        [Description("Whether to ask the user or just notify them")] ApprovalMode mode,
        [Description("Tool requests to approve")] IReadOnlyList<ToolApprovalRequest> requests,
        IServiceProvider services)
    {
        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var broker = services.GetRequiredService<ApprovalCaptureBroker>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var settings = services.GetRequiredService<VoiceSettings>();
        var metrics = services.GetRequiredService<IMetricsPublisher>();

        var session = sessions.Get(conversationId);
        if (session is null) return mode == ApprovalMode.Notify ? "notified" : "declined";

        var toolList = string.Join(", ", requests.Select(r => r.ToolName.Split("__").Last()));

        if (mode == ApprovalMode.Notify)
        {
            await SpeakAsync(session, $"Aprobado automáticamente: {toolList}", tts, settings);
            return "notified";
        }

        var prompt = $"¿Apruebas {toolList}? Di sí o no.";

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await SpeakAsync(session, prompt, tts, settings);

            var answer = await broker.WaitForUtteranceAsync(session.SatelliteId, _captureWindow, default);
            var parsed = ApprovalGrammarParser.Parse(answer);

            await metrics.PublishAsync(new VoiceEvent
            {
                Metric = VoiceMetric.ApprovalResolved,
                SatelliteId = session.SatelliteId,
                Identity = session.Config.Identity,
                Outcome = parsed.ToString(),
                ConversationId = session.ConversationId
            });

            switch (parsed)
            {
                case ApprovalResponse.Approved: return "approved";
                case ApprovalResponse.Declined: return "declined";
            }

            prompt = $"No entendí. ¿Apruebas {toolList}? Di sí o no.";
        }

        return "declined";
    }

    private static async Task SpeakAsync(SatelliteSession session, string text, ITextToSpeech tts, VoiceSettings settings)
    {
        var voice = session.Config.Tts?.Wyoming?.Voice ?? settings.Tts.Wyoming?.Voice;
        var options = new SynthesisOptions { Voice = voice };
        var job = new PlaybackJob(
            Label: $"approval:{session.SatelliteId}",
            Priority: AnnouncePriority.High,
            Audio: tts.SynthesizeAsync(text, options, default),
            OnStarted: _ => Task.CompletedTask,
            OnPreempted: _ => Task.CompletedTask);
        await session.EnqueuePlaybackAsync(job, settings.Announce.QueueMaxDepth);
    }
}
```

- [ ] **Step 6: Register the broker in `ConfigModule`**

Inside `ConfigureVoiceChannel`, add `services.AddSingleton<ApprovalCaptureBroker>();` near the registry registrations.

The `TranscriptDispatcher` factory must now resolve the broker — update its registration:

```csharp
            .AddSingleton<TranscriptDispatcher>(sp => new TranscriptDispatcher(
                sp.GetRequiredService<ChannelNotificationEmitter>(),
                sp.GetRequiredService<IMetricsPublisher>(),
                sp.GetRequiredService<ApprovalCaptureBroker>(),
                settings.ConfidenceThreshold,
                sp.GetRequiredService<ILogger<TranscriptDispatcher>>()))
```

And the `TranscriptDispatcher` constructor signature needs the new parameter (already required by Step 4 above).

- [ ] **Step 7: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~RequestApprovalToolTests" --no-restore`
Expected: 4 passed.

- [ ] **Step 8: Commit**

```bash
git add McpChannelVoice/Services/ApprovalCaptureBroker.cs \
        McpChannelVoice/Services/TranscriptDispatcher.cs \
        McpChannelVoice/Modules/ConfigModule.cs \
        McpChannelVoice/McpTools/RequestApprovalTool.cs \
        Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs
git commit -m "feat(voice): voice approval flow with grammar parser + re-prompt"
```

---

### Task 5.3: Button-press fallback (Wyoming `button` event maps to confirm/decline)

`wyoming-satellite` can forward GPIO button events as Wyoming `event` messages (type `event` with `name="button-press"` or similar). When the broker has a pending listener, a single press counts as "yes" and a double press as "no".

**Files:**
- Modify: `McpChannelVoice/Services/WyomingServer.cs`

- [ ] **Step 1: Extend `HandleClientAsync` switch**

Inside the per-event `await foreach` loop, after the `audio-stop` case, add:

```csharp
                if (evt.Type == "button-press" && session is not null)
                {
                    var pressCount = evt.Data["count"]?.GetValue<int>() ?? 1;
                    var injected = pressCount == 1 ? "sí" : "no";
                    var broker = sp.GetRequiredService<ApprovalCaptureBroker>();
                    broker.SubmitUtterance(session.SatelliteId, injected);
                    continue;
                }
```

This requires the server to hold a reference to `IServiceProvider` or to receive the broker directly. The cleanest fix is to add it as a constructor parameter. Update the primary constructor of `WyomingServer` to add `ApprovalCaptureBroker broker` and use that directly:

```csharp
                if (evt.Type == "button-press" && session is not null)
                {
                    var count = evt.Data["count"]?.GetValue<int>() ?? 1;
                    broker.SubmitUtterance(session.SatelliteId, count == 1 ? "sí" : "no");
                    continue;
                }
```

- [ ] **Step 2: Verify the project builds**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add McpChannelVoice/Services/WyomingServer.cs
git commit -m "feat(voice): button-press maps to approval yes/no"
```

---

### Task 5.4: Slice 5 wrap-up — manual smoke test

- [ ] **Step 1: Trigger a voice approval**

In WebChat (or via Telegram) ask the agent to do something that requires approval. The satellite should speak the prompt; say "sí". The tool should run.

- [ ] **Step 2: Try ambiguous → declined fallback**

Repeat, say "tal vez" twice. The tool should be declined.

**Slice 5 done when:** Both behaviours reproduce on a real satellite, and `ApprovalResolved` events appear in the dashboard.

---

## Slice 6 — Cloud STT/TTS adapters

### Task 6.1: `OpenAiSpeechToText` adapter

The adapter calls `POST https://api.openai.com/v1/audio/transcriptions` with a multipart upload of the buffered audio. Audio must be muxed into a WAV first because OpenAI expects a complete file (not a stream).

**Files:**
- Create: `Infrastructure/Clients/Voice/OpenAiSpeechToText.cs`
- Create: `Infrastructure/Clients/Voice/PcmWavWriter.cs` (tiny WAV header builder)
- Test: `Tests/Unit/Infrastructure/Clients/Voice/OpenAiSpeechToTextTests.cs`
- Test: `Tests/Unit/Infrastructure/Clients/Voice/PcmWavWriterTests.cs`

- [ ] **Step 1: Write the failing WAV-writer test**

```csharp
// Tests/Unit/Infrastructure/Clients/Voice/PcmWavWriterTests.cs
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class PcmWavWriterTests
{
    [Fact]
    public void WriteWav_ProducesHeaderedFile()
    {
        var pcm = new byte[3200]; // 100 ms @ 16 kHz, 16-bit mono
        var wav = PcmWavWriter.Encode(pcm, AudioFormat.WyomingStandard);

        wav[..4].ShouldBe("RIFF"u8.ToArray());
        wav[8..12].ShouldBe("WAVE"u8.ToArray());
        wav.Length.ShouldBe(pcm.Length + 44);
    }
}
```

- [ ] **Step 2: Implement `PcmWavWriter`**

```csharp
// Infrastructure/Clients/Voice/PcmWavWriter.cs
using System.Buffers.Binary;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Voice;

public static class PcmWavWriter
{
    public static byte[] Encode(ReadOnlySpan<byte> pcm, AudioFormat fmt)
    {
        var wav = new byte[44 + pcm.Length];
        Span<byte> s = wav;

        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + pcm.Length);
        "WAVE"u8.CopyTo(s[8..]);
        "fmt "u8.CopyTo(s[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1);                            // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], (short)fmt.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], fmt.SampleRateHz);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], fmt.SampleRateHz * fmt.Channels * fmt.SampleWidthBytes);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], (short)(fmt.Channels * fmt.SampleWidthBytes));
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], (short)(fmt.SampleWidthBytes * 8));
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], pcm.Length);
        pcm.CopyTo(s[44..]);

        return wav;
    }
}
```

- [ ] **Step 3: Write the failing adapter test (HTTP stub)**

```csharp
// Tests/Unit/Infrastructure/Clients/Voice/OpenAiSpeechToTextTests.cs
using System.Net;
using System.Net.Http;
using System.Text;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class OpenAiSpeechToTextTests
{
    private sealed class StubHandler(string body, HttpStatusCode code = HttpStatusCode.OK) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            if (request.Content is not null) await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(code) { Content = new StringContent(body) };
        }
    }

    [Fact]
    public async Task TranscribeAsync_ReturnsTextAndLanguage()
    {
        var stub = new StubHandler("""{"text":"hola","language":"es"}""");
        var http = new HttpClient(stub) { BaseAddress = new Uri("https://api.openai.com") };
        var sut = new OpenAiSpeechToText(http, model: "whisper-1", apiKey: "sk-test",
            NullLogger<OpenAiSpeechToText>.Instance);

        async IAsyncEnumerable<AudioChunk> Audio()
        {
            yield return new AudioChunk { Data = new byte[160], Format = AudioFormat.WyomingStandard };
            await Task.Yield();
        }

        var result = await sut.TranscribeAsync(Audio(), new TranscriptionOptions(), CancellationToken.None);
        result.Text.ShouldBe("hola");
        result.Language.ShouldBe("es");
        stub.LastRequest!.Headers.Authorization!.Parameter.ShouldBe("sk-test");
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiSpeechToTextTests" --no-restore`
Expected: FAIL.

- [ ] **Step 5: Implement the adapter**

```csharp
// Infrastructure/Clients/Voice/OpenAiSpeechToText.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Voice;

public sealed class OpenAiSpeechToText(
    HttpClient http,
    string model,
    string apiKey,
    ILogger<OpenAiSpeechToText> logger) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken ct)
    {
        var buffer = new MemoryStream();
        AudioFormat? format = null;
        await foreach (var chunk in audio.WithCancellation(ct))
        {
            format ??= chunk.Format;
            await buffer.WriteAsync(chunk.Data, ct);
        }

        var wav = PcmWavWriter.Encode(buffer.ToArray(), format ?? AudioFormat.WyomingStandard);

        using var content = new MultipartFormDataContent
        {
            { new ByteArrayContent(wav) { Headers = { ContentType = MediaTypeHeaderValue.Parse("audio/wav") } }, "file", "audio.wav" },
            { new StringContent(model), "model" },
            { new StringContent("verbose_json"), "response_format" }
        };
        if (options.Language is not null)
        {
            content.Add(new StringContent(options.Language), "language");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/transcriptions") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<OpenAiTranscription>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("Empty OpenAI response");

        logger.LogInformation("OpenAI STT: lang={Lang} duration={Duration:F2}", payload.Language, payload.Duration);

        return new TranscriptionResult
        {
            Text = payload.Text,
            Language = payload.Language,
            Confidence = null
        };
    }

    private sealed record OpenAiTranscription(string Text, string? Language, double? Duration);
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiSpeechToText" --no-restore`
Expected: PASS (both tests).

- [ ] **Step 7: Commit**

```bash
git add Infrastructure/Clients/Voice/OpenAiSpeechToText.cs \
        Infrastructure/Clients/Voice/PcmWavWriter.cs \
        Tests/Unit/Infrastructure/Clients/Voice/OpenAiSpeechToTextTests.cs \
        Tests/Unit/Infrastructure/Clients/Voice/PcmWavWriterTests.cs
git commit -m "feat(voice): OpenAI STT adapter + WAV encoder"
```

---

### Task 6.2: `OpenAiTextToSpeech` adapter

OpenAI's TTS streams raw audio; we request `pcm` format so we can re-frame as 24 kHz PCM chunks.

**Files:**
- Create: `Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs`
- Test: `Tests/Unit/Infrastructure/Clients/Voice/OpenAiTextToSpeechTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
// Tests/Unit/Infrastructure/Clients/Voice/OpenAiTextToSpeechTests.cs
using System.Net;
using System.Net.Http;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.Infrastructure.Clients.Voice;

public class OpenAiTextToSpeechTests
{
    private sealed class StubHandler(byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body)
            };
            resp.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wave");
            return Task.FromResult(resp);
        }
    }

    [Fact]
    public async Task SynthesizeAsync_StreamsBackChunkedPcm()
    {
        var pcm = Enumerable.Range(0, 4800).Select(i => (byte)(i & 0xff)).ToArray(); // 200 ms @ 24 kHz
        var http = new HttpClient(new StubHandler(pcm)) { BaseAddress = new Uri("https://api.openai.com") };
        var sut = new OpenAiTextToSpeech(http, model: "tts-1", voice: "alloy", apiKey: "sk-test",
            NullLogger<OpenAiTextToSpeech>.Instance);

        var collected = new List<byte>();
        await foreach (var chunk in sut.SynthesizeAsync("hola", new SynthesisOptions(), CancellationToken.None))
        {
            collected.AddRange(chunk.Data.ToArray());
        }

        collected.Count.ShouldBe(pcm.Length);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiTextToSpeechTests" --no-restore`
Expected: FAIL.

- [ ] **Step 3: Implement the adapter**

```csharp
// Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Voice;
using Microsoft.Extensions.Logging;

namespace Infrastructure.Clients.Voice;

public sealed class OpenAiTextToSpeech(
    HttpClient http,
    string model,
    string voice,
    string apiKey,
    ILogger<OpenAiTextToSpeech> logger) : ITextToSpeech
{
    private static readonly AudioFormat _format = new()
    {
        SampleRateHz = 24_000,
        SampleWidthBytes = 2,
        Channels = 1
    };

    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/audio/speech")
        {
            Content = JsonContent.Create(new
            {
                model,
                input = text,
                voice = options.Voice ?? voice,
                response_format = "pcm"
            })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            var slice = new byte[read];
            Array.Copy(buffer, slice, read);
            yield return new AudioChunk { Data = slice, Format = _format };
        }
        logger.LogDebug("OpenAI TTS stream complete");
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~OpenAiTextToSpeechTests" --no-restore`
Expected: 1 passed.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs \
        Tests/Unit/Infrastructure/Clients/Voice/OpenAiTextToSpeechTests.cs
git commit -m "feat(voice): OpenAI TTS adapter"
```

---

### Task 6.3: Provider switch in `ConfigModule`

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`
- Modify: `McpChannelVoice/Settings/SttSettings.cs` (already has `OpenAi`)
- Modify: `McpChannelVoice/Settings/TtsSettings.cs` (already has `OpenAi`)
- Modify: `DockerCompose/.env` (add `OPENAI_API_KEY` placeholder)
- Modify: `DockerCompose/docker-compose.yml` (pass `OPENAI_API_KEY` to `mcp-channel-voice`)

- [ ] **Step 1: Replace the STT/TTS registration in `ConfigModule`**

Replace the two `Provider.Equals("Wyoming"…)` blocks with:

```csharp
        services.AddHttpClient("openai", c => c.BaseAddress = new Uri("https://api.openai.com"));

        services.AddSingleton<ISpeechToText>(sp =>
        {
            if (settings.Stt.Provider.Equals("OpenAi", StringComparison.OrdinalIgnoreCase))
            {
                var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                          ?? throw new InvalidOperationException("OPENAI_API_KEY missing");
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai");
                return new Infrastructure.Clients.Voice.OpenAiSpeechToText(
                    http, settings.Stt.OpenAi?.Model ?? "whisper-1", key,
                    sp.GetRequiredService<ILogger<Infrastructure.Clients.Voice.OpenAiSpeechToText>>());
            }
            return new McpChannelVoice.Services.Stt.WyomingSpeechToText(
                settings.Stt.Wyoming ?? throw new InvalidOperationException("Stt.Wyoming missing"),
                sp.GetRequiredService<ILogger<McpChannelVoice.Services.Stt.WyomingSpeechToText>>());
        });

        services.AddSingleton<ITextToSpeech>(sp =>
        {
            if (settings.Tts.Provider.Equals("OpenAi", StringComparison.OrdinalIgnoreCase))
            {
                var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                          ?? throw new InvalidOperationException("OPENAI_API_KEY missing");
                var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai");
                return new Infrastructure.Clients.Voice.OpenAiTextToSpeech(
                    http,
                    settings.Tts.OpenAi?.Model ?? "tts-1",
                    settings.Tts.OpenAi?.Voice ?? "alloy",
                    key,
                    sp.GetRequiredService<ILogger<Infrastructure.Clients.Voice.OpenAiTextToSpeech>>());
            }
            return new McpChannelVoice.Services.Tts.WyomingTextToSpeech(
                settings.Tts.Wyoming ?? throw new InvalidOperationException("Tts.Wyoming missing"),
                sp.GetRequiredService<ILogger<McpChannelVoice.Services.Tts.WyomingTextToSpeech>>());
        });
```

- [ ] **Step 2: Add `OPENAI_API_KEY` to `.env`**

```bash
# OpenAI (used by mcp-channel-voice when Voice.Stt|Tts.Provider=OpenAi)
OPENAI_API_KEY=
```

- [ ] **Step 3: Pass it to the container**

In `docker-compose.yml`, in the `mcp-channel-voice` service's `environment` block:

```yaml
      - OPENAI_API_KEY=${OPENAI_API_KEY}
```

- [ ] **Step 4: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj --nologo`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Modules/ConfigModule.cs DockerCompose/.env DockerCompose/docker-compose.yml
git commit -m "feat(voice): provider switch wiring for OpenAI STT/TTS"
```

---

### Task 6.4: Integration test — STT provider switch

**Files:**
- Create: `Tests/Integration/McpChannelVoice/SttProviderSwitchTests.cs`

- [ ] **Step 1: Write the test**

```csharp
// Tests/Integration/McpChannelVoice/SttProviderSwitchTests.cs
using System.Net;
using System.Net.Http;
using Domain.Contracts;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using McpChannelVoice.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Integration.McpChannelVoice;

public class SttProviderSwitchTests
{
    private sealed class FixedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"text":"hi","language":"en"}""")
            });
    }

    [Fact]
    public async Task ProviderOpenAi_AdapterTranscribesViaHttp()
    {
        var http = new HttpClient(new FixedHandler()) { BaseAddress = new Uri("https://api.openai.com") };
        ISpeechToText sut = new OpenAiSpeechToText(http, "whisper-1", "sk", NullLogger<OpenAiSpeechToText>.Instance);

        async IAsyncEnumerable<AudioChunk> Audio()
        {
            yield return new AudioChunk { Data = new byte[16], Format = AudioFormat.WyomingStandard };
            await Task.Yield();
        }

        var result = await sut.TranscribeAsync(Audio(), new TranscriptionOptions(), CancellationToken.None);
        result.Text.ShouldBe("hi");
    }
}
```

- [ ] **Step 2: Run and commit**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~SttProviderSwitchTests" --no-restore`
Expected: 1 passed.

```bash
git add Tests/Integration/McpChannelVoice/SttProviderSwitchTests.cs
git commit -m "test(voice): provider switch coverage"
```

---

### Task 6.5: Slice 6 wrap-up — manual switch

- [ ] **Step 1: Set the OpenAI key**

In `DockerCompose/.env`, set `OPENAI_API_KEY=sk-...`.

- [ ] **Step 2: Switch the provider**

Edit `McpChannelVoice/appsettings.json`:

```json
    "Stt": { "Provider": "OpenAi", ... },
    "Tts": { "Provider": "OpenAi", ... }
```

- [ ] **Step 3: Restart and try**

Run:

```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build mcp-channel-voice
```

Speak a wake-word + question. Confirm:
- `docker logs mcp-channel-voice` shows the OpenAI request/response logs.
- Dashboard `Tokens` page shows a new entry with `Origin=voice` (this requires the OpenAI adapters to publish `TokenUsageEvent` events; see Task 6.6 below).

---

### Task 6.6: Token usage events from cloud adapters (optional, per spec)

**Files:**
- Modify: `Infrastructure/Clients/Voice/OpenAiSpeechToText.cs`
- Modify: `Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs`

OpenAI's transcription `verbose_json` includes `duration` — convert that to a token-equivalent cost line (`AudioSeconds`-based dimension). OpenAI's TTS doesn't return usage; estimate per character at the documented `$15 / 1M chars` for `tts-1`.

- [ ] **Step 1: Inject `IMetricsPublisher` into both adapters**

For both adapter classes, add an `IMetricsPublisher metrics` constructor parameter. After a successful call, publish a `TokenUsageEvent` with the existing token-cost shape, but with `AgentId = null` and a custom dimension/property `Origin = "voice"`.

Read the current `TokenUsageEvent` shape:

```bash
grep -n "record TokenUsageEvent" Domain/DTOs/Metrics/TokenUsageEvent.cs
```

…and add an `Origin` property to it (matching the spec's note that "Cloud STT/TTS adapters publish their cost via the existing `TokenMetric` path tagged with a new `Origin = 'voice'` dimension value").

- [ ] **Step 2: Test that the field round-trips**

Add to `Tests/Unit/Domain/DTOs/Metrics/TokenUsageEventTests.cs` (or create it) a test that `Origin` serialises.

```csharp
[Fact]
public void TokenUsageEvent_OriginIsPersisted()
{
    var evt = new TokenUsageEvent { Model = "tts-1", PromptTokens = 0, CompletionTokens = 0, Origin = "voice" };
    System.Text.Json.JsonSerializer.Serialize(evt).ShouldContain("\"origin\":\"voice\"");
}
```

(Add the `Origin` field to `TokenUsageEvent.cs` with `public string? Origin { get; init; }`. If a dashboard page currently filters out `Origin`-tagged entries, ensure they continue to render without changes — the spec says "Tokens.razor: no UI change".)

- [ ] **Step 3: Run tests, build, commit**

```bash
dotnet test Tests/Tests.csproj --filter "Category!=E2E" --no-restore
```

Expected: full suite passes (ignoring pre-existing Docker-baseline failures noted in CLAUDE.md memory).

```bash
git add Infrastructure/Clients/Voice/OpenAiSpeechToText.cs \
        Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs \
        Domain/DTOs/Metrics/TokenUsageEvent.cs \
        Tests/Unit/Domain/DTOs/Metrics/TokenUsageEventTests.cs
git commit -m "feat(voice): publish TokenUsageEvent with Origin=voice from OpenAI adapters"
```

**Slice 6 done when:** Switching `Voice.Stt.Provider` / `Voice.Tts.Provider` to `OpenAi` produces a working voice round-trip with no other config changes, and the Tokens dashboard shows the new entries.

---

## Final acceptance check

After all six slices are merged, run:

```bash
dotnet test Tests/Tests.csproj --filter "Category!=E2E" --no-restore
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build redis wyoming-whisper wyoming-piper mcp-channel-voice agent
```

Expected:
- Unit + integration tests green (modulo the Docker-baseline failures noted in CLAUDE.md memory).
- A desktop satellite can wake → speak → hear an answer.
- `curl … /api/voice/announce` plays the announce.
- A voice approval round-trip works in Spanish and English.
- Switching `Voice.Stt.Provider` to `OpenAi` (with `OPENAI_API_KEY`) still works.
- Dashboard `Voice` page populates; `Overview` shows voice KPIs; `HealthGrid` shows whisper / piper / channel green.

---

## Self-review notes

This plan implements every section of the spec:

- **Architecture diagram** → Slices 1–4. Hub + agent split honored; LAN trust assumed.
- **Stock components** → `wyoming-satellite` (provisioning script, Task 2.11), `wyoming-faster-whisper` / `wyoming-piper` (compose Tasks 2.10, 3.5).
- **`McpChannelVoice` layout** → Task 1.5 (project) + all later tasks creating each file in the spec's tree.
- **Speech contracts** → Task 1.4.
- **Speech adapters** → Wyoming Slices 2/3 (Tasks 2.7, 3.2). OpenAI Slice 6 (Tasks 6.1, 6.2).
- **Data flow — utterance round-trip** → Slices 1–3.
- **Approval flow** → Slice 5.
- **Announce endpoint** → Slice 4 (Tasks 4.1–4.6).
- **Authentication** (`X-Announce-Token`, `BindToLoopbackOnly`) → Tasks 4.3, 4.4, 1.6.
- **Configuration** → Task 1.6 + Task 6.3.
- **Docker Compose** → Tasks 1.11, 2.10, 3.5, 6.3.
- **Satellite provisioning** → Task 2.11.
- **Identity & threading** → `SatelliteRegistry` rejects unknown ids (Task 2.9 server validation).
- **Observability** → enums + event (Tasks 1.1, 1.2), queries/endpoints (Task 3.6), dashboard page (Task 3.7), Overview/HealthGrid (Tasks 3.8, 3.9).
- **Error handling** matrix → covered by tests in 2.7, 2.9, 3.2, 4.2 (`AnnounceError`, `AnnouncePreemptedReply`, `SttError`, etc.).
- **Testing structure** → Unit tests live in `Tests/Unit/McpChannelVoice/`, integration in `Tests/Integration/McpChannelVoice/`.
- **Phasing** → six slices map 1-to-1 to the spec's slices.
- **Out of scope** items are not touched.

Open notes / divergences from the spec that the implementer should be aware of:

1. The spec uses `ASPNETCORE_URLS=http://+:5010` and exposes port `5010`. This plan uses the project-wide convention of `:8080` inside the container, mapped to host `:6014` (consistent with Telegram/SignalR/ServiceBus channels). The Home Assistant snippet was updated to use `mcp-channel-voice:8080` accordingly. The Wyoming TCP port (`10700`) is unchanged.
2. The adapters live in `McpChannelVoice/Services/Stt|Tts/` instead of `Infrastructure/Clients/Voice/` for the **Wyoming** variants only, because they depend on `WyomingClient` which is in the channel project (Domain/Infrastructure layering rule). The OpenAI variants stay in `Infrastructure/Clients/Voice/` as the spec specified.
3. The HA reference doc was added at `docs/homeassistant-voice-announce.md` rather than only being inlined in the spec, since the spec is approved and frozen.

If those three deviations are not acceptable, raise them at the start of Slice 1.
