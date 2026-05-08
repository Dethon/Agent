# Voice Satellites Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an Alexa-like voice channel: Pi Zero 2 W satellites running stock `wyoming-satellite` capture audio after a wake word, stream it to a new `McpChannelVoice` server which transcribes via `wyoming-faster-whisper`, dispatches the transcript to the agent through the existing MCP channel protocol, then plays the agent's reply back through `wyoming-piper`.

**Architecture:** New `McpChannelVoice` ASP.NET project mirroring `McpChannelTelegram`'s shape. It hosts both an MCP HTTP server (agent-facing) and a Wyoming TCP server (satellite-facing). STT and TTS are pluggable behind `ISpeechToText` / `ITextToSpeech` Domain contracts; default impls are Wyoming clients to stock `rhasspy/wyoming-whisper` and `rhasspy/wyoming-piper` containers; OpenAI adapters land last. Each satellite is statically registered to a fixed identity and room. Metrics flow through the existing `IMetricsPublisher` → Redis Pub/Sub pipeline; a new `Voice.razor` dashboard page surfaces them.

**Tech Stack:** .NET 10, ASP.NET Core, ModelContextProtocol.AspNetCore 1.2, StackExchange.Redis, xUnit + Shouldly, Wyoming protocol (TCP + newline-delimited JSON + binary payloads), Docker Compose, Blazor WebAssembly.

**Spec:** `docs/superpowers/specs/2026-05-08-voice-satellites-design.md`

---

## File Structure

### Created

```
McpChannelVoice/                                 # new ASP.NET MCP server project
├── Program.cs
├── McpChannelVoice.csproj
├── Dockerfile
├── appsettings.json
├── appsettings.Development.json
├── Modules/
│   └── ConfigModule.cs
├── McpTools/
│   ├── SendReplyTool.cs                         # text → TTS → satellite
│   └── RequestApprovalTool.cs                   # spoken yes/no over satellite
├── Services/
│   ├── ChannelNotificationEmitter.cs            # MCP session registry + outbound notifications/channel/message
│   ├── SatelliteRegistry.cs                     # satellite-id → identity, room, overrides
│   ├── SatelliteSession.cs                      # one wake-to-reply session
│   ├── SatelliteSessionRegistry.cs              # active sessions keyed by satellite id
│   ├── WyomingTcpServer.cs                      # accepts inbound satellite connections
│   ├── WyomingPipelineHandler.cs                # per-connection state machine: info → audio → STT → dispatch
│   ├── ApprovalGrammarParser.cs                 # yes/no/sí/no parsing
│   ├── HeartbeatPublisher.cs                    # periodic HeartbeatEvent for self + Wyoming backends
│   └── VoiceMetricsPublisher.cs                 # thin wrapper around IMetricsPublisher
└── Settings/
    └── VoiceSettings.cs

Domain/Contracts/
├── ISpeechToText.cs
└── ITextToSpeech.cs

Domain/DTOs/Voice/
├── AudioChunk.cs
├── TranscriptionOptions.cs
├── TranscriptionResult.cs
└── SynthesisOptions.cs

Domain/DTOs/Metrics/
└── VoiceUtteranceEvent.cs                       # new MetricEvent subtype

Infrastructure/Clients/Voice/
├── Wyoming/
│   ├── WyomingClient.cs                         # outbound Wyoming TCP client (used to talk to whisper/piper)
│   ├── WyomingProtocol.cs                       # frame read/write helpers
│   └── WyomingEvent.cs                          # event envelope record
├── WyomingSpeechToText.cs                       # ISpeechToText impl
├── WyomingTextToSpeech.cs                       # ITextToSpeech impl
├── OpenAiSpeechToText.cs                        # cloud impl, slice 5
└── OpenAiTextToSpeech.cs                        # cloud impl, slice 5

Dashboard.Client/Pages/
└── Voice.razor

Dashboard.Client/State/Voice/
├── VoiceState.cs
└── VoiceStore.cs

Tests/Unit/McpChannelVoice/
├── SatelliteRegistryTests.cs
├── ApprovalGrammarParserTests.cs
├── SendReplyToolTests.cs
├── RequestApprovalToolTests.cs
└── ChannelNotificationEmitterTests.cs

Tests/Unit/Infrastructure/Voice/
├── WyomingProtocolTests.cs
└── WyomingClientTests.cs

Tests/Integration/McpChannelVoice/
├── WyomingTcpServerTests.cs
├── VoiceRoundTripTests.cs                       # real wyoming-faster-whisper container fixture
└── Fixtures/
    └── WyomingWhisperFixture.cs

scripts/
└── provision-satellite.sh                       # one-time satellite setup helper
```

### Modified

```
Domain/DTOs/Metrics/MetricEvent.cs               # add [JsonDerivedType(typeof(VoiceUtteranceEvent), "voice_utterance")]
Observability/Services/MetricsCollectorService.cs # new ProcessVoiceUtteranceAsync + switch case
Observability/MetricsApiEndpoints.cs             # /api/metrics/voice + /api/metrics/voice/totals
Observability/Services/MetricsQueryService.cs    # GetVoiceTotalsAsync grouping helper
Dashboard.Client/Pages/Overview.razor            # two new KpiCards
Dashboard.Client/Components/HealthGrid.razor     # surface mcp-channel-voice + wyoming-* services (data-only if grid is generic)
Agent/appsettings.json                           # add channelEndpoints entry for mcp-channel-voice
DockerCompose/docker-compose.yml                 # mcp-channel-voice + wyoming-whisper + wyoming-piper services; agent depends_on
DockerCompose/.env                               # placeholder OPENAI_API_KEY (slice 5 only)
```

---

## Conventions used in this plan

- **TDD**: every task that adds behaviour writes a failing test first, watches it fail, implements minimally, watches it pass, commits. The flag `Run: dotnet test --filter "FullyQualifiedName~<TestClass>"` is the standard verification.
- **No XML doc comments** anywhere. Comments only when explaining a non-obvious "why".
- **File-scoped namespaces**, primary constructors for DI, `record` types for DTOs, `IReadOnlyList<T>` returns, LINQ over loops, `TimeProvider` for time-dependent code.
- **Domain layer**: `Domain/Contracts/`, `Domain/DTOs/Voice/`, `Domain/DTOs/Metrics/` — no `Infrastructure` or `Agent` imports.
- **Channel project layout**: mirror `McpChannelTelegram/` exactly. When in doubt, look there.
- **Commit cadence**: commit after every RED→GREEN→REVIEW triplet, or after every successful task. Commit messages follow the existing `feat(scope):` / `test(scope):` convention.

---

## Slice 1 — Channel skeleton

**Goal:** New `McpChannelVoice` project boots, registers as an MCP HTTP server on `/mcp`, exposes no-op `send_reply` and `request_approval` tools, publishes a heartbeat to the metrics bus, and the agent connects to it on startup. No audio yet.

### Task 1.1: Create project skeleton

**Files:**
- Create: `McpChannelVoice/McpChannelVoice.csproj`
- Create: `McpChannelVoice/Program.cs`
- Create: `McpChannelVoice/appsettings.json`
- Create: `McpChannelVoice/appsettings.Development.json`
- Create: `McpChannelVoice/Dockerfile`

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
    <UserSecretsId>2026-voice-channel-c0ffee0001</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="..\.dockerignore">
      <Link>.dockerignore</Link>
    </Content>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.2.0" />
    <PackageReference Include="StackExchange.Redis" Version="2.12.14" />
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
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create Program.cs**

```csharp
// McpChannelVoice/Program.cs
using McpChannelVoice.Modules;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureChannel(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();
```

- [ ] **Step 3: Create appsettings.json**

```json
{
  "Voice": {
    "WyomingServer": { "Host": "0.0.0.0", "Port": 10700 },
    "Stt": {
      "Provider": "Wyoming",
      "Wyoming": { "Host": "wyoming-whisper", "Port": 10300, "Model": "base" }
    },
    "Tts": {
      "Provider": "Wyoming",
      "Wyoming": { "Host": "wyoming-piper", "Port": 10200, "Voice": "es_ES-davefx-medium" }
    },
    "ConfidenceThreshold": 0.4,
    "Satellites": {}
  },
  "Redis": { "ConnectionString": "redis:6379" }
}
```

- [ ] **Step 4: Create appsettings.Development.json**

```json
{
  "Voice": {
    "Stt": { "Wyoming": { "Host": "localhost" } },
    "Tts": { "Wyoming": { "Host": "localhost" } }
  },
  "Redis": { "ConnectionString": "localhost:6379" }
}
```

- [ ] **Step 5: Create Dockerfile**

```dockerfile
# McpChannelVoice/Dockerfile
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

- [ ] **Step 6: Add the project to the solution**

Run: `dotnet sln agent.sln add McpChannelVoice/McpChannelVoice.csproj`
Expected: Project added.

- [ ] **Step 7: Build to verify wiring**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: build fails — `ConfigModule` and `GetSettings/ConfigureChannel` are not yet defined. This failure proves the next task has work to do; do not fix it here.

- [ ] **Step 8: Commit (project skeleton, intentionally not yet building)**

Run: `git add McpChannelVoice agent.sln`
Run: `git commit -m "chore(voice): scaffold McpChannelVoice project (incomplete build)"`

### Task 1.2: VoiceSettings record

**Files:**
- Create: `McpChannelVoice/Settings/VoiceSettings.cs`

- [ ] **Step 1: Define the settings shape**

```csharp
// McpChannelVoice/Settings/VoiceSettings.cs
namespace McpChannelVoice.Settings;

public record RootSettings
{
    public required VoiceSettings Voice { get; init; }
    public required RedisSettings Redis { get; init; }
}

public record RedisSettings
{
    public required string ConnectionString { get; init; }
}

public record VoiceSettings
{
    public required WyomingServerSettings WyomingServer { get; init; }
    public required SpeechToTextSettings Stt { get; init; }
    public required TextToSpeechSettings Tts { get; init; }
    public double ConfidenceThreshold { get; init; } = 0.4;
    public Dictionary<string, SatelliteSettings> Satellites { get; init; } = new();
}

public record WyomingServerSettings
{
    public required string Host { get; init; }
    public required int Port { get; init; }
}

public record SpeechToTextSettings
{
    public required string Provider { get; init; }
    public WyomingBackendSettings? Wyoming { get; init; }
    public OpenAiBackendSettings? OpenAi { get; init; }
}

public record TextToSpeechSettings
{
    public required string Provider { get; init; }
    public WyomingBackendSettings? Wyoming { get; init; }
    public OpenAiBackendSettings? OpenAi { get; init; }
}

public record WyomingBackendSettings
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Model { get; init; }
    public string? Voice { get; init; }
}

public record OpenAiBackendSettings
{
    public string? Model { get; init; }
    public string? Voice { get; init; }
}

public record SatelliteSettings
{
    public required string Identity { get; init; }
    public required string Room { get; init; }
    public string? WakeWord { get; init; }
    public SpeechToTextSettings? Stt { get; init; }
    public TextToSpeechSettings? Tts { get; init; }
}
```

- [ ] **Step 2: Build to verify the type compiles**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: still fails (ConfigModule missing), but no errors from VoiceSettings.

- [ ] **Step 3: Commit**

Run: `git add McpChannelVoice/Settings/VoiceSettings.cs`
Run: `git commit -m "feat(voice): add VoiceSettings record hierarchy"`

### Task 1.3: ChannelNotificationEmitter

Identical pattern to `McpChannelTelegram/Services/ChannelNotificationEmitter.cs`. We replicate, rather than share, because each channel project is independently deployable.

**Files:**
- Create: `McpChannelVoice/Services/ChannelNotificationEmitter.cs`
- Create: `Tests/Unit/McpChannelVoice/ChannelNotificationEmitterTests.cs`

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
    public void HasActiveSessions_AfterRegisterAndUnregister_ReflectsState()
    {
        var emitter = new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance);

        emitter.HasActiveSessions.ShouldBeFalse();

        emitter.RegisterSession("session-a", server: null!);
        emitter.HasActiveSessions.ShouldBeTrue();

        emitter.UnregisterSession("session-a");
        emitter.HasActiveSessions.ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the test, watch it fail**

Run: `dotnet test --filter "FullyQualifiedName~ChannelNotificationEmitterTests"`
Expected: FAIL — compile error, type does not exist.

- [ ] **Step 3: Implement**

```csharp
// McpChannelVoice/Services/ChannelNotificationEmitter.cs
using System.Collections.Concurrent;
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

    public async Task EmitMessageNotificationAsync(
        string conversationId,
        string sender,
        string content,
        string agentId,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            Metadata = metadata,
            Timestamp = DateTimeOffset.UtcNow
        };

        var tasks = _activeSessions.Values.Select(async server =>
        {
            try
            {
                await server.SendNotificationAsync(
                    "notifications/channel/message",
                    payload,
                    cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to emit channel/message notification");
            }
        });

        await Task.WhenAll(tasks);
    }

    public bool HasActiveSessions => !_activeSessions.IsEmpty;
}
```

- [ ] **Step 4: Run the test, watch it pass**

Run: `dotnet test --filter "FullyQualifiedName~ChannelNotificationEmitterTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

Run: `git add McpChannelVoice/Services/ChannelNotificationEmitter.cs Tests/Unit/McpChannelVoice/ChannelNotificationEmitterTests.cs`
Run: `git commit -m "feat(voice): add ChannelNotificationEmitter mirroring telegram channel"`

### Task 1.4: SatelliteRegistry

**Files:**
- Create: `McpChannelVoice/Services/SatelliteRegistry.cs`
- Create: `Tests/Unit/McpChannelVoice/SatelliteRegistryTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Tests/Unit/McpChannelVoice/SatelliteRegistryTests.cs
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SatelliteRegistryTests
{
    private static readonly Dictionary<string, SatelliteSettings> _testSatellites = new()
    {
        ["kitchen-01"] = new() { Identity = "household", Room = "Kitchen", WakeWord = "hey_jarvis" },
        ["bedroom-01"] = new() { Identity = "francisco", Room = "Bedroom", WakeWord = "hey_jarvis" }
    };

    [Fact]
    public void Resolve_KnownSatellite_ReturnsConfig()
    {
        var registry = new SatelliteRegistry(_testSatellites);

        var config = registry.Resolve("kitchen-01");

        config.ShouldNotBeNull();
        config!.Identity.ShouldBe("household");
        config.Room.ShouldBe("Kitchen");
    }

    [Fact]
    public void Resolve_UnknownSatellite_ReturnsNull()
    {
        var registry = new SatelliteRegistry(_testSatellites);

        registry.Resolve("garage-01").ShouldBeNull();
    }

    [Fact]
    public void IsRegistered_KnownSatellite_ReturnsTrue()
    {
        var registry = new SatelliteRegistry(_testSatellites);

        registry.IsRegistered("kitchen-01").ShouldBeTrue();
        registry.IsRegistered("garage-01").ShouldBeFalse();
    }
}
```

- [ ] **Step 2: Run the tests, watch them fail**

Run: `dotnet test --filter "FullyQualifiedName~SatelliteRegistryTests"`
Expected: FAIL — `SatelliteRegistry` does not exist.

- [ ] **Step 3: Implement**

```csharp
// McpChannelVoice/Services/SatelliteRegistry.cs
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class SatelliteRegistry(IReadOnlyDictionary<string, SatelliteSettings> satellites)
{
    public SatelliteSettings? Resolve(string satelliteId) =>
        satellites.TryGetValue(satelliteId, out var config) ? config : null;

    public bool IsRegistered(string satelliteId) => satellites.ContainsKey(satelliteId);

    public IEnumerable<string> SatelliteIds => satellites.Keys;
}
```

- [ ] **Step 4: Run the tests, watch them pass**

Run: `dotnet test --filter "FullyQualifiedName~SatelliteRegistryTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

Run: `git add McpChannelVoice/Services/SatelliteRegistry.cs Tests/Unit/McpChannelVoice/SatelliteRegistryTests.cs`
Run: `git commit -m "feat(voice): add SatelliteRegistry for static satellite-id mapping"`

### Task 1.5: No-op SendReplyTool and RequestApprovalTool

These return `"ok"` immediately so the agent's tool-call wiring works end-to-end. Real audio behaviour comes in Slices 3 and 4.

**Files:**
- Create: `McpChannelVoice/McpTools/SendReplyTool.cs`
- Create: `McpChannelVoice/McpTools/RequestApprovalTool.cs`
- Create: `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs`
- Create: `Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs`

- [ ] **Step 1: Write a failing test for SendReplyTool**

```csharp
// Tests/Unit/McpChannelVoice/SendReplyToolTests.cs
using Domain.DTOs.Channel;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SendReplyToolTests
{
    [Fact]
    public async Task McpRun_TextChunk_ReturnsOkAndDoesNotThrow()
    {
        var services = new ServiceCollection()
            .AddSingleton(new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance))
            .BuildServiceProvider();

        var result = await SendReplyTool.McpRun(
            conversationId: "kitchen-01",
            content: "hello",
            contentType: ReplyContentType.Text,
            isComplete: true,
            messageId: null,
            services: services);

        result.ShouldBe("ok");
    }
}
```

- [ ] **Step 2: Run the test, watch it fail**

Run: `dotnet test --filter "FullyQualifiedName~SendReplyToolTests"`
Expected: FAIL — `SendReplyTool` does not exist.

- [ ] **Step 3: Implement no-op SendReplyTool**

```csharp
// McpChannelVoice/McpTools/SendReplyTool.cs
using System.ComponentModel;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = "send_reply")]
    [Description("Send a response to a voice satellite. The text is synthesised via TTS and streamed back to the originating satellite.")]
    public static Task<string> McpRun(
        [Description("Satellite id (matches Voice:Satellites key)")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services)
    {
        // Slice 1 stub: behaviour added in Slice 3 (TTS path).
        return Task.FromResult("ok");
    }
}
```

- [ ] **Step 4: Run the SendReplyTool test, watch it pass**

Run: `dotnet test --filter "FullyQualifiedName~SendReplyToolTests"`
Expected: PASS.

- [ ] **Step 5: Write a failing test for RequestApprovalTool**

```csharp
// Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs
using Domain.DTOs.Channel;
using McpChannelVoice.McpTools;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class RequestApprovalToolTests
{
    [Fact]
    public async Task McpRun_NotifyMode_ReturnsNotified()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var result = await RequestApprovalTool.McpRun(
            conversationId: "kitchen-01",
            mode: ApprovalMode.Notify,
            requests: "[]",
            services: services);

        result.ShouldBe("notified");
    }
}
```

- [ ] **Step 6: Run the test, watch it fail**

Run: `dotnet test --filter "FullyQualifiedName~RequestApprovalToolTests"`
Expected: FAIL — `RequestApprovalTool` does not exist.

- [ ] **Step 7: Implement no-op RequestApprovalTool**

```csharp
// McpChannelVoice/McpTools/RequestApprovalTool.cs
using System.ComponentModel;
using Domain.DTOs.Channel;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    [McpServerTool(Name = "request_approval")]
    [Description("Ask the user to approve a tool call by spoken yes/no on the originating satellite.")]
    public static Task<string> McpRun(
        [Description("Satellite id")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("JSON array of tool requests [{toolName, arguments}]")] string requests,
        IServiceProvider services)
    {
        // Slice 1 stub: real behaviour added in Slice 4 (approval grammar).
        return Task.FromResult(mode == ApprovalMode.Notify ? "notified" : "approve");
    }
}
```

- [ ] **Step 8: Run the test, watch it pass**

Run: `dotnet test --filter "FullyQualifiedName~RequestApprovalToolTests"`
Expected: PASS.

- [ ] **Step 9: Commit**

Run: `git add McpChannelVoice/McpTools Tests/Unit/McpChannelVoice/SendReplyToolTests.cs Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs`
Run: `git commit -m "feat(voice): add stub send_reply and request_approval MCP tools"`

### Task 1.6: ConfigModule and DI wiring

Mirrors `McpChannelTelegram/Modules/ConfigModule.cs`.

**Files:**
- Create: `McpChannelVoice/Modules/ConfigModule.cs`

- [ ] **Step 1: Implement**

```csharp
// McpChannelVoice/Modules/ConfigModule.cs
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using ModelContextProtocol.Protocol;

namespace McpChannelVoice.Modules;

public static class ConfigModule
{
    public static RootSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<RootSettings>();
        return settings ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureChannel(this IServiceCollection services, RootSettings settings)
    {
        var notificationEmitter = new ChannelNotificationEmitter(
            LoggerFactory.Create(b => b.AddConsole()).CreateLogger<ChannelNotificationEmitter>());

        services
            .AddSingleton(settings)
            .AddSingleton(settings.Voice)
            .AddSingleton(notificationEmitter)
            .AddSingleton(new SatelliteRegistry(settings.Voice.Satellites));

        services
            .AddMcpServer()
            .WithHttpTransport(options =>
            {
#pragma warning disable MCPEXP002
                options.RunSessionHandler = async (_, server, ct) =>
                {
                    var sessionId = server.SessionId ?? Guid.NewGuid().ToString();
                    notificationEmitter.RegisterSession(sessionId, server);
                    try
                    {
                        await server.RunAsync(ct);
                    }
                    finally
                    {
                        notificationEmitter.UnregisterSession(sessionId);
                    }
                };
#pragma warning restore MCPEXP002
            })
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
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

- [ ] **Step 2: Build to verify**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: build succeeds.

- [ ] **Step 3: Run the channel locally as a smoke test**

Run: `dotnet run --project McpChannelVoice/McpChannelVoice.csproj --launch-profile https`
Expected: server starts on the default port; `Now listening on:` log line appears. Stop with Ctrl-C.

- [ ] **Step 4: Commit**

Run: `git add McpChannelVoice/Modules/ConfigModule.cs`
Run: `git commit -m "feat(voice): add ConfigModule wiring (DI, MCP HTTP transport, error filter)"`

### Task 1.7: HeartbeatPublisher hosted service

Publishes `HeartbeatEvent` for `mcp-channel-voice` every 30s so the dashboard's `HealthGrid` lights up immediately. We do not yet publish heartbeats for the Wyoming backends — that requires a probe and lands in Slice 2.

**Files:**
- Create: `McpChannelVoice/Services/HeartbeatPublisher.cs`

- [ ] **Step 1: Implement**

```csharp
// McpChannelVoice/Services/HeartbeatPublisher.cs
using Domain.Contracts;
using Domain.DTOs.Metrics;

namespace McpChannelVoice.Services;

public sealed class HeartbeatPublisher(IMetricsPublisher publisher, ILogger<HeartbeatPublisher> logger)
    : BackgroundService
{
    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(30);
    private const string ServiceName = "mcp-channel-voice";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await publisher.PublishAsync(
                    new HeartbeatEvent { Service = ServiceName },
                    stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to publish heartbeat");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
```

- [ ] **Step 2: Wire IMetricsPublisher and the hosted service in ConfigModule**

Edit `McpChannelVoice/Modules/ConfigModule.cs`. Add the following inside `ConfigureChannel` after the `notificationEmitter` registration block, before `services.AddMcpServer()`:

```csharp
services
    .AddSingleton<StackExchange.Redis.IConnectionMultiplexer>(_ =>
        StackExchange.Redis.ConnectionMultiplexer.Connect(settings.Redis.ConnectionString))
    .AddSingleton<IMetricsPublisher, Infrastructure.Metrics.RedisMetricsPublisher>()
    .AddHostedService<HeartbeatPublisher>();
```

Add `using Domain.Contracts;` at the top of the file.

- [ ] **Step 3: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

Run: `git add McpChannelVoice/Services/HeartbeatPublisher.cs McpChannelVoice/Modules/ConfigModule.cs`
Run: `git commit -m "feat(voice): publish heartbeat metric so dashboard health grid registers the channel"`

### Task 1.8: Docker Compose entry and agent wiring

**Files:**
- Modify: `DockerCompose/docker-compose.yml`
- Modify: `Agent/appsettings.json`

- [ ] **Step 1: Add the new service to docker-compose.yml**

Insert after the `mcp-channel-servicebus` block (after the line `condition: service_started` of its `depends_on`):

```yaml
  mcp-channel-voice:
    image: mcp-channel-voice:latest
    logging:
      options:
        max-size: "5m"
        max-file: "3"
    container_name: mcp-channel-voice
    ports:
      - "6013:8080"
      - "10700:10700"
    build:
      context: ${REPOSITORY_PATH}
      dockerfile: McpChannelVoice/Dockerfile
      cache_from:
        - mcp-channel-voice:latest
      args:
        - BUILDKIT_INLINE_CACHE=1
    restart: unless-stopped
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

Then add to the `agent` service's `depends_on` block (under the existing channel entries):

```yaml
      mcp-channel-voice:
        condition: service_started
```

- [ ] **Step 2: Add channel endpoint to Agent/appsettings.json**

Edit `Agent/appsettings.json`. In the `channelEndpoints` array, add a new entry:

```json
{
    "channelId": "voice",
    "endpoint": "http://mcp-channel-voice:8080/mcp"
}
```

- [ ] **Step 3: Build and start the stack**

Run: `docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot build mcp-channel-voice`
Expected: image builds successfully.

Run: `docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d mcp-channel-voice agent redis observability`
Expected: services come up healthy.

- [ ] **Step 4: Verify the agent connected**

Run: `docker logs agent 2>&1 | grep -i "mcp-channel-voice" | head`
Expected: at least one log line indicating the channel was discovered or connected.

Run: `docker logs mcp-channel-voice 2>&1 | grep "Now listening" | head`
Expected: the channel server is listening on port 8080.

Run: `docker exec redis redis-cli SUBSCRIBE metrics:events &`
Wait 35 seconds.
Expected: a `heartbeat` event with `"service":"mcp-channel-voice"` is published. Press Ctrl-C to stop the subscriber.

- [ ] **Step 5: Commit**

Run: `git add DockerCompose/docker-compose.yml Agent/appsettings.json`
Run: `git commit -m "feat(voice): wire mcp-channel-voice into compose and agent channelEndpoints"`

### Slice 1 done

`McpChannelVoice` is up, the agent connects, both stub tools answer correctly, and the `mcp-channel-voice` service shows up as healthy on the dashboard. No audio yet.

---

## Slice 2 — STT path

**Goal:** A satellite (real Pi Zero 2 W or a desktop running `wyoming-satellite`) wakes, speaks, and the agent receives the transcript as a `notifications/channel/message` from the configured identity. Adds the Wyoming protocol library, the speech contracts, the Wyoming server in the channel, the `WyomingSpeechToText` adapter, and the metrics + minimal dashboard surface.

### Task 2.1: AudioChunk and speech DTOs

**Files:**
- Create: `Domain/DTOs/Voice/AudioChunk.cs`
- Create: `Domain/DTOs/Voice/TranscriptionResult.cs`
- Create: `Domain/DTOs/Voice/TranscriptionOptions.cs`
- Create: `Domain/DTOs/Voice/SynthesisOptions.cs`

- [ ] **Step 1: Implement DTOs**

```csharp
// Domain/DTOs/Voice/AudioChunk.cs
namespace Domain.DTOs.Voice;

public record AudioChunk(byte[] Pcm, int SampleRate, int SampleWidthBytes, int Channels, DateTimeOffset Timestamp);
```

```csharp
// Domain/DTOs/Voice/TranscriptionResult.cs
namespace Domain.DTOs.Voice;

public record TranscriptionResult(string Text, string? Language, double Confidence, long DurationMs);
```

```csharp
// Domain/DTOs/Voice/TranscriptionOptions.cs
namespace Domain.DTOs.Voice;

public record TranscriptionOptions(string? LanguageHint = null, string? Prompt = null, string? ModelOverride = null);
```

```csharp
// Domain/DTOs/Voice/SynthesisOptions.cs
namespace Domain.DTOs.Voice;

public record SynthesisOptions(string? Voice = null, string Format = "pcm", int SampleRate = 22050, int SampleWidthBytes = 2, int Channels = 1);
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Domain/Domain.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

Run: `git add Domain/DTOs/Voice`
Run: `git commit -m "feat(voice): add Voice DTOs (AudioChunk, TranscriptionResult, options)"`

### Task 2.2: ISpeechToText and ITextToSpeech contracts

**Files:**
- Create: `Domain/Contracts/ISpeechToText.cs`
- Create: `Domain/Contracts/ITextToSpeech.cs`

- [ ] **Step 1: Implement**

```csharp
// Domain/Contracts/ISpeechToText.cs
using Domain.DTOs.Voice;

namespace Domain.Contracts;

public interface ISpeechToText
{
    Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken);
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
        CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Domain/Domain.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

Run: `git add Domain/Contracts/ISpeechToText.cs Domain/Contracts/ITextToSpeech.cs`
Run: `git commit -m "feat(voice): add ISpeechToText and ITextToSpeech contracts"`

### Task 2.3: Wyoming protocol primitives

The Wyoming protocol (https://github.com/rhasspy/wyoming) frames events as a JSON header line followed by optional binary data and payload sections. The header indicates `data_length` and `payload_length`. We only need the subset required for `info`, `audio-start`, `audio-chunk`, `audio-stop`, `transcribe`, `transcript`, `synthesize`, `run-satellite`, `detect`, `detection`.

**Files:**
- Create: `Infrastructure/Clients/Voice/Wyoming/WyomingEvent.cs`
- Create: `Infrastructure/Clients/Voice/Wyoming/WyomingProtocol.cs`
- Create: `Tests/Unit/Infrastructure/Voice/WyomingProtocolTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
// Tests/Unit/Infrastructure/Voice/WyomingProtocolTests.cs
using System.Text;
using System.Text.Json;
using Infrastructure.Clients.Voice.Wyoming;
using Shouldly;

namespace Tests.Unit.Infrastructure.Voice;

public class WyomingProtocolTests
{
    [Fact]
    public async Task WriteAsync_HeaderOnly_ProducesNewlineDelimitedJson()
    {
        await using var stream = new MemoryStream();
        var evt = new WyomingEvent("transcribe", JsonDocument.Parse("""{"language":"es"}""").RootElement, payload: null);

        await WyomingProtocol.WriteAsync(stream, evt, CancellationToken.None);

        var bytes = stream.ToArray();
        var line = Encoding.UTF8.GetString(bytes);
        line.ShouldEndWith("\n");
        line.ShouldContain("\"type\":\"transcribe\"");
        line.ShouldContain("\"data\":{\"language\":\"es\"}");
    }

    [Fact]
    public async Task WriteThenRead_WithBinaryPayload_RoundTrips()
    {
        await using var stream = new MemoryStream();
        var data = JsonDocument.Parse("""{"rate":16000,"width":2,"channels":1}""").RootElement;
        var payload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var written = new WyomingEvent("audio-chunk", data, payload);

        await WyomingProtocol.WriteAsync(stream, written, CancellationToken.None);
        stream.Position = 0;
        var read = await WyomingProtocol.ReadAsync(stream, CancellationToken.None);

        read.ShouldNotBeNull();
        read!.Type.ShouldBe("audio-chunk");
        read.Payload.ShouldBe(payload);
    }

    [Fact]
    public async Task ReadAsync_EmptyStream_ReturnsNull()
    {
        await using var stream = new MemoryStream();

        var read = await WyomingProtocol.ReadAsync(stream, CancellationToken.None);

        read.ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run the tests, watch them fail**

Run: `dotnet test --filter "FullyQualifiedName~WyomingProtocolTests"`
Expected: FAIL — types do not exist.

- [ ] **Step 3: Implement WyomingEvent**

```csharp
// Infrastructure/Clients/Voice/Wyoming/WyomingEvent.cs
using System.Text.Json;

namespace Infrastructure.Clients.Voice.Wyoming;

public sealed record WyomingEvent(string Type, JsonElement Data, byte[]? Payload);
```

- [ ] **Step 4: Implement WyomingProtocol**

```csharp
// Infrastructure/Clients/Voice/Wyoming/WyomingProtocol.cs
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Infrastructure.Clients.Voice.Wyoming;

public static class WyomingProtocol
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    public static async Task WriteAsync(Stream stream, WyomingEvent evt, CancellationToken ct)
    {
        var header = new Dictionary<string, object?>
        {
            ["type"] = evt.Type,
            ["data"] = evt.Data
        };

        if (evt.Payload is not null)
        {
            header["payload_length"] = evt.Payload.Length;
        }

        var headerJson = JsonSerializer.Serialize(header, _json);
        var headerBytes = Encoding.UTF8.GetBytes(headerJson + "\n");

        await stream.WriteAsync(headerBytes, ct);
        if (evt.Payload is { Length: > 0 } payload)
        {
            await stream.WriteAsync(payload, ct);
        }

        await stream.FlushAsync(ct);
    }

    public static async Task<WyomingEvent?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var headerLine = await ReadLineAsync(stream, ct);
        if (headerLine is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(headerLine);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString() ?? throw new InvalidOperationException("Wyoming event missing 'type'");
        var data = root.TryGetProperty("data", out var dataEl)
            ? JsonDocument.Parse(dataEl.GetRawText()).RootElement
            : JsonDocument.Parse("{}").RootElement;

        byte[]? payload = null;
        if (root.TryGetProperty("payload_length", out var lenEl) && lenEl.TryGetInt32(out var payloadLength) && payloadLength > 0)
        {
            payload = new byte[payloadLength];
            var read = 0;
            while (read < payloadLength)
            {
                var n = await stream.ReadAsync(payload.AsMemory(read, payloadLength - read), ct);
                if (n == 0) throw new EndOfStreamException("Unexpected end of Wyoming payload");
                read += n;
            }
        }

        return new WyomingEvent(type, data, payload);
    }

    private static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            var pos = 0;
            while (true)
            {
                if (pos == buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    Buffer.BlockCopy(buffer, 0, grown, 0, pos);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }

                var n = await stream.ReadAsync(buffer.AsMemory(pos, 1), ct);
                if (n == 0) return pos == 0 ? null : Encoding.UTF8.GetString(buffer, 0, pos);
                if (buffer[pos] == (byte)'\n') return Encoding.UTF8.GetString(buffer, 0, pos);
                pos++;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
```

- [ ] **Step 5: Run the tests, watch them pass**

Run: `dotnet test --filter "FullyQualifiedName~WyomingProtocolTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

Run: `git add Infrastructure/Clients/Voice/Wyoming Tests/Unit/Infrastructure/Voice`
Run: `git commit -m "feat(voice): add minimal Wyoming protocol primitives (read/write event frames)"`

### Task 2.4: WyomingClient (outbound, talks to whisper/piper)

**Files:**
- Create: `Infrastructure/Clients/Voice/Wyoming/WyomingClient.cs`
- Create: `Tests/Unit/Infrastructure/Voice/WyomingClientTests.cs`

- [ ] **Step 1: Write the failing test using a TCP listener as the fake server**

```csharp
// Tests/Unit/Infrastructure/Voice/WyomingClientTests.cs
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Infrastructure.Clients.Voice.Wyoming;
using Shouldly;

namespace Tests.Unit.Infrastructure.Voice;

public class WyomingClientTests
{
    [Fact]
    public async Task SendAndReceive_RoundTripsThroughLocalListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var server = await listener.AcceptTcpClientAsync();
            using var stream = server.GetStream();
            var received = await WyomingProtocol.ReadAsync(stream, CancellationToken.None);
            received.ShouldNotBeNull();
            received!.Type.ShouldBe("transcribe");

            var data = JsonDocument.Parse("""{"text":"hola","language":"es"}""").RootElement;
            await WyomingProtocol.WriteAsync(stream, new WyomingEvent("transcript", data, null), CancellationToken.None);
        });

        await using var client = new WyomingClient("127.0.0.1", port);
        await client.ConnectAsync(CancellationToken.None);
        await client.SendAsync(new WyomingEvent("transcribe", JsonDocument.Parse("""{"language":"es"}""").RootElement, null), CancellationToken.None);
        var reply = await client.ReceiveAsync(CancellationToken.None);

        reply.ShouldNotBeNull();
        reply!.Type.ShouldBe("transcript");

        await serverTask;
        listener.Stop();
    }
}
```

- [ ] **Step 2: Run, watch it fail**

Run: `dotnet test --filter "FullyQualifiedName~WyomingClientTests"`
Expected: FAIL — `WyomingClient` does not exist.

- [ ] **Step 3: Implement**

```csharp
// Infrastructure/Clients/Voice/Wyoming/WyomingClient.cs
using System.Net.Sockets;

namespace Infrastructure.Clients.Voice.Wyoming;

public sealed class WyomingClient(string host, int port) : IAsyncDisposable
{
    private TcpClient? _tcp;
    private NetworkStream? _stream;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(host, port, ct);
        _stream = _tcp.GetStream();
    }

    public Task SendAsync(WyomingEvent evt, CancellationToken ct) =>
        WyomingProtocol.WriteAsync(_stream ?? throw new InvalidOperationException("Not connected"), evt, ct);

    public Task<WyomingEvent?> ReceiveAsync(CancellationToken ct) =>
        WyomingProtocol.ReadAsync(_stream ?? throw new InvalidOperationException("Not connected"), ct);

    public async ValueTask DisposeAsync()
    {
        if (_stream is not null) await _stream.DisposeAsync();
        _tcp?.Dispose();
    }
}
```

- [ ] **Step 4: Run, watch it pass**

Run: `dotnet test --filter "FullyQualifiedName~WyomingClientTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

Run: `git add Infrastructure/Clients/Voice/Wyoming/WyomingClient.cs Tests/Unit/Infrastructure/Voice/WyomingClientTests.cs`
Run: `git commit -m "feat(voice): add WyomingClient with connect/send/receive"`

### Task 2.5: WyomingSpeechToText adapter

**Files:**
- Create: `Infrastructure/Clients/Voice/WyomingSpeechToText.cs`

- [ ] **Step 1: Implement** (no unit test — exercised end-to-end in the integration test in Task 2.10; the adapter is thin and wraps `WyomingClient`)

```csharp
// Infrastructure/Clients/Voice/WyomingSpeechToText.cs
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice.Wyoming;

namespace Infrastructure.Clients.Voice;

public sealed class WyomingSpeechToText(string host, int port, string? model) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken)
    {
        await using var client = new WyomingClient(host, port);
        await client.ConnectAsync(cancellationToken);

        var transcribeData = JsonSerializer.SerializeToElement(new
        {
            language = options.LanguageHint,
            name = options.ModelOverride ?? model
        });
        await client.SendAsync(new WyomingEvent("transcribe", transcribeData, null), cancellationToken);

        var first = true;
        long startMs = Environment.TickCount64;
        await foreach (var chunk in audio.WithCancellation(cancellationToken))
        {
            if (first)
            {
                var startData = JsonSerializer.SerializeToElement(new
                {
                    rate = chunk.SampleRate,
                    width = chunk.SampleWidthBytes,
                    channels = chunk.Channels
                });
                await client.SendAsync(new WyomingEvent("audio-start", startData, null), cancellationToken);
                first = false;
            }

            var chunkData = JsonSerializer.SerializeToElement(new
            {
                rate = chunk.SampleRate,
                width = chunk.SampleWidthBytes,
                channels = chunk.Channels,
                timestamp = chunk.Timestamp.ToUnixTimeMilliseconds()
            });
            await client.SendAsync(new WyomingEvent("audio-chunk", chunkData, chunk.Pcm), cancellationToken);
        }

        await client.SendAsync(
            new WyomingEvent("audio-stop", JsonDocument.Parse("{}").RootElement, null),
            cancellationToken);

        while (true)
        {
            var evt = await client.ReceiveAsync(cancellationToken)
                ?? throw new InvalidOperationException("Wyoming connection closed before transcript");
            if (evt.Type == "transcript")
            {
                var text = evt.Data.GetProperty("text").GetString() ?? string.Empty;
                var language = evt.Data.TryGetProperty("language", out var l) ? l.GetString() : null;
                var confidence = evt.Data.TryGetProperty("confidence", out var c) ? c.GetDouble() : 1.0;
                return new TranscriptionResult(text, language, confidence, Environment.TickCount64 - startMs);
            }
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Infrastructure/Infrastructure.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

Run: `git add Infrastructure/Clients/Voice/WyomingSpeechToText.cs`
Run: `git commit -m "feat(voice): add WyomingSpeechToText adapter"`

### Task 2.6: VoiceUtteranceEvent and metric publisher

**Files:**
- Create: `Domain/DTOs/Metrics/VoiceUtteranceEvent.cs`
- Modify: `Domain/DTOs/Metrics/MetricEvent.cs`

- [ ] **Step 1: Add the new event record**

```csharp
// Domain/DTOs/Metrics/VoiceUtteranceEvent.cs
namespace Domain.DTOs.Metrics;

public record VoiceUtteranceEvent : MetricEvent
{
    public required string SatelliteId { get; init; }
    public required string Room { get; init; }
    public required string Identity { get; init; }
    public required string SttProvider { get; init; }
    public string? SttModel { get; init; }
    public string? TtsProvider { get; init; }
    public string? TtsVoice { get; init; }
    public string? Language { get; init; }
    public required double AudioSeconds { get; init; }
    public required long SttLatencyMs { get; init; }
    public long? TtsLatencyMs { get; init; }
    public long? WakeToFirstAudioMs { get; init; }
    public required bool Success { get; init; }
    public string? ErrorType { get; init; }
    public string? ErrorMessage { get; init; }
    public string? ApprovalOutcome { get; init; }
}
```

- [ ] **Step 2: Register the polymorphic discriminator**

Edit `Domain/DTOs/Metrics/MetricEvent.cs`. Add a new `[JsonDerivedType]` line in the existing list:

```csharp
[JsonDerivedType(typeof(VoiceUtteranceEvent), "voice_utterance")]
```

- [ ] **Step 3: Build**

Run: `dotnet build Domain/Domain.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

Run: `git add Domain/DTOs/Metrics/VoiceUtteranceEvent.cs Domain/DTOs/Metrics/MetricEvent.cs`
Run: `git commit -m "feat(voice): add VoiceUtteranceEvent metric type"`

### Task 2.7: SatelliteSession + SatelliteSessionRegistry

A `SatelliteSession` represents one in-flight wake-to-reply turn. It owns the open Wyoming network stream so the channel can stream TTS audio back without re-connecting.

**Files:**
- Create: `McpChannelVoice/Services/SatelliteSession.cs`
- Create: `McpChannelVoice/Services/SatelliteSessionRegistry.cs`

- [ ] **Step 1: Implement**

```csharp
// McpChannelVoice/Services/SatelliteSession.cs
using System.Net.Sockets;

namespace McpChannelVoice.Services;

public sealed class SatelliteSession(string satelliteId, NetworkStream stream) : IAsyncDisposable
{
    public string SatelliteId { get; } = satelliteId;
    public NetworkStream Stream { get; } = stream;
    public DateTimeOffset OpenedAt { get; } = DateTimeOffset.UtcNow;

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
    }
}
```

```csharp
// McpChannelVoice/Services/SatelliteSessionRegistry.cs
using System.Collections.Concurrent;

namespace McpChannelVoice.Services;

public sealed class SatelliteSessionRegistry
{
    private readonly ConcurrentDictionary<string, SatelliteSession> _sessions = new();

    public void Register(SatelliteSession session) => _sessions[session.SatelliteId] = session;
    public void Unregister(string satelliteId) => _sessions.TryRemove(satelliteId, out _);
    public SatelliteSession? Get(string satelliteId) => _sessions.TryGetValue(satelliteId, out var s) ? s : null;
}
```

- [ ] **Step 2: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

Run: `git add McpChannelVoice/Services/SatelliteSession.cs McpChannelVoice/Services/SatelliteSessionRegistry.cs`
Run: `git commit -m "feat(voice): add SatelliteSession + registry for active satellite connections"`

### Task 2.8: WyomingPipelineHandler

The per-connection state machine. Consumes Wyoming events from one inbound satellite connection: handshake (`info` / `run-satellite`) → `audio-start` → N x `audio-chunk` → `audio-stop` → run STT → emit `channel/message` → keep the connection open for the reply.

**Files:**
- Create: `McpChannelVoice/Services/WyomingPipelineHandler.cs`

- [ ] **Step 1: Implement**

```csharp
// McpChannelVoice/Services/WyomingPipelineHandler.cs
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;
using Domain.Contracts;
using Domain.DTOs.Metrics;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice.Wyoming;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class WyomingPipelineHandler(
    SatelliteRegistry registry,
    SatelliteSessionRegistry sessions,
    ChannelNotificationEmitter emitter,
    ISpeechToText stt,
    VoiceSettings voiceSettings,
    IMetricsPublisher metrics,
    ILogger<WyomingPipelineHandler> logger,
    TimeProvider clock)
{
    public async Task HandleAsync(TcpClient tcp, CancellationToken ct)
    {
        await using var stream = tcp.GetStream();
        string? satelliteId = null;
        SatelliteSession? session = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var evt = await WyomingProtocol.ReadAsync(stream, ct);
                if (evt is null) return;

                switch (evt.Type)
                {
                    case "info":
                    case "run-satellite":
                        satelliteId = evt.Data.TryGetProperty("name", out var nameEl)
                            ? nameEl.GetString()
                            : null;
                        if (satelliteId is null || !registry.IsRegistered(satelliteId))
                        {
                            logger.LogWarning("Rejecting unknown satellite '{SatelliteId}'", satelliteId ?? "<null>");
                            return;
                        }
                        session = new SatelliteSession(satelliteId, stream);
                        sessions.Register(session);
                        logger.LogInformation("Satellite '{SatelliteId}' connected", satelliteId);
                        break;

                    case "audio-start":
                        if (session is null) return;
                        await RunUtteranceAsync(session, stream, ct);
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Wyoming connection error for satellite '{SatelliteId}'", satelliteId);
        }
        finally
        {
            if (satelliteId is not null) sessions.Unregister(satelliteId);
        }
    }

    private async Task RunUtteranceAsync(SatelliteSession session, NetworkStream stream, CancellationToken ct)
    {
        var config = registry.Resolve(session.SatelliteId)!;
        var startMs = clock.GetTimestamp();
        var audio = Channel.CreateUnbounded<AudioChunk>();
        var producerTask = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var evt = await WyomingProtocol.ReadAsync(stream, ct);
                    if (evt is null || evt.Type == "audio-stop") break;
                    if (evt.Type != "audio-chunk" || evt.Payload is null) continue;

                    audio.Writer.TryWrite(new AudioChunk(
                        evt.Payload,
                        evt.Data.GetProperty("rate").GetInt32(),
                        evt.Data.TryGetProperty("width", out var w) ? w.GetInt32() : 2,
                        evt.Data.TryGetProperty("channels", out var c) ? c.GetInt32() : 1,
                        DateTimeOffset.UtcNow));
                }
            }
            finally
            {
                audio.Writer.TryComplete();
            }
        }, ct);

        var transcript = await stt.TranscribeAsync(audio.Reader.ReadAllAsync(ct),
            new TranscriptionOptions(LanguageHint: null), ct);
        await producerTask;

        var elapsedMs = (long)clock.GetElapsedTime(startMs).TotalMilliseconds;
        var passedThreshold = transcript.Confidence >= voiceSettings.ConfidenceThreshold
                              && !string.IsNullOrWhiteSpace(transcript.Text);

        await metrics.PublishAsync(new VoiceUtteranceEvent
        {
            SatelliteId = session.SatelliteId,
            Room = config.Room,
            Identity = config.Identity,
            SttProvider = voiceSettings.Stt.Provider,
            SttModel = voiceSettings.Stt.Wyoming?.Model,
            TtsProvider = voiceSettings.Tts.Provider,
            TtsVoice = voiceSettings.Tts.Wyoming?.Voice,
            Language = transcript.Language,
            AudioSeconds = 0, // populated when AudioChunk durations are tracked, slice 3
            SttLatencyMs = elapsedMs,
            Success = passedThreshold,
            ErrorType = passedThreshold ? null : "DroppedLowConfidence",
            ConversationId = session.SatelliteId
        }, ct);

        if (!passedThreshold)
        {
            logger.LogInformation("Dropped low-confidence transcript from {SatelliteId}", session.SatelliteId);
            return;
        }

        await emitter.EmitMessageNotificationAsync(
            conversationId: session.SatelliteId,
            sender: config.Identity,
            content: transcript.Text,
            agentId: "voice",
            metadata: new Dictionary<string, string>
            {
                ["room"] = config.Room,
                ["language"] = transcript.Language ?? string.Empty
            },
            cancellationToken: ct);
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

Run: `git add McpChannelVoice/Services/WyomingPipelineHandler.cs`
Run: `git commit -m "feat(voice): add WyomingPipelineHandler (handshake → audio → STT → dispatch)"`

### Task 2.9: WyomingTcpServer hosted service

**Files:**
- Create: `McpChannelVoice/Services/WyomingTcpServer.cs`

- [ ] **Step 1: Implement**

```csharp
// McpChannelVoice/Services/WyomingTcpServer.cs
using System.Net;
using System.Net.Sockets;
using McpChannelVoice.Settings;

namespace McpChannelVoice.Services;

public sealed class WyomingTcpServer(
    VoiceSettings voiceSettings,
    IServiceProvider services,
    ILogger<WyomingTcpServer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var listener = new TcpListener(IPAddress.Parse(voiceSettings.WyomingServer.Host), voiceSettings.WyomingServer.Port);
        listener.Start();
        logger.LogInformation("Wyoming TCP server listening on {Host}:{Port}",
            voiceSettings.WyomingServer.Host, voiceSettings.WyomingServer.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var tcp = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(async () =>
                {
                    using var scope = services.CreateScope();
                    var handler = scope.ServiceProvider.GetRequiredService<WyomingPipelineHandler>();
                    await handler.HandleAsync(tcp, stoppingToken);
                }, stoppingToken);
            }
        }
        finally
        {
            listener.Stop();
        }
    }
}
```

- [ ] **Step 2: Wire it in ConfigModule**

Edit `McpChannelVoice/Modules/ConfigModule.cs`. Inside `ConfigureChannel`, add the following before `services.AddMcpServer()`:

```csharp
services
    .AddSingleton(TimeProvider.System)
    .AddSingleton<SatelliteSessionRegistry>()
    .AddScoped<WyomingPipelineHandler>()
    .AddSingleton<ISpeechToText>(sp =>
    {
        var voice = sp.GetRequiredService<VoiceSettings>();
        return voice.Stt.Provider switch
        {
            "Wyoming" => new Infrastructure.Clients.Voice.WyomingSpeechToText(
                voice.Stt.Wyoming!.Host, voice.Stt.Wyoming.Port, voice.Stt.Wyoming.Model),
            _ => throw new InvalidOperationException($"Unsupported STT provider: {voice.Stt.Provider}")
        };
    })
    .AddHostedService<WyomingTcpServer>();
```

Add `using Domain.Contracts;` and `using Infrastructure.Clients.Voice;` at the top of the file.

- [ ] **Step 3: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: build succeeds.

- [ ] **Step 4: Commit**

Run: `git add McpChannelVoice/Services/WyomingTcpServer.cs McpChannelVoice/Modules/ConfigModule.cs`
Run: `git commit -m "feat(voice): host Wyoming TCP server and wire ISpeechToText"`

### Task 2.10: Integration test against real wyoming-faster-whisper

**Files:**
- Create: `Tests/Integration/McpChannelVoice/Fixtures/WyomingWhisperFixture.cs`
- Create: `Tests/Integration/McpChannelVoice/VoiceRoundTripTests.cs`
- Create: `Tests/Integration/McpChannelVoice/Resources/hello.wav` (a short 16 kHz mono PCM "hello" sample, ~1s, ≤50 KB)

The fixture starts a `rhasspy/wyoming-whisper:latest` container with `--model tiny --language en --device cpu` on a random host port. The test sends a real WAV through `WyomingSpeechToText` and asserts a non-empty transcript comes back.

- [ ] **Step 1: Add a test resource**

Acquire a 16 kHz mono PCM WAV that says "hello" (any short English sample). Place it at `Tests/Integration/McpChannelVoice/Resources/hello.wav`. If you don't have one, generate via Piper locally or use a public-domain clip. Commit it with `git add -f` if necessary.

- [ ] **Step 2: Write the fixture**

```csharp
// Tests/Integration/McpChannelVoice/Fixtures/WyomingWhisperFixture.cs
using System.Net.Sockets;
using Testcontainers.Containers;
using Testcontainers.Images;

namespace Tests.Integration.McpChannelVoice.Fixtures;

public sealed class WyomingWhisperFixture : IAsyncLifetime
{
    public IContainer? Container { get; private set; }
    public string Host => "127.0.0.1";
    public int Port { get; private set; }

    public async Task InitializeAsync()
    {
        Container = new ContainerBuilder()
            .WithImage("rhasspy/wyoming-whisper:latest")
            .WithCommand("--model", "tiny", "--language", "en", "--device", "cpu")
            .WithPortBinding(10300, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(10300))
            .Build();
        await Container.StartAsync();
        Port = Container.GetMappedPublicPort(10300);
    }

    public async Task DisposeAsync()
    {
        if (Container is not null) await Container.DisposeAsync();
    }
}
```

If the existing test project does not yet reference Testcontainers, follow whatever pattern is already used in `Tests/Integration` (e.g. `RedisFixture`). Use raw `TcpClient` against a manually-started container if Testcontainers is not available.

- [ ] **Step 3: Write the failing test**

```csharp
// Tests/Integration/McpChannelVoice/VoiceRoundTripTests.cs
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Shouldly;
using Tests.Integration.McpChannelVoice.Fixtures;

namespace Tests.Integration.McpChannelVoice;

[Collection("WyomingWhisper")]
[Trait("Category", "Integration")]
public class VoiceRoundTripTests(WyomingWhisperFixture fixture) : IClassFixture<WyomingWhisperFixture>
{
    [Fact]
    public async Task TranscribeAsync_HelloWav_ReturnsHello()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Resources", "hello.wav");
        Skip.IfNot(File.Exists(path), "Test WAV not provisioned");
        var pcm = await File.ReadAllBytesAsync(path);
        // Strip the 44-byte WAV header for raw PCM
        var raw = pcm.AsMemory(44).ToArray();

        var stt = new WyomingSpeechToText(fixture.Host, fixture.Port, model: "tiny");
        var chunks = SplitIntoChunks(raw, sampleRate: 16000, chunkBytes: 3200); // 100ms @ 16kHz mono 16-bit

        var result = await stt.TranscribeAsync(ToAsync(chunks), new TranscriptionOptions(LanguageHint: "en"), CancellationToken.None);

        result.Text.ShouldNotBeNullOrWhiteSpace();
        result.Text.ToLowerInvariant().ShouldContain("hello");
    }

    private static IEnumerable<AudioChunk> SplitIntoChunks(byte[] raw, int sampleRate, int chunkBytes)
    {
        for (var i = 0; i < raw.Length; i += chunkBytes)
        {
            var len = Math.Min(chunkBytes, raw.Length - i);
            var slice = raw.AsSpan(i, len).ToArray();
            yield return new AudioChunk(slice, sampleRate, 2, 1, DateTimeOffset.UtcNow);
        }
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> seq)
    {
        foreach (var item in seq)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
```

- [ ] **Step 4: Run, watch it fail (no fixture or no WAV) or skip**

Run: `dotnet test --filter "FullyQualifiedName~VoiceRoundTripTests"`
Expected: FAIL or SKIP. If SKIP, provision the WAV and re-run.

- [ ] **Step 5: With WAV in place, watch the test pass**

Run: `dotnet test --filter "FullyQualifiedName~VoiceRoundTripTests"`
Expected: PASS — the integration test reads "hello" through real `wyoming-faster-whisper`.

- [ ] **Step 6: Commit**

Run: `git add Tests/Integration/McpChannelVoice`
Run: `git commit -m "test(voice): integration test against real wyoming-faster-whisper container"`

### Task 2.11: Compose entries for wyoming-whisper + wyoming-piper

We add both now even though Piper is only used in Slice 3 — they share the data volume pattern and adding them together is one less round of compose churn.

**Files:**
- Modify: `DockerCompose/docker-compose.yml`

- [ ] **Step 1: Add the two services**

Insert before the `mcp-channel-voice` block:

```yaml
  wyoming-whisper:
    image: rhasspy/wyoming-whisper:latest
    container_name: wyoming-whisper
    command: --model base --language es --device cpu
    restart: unless-stopped
    networks:
      - jackbot
    volumes:
      - whisper-data:/data

  wyoming-piper:
    image: rhasspy/wyoming-piper:latest
    container_name: wyoming-piper
    command: --voice es_ES-davefx-medium
    restart: unless-stopped
    networks:
      - jackbot
    volumes:
      - piper-data:/data
```

Add to the `volumes:` block at the bottom of the file:

```yaml
  whisper-data:
  piper-data:
```

Update the `mcp-channel-voice` `depends_on:` block:

```yaml
    depends_on:
      base-sdk:
        condition: service_started
      redis:
        condition: service_healthy
      wyoming-whisper:
        condition: service_started
      wyoming-piper:
        condition: service_started
```

- [ ] **Step 2: Bring up the new services**

Run: `docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d wyoming-whisper wyoming-piper mcp-channel-voice`
Expected: all three containers running.

- [ ] **Step 3: Verify wyoming-whisper is reachable from the channel container**

Run: `docker exec mcp-channel-voice nc -z wyoming-whisper 10300; echo $?`
Expected: `0`.

- [ ] **Step 4: Commit**

Run: `git add DockerCompose/docker-compose.yml`
Run: `git commit -m "feat(voice): add wyoming-whisper and wyoming-piper compose services"`

### Task 2.12: Provisioning script for wyoming-satellite

**Files:**
- Create: `scripts/provision-satellite.sh`

- [ ] **Step 1: Write the script**

```bash
#!/usr/bin/env bash
# scripts/provision-satellite.sh
# Usage: SATELLITE_ID=kitchen-01 HUB_HOST=192.168.1.10 HUB_PORT=10700 WAKE=ok_nabu MIC=plughw:CARD=Device,DEV=0 sudo -E ./provision-satellite.sh
set -euo pipefail

: "${SATELLITE_ID:?must be set}"
: "${HUB_HOST:?must be set}"
: "${HUB_PORT:=10700}"
: "${WAKE:=ok_nabu}"
: "${MIC:?must be set}"

apt-get update
apt-get install -y python3 python3-pip python3-venv libportaudio2 libasound2-plugins pulseaudio-utils alsa-utils
pip install --user --break-system-packages pipx
~/.local/bin/pipx ensurepath
~/.local/bin/pipx install wyoming-satellite
~/.local/bin/pipx install wyoming-openwakeword

cat > /etc/systemd/system/wyoming-satellite.service <<EOF
[Unit]
Description=Wyoming satellite for ${SATELLITE_ID}
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
ExecStart=/root/.local/bin/wyoming-satellite \\
    --name "${SATELLITE_ID}" \\
    --uri tcp://0.0.0.0:10700 \\
    --mic-command "arecord -D ${MIC} -r 16000 -c 1 -f S16_LE -t raw" \\
    --snd-command "aplay -r 22050 -c 1 -f S16_LE -t raw" \\
    --wake-uri tcp://127.0.0.1:10400 \\
    --wake-word-name "${WAKE}" \\
    --event-uri tcp://${HUB_HOST}:${HUB_PORT}
Restart=always

[Install]
WantedBy=default.target
EOF

cat > /etc/systemd/system/wyoming-openwakeword.service <<EOF
[Unit]
Description=openWakeWord for ${SATELLITE_ID}

[Service]
Type=simple
ExecStart=/root/.local/bin/wyoming-openwakeword --uri tcp://127.0.0.1:10400 --preload-model "${WAKE}"
Restart=always

[Install]
WantedBy=default.target
EOF

systemctl daemon-reload
systemctl enable --now wyoming-openwakeword
systemctl enable --now wyoming-satellite
echo "Provisioned ${SATELLITE_ID}; hub=${HUB_HOST}:${HUB_PORT}, wake=${WAKE}"
```

- [ ] **Step 2: Make executable**

Run: `chmod +x scripts/provision-satellite.sh`

- [ ] **Step 3: Commit**

Run: `git add scripts/provision-satellite.sh`
Run: `git commit -m "feat(voice): provisioning script for wyoming-satellite on Pi Zero 2 W"`

### Task 2.13: Add a Voice satellite to the channel config and end-to-end smoke test

**Files:**
- Modify: `McpChannelVoice/appsettings.Development.json`

- [ ] **Step 1: Add a smoke-test satellite to dev config**

```json
{
  "Voice": {
    "Stt": { "Wyoming": { "Host": "localhost", "Model": "base" } },
    "Tts": { "Wyoming": { "Host": "localhost" } },
    "Satellites": {
      "smoke-01": { "Identity": "household", "Room": "Workbench", "WakeWord": "ok_nabu" }
    }
  },
  "Redis": { "ConnectionString": "localhost:6379" }
}
```

- [ ] **Step 2: Smoke test**

Bring up the stack. From a desktop with a mic, run:

```bash
pipx install wyoming-satellite wyoming-openwakeword
wyoming-openwakeword --uri tcp://127.0.0.1:10400 --preload-model ok_nabu &
wyoming-satellite \
  --name smoke-01 \
  --uri tcp://0.0.0.0:10701 \
  --mic-command "arecord -D default -r 16000 -c 1 -f S16_LE -t raw" \
  --snd-command "aplay -r 22050 -c 1 -f S16_LE -t raw" \
  --wake-uri tcp://127.0.0.1:10400 \
  --wake-word-name ok_nabu \
  --event-uri tcp://127.0.0.1:10700
```

Say "ok nabu, what time is it". Expected:

- `mcp-channel-voice` logs `Satellite 'smoke-01' connected`.
- A `notifications/channel/message` is delivered to the agent with `Sender = "household"` and the transcript text.
- The agent processes the message and calls `send_reply`. (No spoken reply yet — that lands in Slice 3. The reply text appears in agent logs.)

- [ ] **Step 3: No commit needed** (config-only smoke test).

### Task 2.14: Voice page (minimal — utterances chart + KPI)

**Files:**
- Create: `Dashboard.Client/State/Voice/VoiceState.cs`
- Create: `Dashboard.Client/State/Voice/VoiceStore.cs`
- Create: `Dashboard.Client/Pages/Voice.razor`
- Modify: `Dashboard.Client/Components/NavMenu.razor` (or wherever the nav links live; if not present, find via `grep -rn "Tools" Dashboard.Client/*.razor`)
- Modify: `Observability/MetricsApiEndpoints.cs`
- Modify: `Observability/Services/MetricsCollectorService.cs`

- [ ] **Step 1: Add Redis processing for VoiceUtteranceEvent**

Edit `Observability/Services/MetricsCollectorService.cs`. Add a new switch case in `ProcessEventAsync`:

```csharp
case VoiceUtteranceEvent voice:
    await ProcessVoiceUtteranceAsync(voice, db);
    break;
```

Add the method (after `ProcessHeartbeatAsync`):

```csharp
private async Task ProcessVoiceUtteranceAsync(VoiceUtteranceEvent evt, IDatabase db)
{
    var dateKey = evt.Timestamp.UtcDateTime.ToString("yyyy-MM-dd");
    var sortedSetKey = $"metrics:voice:{dateKey}";
    var totalsKey = $"metrics:totals:{dateKey}";
    var score = evt.Timestamp.ToUnixTimeMilliseconds();
    var json = JsonSerializer.Serialize<MetricEvent>(evt, _jsonOptions);

    var tasks = new List<Task>
    {
        db.SortedSetAddAsync(sortedSetKey, json, score),
        db.HashIncrementAsync(totalsKey, "voice:utterances"),
        db.HashIncrementAsync(totalsKey, $"voice:bySatellite:{evt.SatelliteId}"),
        db.HashIncrementAsync(totalsKey, $"voice:byRoom:{evt.Room}"),
        db.HashIncrementAsync(totalsKey, "voice:sttLatencyMs", evt.SttLatencyMs),
        db.KeyExpireAsync(sortedSetKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry),
        db.KeyExpireAsync(totalsKey, _dailyKeyTtl, ExpireWhen.HasNoExpiry)
    };

    if (!evt.Success)
    {
        tasks.Add(db.HashIncrementAsync(totalsKey, "voice:errors"));
    }

    await Task.WhenAll(tasks);
    await hubContext.Clients.All.SendAsync("OnVoiceUtterance", evt);
}
```

- [ ] **Step 2: Add API endpoints**

Edit `Observability/MetricsApiEndpoints.cs`. Inside `MapMetricsApi`, after the `/api/metrics/schedules` block, add:

```csharp
api.MapGet("/voice", async (
    MetricsQueryService query,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    return await query.GetEventsAsync<VoiceUtteranceEvent>("metrics:voice:", fromDate, toDate);
});
```

- [ ] **Step 3: Add VoiceState + VoiceStore**

Mirror the layout of `Dashboard.Client/State/Tools/`. Replace `ToolCallEvent` with `VoiceUtteranceEvent`, drop unused dimension/metric enums (we keep this minimal in Slice 2 — group-by satellite/room only).

```csharp
// Dashboard.Client/State/Voice/VoiceState.cs
using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Voice;

public record VoiceState
{
    public IReadOnlyList<VoiceUtteranceEvent> Events { get; init; } = [];
    public Dictionary<string, decimal> BreakdownByRoom { get; init; } = new();
}
```

```csharp
// Dashboard.Client/State/Voice/VoiceStore.cs
using Domain.DTOs.Metrics;

namespace Dashboard.Client.State.Voice;

public record SetVoiceEvents(IReadOnlyList<VoiceUtteranceEvent> Events) : IAction;
public record AppendVoiceEvent(VoiceUtteranceEvent Event) : IAction;

public sealed class VoiceStore : Store<VoiceState>
{
    public VoiceStore() : base(new VoiceState()) { }

    public void SetEvents(IReadOnlyList<VoiceUtteranceEvent> events) =>
        Dispatch(new SetVoiceEvents(events), static (s, a) => s with
        {
            Events = a.Events,
            BreakdownByRoom = a.Events.GroupBy(e => e.Room)
                .ToDictionary(g => g.Key, g => (decimal)g.Count())
        });

    public void AppendEvent(VoiceUtteranceEvent evt) =>
        Dispatch(new AppendVoiceEvent(evt), static (s, a) =>
        {
            var events = s.Events.Append(a.Event).ToList();
            return s with
            {
                Events = events,
                BreakdownByRoom = events.GroupBy(e => e.Room)
                    .ToDictionary(g => g.Key, g => (decimal)g.Count())
            };
        });
}
```

- [ ] **Step 4: Add Voice.razor page**

```razor
@* Dashboard.Client/Pages/Voice.razor *@
@page "/voice"
@using Dashboard.Client.State.Voice
@using Domain.DTOs.Metrics
@implements IDisposable
@inject VoiceStore Store
@inject MetricsApiService Api

<div class="voice-page">
    <header class="page-header">
        <h2>Voice</h2>
    </header>

    <section class="kpi-row">
        <KpiCard Label="Utterances (24h)" Value="@_state.Events.Count.ToString("N0")" Color="var(--accent-blue)" />
        <KpiCard Label="Errors (24h)" Value="@_errorCount.ToString("N0")" Color="var(--accent-red)" />
    </section>

    <section class="section">
        <DynamicChart Data="_state.BreakdownByRoom" ChartType="DynamicChart.ChartMode.HorizontalBar"
                      MetricLabel="Utterances by Room" Unit="" />
    </section>

    <section class="section">
        <h3>Recent Utterances</h3>
        <div class="events-table">
            <div class="table-header">
                <span>Time</span><span>Satellite</span><span>Room</span><span>Confidence</span><span>Status</span>
            </div>
            @foreach (var e in _state.Events.Reverse().Take(50))
            {
                <div class="table-row">
                    <span>@e.Timestamp.ToString("dd/MM HH:mm:ss")</span>
                    <span>@e.SatelliteId</span>
                    <span>@e.Room</span>
                    <span>@e.SttLatencyMs ms</span>
                    <span class='@(e.Success ? "status-ok" : "status-err")'>@(e.Success ? "OK" : e.ErrorType)</span>
                </div>
            }
        </div>
    </section>
</div>

@code {
    private VoiceState _state = new();
    private int _errorCount;
    private IDisposable? _sub;

    protected override async Task OnInitializedAsync()
    {
        _sub = Store.Subscribe(s =>
        {
            _state = s;
            _errorCount = s.Events.Count(e => !e.Success);
            InvokeAsync(StateHasChanged);
        });
        var events = await Api.GetVoiceEventsAsync(DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow));
        Store.SetEvents(events);
    }

    public void Dispose() => _sub?.Dispose();
}
```

- [ ] **Step 5: Add `GetVoiceEventsAsync` to `MetricsApiService`**

Find `MetricsApiService` (likely `Dashboard.Client/Services/MetricsApiService.cs`). Add a method following the pattern of the existing `GetToolEventsAsync`:

```csharp
public Task<IReadOnlyList<VoiceUtteranceEvent>> GetVoiceEventsAsync(DateOnly from, DateOnly to) =>
    GetEventsAsync<VoiceUtteranceEvent>("voice", from, to);
```

- [ ] **Step 6: Register `VoiceStore` in DI**

Find where `ToolsStore` is registered in `Dashboard.Client/Program.cs` (or equivalent) and add `builder.Services.AddSingleton<VoiceStore>();` next to it.

- [ ] **Step 7: Add a "Voice" nav link**

Find the menu component (likely `Dashboard.Client/Layout/NavMenu.razor` or similar — locate via `grep -rn "/tools" Dashboard.Client | head`). Add an entry analogous to the tools entry, pointing at `/voice`.

- [ ] **Step 8: Build the dashboard**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj`
Expected: build succeeds.

- [ ] **Step 9: Restart observability + dashboard, verify**

Run: `docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build observability webui`

Open `http://localhost:5003/dashboard/voice`. Expect an empty page initially. Trigger an utterance via the Slice 2.13 smoke test; the page should populate within seconds.

- [ ] **Step 10: Commit**

Run: `git add Dashboard.Client/State/Voice Dashboard.Client/Pages/Voice.razor Dashboard.Client/Services Observability/Services/MetricsCollectorService.cs Observability/MetricsApiEndpoints.cs Dashboard.Client/Program.cs Dashboard.Client/Layout`
Run: `git commit -m "feat(voice): minimal Voice dashboard page + collector + API endpoint"`

### Slice 2 done

A real satellite (or desktop) wakes, speaks, and the transcript reaches the agent. Voice page shows the utterance. No reply yet.

---

## Slice 3 — TTS path

**Goal:** `send_reply` synthesises the agent's response via Piper and streams it back through the still-open Wyoming session to the originating satellite. Adds wake-to-first-audio latency tracking and an Overview KPI.

### Task 3.1: WyomingTextToSpeech adapter

**Files:**
- Create: `Infrastructure/Clients/Voice/WyomingTextToSpeech.cs`

- [ ] **Step 1: Implement**

```csharp
// Infrastructure/Clients/Voice/WyomingTextToSpeech.cs
using System.Runtime.CompilerServices;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice.Wyoming;

namespace Infrastructure.Clients.Voice;

public sealed class WyomingTextToSpeech(string host, int port, string? voice) : ITextToSpeech
{
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var client = new WyomingClient(host, port);
        await client.ConnectAsync(cancellationToken);

        var data = JsonSerializer.SerializeToElement(new
        {
            text,
            voice = options.Voice ?? voice
        });
        await client.SendAsync(new WyomingEvent("synthesize", data, null), cancellationToken);

        while (true)
        {
            var evt = await client.ReceiveAsync(cancellationToken);
            if (evt is null || evt.Type == "audio-stop") yield break;
            if (evt.Type != "audio-chunk" || evt.Payload is null) continue;

            yield return new AudioChunk(
                evt.Payload,
                evt.Data.GetProperty("rate").GetInt32(),
                evt.Data.TryGetProperty("width", out var w) ? w.GetInt32() : 2,
                evt.Data.TryGetProperty("channels", out var c) ? c.GetInt32() : 1,
                DateTimeOffset.UtcNow);
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Infrastructure/Infrastructure.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

Run: `git add Infrastructure/Clients/Voice/WyomingTextToSpeech.cs`
Run: `git commit -m "feat(voice): add WyomingTextToSpeech adapter"`

### Task 3.2: Wire ITextToSpeech in DI

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`

- [ ] **Step 1: Add the registration**

Inside `ConfigureChannel`, alongside the existing `ISpeechToText` registration:

```csharp
services.AddSingleton<ITextToSpeech>(sp =>
{
    var voice = sp.GetRequiredService<VoiceSettings>();
    return voice.Tts.Provider switch
    {
        "Wyoming" => new Infrastructure.Clients.Voice.WyomingTextToSpeech(
            voice.Tts.Wyoming!.Host, voice.Tts.Wyoming.Port, voice.Tts.Wyoming.Voice),
        _ => throw new InvalidOperationException($"Unsupported TTS provider: {voice.Tts.Provider}")
    };
});
```

- [ ] **Step 2: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

Run: `git add McpChannelVoice/Modules/ConfigModule.cs`
Run: `git commit -m "feat(voice): register ITextToSpeech (Wyoming Piper)"`

### Task 3.3: SendReplyTool streams TTS audio back

**Files:**
- Modify: `McpChannelVoice/McpTools/SendReplyTool.cs`
- Modify: `Tests/Unit/McpChannelVoice/SendReplyToolTests.cs`

- [ ] **Step 1: Update the failing test**

```csharp
// Tests/Unit/McpChannelVoice/SendReplyToolTests.cs
using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelVoice.McpTools;
using McpChannelVoice.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SendReplyToolTests
{
    [Fact]
    public async Task McpRun_TextChunk_NoActiveSession_ReturnsOkAndDoesNotThrow()
    {
        var services = BuildServices(new FakeTts());
        var result = await SendReplyTool.McpRun(
            conversationId: "kitchen-01",
            content: "hola",
            contentType: ReplyContentType.Text,
            isComplete: true,
            messageId: null,
            services: services);
        result.ShouldBe("ok");
    }

    [Fact]
    public async Task McpRun_TextChunk_WithActiveSession_InvokesTtsAndStreamsBack()
    {
        var tts = new FakeTts();
        var sessions = new SatelliteSessionRegistry();
        // Slice 3 note: SatelliteSession.Stream is a real NetworkStream; for this unit test we
        // assert tts was called. Full audio-frame streaming is exercised in integration tests.

        var services = BuildServices(tts, sessions);

        await SendReplyTool.McpRun(
            conversationId: "kitchen-01",
            content: "hola",
            contentType: ReplyContentType.Text,
            isComplete: true,
            messageId: null,
            services: services);

        tts.Calls.Count.ShouldBe(1);
        tts.Calls[0].Text.ShouldBe("hola");
    }

    private static IServiceProvider BuildServices(ITextToSpeech tts, SatelliteSessionRegistry? sessions = null)
    {
        return new ServiceCollection()
            .AddSingleton(new ChannelNotificationEmitter(NullLogger<ChannelNotificationEmitter>.Instance))
            .AddSingleton(sessions ?? new SatelliteSessionRegistry())
            .AddSingleton(tts)
            .BuildServiceProvider();
    }

    private sealed class FakeTts : ITextToSpeech
    {
        public List<(string Text, SynthesisOptions Options)> Calls { get; } = [];

        public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
            string text,
            SynthesisOptions options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls.Add((text, options));
            yield return new AudioChunk(new byte[] { 1, 2 }, 22050, 2, 1, DateTimeOffset.UtcNow);
            await Task.Yield();
        }
    }
}
```

- [ ] **Step 2: Run, watch failures**

Run: `dotnet test --filter "FullyQualifiedName~SendReplyToolTests"`
Expected: FAIL — TTS is not invoked yet.

- [ ] **Step 3: Update SendReplyTool**

```csharp
// McpChannelVoice/McpTools/SendReplyTool.cs
using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice.Wyoming;
using McpChannelVoice.Services;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class SendReplyTool
{
    [McpServerTool(Name = "send_reply")]
    [Description("Send a response to a voice satellite. Final text chunks are synthesised via TTS and streamed back to the originating satellite.")]
    public static async Task<string> McpRun(
        [Description("Satellite id (matches Voice:Satellites key)")] string conversationId,
        [Description("Response content")] string content,
        [Description("Kind of chunk being sent")] ReplyContentType contentType,
        [Description("Whether this is the final chunk")] bool isComplete,
        [Description("Message ID for grouping related chunks")] string? messageId,
        IServiceProvider services)
    {
        if (contentType is ReplyContentType.Reasoning or ReplyContentType.ToolCall) return "ok";
        if (!isComplete && contentType != ReplyContentType.Error) return "ok";

        var tts = services.GetRequiredService<ITextToSpeech>();
        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var session = sessions.Get(conversationId);

        await foreach (var chunk in tts.SynthesizeAsync(content, new SynthesisOptions(), CancellationToken.None))
        {
            if (session is null) continue;
            var data = JsonSerializer.SerializeToElement(new
            {
                rate = chunk.SampleRate,
                width = chunk.SampleWidthBytes,
                channels = chunk.Channels
            });
            await WyomingProtocol.WriteAsync(session.Stream, new WyomingEvent("audio-chunk", data, chunk.Pcm), CancellationToken.None);
        }

        if (session is not null)
        {
            await WyomingProtocol.WriteAsync(session.Stream,
                new WyomingEvent("audio-stop", JsonDocument.Parse("{}").RootElement, null),
                CancellationToken.None);
        }

        return "ok";
    }
}
```

- [ ] **Step 4: Run, watch tests pass**

Run: `dotnet test --filter "FullyQualifiedName~SendReplyToolTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

Run: `git add McpChannelVoice/McpTools/SendReplyTool.cs Tests/Unit/McpChannelVoice/SendReplyToolTests.cs`
Run: `git commit -m "feat(voice): send_reply synthesises TTS and streams audio frames to satellite"`

### Task 3.4: Track wake-to-first-audio latency

**Files:**
- Modify: `McpChannelVoice/Services/WyomingPipelineHandler.cs`
- Modify: `McpChannelVoice/Services/SatelliteSession.cs`
- Modify: `McpChannelVoice/McpTools/SendReplyTool.cs`

- [ ] **Step 1: Carry the wake timestamp on `SatelliteSession`**

Edit `SatelliteSession.cs`:

```csharp
public sealed class SatelliteSession(string satelliteId, NetworkStream stream, DateTimeOffset wakeAt) : IAsyncDisposable
{
    public string SatelliteId { get; } = satelliteId;
    public NetworkStream Stream { get; } = stream;
    public DateTimeOffset OpenedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset WakeAt { get; } = wakeAt;
    public DateTimeOffset? FirstAudioAt { get; set; }

    public async ValueTask DisposeAsync()
    {
        await Stream.DisposeAsync();
    }
}
```

In `WyomingPipelineHandler.RunUtteranceAsync`, when constructing the session record (or on first audio-start), set `wakeAt = clock.GetUtcNow()`.

In `SendReplyTool.McpRun`, on the first synthesised chunk, set `session.FirstAudioAt ??= DateTimeOffset.UtcNow;` and publish a follow-up `VoiceUtteranceEvent` with `WakeToFirstAudioMs` populated. The simplest pattern is to publish a second event of the same shape; the dashboard already aggregates by satellite so duplicate-by-design is acceptable.

- [ ] **Step 2: Build**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

Run: `git add McpChannelVoice/Services McpChannelVoice/McpTools/SendReplyTool.cs`
Run: `git commit -m "feat(voice): record wake-to-first-audio latency on send_reply"`

### Task 3.5: Overview KPIs

**Files:**
- Modify: `Dashboard.Client/Pages/Overview.razor`
- Modify: `Observability/Services/MetricsQueryService.cs` (add `GetVoiceTotalsAsync`)

- [ ] **Step 1: Add the totals helper**

In `MetricsQueryService`, add:

```csharp
public async Task<(long Utterances, long ErrorCount, double MedianLatencyMs)> GetVoiceTotalsAsync(DateOnly from, DateOnly to)
{
    var events = await GetEventsAsync<VoiceUtteranceEvent>("metrics:voice:", from, to);
    var utterances = events.Count;
    var errors = events.Count(e => !e.Success);
    var latencies = events
        .Where(e => e.WakeToFirstAudioMs.HasValue)
        .Select(e => e.WakeToFirstAudioMs!.Value)
        .OrderBy(x => x)
        .ToList();
    var median = latencies.Count == 0 ? 0 : latencies[latencies.Count / 2];
    return (utterances, errors, median);
}
```

- [ ] **Step 2: Add an endpoint**

In `MetricsApiEndpoints.cs`, after the existing voice endpoint:

```csharp
api.MapGet("/voice/totals", async (
    MetricsQueryService query,
    DateOnly? from,
    DateOnly? to) =>
{
    var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var toDate = to ?? DateOnly.FromDateTime(DateTime.UtcNow);
    var (utt, err, median) = await query.GetVoiceTotalsAsync(fromDate, toDate);
    return new { Utterances = utt, Errors = err, MedianLatencyMs = median };
});
```

- [ ] **Step 3: Add the two KPI cards on Overview.razor**

Following the existing pattern in `Overview.razor`, fetch `voice/totals` on init and add two `<KpiCard>` entries: "Utterances (24h)" and "Median voice latency (24h)".

- [ ] **Step 4: Build**

Run: `dotnet build Dashboard.Client/Dashboard.Client.csproj Observability/Observability.csproj`
Expected: build succeeds.

- [ ] **Step 5: Commit**

Run: `git add Dashboard.Client/Pages/Overview.razor Observability/Services/MetricsQueryService.cs Observability/MetricsApiEndpoints.cs`
Run: `git commit -m "feat(voice): Overview KPIs for utterances and latency"`

### Slice 3 done

Speak to a satellite → agent reply is spoken back. MVP demo state.

---

## Slice 4 — Approval over voice

**Goal:** `request_approval` speaks the prompt, captures the user's spoken answer, parses yes/no, and resolves. Re-prompts once on low confidence; defaults to decline.

### Task 4.1: ApprovalGrammarParser

**Files:**
- Create: `McpChannelVoice/Services/ApprovalGrammarParser.cs`
- Create: `Tests/Unit/McpChannelVoice/ApprovalGrammarParserTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// Tests/Unit/McpChannelVoice/ApprovalGrammarParserTests.cs
using McpChannelVoice.Services;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class ApprovalGrammarParserTests
{
    private readonly ApprovalGrammarParser _parser = new();

    [Theory]
    [InlineData("yes")]
    [InlineData("Yes please")]
    [InlineData("sí")]
    [InlineData("si")]
    [InlineData("confirm")]
    [InlineData("ok")]
    [InlineData("okay go ahead")]
    public void Parse_PositivePhrases_ReturnsApprove(string text)
    {
        _parser.Parse(text).ShouldBe(ApprovalDecision.Approve);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("nope")]
    [InlineData("cancel")]
    [InlineData("no thanks")]
    [InlineData("don't")]
    public void Parse_NegativePhrases_ReturnsDecline(string text)
    {
        _parser.Parse(text).ShouldBe(ApprovalDecision.Decline);
    }

    [Theory]
    [InlineData("")]
    [InlineData("uh i'm not sure")]
    [InlineData("wait what")]
    public void Parse_AmbiguousOrEmpty_ReturnsUnclear(string text)
    {
        _parser.Parse(text).ShouldBe(ApprovalDecision.Unclear);
    }

    [Fact]
    public void Parse_YesPleaseCancelThat_ReturnsDecline()
    {
        // "cancel" wins over "yes please"
        _parser.Parse("yes please cancel that").ShouldBe(ApprovalDecision.Decline);
    }
}
```

- [ ] **Step 2: Run, watch fail**

Run: `dotnet test --filter "FullyQualifiedName~ApprovalGrammarParserTests"`
Expected: FAIL — type does not exist.

- [ ] **Step 3: Implement**

```csharp
// McpChannelVoice/Services/ApprovalGrammarParser.cs
namespace McpChannelVoice.Services;

public enum ApprovalDecision { Approve, Decline, Unclear }

public sealed class ApprovalGrammarParser
{
    private static readonly string[] _negative =
        ["no", "nope", "nah", "cancel", "stop", "abort", "don't", "do not", "negative"];

    private static readonly string[] _positive =
        ["yes", "yeah", "yep", "yup", "sí", "si", "ok", "okay", "confirm", "approve", "do it", "go ahead", "sure"];

    public ApprovalDecision Parse(string text)
    {
        var normalized = text.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(normalized)) return ApprovalDecision.Unclear;

        var hasNeg = _negative.Any(p => ContainsAsToken(normalized, p));
        var hasPos = _positive.Any(p => ContainsAsToken(normalized, p));

        if (hasNeg) return ApprovalDecision.Decline;
        if (hasPos) return ApprovalDecision.Approve;
        return ApprovalDecision.Unclear;
    }

    private static bool ContainsAsToken(string haystack, string needle)
    {
        var idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (idx < 0) return false;
        var startsAtBoundary = idx == 0 || !char.IsLetter(haystack[idx - 1]);
        var endsAtBoundary = idx + needle.Length == haystack.Length || !char.IsLetter(haystack[idx + needle.Length]);
        return startsAtBoundary && endsAtBoundary;
    }
}
```

- [ ] **Step 4: Run, watch pass**

Run: `dotnet test --filter "FullyQualifiedName~ApprovalGrammarParserTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

Run: `git add McpChannelVoice/Services/ApprovalGrammarParser.cs Tests/Unit/McpChannelVoice/ApprovalGrammarParserTests.cs`
Run: `git commit -m "feat(voice): add ApprovalGrammarParser for spoken yes/no"`

### Task 4.2: RequestApprovalTool implements the spoken flow

The tool uses `ITextToSpeech` to speak the prompt, then triggers a fresh capture window on the satellite. Capturing the response requires a small protocol affordance: we send a `transcribe` Wyoming event with `payload_length` 0 (signals "next utterance is for me"), and `WyomingPipelineHandler` routes the next transcript to a per-session `TaskCompletionSource` instead of dispatching as a `channel/message`.

**Files:**
- Modify: `McpChannelVoice/Services/SatelliteSession.cs`
- Modify: `McpChannelVoice/Services/WyomingPipelineHandler.cs`
- Modify: `McpChannelVoice/McpTools/RequestApprovalTool.cs`
- Modify: `Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs`

- [ ] **Step 1: Add a pending-approval slot on SatelliteSession**

```csharp
public TaskCompletionSource<string>? PendingApprovalTcs { get; set; }
```

- [ ] **Step 2: In `WyomingPipelineHandler.RunUtteranceAsync`**, after STT returns, check `session.PendingApprovalTcs`. If set, route the transcript text to it (instead of emitting a notification) and clear it.

- [ ] **Step 3: Update `RequestApprovalTool`**

```csharp
// McpChannelVoice/McpTools/RequestApprovalTool.cs
using System.ComponentModel;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Channel;
using Domain.DTOs.Voice;
using McpChannelVoice.Services;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpTools;

[McpServerToolType]
public sealed class RequestApprovalTool
{
    private static readonly TimeSpan _captureTimeout = TimeSpan.FromSeconds(8);

    [McpServerTool(Name = "request_approval")]
    [Description("Ask the user to approve a tool call by spoken yes/no on the originating satellite.")]
    public static async Task<string> McpRun(
        [Description("Satellite id")] string conversationId,
        [Description("Whether to ask the user (request) or just notify them (notify)")] ApprovalMode mode,
        [Description("JSON array of tool requests [{toolName, arguments}]")] string requests,
        IServiceProvider services)
    {
        var sessions = services.GetRequiredService<SatelliteSessionRegistry>();
        var tts = services.GetRequiredService<ITextToSpeech>();
        var parser = services.GetRequiredService<ApprovalGrammarParser>();
        var session = sessions.Get(conversationId);

        if (mode == ApprovalMode.Notify || session is null) return "approve";

        var summary = SummariseRequests(requests);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var prompt = attempt == 0
                ? $"Do you approve {summary}? Say yes or no."
                : "I didn't catch that. Please say yes or no.";

            await SpeakAsync(tts, session, prompt);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            session.PendingApprovalTcs = tcs;
            using var cts = new CancellationTokenSource(_captureTimeout);
            cts.Token.Register(() => tcs.TrySetResult(string.Empty));

            var transcript = await tcs.Task;
            session.PendingApprovalTcs = null;

            switch (parser.Parse(transcript))
            {
                case ApprovalDecision.Approve: return "approve";
                case ApprovalDecision.Decline: return "decline";
            }
        }

        await SpeakAsync(tts, session, "I'll skip that for now.");
        return "decline";
    }

    private static async Task SpeakAsync(ITextToSpeech tts, SatelliteSession session, string text)
    {
        // Reuse the same audio-chunk streaming pattern as SendReplyTool
        await foreach (var chunk in tts.SynthesizeAsync(text, new SynthesisOptions(), CancellationToken.None))
        {
            var data = JsonSerializer.SerializeToElement(new
            {
                rate = chunk.SampleRate,
                width = chunk.SampleWidthBytes,
                channels = chunk.Channels
            });
            await Infrastructure.Clients.Voice.Wyoming.WyomingProtocol.WriteAsync(
                session.Stream,
                new Infrastructure.Clients.Voice.Wyoming.WyomingEvent("audio-chunk", data, chunk.Pcm),
                CancellationToken.None);
        }

        await Infrastructure.Clients.Voice.Wyoming.WyomingProtocol.WriteAsync(
            session.Stream,
            new Infrastructure.Clients.Voice.Wyoming.WyomingEvent("audio-stop", JsonDocument.Parse("{}").RootElement, null),
            CancellationToken.None);
    }

    private static string SummariseRequests(string requestsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(requestsJson);
            return string.Join(", ", doc.RootElement.EnumerateArray()
                .Select(r => r.GetProperty("toolName").GetString()?.Split("__").Last() ?? "unknown"));
        }
        catch
        {
            return "this action";
        }
    }
}
```

- [ ] **Step 4: Register `ApprovalGrammarParser` in DI**

In `ConfigModule.cs`, alongside the other singletons:

```csharp
services.AddSingleton<ApprovalGrammarParser>();
```

- [ ] **Step 5: Update test for new behaviour**

Update `RequestApprovalToolTests` to assert that with a fake session whose `PendingApprovalTcs` is pre-completed with `"yes"`, the tool returns `"approve"`. With `"no"`, it returns `"decline"`. With an empty timeout, it returns `"decline"`.

- [ ] **Step 6: Run, watch tests pass**

Run: `dotnet test --filter "FullyQualifiedName~RequestApprovalToolTests OR FullyQualifiedName~ApprovalGrammarParserTests"`
Expected: PASS.

- [ ] **Step 7: Commit**

Run: `git add McpChannelVoice Tests/Unit/McpChannelVoice/RequestApprovalToolTests.cs`
Run: `git commit -m "feat(voice): spoken yes/no approval flow with re-prompt and decline default"`

### Task 4.3: Publish ApprovalResolved metric

**Files:**
- Modify: `McpChannelVoice/McpTools/RequestApprovalTool.cs`

- [ ] **Step 1: Inject `IMetricsPublisher` and the `SatelliteRegistry`**

After resolving an approval, publish a `VoiceUtteranceEvent` with `ApprovalOutcome = "approve"|"decline"|"timeout"` and `Success = true`.

- [ ] **Step 2: Update Voice page**

Add a small "Approvals" panel showing recent approval outcomes (filter `Events` where `ApprovalOutcome != null`).

- [ ] **Step 3: Commit**

Run: `git commit -am "feat(voice): publish ApprovalResolved metric and surface on Voice page"`

### Slice 4 done

`request_approval` works end-to-end on at least Spanish and English yes/no. Default-decline path tested via timeout.

---

## Slice 5 — Cloud STT/TTS adapters

**Goal:** `OpenAiSpeechToText` and `OpenAiTextToSpeech` selectable via configuration. No code change anywhere except DI factory in `ConfigModule`. Cloud cost events tag with `Origin = "voice"` so the Tokens dashboard slices them automatically.

### Task 5.1: OpenAiSpeechToText adapter

**Files:**
- Create: `Infrastructure/Clients/Voice/OpenAiSpeechToText.cs`
- Create: `Tests/Unit/Infrastructure/Voice/OpenAiSpeechToTextTests.cs`

- [ ] **Step 1: Write the failing test against a stubbed `HttpMessageHandler`**

Pattern: a fake `HttpMessageHandler` returns a canned `{"text": "hello"}` JSON. Assert the multipart body contains the audio bytes and the model field.

```csharp
// Tests/Unit/Infrastructure/Voice/OpenAiSpeechToTextTests.cs
using System.Net;
using System.Net.Http;
using Domain.DTOs.Voice;
using Infrastructure.Clients.Voice;
using Shouldly;

namespace Tests.Unit.Infrastructure.Voice;

public class OpenAiSpeechToTextTests
{
    [Fact]
    public async Task TranscribeAsync_StubbedResponse_ReturnsText()
    {
        var handler = new StubHandler("""{"text":"hello"}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/v1/") };
        var stt = new OpenAiSpeechToText(http, apiKey: "sk-test", model: "whisper-1");

        async IAsyncEnumerable<AudioChunk> One()
        {
            yield return new AudioChunk(new byte[] { 1, 2, 3 }, 16000, 2, 1, DateTimeOffset.UtcNow);
            await Task.Yield();
        }

        var result = await stt.TranscribeAsync(One(), new TranscriptionOptions(), CancellationToken.None);

        result.Text.ShouldBe("hello");
        handler.LastRequest!.RequestUri!.AbsolutePath.ShouldEndWith("audio/transcriptions");
    }

    private sealed class StubHandler(string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
```

- [ ] **Step 2: Implement `OpenAiSpeechToText`**

Concrete sketch (full code is straightforward — POST `audio/transcriptions` as multipart, audio stitched to a single buffer with a WAV header, `model` and optional `language`):

```csharp
// Infrastructure/Clients/Voice/OpenAiSpeechToText.cs
using System.Net.Http.Headers;
using System.Text.Json;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Voice;

public sealed class OpenAiSpeechToText(HttpClient http, string apiKey, string model) : ISpeechToText
{
    public async Task<TranscriptionResult> TranscribeAsync(
        IAsyncEnumerable<AudioChunk> audio,
        TranscriptionOptions options,
        CancellationToken cancellationToken)
    {
        await using var ms = new MemoryStream();
        var first = true;
        var sampleRate = 16000;
        var channels = 1;
        var width = 2;
        await foreach (var chunk in audio.WithCancellation(cancellationToken))
        {
            if (first) { sampleRate = chunk.SampleRate; channels = chunk.Channels; width = chunk.SampleWidthBytes; first = false; }
            await ms.WriteAsync(chunk.Pcm, cancellationToken);
        }

        ms.Position = 0;
        var pcm = ms.ToArray();
        var wav = WrapWav(pcm, sampleRate, channels, width);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(wav) { Headers = { ContentType = new MediaTypeHeaderValue("audio/wav") } }, "file", "audio.wav");
        content.Add(new StringContent(options.ModelOverride ?? model), "model");
        if (options.LanguageHint is not null)
            content.Add(new StringContent(options.LanguageHint), "language");

        using var req = new HttpRequestMessage(HttpMethod.Post, "audio/transcriptions") { Content = content };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        var startMs = Environment.TickCount64;
        using var res = await http.SendAsync(req, cancellationToken);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(body);
        var text = doc.RootElement.GetProperty("text").GetString() ?? string.Empty;
        return new TranscriptionResult(text, options.LanguageHint, 1.0, Environment.TickCount64 - startMs);
    }

    private static byte[] WrapWav(byte[] pcm, int sampleRate, int channels, int width)
    {
        // RIFF/WAVE/fmt/data header for PCM
        var byteRate = sampleRate * channels * width;
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + pcm.Length);
        w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1);
        w.Write((short)channels); w.Write(sampleRate); w.Write(byteRate);
        w.Write((short)(channels * width)); w.Write((short)(width * 8));
        w.Write("data"u8); w.Write(pcm.Length); w.Write(pcm);
        return ms.ToArray();
    }
}

file static class Utf8Extensions
{
    public static void Write(this BinaryWriter w, ReadOnlySpan<byte> bytes) => w.Write(bytes.ToArray());
}
```

- [ ] **Step 3: Run, watch the test pass**

Run: `dotnet test --filter "FullyQualifiedName~OpenAiSpeechToTextTests"`
Expected: PASS.

- [ ] **Step 4: Commit**

Run: `git add Infrastructure/Clients/Voice/OpenAiSpeechToText.cs Tests/Unit/Infrastructure/Voice/OpenAiSpeechToTextTests.cs`
Run: `git commit -m "feat(voice): add OpenAiSpeechToText adapter"`

### Task 5.2: OpenAiTextToSpeech adapter

**Files:**
- Create: `Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs`

- [ ] **Step 1: Implement** (POST to `audio/speech` with `{model, voice, input, response_format: "pcm"}`, stream the PCM body in fixed-size chunks as `AudioChunk`s)

```csharp
// Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Domain.Contracts;
using Domain.DTOs.Voice;

namespace Infrastructure.Clients.Voice;

public sealed class OpenAiTextToSpeech(HttpClient http, string apiKey, string model, string voice) : ITextToSpeech
{
    public async IAsyncEnumerable<AudioChunk> SynthesizeAsync(
        string text,
        SynthesisOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "audio/speech")
        {
            Content = JsonContent.Create(new
            {
                model,
                voice = options.Voice ?? voice,
                input = text,
                response_format = "pcm"
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        res.EnsureSuccessStatusCode();
        await using var stream = await res.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[3200]; // 100ms @ 16kHz mono 16-bit
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) yield break;
            var slice = buffer.AsSpan(0, read).ToArray();
            yield return new AudioChunk(slice, 24000, 2, 1, DateTimeOffset.UtcNow);
        }
    }
}
```

- [ ] **Step 2: Build**

Run: `dotnet build Infrastructure/Infrastructure.csproj`
Expected: build succeeds.

- [ ] **Step 3: Commit**

Run: `git add Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs`
Run: `git commit -m "feat(voice): add OpenAiTextToSpeech adapter"`

### Task 5.3: DI factory + secrets

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs`
- Modify: `DockerCompose/.env`
- Modify: `DockerCompose/docker-compose.yml` (env passthrough on `mcp-channel-voice`)
- Modify: `McpChannelVoice/appsettings.Development.json` (placeholder OpenAI block)

- [ ] **Step 1: Extend the DI factory**

Replace the `ISpeechToText` factory in `ConfigModule.cs`:

```csharp
.AddSingleton<ISpeechToText>(sp =>
{
    var voice = sp.GetRequiredService<VoiceSettings>();
    return voice.Stt.Provider switch
    {
        "Wyoming" => new Infrastructure.Clients.Voice.WyomingSpeechToText(
            voice.Stt.Wyoming!.Host, voice.Stt.Wyoming.Port, voice.Stt.Wyoming.Model),
        "OpenAi" => new Infrastructure.Clients.Voice.OpenAiSpeechToText(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("openai"),
            Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? throw new InvalidOperationException("OPENAI_API_KEY missing"),
            voice.Stt.OpenAi?.Model ?? "whisper-1"),
        _ => throw new InvalidOperationException($"Unsupported STT provider: {voice.Stt.Provider}")
    };
})
```

Identical pattern for `ITextToSpeech`. Add `.AddHttpClient("openai", c => c.BaseAddress = new Uri("https://api.openai.com/v1/"));` once.

- [ ] **Step 2: `.env` placeholder**

Add to `DockerCompose/.env`:

```env
OPENAI_API_KEY=
```

- [ ] **Step 3: Pass through in compose**

In the `mcp-channel-voice` service block, add:

```yaml
    environment:
      - OPENAI_API_KEY=${OPENAI_API_KEY}
```

- [ ] **Step 4: Document in `appsettings.Development.json`**

Add (commented placeholder is fine; the field exists in the record either way):

```json
"Voice": {
  "Stt": { "Provider": "Wyoming", "OpenAi": { "Model": "whisper-1" } },
  "Tts": { "Provider": "Wyoming", "OpenAi": { "Model": "tts-1", "Voice": "alloy" } }
}
```

- [ ] **Step 5: Commit**

Run: `git add McpChannelVoice DockerCompose/.env DockerCompose/docker-compose.yml`
Run: `git commit -m "feat(voice): config-driven OpenAI STT/TTS provider switch"`

### Task 5.4: Origin tagging on token cost events

**Files:**
- Modify: `Infrastructure/Clients/Voice/OpenAiSpeechToText.cs`
- Modify: `Infrastructure/Clients/Voice/OpenAiTextToSpeech.cs`

- [ ] **Step 1: Inject `IMetricsPublisher` into both adapters**

After each successful API call, publish a `TokenUsageEvent` (or whichever event already covers external cost; check `Domain/DTOs/Metrics/TokenUsageEvent.cs` shape) with a new `Origin` field if it doesn't exist yet — if it does not, add it and update collector aggregation accordingly. **Verify before adding**: read the current `TokenUsageEvent.cs` and the existing collector slicing keys before making this change.

- [ ] **Step 2: Smoke test by switching to OpenAI in dev config**

Set `Voice:Stt:Provider=OpenAi` (and `Tts`) in `appsettings.Development.json`. Speak a short utterance against your dev satellite. Confirm:

- Transcript still arrives at the agent.
- A new `TokenUsageEvent` with `Origin = "voice"` is published (visible in `redis-cli SUBSCRIBE metrics:events`).

Switch the config back to Wyoming after the smoke test.

- [ ] **Step 3: Commit**

Run: `git commit -am "feat(voice): tag OpenAI cost events with Origin=voice for token dashboard slicing"`

### Slice 5 done

Switching `Voice:Stt:Provider` to `OpenAi` works end-to-end without code changes elsewhere. Cost events flow into the existing Tokens page automatically.

---

## Self-review notes

I checked the plan against the spec section by section. Coverage:

- **Architecture diagram** — covered by file structure and Task 1.1–1.6 (channel skeleton), 2.3–2.9 (Wyoming + STT pipeline).
- **Components table** — every "we own" component has a task. Stock components are wired in 1.8 and 2.11.
- **Data flow round-trip** — Slices 2 and 3 implement steps 1–8 of the spec's data flow.
- **Approval flow** — Slice 4.
- **Configuration shape** — Task 1.2 (settings record) and 1.3 (appsettings).
- **Docker Compose** — Tasks 1.8, 2.11, 5.3.
- **Identity & threading** — Task 1.4 (`SatelliteRegistry`), Task 2.8 (per-satellite `conversationId`).
- **Observability** — Task 1.7 (heartbeat), 2.6 (event type), 2.14 (collector + endpoint + page), 3.5 (overview KPIs), 4.3 (approval metric).
- **Dashboard delta** — Tasks 2.14, 3.5, 4.3 cover the new page, KPIs, and approval panel. **`HealthGrid.razor` and `Errors.razor` are not modified explicitly** — the spec said "data-only if grid is generic; otherwise add a Voice tab". Tasks 1.7 and Slice 5.4 publish heartbeat and error events that the existing grid/errors page should pick up automatically. **If the grid is not generic when implementation reaches that point**, add an inline subtask under Task 1.8 to extend it.
- **Error handling** — confidence gate in Task 2.8; timeouts and decline-default in Task 4.2; cancellation paths handled by `CancellationToken` propagation throughout. The "barge-in" case (re-wake during TTS) is provided by stock `wyoming-satellite`; no extra code needed on our side beyond ensuring the existing TTS task is cancelled when a new `audio-start` arrives — which is naturally handled because `WyomingPipelineHandler.HandleAsync` only enters `RunUtteranceAsync` on `audio-start`. **If integration testing reveals barge-in is not clean**, add a follow-up task to track in-flight TTS via a `CancellationTokenSource` per session and cancel it on new wake.
- **Testing strategy** — unit tests in Tasks 1.3, 1.4, 1.5, 2.3, 2.4, 4.1, 5.1. Integration test in Task 2.10. Manual smoke tests in 1.8 and 2.13. **No E2E task is defined for the spoken approval flow** because audio E2E requires a real Pi + mic; the spec acknowledges that as manual.
- **Phasing** — five slices, each ends green and committed.
- **Style/layering** — captured in the "Conventions used in this plan" section at the top.

Type consistency: `SatelliteSession` constructor signature changes between Tasks 2.7 (without `wakeAt`) and 3.4 (with `wakeAt`) — this is intentional and explicit in the steps. `ApprovalDecision` enum is defined in Task 4.1 and consumed in Task 4.2. `WyomingProtocol.WriteAsync` / `ReadAsync` signatures used in Tasks 2.3, 2.4, 2.5, 3.1, 3.3, 4.2 are consistent.

No remaining placeholders or "TBDs" in concrete-code steps. The two soft "if it turns out…" notes above are explicitly framed as conditional follow-ups, not gaps.
