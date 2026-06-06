# Expose Voice Satellites in Mycroft's System Prompt — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the `mycroft` voice agent a system-prompt fragment listing the available voice satellites (id + room), sourced live from the voice channel's `SatelliteRegistry`.

**Architecture:** Mirror the existing scheduling/printing MCP-prompt pattern. A static `VoicePrompt` (Domain) composes the text; an `[McpServerPromptType]` `VoiceSystemPrompt` (McpChannelVoice) renders it from `SatelliteRegistry` and is registered via `.WithPrompts<>()`. Adding `mcp-channel-voice` to Mycroft's `mcpServerEndpoints` makes `McpClientManager.LoadPrompts()` pull the fragment; `ThreadSession` already strips the voice channel's protocol tools, so only the prompt is exposed.

**Tech Stack:** .NET 10, C# 14, xUnit + Shouldly, ModelContextProtocol server SDK.

**Spec:** `docs/superpowers/specs/2026-06-06-voice-satellites-system-prompt-design.md`

---

## File Structure

- **Create** `Domain/Prompts/VoicePrompt.cs` — static prompt: `Name`, `Description`, `Build(satellites)`. Pure text composition, no dependencies. (Mirrors `Domain/Prompts/SchedulingPrompt.cs`.)
- **Create** `McpChannelVoice/McpPrompts/VoiceSystemPrompt.cs` — `[McpServerPromptType]` class injecting `SatelliteRegistry`, returns `VoicePrompt.Build(...)`. (Mirrors `McpServerScheduling/McpPrompts/McpSystemPrompt.cs`.)
- **Create** `Tests/Unit/Domain/Prompts/VoicePromptTests.cs` — unit tests for `VoicePrompt.Build`.
- **Create** `Tests/Unit/McpChannelVoice/VoiceSystemPromptTests.cs` — unit tests for `VoiceSystemPrompt.GetVoicePrompt`.
- **Modify** `McpChannelVoice/Modules/ConfigModule.cs` — add `using McpChannelVoice.McpPrompts;` and `.WithPrompts<VoiceSystemPrompt>()` to the `.AddMcpServer()` chain.
- **Modify** `Agent/appsettings.json` — add `"http://mcp-channel-voice:8080/mcp"` to the `mycroft` agent's `mcpServerEndpoints`.

**Conventions (enforce in every file):**
- **No trailing newline** in any `.cs` file (including tests).
- Prefer LINQ over loops (`.claude/rules/dotnet-style.md`).
- `ImplicitUsings` is enabled — `System.Linq`, `System.Collections.Generic` need no `using`.

**Note on the test signal:** The repo has ~148 pre-existing non-E2E test failures in this WSL env (Docker-unavailable). **Always filter to the specific test class** so RED/GREEN is unambiguous; never judge by the full-suite count.

---

### Task 1: `VoicePrompt` (Domain text composition)

**Files:**
- Test: `Tests/Unit/Domain/Prompts/VoicePromptTests.cs`
- Create: `Domain/Prompts/VoicePrompt.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/Domain/Prompts/VoicePromptTests.cs` (note: file must end with no trailing newline after the final `}`):

```csharp
using Domain.Prompts;
using Shouldly;

namespace Tests.Unit.Domain.Prompts;

public class VoicePromptTests
{
    [Fact]
    public void Build_WithSatellites_ListsIdAndRoomInOrder()
    {
        var result = VoicePrompt.Build(
        [
            ("fran-office-01", "Fran's office"),
            ("laura-office-01", "Laura's office")
        ]);

        result.ShouldBe(
            "## Voice satellites\n\n" +
            "These are the voice satellites you can be heard on — the spoken devices placed around the home. Each entry is a stable satellite id and the room it's in:\n\n" +
            "- fran-office-01 — Fran's office\n" +
            "- laura-office-01 — Laura's office\n\n" +
            "Each incoming message tells you which satellite and room it came from, so you can tailor answers to where the person is.");
    }

    [Fact]
    public void Build_NoSatellites_ReturnsEmpty()
    {
        VoicePrompt.Build([]).ShouldBe(string.Empty);
    }

    [Fact]
    public void Build_SingleSatellite_RendersOneBullet()
    {
        var result = VoicePrompt.Build([("office-01", "Office")]);

        result.ShouldContain("## Voice satellites");
        result.ShouldContain("- office-01 — Office");
    }
}
```

> The dashes between id and room, and in the intro line, are em dashes (`—`, U+2014). Copy them exactly.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoicePromptTests"`
Expected: **FAIL** — build error `CS0103: The name 'VoicePrompt' does not exist in the current context` (the class isn't created yet). Capture this output before proceeding.

- [ ] **Step 3: Write the minimal implementation**

Create `Domain/Prompts/VoicePrompt.cs` (no trailing newline after the final `}`):

```csharp
namespace Domain.Prompts;

public static class VoicePrompt
{
    public const string Name = "voice_prompt";

    public const string Description =
        "Lists the available voice satellites (id + room) the agent can be heard on";

    public static string Build(IReadOnlyList<(string Id, string Room)> satellites)
    {
        if (satellites.Count == 0)
        {
            return string.Empty;
        }

        string[] sections =
        [
            "## Voice satellites",
            "",
            "These are the voice satellites you can be heard on — the spoken devices placed around the home. Each entry is a stable satellite id and the room it's in:",
            "",
            .. satellites.Select(s => $"- {s.Id} — {s.Room}"),
            "",
            "Each incoming message tells you which satellite and room it came from, so you can tailor answers to where the person is."
        ];

        return string.Join("\n", sections);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoicePromptTests"`
Expected: **PASS** — 3 passed.

- [ ] **Step 5: Commit**

```bash
git add Domain/Prompts/VoicePrompt.cs Tests/Unit/Domain/Prompts/VoicePromptTests.cs
git commit -m "feat(voice): add VoicePrompt satellite-list fragment"
```

---

### Task 2: `VoiceSystemPrompt` (MCP prompt from the registry)

**Files:**
- Test: `Tests/Unit/McpChannelVoice/VoiceSystemPromptTests.cs`
- Create: `McpChannelVoice/McpPrompts/VoiceSystemPrompt.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/Unit/McpChannelVoice/VoiceSystemPromptTests.cs` (no trailing newline after the final `}`):

```csharp
using Domain.Prompts;
using McpChannelVoice.McpPrompts;
using McpChannelVoice.Services;
using McpChannelVoice.Settings;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class VoiceSystemPromptTests
{
    [Fact]
    public void GetVoicePrompt_WithSatellites_IncludesHeadingIdAndRoom()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>
        {
            ["fran-office-01"] = new() { Identity = "household", Room = "Fran's office" },
            ["laura-office-01"] = new() { Identity = "household", Room = "Laura's office" }
        });

        var result = new VoiceSystemPrompt(registry).GetVoicePrompt();

        result.ShouldContain("## Voice satellites");
        result.ShouldContain("- fran-office-01 — Fran's office");
        result.ShouldContain("- laura-office-01 — Laura's office");
    }

    [Fact]
    public void GetVoicePrompt_NoSatellites_ReturnsEmpty()
    {
        var registry = new SatelliteRegistry(new Dictionary<string, SatelliteConfig>());

        new VoiceSystemPrompt(registry).GetVoicePrompt().ShouldBe(string.Empty);
    }
}
```

> Equality-by-`ShouldContain` (not exact whole-string match) so the test does not depend on `Dictionary` key-enumeration order — consistent with `SatelliteRegistryTests`, which uses `ignoreOrder` for `GetAllIds`.

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSystemPromptTests"`
Expected: **FAIL** — build error `CS0246: The type or namespace name 'VoiceSystemPrompt' could not be found` / `McpChannelVoice.McpPrompts` does not exist. Capture before proceeding.

- [ ] **Step 3: Write the minimal implementation**

Create `McpChannelVoice/McpPrompts/VoiceSystemPrompt.cs` (no trailing newline after the final `}`):

```csharp
using System.ComponentModel;
using Domain.Prompts;
using McpChannelVoice.Services;
using ModelContextProtocol.Server;

namespace McpChannelVoice.McpPrompts;

[McpServerPromptType]
public class VoiceSystemPrompt(SatelliteRegistry registry)
{
    [McpServerPrompt(Name = VoicePrompt.Name)]
    [Description(VoicePrompt.Description)]
    public string GetVoicePrompt()
    {
        var satellites = registry.GetAllIds()
            .Select(id => (Id: id, Config: registry.GetById(id)))
            .Where(x => x.Config is not null)
            .Select(x => (x.Id, x.Config!.Room))
            .ToList();

        return VoicePrompt.Build(satellites);
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoiceSystemPromptTests"`
Expected: **PASS** — 2 passed.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/McpPrompts/VoiceSystemPrompt.cs Tests/Unit/McpChannelVoice/VoiceSystemPromptTests.cs
git commit -m "feat(voice): render voice_prompt from SatelliteRegistry"
```

---

### Task 3: Wire it up (register prompt + connect Mycroft)

This task has no new unit test — it is DI/config wiring, verified by build. (The scheduling/printing prompts are wired the same way, without a dedicated registration test; true end-to-end verification is running the stack, which is out of scope here.)

**Files:**
- Modify: `McpChannelVoice/Modules/ConfigModule.cs` (add using at top; add `.WithPrompts<VoiceSystemPrompt>()` after line 121 `.WithTools<CreateConversationTool>()`)
- Modify: `Agent/appsettings.json` (Mycroft's `mcpServerEndpoints`, after line 97 `"http://mcp-printer:8080/mcp"`)

- [ ] **Step 1: Register the prompt in the voice MCP server**

In `McpChannelVoice/Modules/ConfigModule.cs`, add this `using` to the top import block (after the existing `using McpChannelVoice.McpTools;` on line 4):

```csharp
using McpChannelVoice.McpPrompts;
```

Then, in the `.AddMcpServer()` chain, add `.WithPrompts<VoiceSystemPrompt>()` immediately after `.WithTools<CreateConversationTool>()`. The chain becomes:

```csharp
            .WithTools<SendReplyTool>()
            .WithTools<RequestApprovalTool>()
            .WithTools<RegisterAgentsTool>()
            .WithTools<CreateConversationTool>()
            .WithPrompts<VoiceSystemPrompt>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
```

(`SatelliteRegistry` is already registered as a singleton in this module — `ConfigModule.cs:38` — so `VoiceSystemPrompt`'s constructor dependency resolves.)

- [ ] **Step 2: Connect Mycroft to the voice channel as a prompt/tool server**

In `Agent/appsettings.json`, add the voice endpoint to the `mycroft` agent's `mcpServerEndpoints` array. The array (currently ending at `"http://mcp-printer:8080/mcp"`) becomes:

```json
            "mcpServerEndpoints": [
                "http://mcp-vault:8080/mcp",
                "http://mcp-sandbox:8080/mcp",
                "http://mcp-websearch:8080/mcp",
                "http://mcp-idealista:8080/mcp",
                "http://mcp-homeassistant:8080/mcp",
                "http://mcp-scheduling:8080/mcp",
                "http://mcp-printer:8080/mcp",
                "http://mcp-channel-voice:8080/mcp"
            ],
```

No docker-compose or `.env` change is needed: the agent already `depends_on` `mcp-channel-voice` and reaches this exact URL as a channel; no new environment variable is introduced.

- [ ] **Step 3: Verify it builds and existing tests still pass**

Run: `dotnet build McpChannelVoice/McpChannelVoice.csproj`
Expected: **Build succeeded**, 0 errors.

Run: `dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~VoicePromptTests|FullyQualifiedName~VoiceSystemPromptTests|FullyQualifiedName~SatelliteRegistryTests"`
Expected: **PASS** — all (8) passed (Task 1 + Task 2 + the unchanged registry tests).

- [ ] **Step 4: Sanity-check the JSON is valid**

Run: `python3 -c "import json; json.load(open('Agent/appsettings.json'))"`
Expected: no output (valid JSON). If it errors, fix the trailing comma / bracket in the edited array.

- [ ] **Step 5: Commit**

```bash
git add McpChannelVoice/Modules/ConfigModule.cs Agent/appsettings.json
git commit -m "feat(voice): expose voice_prompt to Mycroft via mcpServerEndpoints"
```

---

## Self-Review

**Spec coverage:**
- "Voice channel exposes `voice_prompt` rendered from `SatelliteRegistry`" → Tasks 1 + 2 + 3-Step-1.
- "Add `mcp-channel-voice` to Mycroft's `mcpServerEndpoints`" → Task 3-Step-2.
- "Configured set only, fields = id + room" → `VoiceSystemPrompt` reads `GetAllIds()`/`GetById().Room`; `VoicePrompt.Build` renders `- <id> — <room>` (Tasks 1–2).
- "Empty list → fragment omitted" → `VoicePrompt.Build([]) == ""` (Task 1, Step 1 test 2) and `LoadPrompts` whitespace filter (verified in spec; behavior of existing code, no task needed).
- "No announce tool / no liveness / Mycroft-only" → explicitly out of scope; no task adds them. ✓
- "No new env vars / no docker change" → Task 3-Step-2 note. ✓

**Placeholder scan:** No TBD/TODO; every code/command step contains concrete content. ✓

**Type consistency:** `VoicePrompt.Build(IReadOnlyList<(string Id, string Room)>)` is defined in Task 1 and called identically in Task 2 (`.Select(x => (x.Id, x.Config!.Room)).ToList()` yields `(string Id, string Room)`). `VoicePrompt.Name`/`Description` are referenced by `VoiceSystemPrompt` and exist as `const` (Task 1). `SatelliteRegistry.GetAllIds()` / `GetById(string)` / `SatelliteConfig.Room` match the real signatures. ✓
