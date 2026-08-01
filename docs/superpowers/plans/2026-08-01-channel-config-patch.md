# Per-Message Agent Config Patch Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let channels attach an optional per-message config patch (model, reasoning effort) that overrides the agent's configuration for that turn only, with a WebChat settings UI persisted in local storage.

**Architecture:** A new `AgentConfigPatch` record rides on `ChannelMessageNotification` → `ChannelMessage` → a `ChatMessage` annotation. Reasoning effort is applied in `McpAgent.CreateRunOptions`; the model override is stamped onto the OpenRouter request body in `OpenRouterHttpHelpers.PrepareRequestBodyAsync`. The whitelist and per-agent defaults travel to WebChat through a widened `AgentCatalogEntry` via the existing `register_agents` flow.

**Tech Stack:** .NET 10, xUnit + Shouldly + Moq, Blazor WebAssembly (hand-rolled store), SignalR.

**Spec:** `docs/superpowers/specs/2026-08-01-channel-config-patch-design.md`

## Global Constraints

- TDD (Red-Green-Refactor) for every task: write the failing test, run it, watch it fail, implement, run again.
- Run tests with `dotnet test Tests/Unit --nologo -v q` (filter with `--filter FullyQualifiedName~<TestClass>` while iterating).
- `.cs` files have **no trailing newline** (`.editorconfig`); the pre-commit hook runs `dotnet format` and re-stages files whole.
- File-scoped namespaces, primary constructors, `record` DTOs, LINQ over loops, no XML doc comments (`.claude/rules/dotnet-style.md`).
- Domain never imports Infrastructure/Agent; Infrastructure never imports Agent.
- Test naming: `{Method}_{Scenario}_{Expected}`, Shouldly assertions, files named `{Class}Tests.cs`.
- Never modify `providerRouting` semantics; the model stamp goes into `PrepareRequestBodyAsync` alongside `session_id`, not into the provider node (`.claude/rules/openrouter-routing.md`).
- Commit after each task with a message referencing the task.
- New generic config belongs in `appsettings.json` only — no DockerCompose changes (nothing here is a secret or per-deployment).

---

### Task 1: Domain protocol DTOs

**Files:**
- Create: `Domain/DTOs/Channel/AgentConfigPatch.cs`
- Create: `Domain/DTOs/Channel/PatchableModel.cs`
- Modify: `Domain/DTOs/Channel/ChannelMessageNotification.cs` (add property)
- Modify: `Domain/DTOs/ChannelMessage.cs` (add property)
- Test: `Tests/Unit/Domain/Channel/ChannelProtocolTests.cs` (append tests)

**Interfaces:**
- Produces: `AgentConfigPatch { string? Model, string? ReasoningEffort }` with `static IReadOnlyList<string> SupportedEfforts`; `PatchableModel(string Id, string Name)`; `ChannelMessageNotification.ConfigPatch` and `ChannelMessage.ConfigPatch` (both `AgentConfigPatch?`). Every later task consumes these exact names.

- [ ] **Step 1: Write the failing tests** (append to `ChannelProtocolTests`, matching its existing usings/style)

```csharp
[Fact]
public void Serialize_MessageNotificationWithConfigPatch_RoundTripsCamelCase()
{
    var notification = new ChannelMessageNotification
    {
        ConversationId = "conv-1",
        Sender = "fran",
        Content = "hello",
        ConfigPatch = new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "high" }
    };

    var json = JsonSerializer.Serialize(notification, ChannelProtocol.SerializerOptions);
    var parsed = JsonSerializer.Deserialize<ChannelMessageNotification>(json, ChannelProtocol.SerializerOptions);

    json.ShouldContain("\"configPatch\"");
    parsed.ShouldNotBeNull();
    parsed.ConfigPatch.ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "high" });
}

[Fact]
public void Deserialize_MessageNotificationWithoutConfigPatch_LeavesPatchNull()
{
    var json = """{"conversationId":"c","sender":"s","content":"m"}""";

    var parsed = JsonSerializer.Deserialize<ChannelMessageNotification>(json, ChannelProtocol.SerializerOptions);

    parsed.ShouldNotBeNull();
    parsed.ConfigPatch.ShouldBeNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Unit --nologo -v q --filter FullyQualifiedName~ChannelProtocolTests`
Expected: compile error, `ConfigPatch`/`AgentConfigPatch` not defined.

- [ ] **Step 3: Implement**

`Domain/DTOs/Channel/AgentConfigPatch.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record AgentConfigPatch
{
    // Every value McpAgent.ParseEffort accepts; the WebChat effort dropdown offers exactly this list.
    public static readonly IReadOnlyList<string> SupportedEfforts =
        ["none", "low", "medium", "high", "xhigh", "max"];

    public string? Model { get; init; }
    public string? ReasoningEffort { get; init; }
}
```

`Domain/DTOs/Channel/PatchableModel.cs`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record PatchableModel(string Id, string Name);
```

In `ChannelMessageNotification` (after `DismissedAlert`):

```csharp
    // Optional per-message config override (model, reasoning effort). Part of the shared protocol
    // but currently only populated by the SignalR channel; other channels leave it null.
    public AgentConfigPatch? ConfigPatch { get; init; }
```

In `ChannelMessage` (after `DismissedAlert`):

```csharp
    public AgentConfigPatch? ConfigPatch { get; init; }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Unit --nologo -v q --filter FullyQualifiedName~ChannelProtocolTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Channel/AgentConfigPatch.cs Domain/DTOs/Channel/PatchableModel.cs Domain/DTOs/Channel/ChannelMessageNotification.cs Domain/DTOs/ChannelMessage.cs Tests/Unit/Domain/Channel/ChannelProtocolTests.cs
git commit -m "feat(channels): add AgentConfigPatch to channel protocol"
```

---

### Task 2: Map the patch in McpChannelConnection

**Files:**
- Modify: `Infrastructure/Clients/Channels/McpChannelConnection.cs:232-244` (`HandleChannelMessageNotification`)
- Test: `Tests/Unit/Infrastructure/Channels/McpChannelConnectionParsingTests.cs` (append)

**Interfaces:**
- Consumes: `ChannelMessageNotification.ConfigPatch`, `ChannelMessage.ConfigPatch` (Task 1).
- Produces: `ChannelMessage` instances emitted by the connection carry `ConfigPatch`.

- [ ] **Step 1: Write the failing test.** Open `McpChannelConnectionParsingTests.cs` and copy the arrangement of the existing test that feeds `HandleChannelMessageNotification` a `JsonElement` and reads the resulting `ChannelMessage` (reuse its helper for reading from the connection). New test body:

```csharp
[Fact]
public void HandleChannelMessageNotification_WithConfigPatch_MapsPatchOntoChannelMessage()
{
    // Arrange the connection exactly like the neighboring parsing tests in this file.
    var payload = JsonSerializer.SerializeToElement(new
    {
        conversationId = "conv-1",
        sender = "fran",
        content = "hi",
        configPatch = new { model = "z-ai/glm-5.2", reasoningEffort = "low" }
    });

    connection.HandleChannelMessageNotification(payload);

    // Read the message the same way the neighboring tests do, then:
    message.ConfigPatch.ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "low" });
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Unit --nologo -v q --filter FullyQualifiedName~McpChannelConnectionParsingTests`
Expected: FAIL — `ConfigPatch` is null (mapping missing).

- [ ] **Step 3: Implement.** In `HandleChannelMessageNotification`, add to the `ChannelMessage` initializer:

```csharp
            DismissedAlert = notification.DismissedAlert,
            ConfigPatch = notification.ConfigPatch
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/Unit --nologo -v q --filter FullyQualifiedName~McpChannelConnectionParsingTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Clients/Channels/McpChannelConnection.cs Tests/Unit/Infrastructure/Channels/McpChannelConnectionParsingTests.cs
git commit -m "feat(channels): map ConfigPatch in McpChannelConnection"
```

---

### Task 3: ChatMessage annotation + ChatMonitor stamping

**Files:**
- Modify: `Domain/Extensions/ChatMessageExtensions.cs` (new Get/Set pair)
- Modify: `Domain/Monitor/ChatMonitor.cs:190-206` (`BuildUserMessageAsync`)
- Test: `Tests/Unit/Domain/ChatMessageSerializationTests.cs` or a new `Tests/Unit/Domain/ChatMessageConfigPatchExtensionTests.cs`
- Test: `Tests/Unit/Domain/Monitor/ChatMonitorConfigPatchTests.cs` (new, mirrors `ChatMonitorConversationContextTests`)

**Interfaces:**
- Consumes: `AgentConfigPatch`, `ChannelMessage.ConfigPatch`.
- Produces: `ChatMessage.GetConfigPatch(): AgentConfigPatch?` and `ChatMessage.SetConfigPatch(AgentConfigPatch?)` extension members (key `"ConfigPatch"` in `AdditionalProperties`). Tasks 4 and 5 call `GetConfigPatch()`.

- [ ] **Step 1: Write the failing tests**

`Tests/Unit/Domain/ChatMessageConfigPatchExtensionTests.cs`:

```csharp
using System.Text.Json;
using Domain.DTOs.Channel;
using Domain.Extensions;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatMessageConfigPatchExtensionTests
{
    [Fact]
    public void GetConfigPatch_AfterSet_ReturnsPatch()
    {
        var message = new ChatMessage(ChatRole.User, "hi");
        var patch = new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "high" };

        message.SetConfigPatch(patch);

        message.GetConfigPatch().ShouldBe(patch);
    }

    [Fact]
    public void GetConfigPatch_FromJsonElement_Deserializes()
    {
        var message = new ChatMessage(ChatRole.User, "hi")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["ConfigPatch"] = JsonSerializer.SerializeToElement(
                    new AgentConfigPatch { Model = "z-ai/glm-5.2" }, ChannelProtocol.SerializerOptions)
            }
        };

        message.GetConfigPatch().ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2" });
    }

    [Fact]
    public void SetConfigPatch_Null_LeavesPropertiesUntouched()
    {
        var message = new ChatMessage(ChatRole.User, "hi");

        message.SetConfigPatch(null);

        message.AdditionalProperties.ShouldBeNull();
    }
}
```

(`ChannelProtocol` lives in `Domain.DTOs.Channel` — the using above covers it.)

`Tests/Unit/Domain/Monitor/ChatMonitorConfigPatchTests.cs` (copy the monitor arrangement from `ChatMonitorConversationContextTests.Monitor_InteractiveMessage_StampsOriginContextOnUserMessage`, changing only the message and the assertion):

```csharp
[Fact]
public async Task Monitor_MessageWithConfigPatch_StampsPatchOnUserMessage()
{
    var threadResolver = MonitorTestMocks.CreateThreadResolver();
    var patch = new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "high" };
    var message = MonitorTestMocks.CreateChannelMessage(
        conversationId: "conv-1", channelId: "signalr", agentId: "jonas", sender: "test")
        with { ConfigPatch = patch };
    var signalr = MonitorTestMocks.CreateChannel("signalr", message);
    var fakeAgent = MonitorTestMocks.CreateAgent();

    var monitor = new ChatMonitor(
        [signalr],
        MonitorTestMocks.CreateAgentFactory(fakeAgent),
        MonitorTestMocks.CreateApprovalHandlerFactory(),
        threadResolver,
        new Mock<IMetricsPublisher>().Object,
        null,
        new Mock<ILogger<ChatMonitor>>().Object);

    await monitor.Monitor(CancellationToken.None);

    fakeAgent.ReceivedMessages.TryDequeue(out var messages).ShouldBeTrue();
    var userMessage = messages!.ShouldHaveSingleItem();
    userMessage.GetConfigPatch().ShouldBe(patch);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Unit --nologo -v q --filter "FullyQualifiedName~ConfigPatch"`
Expected: compile error, `SetConfigPatch`/`GetConfigPatch` not defined.

- [ ] **Step 3: Implement**

In `ChatMessageExtensions`, add key constant and pair (model it on the `ConversationContext` pair, which also deserializes with `ChannelProtocol.SerializerOptions`):

```csharp
    private const string ConfigPatchKey = "ConfigPatch";
```

```csharp
        public AgentConfigPatch? GetConfigPatch()
        {
            var value = message.AdditionalProperties?.GetValueOrDefault(ConfigPatchKey);
            return value switch
            {
                AgentConfigPatch patch => patch,
                JsonElement je => je.Deserialize<AgentConfigPatch>(ChannelProtocol.SerializerOptions),
                _ => null
            };
        }

        public void SetConfigPatch(AgentConfigPatch? patch)
        {
            if (patch is null)
            {
                return;
            }

            message.AdditionalProperties ??= [];
            message.AdditionalProperties[ConfigPatchKey] = patch;
        }
```

In `ChatMonitor.BuildUserMessageAsync`, after `SetDismissedAlert`:

```csharp
        userMessage.SetConfigPatch(message.ConfigPatch);
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Unit --nologo -v q --filter "FullyQualifiedName~ConfigPatch"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Extensions/ChatMessageExtensions.cs Domain/Monitor/ChatMonitor.cs Tests/Unit/Domain/ChatMessageConfigPatchExtensionTests.cs Tests/Unit/Domain/Monitor/ChatMonitorConfigPatchTests.cs
git commit -m "feat(agent): stamp ConfigPatch onto the turn's user message"
```

---

### Task 4: Reasoning-effort override in McpAgent

**Files:**
- Modify: `Infrastructure/Agents/McpAgent.cs` (`RunCoreStreamingInnerAsync` :245-266, `CreateRunOptions` :268-287, new `TryParseEffort` next to `ParseEffort` :358)
- Test: `Tests/Integration/Agents/McpAgentReasoningTests.cs` (append; runs without Docker — it uses a fake chat client. If it does need services, mirror its fixture exactly.)

**Interfaces:**
- Consumes: `ChatMessage.GetConfigPatch()` (Task 3).
- Produces: `internal static ReasoningEffort? TryParseEffort(string? value)`; `CreateRunOptions(ThreadSession, ConversationContext?, AgentConfigPatch?)`.

- [ ] **Step 1: Write the failing tests.** Copy the arrangement of the existing test in `McpAgentReasoningTests` that builds an `McpAgent` with a configured `reasoningEffort` and asserts the captured `ChatOptions.Reasoning`. Add:

```csharp
[Fact]
public async Task RunStreaming_UserMessageWithEffortPatch_OverridesConfiguredEffort()
{
    // Build the agent with reasoningEffort: "low" exactly like the existing configured-effort test.
    var userMessage = new ChatMessage(ChatRole.User, "hi");
    userMessage.SetConfigPatch(new AgentConfigPatch { ReasoningEffort = "high" });

    // Run and capture ChatOptions exactly like the existing test, then:
    capturedOptions.Reasoning.ShouldNotBeNull();
    capturedOptions.Reasoning.Effort.ShouldBe(ReasoningEffort.High);
}

[Fact]
public async Task RunStreaming_UserMessageWithInvalidEffortPatch_FallsBackToConfigured()
{
    // Same arrangement, agent configured with reasoningEffort: "low".
    var userMessage = new ChatMessage(ChatRole.User, "hi");
    userMessage.SetConfigPatch(new AgentConfigPatch { ReasoningEffort = "turbo" });

    capturedOptions.Reasoning.ShouldNotBeNull();
    capturedOptions.Reasoning.Effort.ShouldBe(ReasoningEffort.Low);
}
```

Also add a plain unit assertion for the parser:

```csharp
[Fact]
public void TryParseEffort_UnknownValue_ReturnsNull()
{
    McpAgent.TryParseEffort("turbo").ShouldBeNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Integration --nologo -v q --filter FullyQualifiedName~McpAgentReasoningTests`
Expected: compile error (`TryParseEffort` missing), then behavioral failure (effort stays Low).

- [ ] **Step 3: Implement**

Add next to `ParseEffort`:

```csharp
    internal static ReasoningEffort? TryParseEffort(string? value)
    {
        try
        {
            return ParseEffort(value);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
```

In `RunCoreStreamingInnerAsync`, after the `conversationContext` extraction:

```csharp
        var configPatch = messageList
            .Where(m => m.Role == ChatRole.User)
            .Select(m => m.GetConfigPatch())
            .LastOrDefault(p => p is not null);

        options ??= CreateRunOptions(session, conversationContext, configPatch);
```

Change `CreateRunOptions` signature and the `Reasoning` assignment:

```csharp
    private ChatClientAgentRunOptions CreateRunOptions(
        ThreadSession session, ConversationContext? conversationContext = null, AgentConfigPatch? configPatch = null)
    {
        var effort = TryParseEffort(configPatch?.ReasoningEffort) ?? _reasoningEffort;
        return new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [.. session.Tools],
            Instructions = BuildInstructions(
                _name,
                _description,
                _customInstructions,
                _language,
                _domainPrompts,
                session.FileSystemPrompts,
                session.ClientManager.Prompts,
                _timeProvider.GetLocalNow()),
            Reasoning = effort is null
                ? null
                : new ReasoningOptions { Effort = effort.Value },
            AdditionalProperties = BuildConversationContextProperties(conversationContext)
        });
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Integration --nologo -v q --filter FullyQualifiedName~McpAgentReasoningTests` and `dotnet test Tests/Unit --nologo -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/McpAgent.cs Tests/Integration/Agents/McpAgentReasoningTests.cs
git commit -m "feat(agent): per-turn reasoning effort override from ConfigPatch"
```

---

### Task 5: Model override on the OpenRouter wire

**Files:**
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs:14-58` (`PrepareRequestBodyAsync` gains `string? modelOverride`)
- Modify: `Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs` (whitelist ctor param, override box, resolve per call)
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs` (`OpenRouterConfig.PatchableModelIds`, pass through `CreateChatClient`)
- Test: the existing tests file covering `PrepareRequestBodyAsync` (find with `grep -rln PrepareRequestBodyAsync Tests/`), plus new `Tests/Unit/Infrastructure/Agents/OpenRouterModelOverrideTests.cs`

**Interfaces:**
- Consumes: `ChatMessage.GetConfigPatch()` (Task 3).
- Produces: `PrepareRequestBodyAsync(HttpRequestMessage, string? sessionId, ProviderRouting?, string? modelOverride, CancellationToken)`; `OpenRouterChatClient` public ctor gains `IReadOnlyList<string>? patchableModelIds = null`; `internal static string? ResolveModelOverride(AgentConfigPatch? patch, string configuredModel, IReadOnlyList<string> patchableModelIds)`; `OpenRouterConfig.PatchableModelIds: IReadOnlyList<string>?`. Task 6 sets `PatchableModelIds`.

Subagents need no gating: they share `CreateChatClient`, but the patch annotation only ever exists on user messages built by `ChatMonitor`, and subagent runs build their own prompts, so `ResolveModelOverride` sees a null patch there.

- [ ] **Step 1: Write the failing tests**

Append to the existing `PrepareRequestBodyAsync` tests (match their request-building helper):

```csharp
[Fact]
public async Task PrepareRequestBodyAsync_WithModelOverride_StampsModel()
{
    var request = CreateJsonPostRequest("""{"model":"openai/gpt-5.6-luna","messages":[]}""");

    await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
        request, sessionId: null, providerRouting: null, modelOverride: "z-ai/glm-5.2", CancellationToken.None);

    var body = await request.Content!.ReadAsStringAsync();
    JsonNode.Parse(body)!["model"]!.GetValue<string>().ShouldBe("z-ai/glm-5.2");
}

[Fact]
public async Task PrepareRequestBodyAsync_NoModelOverride_KeepsOriginalModel()
{
    var request = CreateJsonPostRequest("""{"model":"openai/gpt-5.6-luna","messages":[]}""");

    await OpenRouterHttpHelpers.PrepareRequestBodyAsync(
        request, sessionId: null, providerRouting: null, modelOverride: null, CancellationToken.None);

    var body = await request.Content!.ReadAsStringAsync();
    JsonNode.Parse(body)!["model"]!.GetValue<string>().ShouldBe("openai/gpt-5.6-luna");
}
```

`Tests/Unit/Infrastructure/Agents/OpenRouterModelOverrideTests.cs`:

```csharp
using Domain.DTOs.Channel;
using Infrastructure.Agents.ChatClients;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents;

public class OpenRouterModelOverrideTests
{
    private static readonly IReadOnlyList<string> Whitelist = ["openai/gpt-5.6-luna", "z-ai/glm-5.2"];

    [Fact]
    public void ResolveModelOverride_WhitelistedDifferentModel_ReturnsIt()
    {
        var patch = new AgentConfigPatch { Model = "z-ai/glm-5.2" };

        OpenRouterChatClient.ResolveModelOverride(patch, "openai/gpt-5.6-luna", Whitelist)
            .ShouldBe("z-ai/glm-5.2");
    }

    [Fact]
    public void ResolveModelOverride_NonWhitelistedModel_ReturnsNull()
    {
        var patch = new AgentConfigPatch { Model = "evil/model" };

        OpenRouterChatClient.ResolveModelOverride(patch, "openai/gpt-5.6-luna", Whitelist).ShouldBeNull();
    }

    [Fact]
    public void ResolveModelOverride_SameAsConfigured_ReturnsNull()
    {
        var patch = new AgentConfigPatch { Model = "openai/gpt-5.6-luna" };

        OpenRouterChatClient.ResolveModelOverride(patch, "openai/gpt-5.6-luna", Whitelist).ShouldBeNull();
    }

    [Fact]
    public void ResolveModelOverride_NullPatch_ReturnsNull()
    {
        OpenRouterChatClient.ResolveModelOverride(null, "openai/gpt-5.6-luna", Whitelist).ShouldBeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Unit --nologo -v q --filter "FullyQualifiedName~OpenRouter"`
Expected: compile errors (`modelOverride` param, `ResolveModelOverride` missing).

- [ ] **Step 3: Implement**

`OpenRouterHttpHelpers.PrepareRequestBodyAsync` — new parameter and stamp (after the `session_id` block):

```csharp
    public static async Task PrepareRequestBodyAsync(
        HttpRequestMessage request, string? sessionId, ProviderRouting? providerRouting,
        string? modelOverride, CancellationToken ct)
```

```csharp
        // Per-message model patch. Stamped like session_id: the OpenAI SDK bakes the configured
        // model into the body, so the override rewrites it here, after whitelist validation upstream.
        if (!string.IsNullOrWhiteSpace(modelOverride))
        {
            obj["model"] = modelOverride;
        }
```

`OpenRouterChatClient`:

- Add field + ctor param (public ctor): `IReadOnlyList<string>? patchableModelIds = null` stored as `_patchableModelIds = patchableModelIds ?? [];`. The internal test ctor sets `_patchableModelIds = [];` and `_modelOverrideBox = new ModelOverrideBox();`.
- Add the box type and resolver:

```csharp
    internal sealed class ModelOverrideBox
    {
        public volatile string? Value;
    }

    internal static string? ResolveModelOverride(
        AgentConfigPatch? patch, string configuredModel, IReadOnlyList<string> patchableModelIds)
    {
        return patch?.Model is { } model
               && !string.Equals(model, configuredModel, StringComparison.OrdinalIgnoreCase)
               && patchableModelIds.Contains(model, StringComparer.OrdinalIgnoreCase)
            ? model
            : null;
    }
```

- Create one `ModelOverrideBox` per client instance, pass it through `CreateHttpClient` into `ReasoningHandler`, and change the handler to call `PrepareRequestBodyAsync(request, sessionId, providerRouting, overrideBox.Value, cancellationToken)`.
- In `GetStreamingResponseAsync`, right after `transformedMessages` is built:

```csharp
        _modelOverrideBox.Value = ResolveModelOverride(
            transformedMessages.LastOrDefault(m => m.Role == ChatRole.User)?.GetConfigPatch(),
            _model,
            _patchableModelIds);
```

(One client instance serves one conversation and its turns run sequentially, so a plain volatile field is enough.)

`MultiAgentFactory`:

- `OpenRouterConfig` gains `public IReadOnlyList<string>? PatchableModelIds { get; init; }`.
- `CreateChatClient` passes `patchableModelIds: openRouterConfig.PatchableModelIds` to the `OpenRouterChatClient` ctor.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Unit --nologo -v q` (full unit suite — the signature change touches other tests; fix call sites by passing `modelOverride: null`).
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Infrastructure/Agents/ChatClients/OpenRouterHttpHelpers.cs Infrastructure/Agents/ChatClients/OpenRouterChatClient.cs Infrastructure/Agents/MultiAgentFactory.cs Tests/Unit
git commit -m "feat(agent): whitelisted per-turn model override on the OpenRouter request"
```

---

### Task 6: Whitelist config + widened AgentCatalogEntry

**Files:**
- Modify: `Domain/DTOs/Channel/AgentCatalogEntry.cs`
- Modify: `Agent/Settings/AgentSettings.cs` (add `PatchableModels`)
- Modify: `Agent/appsettings.json` (add `patchableModels` at root, next to `agents`)
- Modify: `Agent/Modules/InjectorModule.cs:24-28` (OpenRouterConfig) and `:75` (catalog entries)
- Test: the existing settings-binding tests (find with `grep -rln AgentAppSettings Tests/`), plus a catalog round-trip in `Tests/Unit/Domain/Channel/ChannelProtocolTests.cs`

**Interfaces:**
- Consumes: `PatchableModel`, `AgentConfigPatch.SupportedEfforts` (Task 1), `OpenRouterConfig.PatchableModelIds` (Task 5).
- Produces: `AgentCatalogEntry(string Id, string Name, string? Description, string? DefaultModel = null, string? DefaultReasoningEffort = null, IReadOnlyList<PatchableModel>? PatchableModels = null, IReadOnlyList<string>? PatchableReasoningEfforts = null)`; `AgentSettings.PatchableModels: PatchableModel[]`. Tasks 8-11 consume the widened entry.

- [ ] **Step 1: Write the failing tests**

Binding test (append to the existing settings-binding test class, matching how it builds configuration):

```csharp
[Fact]
public void Bind_PatchableModels_BindsIdAndName()
{
    // Build IConfiguration exactly like the neighboring binding tests, with:
    // "patchableModels:0:id" = "openai/gpt-5.6-luna", "patchableModels:0:name" = "GPT Luna",
    // "patchableModels:1:id" = "z-ai/glm-5.2",       "patchableModels:1:name" = "GLM 5.2"

    settings.PatchableModels.ShouldBe([
        new PatchableModel("openai/gpt-5.6-luna", "GPT Luna"),
        new PatchableModel("z-ai/glm-5.2", "GLM 5.2")
    ]);
}
```

Catalog round-trip (append to `ChannelProtocolTests`):

```csharp
[Fact]
public void Serialize_WidenedAgentCatalogEntry_RoundTrips()
{
    var entry = new AgentCatalogEntry(
        "jack", "Jack", "Main agent",
        "openai/gpt-5.6-luna", "low",
        [new PatchableModel("z-ai/glm-5.2", "GLM 5.2")],
        AgentConfigPatch.SupportedEfforts);

    var json = JsonSerializer.Serialize(entry, ChannelProtocol.SerializerOptions);
    var parsed = JsonSerializer.Deserialize<AgentCatalogEntry>(json, ChannelProtocol.SerializerOptions);

    parsed.ShouldBe(entry with
    {
        PatchableModels = parsed!.PatchableModels,
        PatchableReasoningEfforts = parsed.PatchableReasoningEfforts
    });
    parsed.PatchableModels.ShouldBe([new PatchableModel("z-ai/glm-5.2", "GLM 5.2")]);
    parsed.PatchableReasoningEfforts.ShouldBe(AgentConfigPatch.SupportedEfforts);
}
```

(Records compare list properties by reference; assert the lists separately as shown.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Unit --nologo -v q --filter "FullyQualifiedName~ChannelProtocolTests|FullyQualifiedName~Settings"`
Expected: compile errors (new positional params, `PatchableModels` property missing).

- [ ] **Step 3: Implement**

`AgentCatalogEntry`:

```csharp
using JetBrains.Annotations;

namespace Domain.DTOs.Channel;

[PublicAPI]
public record AgentCatalogEntry(
    string Id,
    string Name,
    string? Description,
    string? DefaultModel = null,
    string? DefaultReasoningEffort = null,
    IReadOnlyList<PatchableModel>? PatchableModels = null,
    IReadOnlyList<string>? PatchableReasoningEfforts = null);
```

`AgentSettings`: add `public PatchableModel[] PatchableModels { get; init; } = [];` (using `Domain.DTOs.Channel`).

`Agent/appsettings.json` — add at root level:

```json
    "patchableModels": [
        { "id": "openai/gpt-5.6-luna", "name": "GPT Luna" },
        { "id": "z-ai/glm-5.2", "name": "GLM 5.2" }
    ],
```

`InjectorModule`:

```csharp
            var config = new OpenRouterConfig
            {
                ApiUrl = settings.OpenRouter.ApiUrl,
                ApiKey = settings.OpenRouter.ApiKey,
                MaxContextTokens = settings.OpenRouter.MaxContextTokens,
                ProviderRouting = settings.OpenRouter.ProviderRouting,
                PatchableModelIds = settings.PatchableModels.Select(m => m.Id).ToList()
            };
```

```csharp
                        settings.Agents.Select(a => new AgentCatalogEntry(
                            a.Id, a.Name, a.Description,
                            a.Model, a.ReasoningEffort,
                            settings.PatchableModels, AgentConfigPatch.SupportedEfforts)).ToList(),
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Unit --nologo -v q`
Expected: PASS (the widened record has defaulted params, so existing constructions compile).

- [ ] **Step 5: Commit**

```bash
git add Domain/DTOs/Channel/AgentCatalogEntry.cs Agent/Settings/AgentSettings.cs Agent/appsettings.json Agent/Modules/InjectorModule.cs Tests/Unit
git commit -m "feat(agent): publish patchable models and defaults through the agent catalog"
```

---

### Task 7: SignalR hub and emitter pass-through

**Files:**
- Modify: `McpChannelSignalR/Services/ChannelNotificationEmitter.cs:8-25`
- Modify: `McpChannelSignalR/Hubs/ChatHub.cs` (`SendMessage` :130, `EnqueueMessage` :189)
- Test: `Tests/Unit/McpChannelSignalR/ChannelNotificationEmitterTests.cs` (append)

**Interfaces:**
- Consumes: `AgentConfigPatch`, `ChannelMessageNotification.ConfigPatch` (Task 1).
- Produces: `EmitMessageNotificationAsync(string conversationId, string sender, string content, string agentId, AgentConfigPatch? configPatch = null, CancellationToken cancellationToken = default)`; hub methods `SendMessage(string topicId, string message, string? correlationId, AgentConfigPatch? configPatch, CancellationToken)` and `EnqueueMessage(string topicId, string message, string? correlationId, AgentConfigPatch? configPatch)`. Task 10's client calls must pass args in exactly this order (SignalR binds positionally; the client always sends the patch argument, null when unused).

- [ ] **Step 1: Write the failing test** (append, matching the existing emitter tests' inbox arrangement):

```csharp
[Fact]
public async Task EmitMessageNotificationAsync_WithConfigPatch_PutsPatchOnNotification()
{
    // Arrange inbox + subscriber exactly like the neighboring tests in this file.
    await emitter.EmitMessageNotificationAsync(
        "chat:thread", "fran", "hello", "jack",
        new AgentConfigPatch { Model = "z-ai/glm-5.2" });

    // Dequeue the item like the neighboring tests, then:
    item.Message!.ConfigPatch.ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2" });
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/Unit --nologo -v q --filter FullyQualifiedName~ChannelNotificationEmitterTests`
Expected: compile error (no such parameter).

- [ ] **Step 3: Implement**

Emitter:

```csharp
    public Task EmitMessageNotificationAsync(
        string conversationId,
        string sender,
        string content,
        string agentId,
        AgentConfigPatch? configPatch = null,
        CancellationToken cancellationToken = default)
    {
        inbox.Enqueue(ChannelInboxItem.ForMessage(new ChannelMessageNotification
        {
            ConversationId = conversationId,
            Sender = sender,
            Content = content,
            AgentId = agentId,
            ConfigPatch = configPatch,
            Timestamp = DateTimeOffset.UtcNow
        }));

        return Task.CompletedTask;
    }
```

`ChatHub.SendMessage` signature becomes:

```csharp
    public async IAsyncEnumerable<ChatStreamMessage> SendMessage(
        string topicId,
        string message,
        string? correlationId,
        AgentConfigPatch? configPatch,
        [EnumeratorCancellation] CancellationToken cancellationToken)
```

and its emitter call gains `configPatch` after `session.AgentId` (before the cancellation token). `EnqueueMessage` becomes `(string topicId, string message, string? correlationId, AgentConfigPatch? configPatch)` and passes `configPatch` likewise. Update any tests invoking these hub methods (e.g. `AgentInitiatedStreamingFlowTests`) to pass `null`.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Unit --nologo -v q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add McpChannelSignalR Tests/Unit
git commit -m "feat(signalr): accept a per-message config patch on SendMessage/EnqueueMessage"
```

---

### Task 8: WebChat client settings slice

**Files:**
- Create: `WebChat.Client/State/AgentSettings/AgentSettingsState.cs`
- Create: `WebChat.Client/State/AgentSettings/AgentSettingsActions.cs`
- Create: `WebChat.Client/State/AgentSettings/AgentSettingsStore.cs`
- Create: `WebChat.Client/State/AgentSettings/AgentSettingsSelectors.cs`
- Modify: `WebChat.Client/Program.cs` (register `AgentSettingsStore` like the other stores)
- Test: `Tests/Unit/WebChat.Client/State/AgentSettingsStoreTests.cs`, `Tests/Unit/WebChat.Client/State/AgentSettingsSelectorsTests.cs`

**Interfaces:**
- Consumes: widened `AgentCatalogEntry` (Task 6), `AgentConfigPatch`.
- Produces:
  - `AgentModelSettings(string? Model, string? ReasoningEffort)`
  - `AgentSettingsState { IReadOnlyDictionary<string, AgentModelSettings> ByAgent }`, `AgentSettingsState.Initial`
  - Actions: `SetAgentModel(string AgentId, string? Model)`, `SetAgentReasoningEffort(string AgentId, string? Effort)`, `AgentSettingsLoaded(string AgentId, AgentModelSettings Settings)` — all `: IAction`
  - `AgentSettingsStore(Dispatcher)` with `State` / `StateObservable`, modeled on `ToastStore`
  - `AgentSettingsSelectors.GetConfigPatch(AgentSettingsState, IReadOnlyList<AgentCatalogEntry>, string agentId): AgentConfigPatch?`
  - `AgentSettingsSelectors.Sanitize(AgentModelSettings, AgentCatalogEntry): AgentModelSettings` (drops non-whitelisted values)

- [ ] **Step 1: Write the failing tests**

`AgentSettingsStoreTests.cs`:

```csharp
using WebChat.Client.State;
using WebChat.Client.State.AgentSettings;
using Shouldly;

namespace Tests.Unit.WebChat.Client.State;

public class AgentSettingsStoreTests
{
    [Fact]
    public void SetAgentModel_NewAgent_AddsEntry()
    {
        var dispatcher = new Dispatcher();
        var store = new AgentSettingsStore(dispatcher);

        dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));

        store.State.ByAgent["jack"].Model.ShouldBe("z-ai/glm-5.2");
    }

    [Fact]
    public void SetAgentReasoningEffort_ExistingAgent_KeepsModel()
    {
        var dispatcher = new Dispatcher();
        var store = new AgentSettingsStore(dispatcher);
        dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));

        dispatcher.Dispatch(new SetAgentReasoningEffort("jack", "high"));

        store.State.ByAgent["jack"].ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "high"));
    }

    [Fact]
    public void AgentSettingsLoaded_ReplacesAgentEntry()
    {
        var dispatcher = new Dispatcher();
        var store = new AgentSettingsStore(dispatcher);

        dispatcher.Dispatch(new AgentSettingsLoaded("jack", new AgentModelSettings("z-ai/glm-5.2", "max")));

        store.State.ByAgent["jack"].ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "max"));
    }
}
```

(If `Dispatcher` construction differs, mirror `Tests/Unit/WebChat.Client/State/TopicsStoreTests.cs`.)

`AgentSettingsSelectorsTests.cs`:

```csharp
using Domain.DTOs.Channel;
using WebChat.Client.State.AgentSettings;
using Shouldly;

namespace Tests.Unit.WebChat.Client.State;

public class AgentSettingsSelectorsTests
{
    private static readonly AgentCatalogEntry Jack = new(
        "jack", "Jack", null,
        "openai/gpt-5.6-luna", "low",
        [new PatchableModel("openai/gpt-5.6-luna", "GPT Luna"), new PatchableModel("z-ai/glm-5.2", "GLM 5.2")],
        AgentConfigPatch.SupportedEfforts);

    private static AgentSettingsState StateWith(AgentModelSettings settings) =>
        new() { ByAgent = new Dictionary<string, AgentModelSettings> { ["jack"] = settings } };

    [Fact]
    public void GetConfigPatch_AllValuesMatchDefaults_ReturnsNull()
    {
        var state = StateWith(new AgentModelSettings("openai/gpt-5.6-luna", "low"));

        AgentSettingsSelectors.GetConfigPatch(state, [Jack], "jack").ShouldBeNull();
    }

    [Fact]
    public void GetConfigPatch_ModelDiffers_ReturnsModelOnlyPatch()
    {
        var state = StateWith(new AgentModelSettings("z-ai/glm-5.2", "low"));

        AgentSettingsSelectors.GetConfigPatch(state, [Jack], "jack")
            .ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2" });
    }

    [Fact]
    public void GetConfigPatch_BothDiffer_ReturnsBothFields()
    {
        var state = StateWith(new AgentModelSettings("z-ai/glm-5.2", "max"));

        AgentSettingsSelectors.GetConfigPatch(state, [Jack], "jack")
            .ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2", ReasoningEffort = "max" });
    }

    [Fact]
    public void GetConfigPatch_UnknownAgent_ReturnsNull()
    {
        var state = StateWith(new AgentModelSettings("z-ai/glm-5.2", "max"));

        AgentSettingsSelectors.GetConfigPatch(state, [Jack], "ghost").ShouldBeNull();
    }

    [Fact]
    public void Sanitize_NonWhitelistedModel_FallsBackToDefault()
    {
        var sanitized = AgentSettingsSelectors.Sanitize(new AgentModelSettings("old/model", "low"), Jack);

        sanitized.ShouldBe(new AgentModelSettings("openai/gpt-5.6-luna", "low"));
    }

    [Fact]
    public void Sanitize_UnknownEffort_FallsBackToDefault()
    {
        var sanitized = AgentSettingsSelectors.Sanitize(new AgentModelSettings("z-ai/glm-5.2", "turbo"), Jack);

        sanitized.ShouldBe(new AgentModelSettings("z-ai/glm-5.2", "low"));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Unit --nologo -v q --filter FullyQualifiedName~AgentSettings`
Expected: compile errors — types don't exist.

- [ ] **Step 3: Implement**

`AgentSettingsState.cs`:

```csharp
namespace WebChat.Client.State.AgentSettings;

public sealed record AgentModelSettings(string? Model, string? ReasoningEffort);

public sealed record AgentSettingsState
{
    public IReadOnlyDictionary<string, AgentModelSettings> ByAgent { get; init; } =
        new Dictionary<string, AgentModelSettings>();

    public static AgentSettingsState Initial => new();
}
```

`AgentSettingsActions.cs`:

```csharp
namespace WebChat.Client.State.AgentSettings;

public record SetAgentModel(string AgentId, string? Model) : IAction;

public record SetAgentReasoningEffort(string AgentId, string? Effort) : IAction;

public record AgentSettingsLoaded(string AgentId, AgentModelSettings Settings) : IAction;
```

`AgentSettingsStore.cs` (mirror `ToastStore`):

```csharp
namespace WebChat.Client.State.AgentSettings;

public sealed class AgentSettingsStore : IDisposable
{
    private readonly Store<AgentSettingsState> _store;

    public AgentSettingsStore(Dispatcher dispatcher)
    {
        _store = new Store<AgentSettingsState>(AgentSettingsState.Initial);

        dispatcher.RegisterHandler<SetAgentModel>(action => _store.Dispatch(action, Reduce));
        dispatcher.RegisterHandler<SetAgentReasoningEffort>(action => _store.Dispatch(action, Reduce));
        dispatcher.RegisterHandler<AgentSettingsLoaded>(action => _store.Dispatch(action, Reduce));
    }

    public AgentSettingsState State => _store.State;
    public IObservable<AgentSettingsState> StateObservable => _store.StateObservable;
    public void Dispose() => _store.Dispose();

    private static AgentSettingsState Reduce(AgentSettingsState state, SetAgentModel action)
    {
        var current = state.ByAgent.GetValueOrDefault(action.AgentId) ?? new AgentModelSettings(null, null);
        return WithEntry(state, action.AgentId, current with { Model = action.Model });
    }

    private static AgentSettingsState Reduce(AgentSettingsState state, SetAgentReasoningEffort action)
    {
        var current = state.ByAgent.GetValueOrDefault(action.AgentId) ?? new AgentModelSettings(null, null);
        return WithEntry(state, action.AgentId, current with { ReasoningEffort = action.Effort });
    }

    private static AgentSettingsState Reduce(AgentSettingsState state, AgentSettingsLoaded action) =>
        WithEntry(state, action.AgentId, action.Settings);

    private static AgentSettingsState WithEntry(
        AgentSettingsState state, string agentId, AgentModelSettings settings)
    {
        var byAgent = state.ByAgent
            .Where(kv => kv.Key != agentId)
            .ToDictionary(kv => kv.Key, kv => kv.Value);
        byAgent[agentId] = settings;
        return state with { ByAgent = byAgent };
    }
}
```

`AgentSettingsSelectors.cs`:

```csharp
using Domain.DTOs.Channel;

namespace WebChat.Client.State.AgentSettings;

public static class AgentSettingsSelectors
{
    public static AgentConfigPatch? GetConfigPatch(
        AgentSettingsState state, IReadOnlyList<AgentCatalogEntry> agents, string agentId)
    {
        var agent = agents.FirstOrDefault(a => a.Id == agentId);
        var settings = state.ByAgent.GetValueOrDefault(agentId);
        if (agent is null || settings is null)
        {
            return null;
        }

        var model = Differs(settings.Model, agent.DefaultModel) ? settings.Model : null;
        var effort = Differs(settings.ReasoningEffort, agent.DefaultReasoningEffort)
            ? settings.ReasoningEffort
            : null;

        return model is null && effort is null
            ? null
            : new AgentConfigPatch { Model = model, ReasoningEffort = effort };
    }

    public static AgentModelSettings Sanitize(AgentModelSettings settings, AgentCatalogEntry agent)
    {
        var modelValid = settings.Model is { } model &&
                         (agent.PatchableModels ?? []).Any(m =>
                             string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));
        var effortValid = settings.ReasoningEffort is { } effort &&
                          (agent.PatchableReasoningEfforts ?? []).Contains(
                              effort, StringComparer.OrdinalIgnoreCase);

        return new AgentModelSettings(
            modelValid ? settings.Model : agent.DefaultModel,
            effortValid ? settings.ReasoningEffort : agent.DefaultReasoningEffort);
    }

    private static bool Differs(string? selected, string? fallback) =>
        selected is not null && !string.Equals(selected, fallback, StringComparison.OrdinalIgnoreCase);
}
```

Register `AgentSettingsStore` in `WebChat.Client/Program.cs` next to the other store registrations (same lifetime/pattern as `ToastStore`).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Unit --nologo -v q --filter FullyQualifiedName~AgentSettings`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WebChat.Client/State/AgentSettings WebChat.Client/Program.cs Tests/Unit/WebChat.Client/State/AgentSettingsStoreTests.cs Tests/Unit/WebChat.Client/State/AgentSettingsSelectorsTests.cs
git commit -m "feat(webchat): agent settings state slice with patch selector"
```

---

### Task 9: Local-storage persistence and initialization

**Files:**
- Create: `WebChat.Client/State/Effects/AgentSettingsEffect.cs`
- Modify: `WebChat.Client/State/Effects/InitializationEffect.cs` (load settings after `SetAgents`, around :119-127)
- Modify: `WebChat.Client/Program.cs` (register the effect like the other effects)
- Test: `Tests/Unit/WebChat.Client/State/AgentSettingsEffectTests.cs`

**Interfaces:**
- Consumes: `AgentSettingsStore`, actions, `Sanitize` (Task 8); `ILocalStorageService` (`GetAsync`/`SetAsync`).
- Produces: storage key format `agentConfigPatch:{agentId}` holding `JsonSerializer.Serialize(AgentModelSettings)`; `AgentSettingsEffect(AgentSettingsStore, ILocalStorageService)`; `static Task LoadAsync(IReadOnlyList<AgentCatalogEntry>, ILocalStorageService, IDispatcher)` used by `InitializationEffect`.

- [ ] **Step 1: Write the failing tests** (fake `ILocalStorageService` backed by a `Dictionary<string, string>`; mirror mock style from `SendMessageEffectTests`):

```csharp
[Fact]
public async Task LoadAsync_StoredSettings_SanitizesAndDispatchesLoaded()
{
    var storage = new FakeLocalStorage();
    await storage.SetAsync("agentConfigPatch:jack",
        """{"Model":"z-ai/glm-5.2","ReasoningEffort":"turbo"}""");
    var dispatched = new List<IAction>();
    var dispatcher = CreateCapturingDispatcher(dispatched);

    await AgentSettingsEffect.LoadAsync([Jack], storage, dispatcher);

    dispatched.OfType<AgentSettingsLoaded>().ShouldHaveSingleItem()
        .ShouldBe(new AgentSettingsLoaded("jack", new AgentModelSettings("z-ai/glm-5.2", "low")));
}

[Fact]
public async Task LoadAsync_NothingStored_DispatchesDefaults()
{
    var storage = new FakeLocalStorage();
    var dispatched = new List<IAction>();
    var dispatcher = CreateCapturingDispatcher(dispatched);

    await AgentSettingsEffect.LoadAsync([Jack], storage, dispatcher);

    dispatched.OfType<AgentSettingsLoaded>().ShouldHaveSingleItem()
        .ShouldBe(new AgentSettingsLoaded("jack", new AgentModelSettings("openai/gpt-5.6-luna", "low")));
}

[Fact]
public async Task StateChange_ChangedEntry_PersistsToStorage()
{
    var storage = new FakeLocalStorage();
    var dispatcher = new Dispatcher();
    var store = new AgentSettingsStore(dispatcher);
    using var effect = new AgentSettingsEffect(store, storage);

    dispatcher.Dispatch(new SetAgentModel("jack", "z-ai/glm-5.2"));
    await Task.Delay(50); // fire-and-forget write

    (await storage.GetAsync("agentConfigPatch:jack"))
        .ShouldBe(JsonSerializer.Serialize(new AgentModelSettings("z-ai/glm-5.2", null)));
}
```

(`Jack` is the same catalog fixture as in `AgentSettingsSelectorsTests`; `CreateCapturingDispatcher` — use whatever `IDispatcher` faking the existing effect tests use.)

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Unit --nologo -v q --filter FullyQualifiedName~AgentSettingsEffect`
Expected: compile errors.

- [ ] **Step 3: Implement**

`AgentSettingsEffect.cs`:

```csharp
using System.Text.Json;
using Domain.DTOs.Channel;
using WebChat.Client.Contracts;
using WebChat.Client.State.AgentSettings;

namespace WebChat.Client.State.Effects;

public sealed class AgentSettingsEffect : IDisposable
{
    private const string KeyPrefix = "agentConfigPatch:";

    private readonly IDisposable _subscription;
    private readonly ILocalStorageService _localStorage;
    private IReadOnlyDictionary<string, AgentModelSettings> _previous;

    public AgentSettingsEffect(AgentSettingsStore store, ILocalStorageService localStorage)
    {
        _localStorage = localStorage;
        _previous = store.State.ByAgent;
        _subscription = store.StateObservable.Subscribe(HandleStateChange);
    }

    public static async Task LoadAsync(
        IReadOnlyList<AgentCatalogEntry> agents, ILocalStorageService localStorage, IDispatcher dispatcher)
    {
        foreach (var agent in agents)
        {
            var stored = await localStorage.GetAsync($"{KeyPrefix}{agent.Id}");
            var settings = Deserialize(stored) ?? new AgentModelSettings(null, null);
            dispatcher.Dispatch(new AgentSettingsLoaded(
                agent.Id, AgentSettingsSelectors.Sanitize(settings, agent)));
        }
    }

    private void HandleStateChange(AgentSettingsState state)
    {
        var changed = state.ByAgent
            .Where(kv => !Equals(_previous.GetValueOrDefault(kv.Key), kv.Value))
            .ToList();
        _previous = state.ByAgent;

        changed.ForEach(kv =>
            _ = _localStorage.SetAsync($"{KeyPrefix}{kv.Key}", JsonSerializer.Serialize(kv.Value)));
    }

    private static AgentModelSettings? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AgentModelSettings>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Dispose() => _subscription.Dispose();
}
```

In `InitializationEffect`, right after `_dispatcher.Dispatch(new SetAgents(agents));`:

```csharp
        await AgentSettingsEffect.LoadAsync(agents, _localStorage, _dispatcher);
```

(`_localStorage` and `_dispatcher` are already fields there.) Register `AgentSettingsEffect` in `Program.cs` alongside the other effects.

Note: on catalog refresh (`OnAgentsUpdated`), settings stay as loaded; a stale value only matters on next app load, when `LoadAsync` sanitizes again. This is accepted.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Unit --nologo -v q --filter FullyQualifiedName~AgentSettings`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add WebChat.Client/State/Effects/AgentSettingsEffect.cs WebChat.Client/State/Effects/InitializationEffect.cs WebChat.Client/Program.cs Tests/Unit/WebChat.Client/State/AgentSettingsEffectTests.cs
git commit -m "feat(webchat): persist agent settings in local storage, seed from catalog defaults"
```

---

### Task 10: Send the patch from the client

**Files:**
- Modify: `WebChat.Client/Contracts/IChatMessagingService.cs`
- Modify: `WebChat.Client/Services/ChatMessagingService.cs`
- Modify: `WebChat.Client/Services/Streaming/StreamingService.cs`
- Test: `Tests/Unit/WebChat.Client/State/SendMessageEffectTests.cs` (fix construction) + new assertions in a `StreamingService` test

**Interfaces:**
- Consumes: `AgentSettingsSelectors.GetConfigPatch`, `AgentSettingsStore` (Task 8), hub signatures (Task 7), `StoredTopic.AgentId`.
- Produces: `IChatMessagingService.SendMessageAsync(string topicId, string message, string? correlationId = null, AgentConfigPatch? configPatch = null)` and `EnqueueMessageAsync(string topicId, string message, string? correlationId = null, AgentConfigPatch? configPatch = null)`; `StreamingService` ctor gains `AgentSettingsStore agentSettingsStore`.

- [ ] **Step 1: Write the failing test.** Add to the streaming-service coverage (mirror the fakes `SendMessageEffectTests` uses for `IChatMessagingService`):

```csharp
[Fact]
public async Task SendMessageAsync_SettingsDifferFromDefaults_PassesConfigPatch()
{
    // topicsStore seeded with the Jack catalog entry (defaults luna/low) and a topic with AgentId "jack";
    // agentSettingsStore seeded via dispatcher with SetAgentModel("jack", "z-ai/glm-5.2").
    // fakeMessaging captures the configPatch argument.

    await streamingService.SendMessageAsync(topic, "hello");

    fakeMessaging.LastConfigPatch.ShouldBe(new AgentConfigPatch { Model = "z-ai/glm-5.2" });
}

[Fact]
public async Task SendMessageAsync_SettingsMatchDefaults_PassesNullPatch()
{
    await streamingService.SendMessageAsync(topic, "hello");

    fakeMessaging.LastConfigPatch.ShouldBeNull();
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests/Unit --nologo -v q --filter FullyQualifiedName~StreamingService`
Expected: compile errors (no `configPatch` parameter, ctor mismatch).

- [ ] **Step 3: Implement**

`IChatMessagingService` — extend the two signatures as in Interfaces above. `ChatMessagingService`:

```csharp
        var stream = hubConnection.StreamAsync<ChatStreamMessage>(
            "SendMessage", topicId, message, correlationId, configPatch);
```

```csharp
        return await hubConnection.InvokeAsync<bool>(
            "EnqueueMessage", topicId, message, correlationId, configPatch);
```

`StreamingService` — add `AgentSettingsStore agentSettingsStore` to the primary constructor, and:

```csharp
    private AgentConfigPatch? GetConfigPatch(StoredTopic topic) =>
        AgentSettingsSelectors.GetConfigPatch(
            agentSettingsStore.State, topicsStore.State.Agents, topic.AgentId);
```

Pass `GetConfigPatch(topic)` in `SendMessageAsync`'s enqueue call, and thread it through `StartNewStream` → `StreamResponseAsync` → `messagingService.SendMessageAsync(topic.TopicId, message, correlationId, configPatch)` (add the parameter to those two private/public methods; `ResumeStreamAsync` is untouched — resuming replays an existing turn).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test Tests/Unit --nologo -v q`
Expected: PASS (fix any effect/service test constructions broken by the new ctor param by passing a fresh `AgentSettingsStore`).

- [ ] **Step 5: Commit**

```bash
git add WebChat.Client Tests/Unit
git commit -m "feat(webchat): send config patch with outgoing messages"
```

---

### Task 11: Settings UI

**Files:**
- Create: `WebChat.Client/Components/AgentConfigMenu.razor`
- Modify: `WebChat.Client/Components/TopicList.razor` (render `<AgentConfigMenu />` directly after the `<AgentSelector .../>` usage)
- CSS: reuse existing dropdown/select styling; add minimal rules to the stylesheet that styles `TopicList` (match how sibling components' styles are organized)

**Interfaces:**
- Consumes: `TopicsStore` (`Agents`, `SelectedAgentId`), `AgentSettingsStore`, `Dispatcher`, actions from Task 8.

No new unit test: the component contains markup only; all logic (selectors, reducers, sanitize, patch computation) is already covered by Tasks 8-10. Verify visually in Step 3.

- [ ] **Step 1: Implement the component**

```razor
@using Domain.DTOs.Channel
@using WebChat.Client.State
@using WebChat.Client.State.AgentSettings
@using WebChat.Client.State.Topics
@implements IDisposable
@inject TopicsStore TopicsStore
@inject AgentSettingsStore SettingsStore
@inject Dispatcher Dispatcher

@if (SelectedAgent is { PatchableModels.Count: > 0 } agent)
{
    <div class="agent-config">
        <select class="agent-config-select" title="Model"
                value="@CurrentModel(agent)" @onchange="e => SetModel(agent, e)">
            @foreach (var model in agent.PatchableModels!)
            {
                <option value="@model.Id">@model.Name</option>
            }
        </select>
        <select class="agent-config-select" title="Reasoning effort"
                value="@CurrentEffort(agent)" @onchange="e => SetEffort(agent, e)">
            @foreach (var effort in agent.PatchableReasoningEfforts ?? AgentConfigPatch.SupportedEfforts)
            {
                <option value="@effort">@effort</option>
            }
        </select>
    </div>
}

@code {
    private IDisposable? _topicsSub;
    private IDisposable? _settingsSub;

    private AgentCatalogEntry? SelectedAgent =>
        TopicsStore.State.Agents.FirstOrDefault(a => a.Id == TopicsStore.State.SelectedAgentId);

    protected override void OnInitialized()
    {
        _topicsSub = TopicsStore.StateObservable.Subscribe(_ => InvokeAsync(StateHasChanged));
        _settingsSub = SettingsStore.StateObservable.Subscribe(_ => InvokeAsync(StateHasChanged));
    }

    private string? CurrentModel(AgentCatalogEntry agent) =>
        SettingsStore.State.ByAgent.GetValueOrDefault(agent.Id)?.Model ?? agent.DefaultModel;

    private string? CurrentEffort(AgentCatalogEntry agent) =>
        SettingsStore.State.ByAgent.GetValueOrDefault(agent.Id)?.ReasoningEffort ?? agent.DefaultReasoningEffort;

    private void SetModel(AgentCatalogEntry agent, ChangeEventArgs e) =>
        Dispatcher.Dispatch(new SetAgentModel(agent.Id, e.Value?.ToString()));

    private void SetEffort(AgentCatalogEntry agent, ChangeEventArgs e) =>
        Dispatcher.Dispatch(new SetAgentReasoningEffort(agent.Id, e.Value?.ToString()));

    public void Dispose()
    {
        _topicsSub?.Dispose();
        _settingsSub?.Dispose();
    }
}
```

Adjust the `Dispatcher`/`IDispatcher` injection to whatever sibling components inject; if `PatchableModels` on the entry is null for some agent, the whole control hides (the `is { PatchableModels.Count: > 0 }` guard).

- [ ] **Step 2: Build**

Run: `dotnet build Ziggurat.sln --nologo -v q`
Expected: no errors.

- [ ] **Step 3: Verify in the running stack.** Follow the `launch-stack` skill to bring the compose stack up, open WebChat, and check: the two dropdowns render next to the agent selector, initial values match the agent's configured model/effort, a change survives a page reload (local storage), and a message sent with GLM 5.2 selected reaches OpenRouter with that model (check the agent logs / dashboard token-usage model field).

- [ ] **Step 4: Commit**

```bash
git add WebChat.Client
git commit -m "feat(webchat): model and reasoning-effort settings UI"
```

---

### Task 12: Docs and final verification

**Files:**
- Modify: `CLAUDE.md` (Channel Architecture section — one line noting `ChannelMessageNotification.ConfigPatch` and that only SignalR populates it)

- [ ] **Step 1: Add the doc line** to the Channel Architecture bullet list:

```markdown
- `ChannelMessageNotification.ConfigPatch` (`AgentConfigPatch`: model + reasoning effort) lets a channel override agent config per message; only the SignalR channel populates it. Whitelist: `patchableModels` in `Agent/appsettings.json`, surfaced to clients through the widened `AgentCatalogEntry`.
```

- [ ] **Step 2: Full verification**

Run: `dotnet build Ziggurat.sln --nologo -v q && dotnet test Tests/Unit --nologo -v q`
Expected: build clean, all unit tests pass. If Docker services are available, also run `dotnet test Tests/Integration --nologo -v q --filter FullyQualifiedName~McpAgentReasoningTests`.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md
git commit -m "docs: document per-message ConfigPatch in channel architecture"
```
