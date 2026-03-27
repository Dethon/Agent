# Domain Feature Prompts Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable domain tool features to contribute system prompt segments, then add a SubAgentPrompt that makes agents proactively delegate work to subagents.

**Architecture:** Extend `IDomainToolFeature` with a `Prompt` property (default `null`), add `GetPromptsForFeatures()` to `IDomainToolRegistry`, pass collected prompts through `MultiAgentFactory` → `McpAgent` → `CreateRunOptions`. Create `SubAgentPrompt` with delegation guidance.

**Tech Stack:** .NET 10, Moq, Shouldly, xUnit

---

### Task 1: Add Prompt Property to IDomainToolFeature

**Files:**
- Modify: `Domain/Contracts/IDomainToolFeature.cs`

- [ ] **Step 1: Add `Prompt` property with default interface implementation**

```csharp
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IDomainToolFeature
{
    string FeatureName { get; }
    string? Prompt => null;
    IEnumerable<AIFunction> GetTools(FeatureConfig config);
}
```

- [ ] **Step 2: Verify build succeeds**

Run: `dotnet build Agent.sln --no-restore -v q`
Expected: Build succeeded, 0 errors. Existing features don't break because `Prompt` defaults to `null`.

- [ ] **Step 3: Commit**

```bash
git add Domain/Contracts/IDomainToolFeature.cs
git commit -m "feat: add Prompt property to IDomainToolFeature with default null"
```

---

### Task 2: Add GetPromptsForFeatures to Registry (RED)

**Files:**
- Create: `Tests/Unit/Infrastructure/DomainToolRegistryTests.cs`

- [ ] **Step 1: Write failing tests for `GetPromptsForFeatures`**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public sealed class DomainToolRegistryTests
{
    [Fact]
    public void GetPromptsForFeatures_EnabledFeatureWithPrompt_ReturnsPrompt()
    {
        var feature = new Mock<IDomainToolFeature>();
        feature.Setup(f => f.FeatureName).Returns("subagents");
        feature.Setup(f => f.Prompt).Returns("Use subagents proactively.");
        feature.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([feature.Object]);

        var prompts = registry.GetPromptsForFeatures(["subagents"]).ToList();

        prompts.ShouldBe(["Use subagents proactively."]);
    }

    [Fact]
    public void GetPromptsForFeatures_FeatureWithNullPrompt_ReturnsEmpty()
    {
        var feature = new Mock<IDomainToolFeature>();
        feature.Setup(f => f.FeatureName).Returns("scheduling");
        feature.Setup(f => f.Prompt).Returns((string?)null);
        feature.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([feature.Object]);

        var prompts = registry.GetPromptsForFeatures(["scheduling"]).ToList();

        prompts.ShouldBeEmpty();
    }

    [Fact]
    public void GetPromptsForFeatures_DisabledFeature_ReturnsEmpty()
    {
        var feature = new Mock<IDomainToolFeature>();
        feature.Setup(f => f.FeatureName).Returns("subagents");
        feature.Setup(f => f.Prompt).Returns("Use subagents proactively.");
        feature.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([feature.Object]);

        var prompts = registry.GetPromptsForFeatures(["scheduling"]).ToList();

        prompts.ShouldBeEmpty();
    }

    [Fact]
    public void GetPromptsForFeatures_MultipleFeatures_ReturnsOnlyNonNullPrompts()
    {
        var withPrompt = new Mock<IDomainToolFeature>();
        withPrompt.Setup(f => f.FeatureName).Returns("subagents");
        withPrompt.Setup(f => f.Prompt).Returns("Delegate work.");
        withPrompt.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var withoutPrompt = new Mock<IDomainToolFeature>();
        withoutPrompt.Setup(f => f.FeatureName).Returns("scheduling");
        withoutPrompt.Setup(f => f.Prompt).Returns((string?)null);
        withoutPrompt.Setup(f => f.GetTools(It.IsAny<FeatureConfig>())).Returns([]);

        var registry = new DomainToolRegistry([withPrompt.Object, withoutPrompt.Object]);

        var prompts = registry.GetPromptsForFeatures(["subagents", "scheduling"]).ToList();

        prompts.ShouldBe(["Delegate work."]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test Tests --filter "FullyQualifiedName~DomainToolRegistryTests" --no-restore -v q`
Expected: FAIL — `GetPromptsForFeatures` does not exist on `DomainToolRegistry`.

---

### Task 3: Add GetPromptsForFeatures to Registry (GREEN)

**Files:**
- Modify: `Domain/Contracts/IDomainToolRegistry.cs`
- Modify: `Infrastructure/Agents/DomainToolRegistry.cs`

- [ ] **Step 1: Add method to `IDomainToolRegistry` interface**

```csharp
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Domain.Contracts;

public interface IDomainToolRegistry
{
    IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures, FeatureConfig config);
    IEnumerable<string> GetPromptsForFeatures(IEnumerable<string> enabledFeatures);
}
```

- [ ] **Step 2: Implement in `DomainToolRegistry`**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Microsoft.Extensions.AI;

namespace Infrastructure.Agents;

public class DomainToolRegistry(IEnumerable<IDomainToolFeature> features) : IDomainToolRegistry
{
    private readonly Dictionary<string, IDomainToolFeature> _features =
        features.ToDictionary(f => f.FeatureName, StringComparer.OrdinalIgnoreCase);

    public IEnumerable<AIFunction> GetToolsForFeatures(IEnumerable<string> enabledFeatures, FeatureConfig config)
    {
        return enabledFeatures
            .Where(name => _features.ContainsKey(name))
            .SelectMany(name => _features[name].GetTools(config));
    }

    public IEnumerable<string> GetPromptsForFeatures(IEnumerable<string> enabledFeatures)
    {
        return enabledFeatures
            .Where(name => _features.ContainsKey(name))
            .Select(name => _features[name].Prompt)
            .Where(prompt => prompt is not null)!;
    }
}
```

- [ ] **Step 3: Fix `MultiAgentFactoryTests` mock setup**

In `Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs`, add the missing mock setup after the existing `GetToolsForFeatures` setup (around line 43):

```csharp
        domainToolRegistry
            .Setup(r => r.GetPromptsForFeatures(It.IsAny<IEnumerable<string>>()))
            .Returns(Enumerable.Empty<string>());
```

- [ ] **Step 4: Run all tests to verify they pass**

Run: `dotnet test Tests --filter "FullyQualifiedName~DomainToolRegistryTests or FullyQualifiedName~MultiAgentFactoryTests" --no-restore -v q`
Expected: All PASS.

- [ ] **Step 5: Commit**

```bash
git add Domain/Contracts/IDomainToolRegistry.cs Infrastructure/Agents/DomainToolRegistry.cs Tests/Unit/Infrastructure/DomainToolRegistryTests.cs Tests/Unit/Infrastructure/MultiAgentFactoryTests.cs
git commit -m "feat: add GetPromptsForFeatures to domain tool registry"
```

---

### Task 4: Create SubAgentPrompt

**Files:**
- Create: `Domain/Prompts/SubAgentPrompt.cs`

- [ ] **Step 1: Create `SubAgentPrompt.cs`**

```csharp
namespace Domain.Prompts;

public static class SubAgentPrompt
{
    public const string SystemPrompt =
        """
        ## Subagent Delegation

        You have access to subagents — lightweight workers that run tasks independently with their own
        fresh context. Use them proactively to improve response quality and speed.

        ### When to Delegate

        - **Parallel tasks**: When a request involves multiple independent parts (e.g., "search for X
          and also look up Y"), spawn subagents for each part concurrently instead of doing them
          sequentially.
        - **Heavy operations**: Delegate research, web searches, multi-step data gathering, or any
          task requiring many tool calls. This keeps you responsive and lets the subagent focus on
          the work.
        - **Exploration**: When you need to investigate multiple options or approaches, send subagents
          to explore different paths simultaneously.

        ### When NOT to Delegate

        - Simple, single-tool-call tasks — faster to do yourself.
        - Tasks that require conversation context the subagent won't have.
        - Follow-up questions or clarifications with the user.

        ### How to Delegate Effectively

        - **Self-contained prompts**: Subagents have NO conversation history. Include ALL necessary
          context, URLs, names, and requirements in the prompt.
        - **Clear success criteria**: Tell the subagent what a good result looks like.
        - **Synthesize results**: After subagents complete, combine their outputs into a coherent
          response for the user. Don't just relay raw results.
        """;
}
```

- [ ] **Step 2: Verify build succeeds**

Run: `dotnet build Agent.sln --no-restore -v q`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Domain/Prompts/SubAgentPrompt.cs
git commit -m "feat: add SubAgentPrompt with delegation guidance"
```

---

### Task 5: Wire SubAgentToolFeature to Return Prompt (RED)

**Files:**
- Create: `Tests/Unit/Domain/SubAgentToolFeatureTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
using Domain.DTOs;
using Domain.Prompts;
using Domain.Tools.SubAgents;
using Shouldly;

namespace Tests.Unit.Domain;

public sealed class SubAgentToolFeatureTests
{
    [Fact]
    public void Prompt_ReturnsSubAgentSystemPrompt()
    {
        var registryOptions = new SubAgentRegistryOptions { SubAgents = [] };
        var feature = new SubAgentToolFeature(registryOptions);

        feature.Prompt.ShouldBe(SubAgentPrompt.SystemPrompt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests --filter "FullyQualifiedName~SubAgentToolFeatureTests" --no-restore -v q`
Expected: FAIL — `Prompt` returns `null` (default interface implementation).

---

### Task 6: Wire SubAgentToolFeature to Return Prompt (GREEN)

**Files:**
- Modify: `Domain/Tools/SubAgents/SubAgentToolFeature.cs`

- [ ] **Step 1: Add `Prompt` property to `SubAgentToolFeature`**

```csharp
using Domain.Contracts;
using Domain.DTOs;
using Domain.Prompts;
using Microsoft.Extensions.AI;

namespace Domain.Tools.SubAgents;

public class SubAgentToolFeature(
    SubAgentRegistryOptions registryOptions) : IDomainToolFeature
{
    private const string Feature = "subagents";

    public string FeatureName => Feature;

    public string? Prompt => SubAgentPrompt.SystemPrompt;

    public IEnumerable<AIFunction> GetTools(FeatureConfig config)
    {
        var runTool = new SubAgentRunTool(registryOptions, config);
        yield return AIFunctionFactory.Create(
            runTool.RunAsync,
            name: $"domain:{Feature}:{SubAgentRunTool.Name}",
            description: runTool.Description);
    }
}
```

- [ ] **Step 2: Run test to verify it passes**

Run: `dotnet test Tests --filter "FullyQualifiedName~SubAgentToolFeatureTests" --no-restore -v q`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git add Domain/Tools/SubAgents/SubAgentToolFeature.cs Tests/Unit/Domain/SubAgentToolFeatureTests.cs
git commit -m "feat: wire SubAgentToolFeature to return SubAgentPrompt"
```

---

### Task 7: Pass Domain Prompts Through McpAgent (RED)

**Files:**
- Modify: `Tests/Unit/Infrastructure/McpAgentDeserializationTests.cs`

This task extends McpAgent's constructor to accept domain prompts. We test that existing construction still works after adding the new parameter.

- [ ] **Step 1: Check `McpAgentDeserializationTests` constructor calls**

Read `Tests/Unit/Infrastructure/McpAgentDeserializationTests.cs` to see how `McpAgent` is constructed.
The constructor call at line 16-22 is:

```csharp
_agent = new McpAgent(
    [],
    chatClient.Object,
    "test-agent",
    "",
    stateStore.Object,
    "test-user");
```

This uses the existing signature with optional params. Adding `domainPrompts` as a new optional parameter after `domainTools` won't break this call.

- [ ] **Step 2: No test changes needed yet — the new parameter is optional**

The RED step here is verifying the prompt is actually included in the instructions. Since `CreateRunOptions` is private and invoked during `RunAsync` (which requires MCP server connections), we verify behavior through integration. Instead, we'll verify the wiring in Task 8 via the `MultiAgentFactory` and `DomainToolRegistry` integration.

Proceed to GREEN.

---

### Task 8: Pass Domain Prompts Through McpAgent (GREEN)

**Files:**
- Modify: `Infrastructure/Agents/McpAgent.cs`
- Modify: `Infrastructure/Agents/MultiAgentFactory.cs`

- [ ] **Step 1: Add `domainPrompts` parameter to `McpAgent` constructor**

In `Infrastructure/Agents/McpAgent.cs`, add the new field and constructor parameter. Replace the constructor (lines 33-67) with:

```csharp
    private readonly IReadOnlyList<string> _domainPrompts;

    // ... (existing fields stay)

    public McpAgent(
        string[] endpoints,
        IChatClient chatClient,
        string name,
        string description,
        IThreadStateStore stateStore,
        string userId,
        string? customInstructions = null,
        IReadOnlyList<AIFunction>? domainTools = null,
        IReadOnlyList<string>? domainPrompts = null,
        bool enableResourceSubscriptions = true)
    {
        _endpoints = endpoints;
        _name = name;
        _description = description;
        _userId = userId;
        _customInstructions = customInstructions;
        _domainTools = domainTools ?? [];
        _domainPrompts = domainPrompts ?? [];
        _enableResourceSubscriptions = enableResourceSubscriptions;
```

The rest of the constructor body stays the same.

- [ ] **Step 2: Update `CreateRunOptions` to include domain prompts**

In `Infrastructure/Agents/McpAgent.cs`, replace `CreateRunOptions` (lines 212-227) with:

```csharp
    private ChatClientAgentRunOptions CreateRunOptions(ThreadSession session)
    {
        var prompts = session.ClientManager.Prompts
            .Concat(_domainPrompts)
            .Prepend(BasePrompt.Instructions);

        if (!string.IsNullOrEmpty(_customInstructions))
        {
            prompts = prompts.Prepend(_customInstructions);
        }

        return new ChatClientAgentRunOptions(new ChatOptions
        {
            Tools = [.. session.Tools],
            Instructions = string.Join("\n\n", prompts)
        });
    }
```

- [ ] **Step 3: Update `MultiAgentFactory.CreateFromDefinition` to pass domain prompts**

In `Infrastructure/Agents/MultiAgentFactory.cs`, update `CreateFromDefinition` (lines 132-158). After the `domainTools` line (line 147), add:

```csharp
        var domainPrompts = domainToolRegistry
            .GetPromptsForFeatures(definition.EnabledFeatures)
            .ToList();
```

Then update the `McpAgent` constructor call to include `domainPrompts`:

```csharp
        return new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            name,
            definition.Description ?? "",
            stateStore,
            userId,
            definition.CustomInstructions,
            domainTools,
            domainPrompts);
```

- [ ] **Step 4: Update `MultiAgentFactory.CreateSubAgent` similarly**

In `Infrastructure/Agents/MultiAgentFactory.cs`, update `CreateSubAgent` (lines 93-130). After the `domainTools` line (line 118), add:

```csharp
        var domainPrompts = domainToolRegistry
            .GetPromptsForFeatures(enabledFeatures)
            .ToList();
```

Then update the `McpAgent` constructor call:

```csharp
        return new McpAgent(
            definition.McpServerEndpoints,
            effectiveClient,
            $"subagent-{definition.Id}",
            definition.Description ?? "",
            new NullThreadStateStore(),
            userId,
            definition.CustomInstructions,
            domainTools,
            domainPrompts,
            enableResourceSubscriptions: false);
```

- [ ] **Step 5: Run all unit tests to verify nothing breaks**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" --no-restore -v q`
Expected: All PASS. The new `domainPrompts` parameter is optional so existing constructor calls still work.

- [ ] **Step 6: Commit**

```bash
git add Infrastructure/Agents/McpAgent.cs Infrastructure/Agents/MultiAgentFactory.cs
git commit -m "feat: pass domain feature prompts through McpAgent to system instructions"
```

---

### Task 9: Final Verification

**Files:** None (verification only)

- [ ] **Step 1: Run full unit test suite**

Run: `dotnet test Tests/Tests.csproj --filter "Category!=E2E" --no-restore -v q`
Expected: All PASS.

- [ ] **Step 2: Build entire solution**

Run: `dotnet build Agent.sln --no-restore -v q`
Expected: Build succeeded, 0 errors, 0 warnings.
