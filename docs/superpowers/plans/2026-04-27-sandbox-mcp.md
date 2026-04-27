# Sandbox MCP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build `McpServerSandbox` — an MCP server that exposes a Linux container as the `/sandbox` virtual filesystem with an additional `fs_exec` capability — and wire a parallel `VfsExec` domain tool so any filesystem MCP server can opt in.

**Architecture:** Extend `IFileSystemBackend` with `ExecAsync`. `McpFileSystemBackend` calls a new `fs_exec` MCP tool (returns the existing "tool missing" error JSON for backends that don't expose it). A new `VfsExecTool` follows the same path-resolution pattern as `VfsTextRead`. A new `McpServerSandbox` project hosts the sandbox container with bash + Python preinstalled, full network egress, and a persistent named volume mounted at `/home/sandbox_user`.

**Tech Stack:** .NET 10, ModelContextProtocol.AspNetCore 1.2.0, Process.Start for bash, xUnit + Moq + Shouldly for tests, Docker Compose for deployment.

**Spec:** `docs/superpowers/specs/2026-04-27-sandbox-mcp-design.md`.

**Conventions you must follow:**
- File-scoped namespaces, primary constructors, `record` for DTOs.
- LINQ over loops.
- No try/catch in MCP tool methods (the global `AddCallToolFilter` handles errors).
- Domain has no Infrastructure / framework dependencies.
- Tests in `Tests.Unit.*` or `Tests.Integration.*` with `Shouldly`. Test method names: `{Method}_{Scenario}_{ExpectedResult}`.
- After every triplet (RED → GREEN → REVIEW or RED → GREEN → COMMIT), commit. Commit messages should be conventional (`feat:`, `refactor:`, `test:`, `chore:`).

---

## Task 1: Add ExecAsync to IFileSystemBackend

**Files:**
- Modify: `Domain/Contracts/IFileSystemBackend.cs`

This is a contract-only change. It will break compilation of `McpFileSystemBackend` until Task 2 lands.

- [ ] **Step 1: Update interface**

Replace the entire file with:

```csharp
using System.Text.Json.Nodes;

namespace Domain.Contracts;

public interface IFileSystemBackend
{
    string FilesystemName { get; }

    Task<JsonNode> ReadAsync(string path, int? offset, int? limit, CancellationToken ct);
    Task<JsonNode> CreateAsync(string path, string content, bool overwrite, bool createDirectories, CancellationToken ct);
    Task<JsonNode> EditAsync(string path, string oldString, string newString, bool replaceAll, CancellationToken ct);
    Task<JsonNode> GlobAsync(string basePath, string pattern, string mode, CancellationToken ct);
    Task<JsonNode> SearchAsync(string query, bool regex, string? path, string? directoryPath, string? filePattern,
        int maxResults, int contextLines, string outputMode, CancellationToken ct);
    Task<JsonNode> MoveAsync(string sourcePath, string destinationPath, CancellationToken ct);
    Task<JsonNode> DeleteAsync(string path, CancellationToken ct);
    Task<JsonNode> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct);
}
```

- [ ] **Step 2: Verify build fails as expected**

Run: `dotnet build /home/dethon/repos/agent/agent.sln`
Expected: FAIL with errors about `McpFileSystemBackend` not implementing `IFileSystemBackend.ExecAsync` (and any test mocks). Move on to Task 2 — do not commit yet; we will commit Tasks 1+2 together.

---

## Task 2: Implement ExecAsync in McpFileSystemBackend

**Files:**
- Modify: `Infrastructure/Agents/Mcp/McpFileSystemBackend.cs`

- [ ] **Step 1: Add the new method**

Insert this method just after `DeleteAsync` (between line 92 and the `CallToolAsync` private helper):

```csharp
    public async Task<JsonNode> ExecAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        return await CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["filesystem"] = filesystemName,
            ["path"] = path,
            ["command"] = command,
            ["timeoutSeconds"] = timeoutSeconds
        }, ct);
    }
```

- [ ] **Step 2: Verify build passes**

Run: `dotnet build /home/dethon/repos/agent/agent.sln`
Expected: PASS (no errors). Test mocks for `IFileSystemBackend` may produce CS8767 warnings about missing implementation; we'll fix those when they actually break tests.

- [ ] **Step 3: Commit**

```bash
git -C /home/dethon/repos/agent add Domain/Contracts/IFileSystemBackend.cs Infrastructure/Agents/Mcp/McpFileSystemBackend.cs
git -C /home/dethon/repos/agent commit -m "feat: add ExecAsync to IFileSystemBackend contract"
```

---

## Task 3: VfsExecTool (TDD)

**Files:**
- Create: `Tests/Unit/Domain/Tools/FileSystem/VfsExecToolTests.cs`
- Create: `Domain/Tools/FileSystem/VfsExecTool.cs`

- [ ] **Step 1: Write the failing test file**

Create `Tests/Unit/Domain/Tools/FileSystem/VfsExecToolTests.cs`:

```csharp
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.FileSystem;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Tools.FileSystem;

public class VfsExecToolTests
{
    private readonly Mock<IVirtualFileSystemRegistry> _registry = new();
    private readonly Mock<IFileSystemBackend> _backend = new();
    private readonly VfsExecTool _tool;

    public VfsExecToolTests()
    {
        _tool = new VfsExecTool(_registry.Object);
    }

    [Fact]
    public async Task RunAsync_ResolvesPathAndCallsBackend()
    {
        var expected = new JsonObject
        {
            ["stdout"] = "hi\n",
            ["stderr"] = "",
            ["exitCode"] = 0,
            ["timedOut"] = false,
            ["truncated"] = false
        };
        _registry.Setup(r => r.Resolve("/sandbox/home/sandbox_user"))
            .Returns(new FileSystemResolution(_backend.Object, "home/sandbox_user"));
        _backend.Setup(b => b.ExecAsync("home/sandbox_user", "echo hi", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/sandbox/home/sandbox_user", "echo hi");

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_PassesTimeoutSecondsThrough()
    {
        var expected = new JsonObject { ["exitCode"] = 0 };
        _registry.Setup(r => r.Resolve("/sandbox"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.ExecAsync("", "sleep 1", 30, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await _tool.RunAsync("/sandbox", "sleep 1", timeoutSeconds: 30);

        result.ShouldBe(expected);
    }

    [Fact]
    public async Task RunAsync_RootPath_PassesEmptyRelativePath()
    {
        var expected = new JsonObject { ["exitCode"] = 0 };
        _registry.Setup(r => r.Resolve("/sandbox"))
            .Returns(new FileSystemResolution(_backend.Object, ""));
        _backend.Setup(b => b.ExecAsync("", "pwd", null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        await _tool.RunAsync("/sandbox", "pwd");

        _backend.Verify(b => b.ExecAsync("", "pwd", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RunAsync_UnknownMount_ThrowsFromRegistry()
    {
        _registry.Setup(r => r.Resolve("/unknown"))
            .Throws(new InvalidOperationException("No filesystem mounted"));

        await Should.ThrowAsync<InvalidOperationException>(
            () => _tool.RunAsync("/unknown", "echo hi"));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~VfsExecToolTests" --no-restore`
Expected: BUILD FAIL with "type or namespace name 'VfsExecTool' could not be found".

- [ ] **Step 3: Write the minimal implementation**

Create `Domain/Tools/FileSystem/VfsExecTool.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.FileSystem;

public class VfsExecTool(IVirtualFileSystemRegistry registry)
{
    public const string Key = "exec";
    public const string Name = "exec";

    public const string ToolDescription = """
        Execute a bash command on a filesystem that supports execution.
        The path argument is the working directory (CWD) for the command, expressed as a virtual path.
        On the sandbox filesystem, /sandbox uses the agent's home directory as the default CWD;
        deeper paths (e.g., /sandbox/home/sandbox_user/myproject) are used literally as the CWD.
        Commands run via `bash -lc` so login shell env (PATH, etc.) is initialised.
        Non-zero exit codes are returned as data (in `exitCode`), not as errors.
        Output is truncated at the backend's per-stream cap; check `truncated` in the result.
        Optional `timeoutSeconds` is clamped to the backend's max; on timeout the process tree is killed.
        """;

    [Description(ToolDescription)]
    public async Task<JsonNode> RunAsync(
        [Description("Virtual path used as CWD (e.g., /sandbox or /sandbox/home/sandbox_user/myproject)")]
        string path,
        [Description("Bash command line; passed to `bash -lc`")]
        string command,
        [Description("Optional timeout in seconds. Backend clamps to its max.")]
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var resolution = registry.Resolve(path);
        return await resolution.Backend.ExecAsync(resolution.RelativePath, command, timeoutSeconds, cancellationToken);
    }
}
```

- [ ] **Step 4: Run the test again to verify it passes**

Run: `dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~VfsExecToolTests" --no-restore`
Expected: PASS — 4/4 tests pass.

- [ ] **Step 5: Commit**

```bash
git -C /home/dethon/repos/agent add Domain/Tools/FileSystem/VfsExecTool.cs Tests/Unit/Domain/Tools/FileSystem/VfsExecToolTests.cs
git -C /home/dethon/repos/agent commit -m "feat: add VfsExecTool domain tool"
```

---

## Task 4: Register exec in FileSystemToolFeature (TDD)

**Files:**
- Modify: `Tests/Unit/Domain/Tools/FileSystem/FileSystemToolFeatureTests.cs`
- Modify: `Domain/Tools/FileSystem/FileSystemToolFeature.cs`

- [ ] **Step 1: Update tests to expect 8 tools**

In `Tests/Unit/Domain/Tools/FileSystem/FileSystemToolFeatureTests.cs`, replace the `GetTools_NullEnabledTools_ReturnsAllTools` test with:

```csharp
    [Fact]
    public void GetTools_NullEnabledTools_ReturnsAllTools()
    {
        var config = new FeatureConfig();
        var tools = _feature.GetTools(config).ToList();

        tools.Count.ShouldBe(8);
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_read");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_create");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_edit");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:glob_files");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:text_search");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:move");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:remove");
        tools.Select(t => t.Name).ShouldContain("domain:filesystem:exec");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~FileSystemToolFeatureTests" --no-restore`
Expected: FAIL with "tools.Count: 7 should be 8" (or equivalent).

- [ ] **Step 3: Update FileSystemToolFeature**

Open `Domain/Tools/FileSystem/FileSystemToolFeature.cs`. Update the `AllToolKeys` set to include the new key, and add the registration tuple.

Replace the `AllToolKeys` field:

```csharp
    public static readonly IReadOnlySet<string> AllToolKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        VfsTextReadTool.Key, VfsTextCreateTool.Key, VfsTextEditTool.Key,
        VfsGlobFilesTool.Key, VfsTextSearchTool.Key, VfsMoveTool.Key, VfsRemoveTool.Key,
        VfsExecTool.Key
    };
```

Replace the `GetTools` body's tools array:

```csharp
        var tools = new (string Key, Func<AIFunction> Factory)[]
        {
            (VfsTextReadTool.Key, () => AIFunctionFactory.Create(new VfsTextReadTool(registry).RunAsync, name: $"domain:{Feature}:{VfsTextReadTool.Name}")),
            (VfsTextCreateTool.Key, () => AIFunctionFactory.Create(new VfsTextCreateTool(registry).RunAsync, name: $"domain:{Feature}:{VfsTextCreateTool.Name}")),
            (VfsTextEditTool.Key, () => AIFunctionFactory.Create(new VfsTextEditTool(registry).RunAsync, name: $"domain:{Feature}:{VfsTextEditTool.Name}")),
            (VfsGlobFilesTool.Key, () => AIFunctionFactory.Create(new VfsGlobFilesTool(registry).RunAsync, name: $"domain:{Feature}:{VfsGlobFilesTool.Name}")),
            (VfsTextSearchTool.Key, () => AIFunctionFactory.Create(new VfsTextSearchTool(registry).RunAsync, name: $"domain:{Feature}:{VfsTextSearchTool.Name}")),
            (VfsMoveTool.Key, () => AIFunctionFactory.Create(new VfsMoveTool(registry).RunAsync, name: $"domain:{Feature}:{VfsMoveTool.Name}")),
            (VfsRemoveTool.Key, () => AIFunctionFactory.Create(new VfsRemoveTool(registry).RunAsync, name: $"domain:{Feature}:{VfsRemoveTool.Name}")),
            (VfsExecTool.Key, () => AIFunctionFactory.Create(new VfsExecTool(registry).RunAsync, name: $"domain:{Feature}:{VfsExecTool.Name}")),
        };
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~FileSystemToolFeatureTests" --no-restore`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git -C /home/dethon/repos/agent add Domain/Tools/FileSystem/FileSystemToolFeature.cs Tests/Unit/Domain/Tools/FileSystem/FileSystemToolFeatureTests.cs
git -C /home/dethon/repos/agent commit -m "feat: register VfsExecTool in FileSystemToolFeature"
```

---

## Task 5: Filter raw fs_exec in ThreadSession

**Files:**
- Modify: `Infrastructure/Agents/ThreadSession.cs:80-83`

- [ ] **Step 1: Add fs_exec to the filter list**

In `Infrastructure/Agents/ThreadSession.cs`, replace the `_fileSystemMcpToolNames` HashSet:

```csharp
    private static readonly HashSet<string> _fileSystemMcpToolNames =
    [
        "fs_read", "fs_create", "fs_edit", "fs_glob", "fs_search", "fs_move", "fs_delete", "fs_exec"
    ];
```

- [ ] **Step 2: Verify build**

Run: `dotnet build /home/dethon/repos/agent/agent.sln`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git -C /home/dethon/repos/agent add Infrastructure/Agents/ThreadSession.cs
git -C /home/dethon/repos/agent commit -m "feat: filter raw fs_exec MCP tool when domain tool active"
```

---

## Task 6: Domain SandboxPrompt

**Files:**
- Create: `Domain/Prompts/SandboxPrompt.cs`

This is just static prompt text, so no test.

- [ ] **Step 1: Create the prompt**

Create `Domain/Prompts/SandboxPrompt.cs`:

```csharp
namespace Domain.Prompts;

public static class SandboxPrompt
{
    public const string Prompt = """
        ## Sandbox Filesystem

        You have access to a Linux sandbox container exposed as the virtual filesystem mounted at `/sandbox`.

        ### Layout

        - `/sandbox` — the container root (`/`). Read-accessible via filesystem tools (e.g., `/sandbox/etc/os-release`).
        - `/sandbox/home/sandbox_user` — the **persistent workspace** (a Docker named volume). Files here survive container restarts. This is also the **default working directory** for `domain:filesystem:exec`.
        - `/sandbox/etc`, `/sandbox/usr`, `/sandbox/tmp`, etc. — system directories. They reset whenever the container is recreated and you typically cannot write to them (you run as the unprivileged `sandbox_user`).

        ### Capabilities

        - **File operations.** All `domain:filesystem:*` tools work as on any other filesystem. Use them to read system files, edit your project files, glob, search, move, and remove.
        - **Command execution.** `domain:filesystem:exec` runs `bash -lc <command>` inside the sandbox container. You pass a virtual path that becomes the CWD: pass `/sandbox` to use the home directory as CWD, or a deeper path like `/sandbox/home/sandbox_user/myproject` to set CWD literally. Each call is a fresh shell — environment variables and `cd` do **not** persist between calls; files do.
        - **Python.** Python 3 is preinstalled. Install extra packages with `pip install --user <package>` (user-scope; persists in your home).
        - **Network.** Full outbound network is available. You can `curl`, `git clone`, `pip install`, etc.

        ### Limits

        - Each command has a default timeout (the backend chooses; you can override via `timeoutSeconds` up to the backend max). On timeout the process tree is killed.
        - Output is truncated at a per-stream byte cap. The result `truncated` field tells you when this happens. Long-running observability should write to a file and read it.
        - Non-zero exit codes are returned as data — they do not raise errors. Inspect `exitCode`.
        - You run as a non-root user (`sandbox_user`). Writes outside `/home/sandbox_user` will typically fail with permission denied.

        ### Workflow tip

        Edit files in `/sandbox/home/sandbox_user/...` with `domain:filesystem:text_create` / `domain:filesystem:text_edit`, then run them with `domain:filesystem:exec`. The two tools operate on the same volume.
        """;
}
```

- [ ] **Step 2: Verify build**

Run: `dotnet build /home/dethon/repos/agent/agent.sln`
Expected: PASS.

- [ ] **Step 3: Commit**

```bash
git -C /home/dethon/repos/agent add Domain/Prompts/SandboxPrompt.cs
git -C /home/dethon/repos/agent commit -m "feat: add Domain SandboxPrompt with sandbox usage instructions"
```

---

## Task 7: McpServerSandbox project skeleton

**Files:**
- Create: `McpServerSandbox/McpServerSandbox.csproj`
- Create: `McpServerSandbox/Program.cs`
- Create: `McpServerSandbox/appsettings.json`
- Modify: `agent.sln`

- [ ] **Step 1: Create the csproj**

Create `McpServerSandbox/McpServerSandbox.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <DockerDefaultTargetOS>Linux</DockerDefaultTargetOS>
    <LangVersion>14</LangVersion>
  </PropertyGroup>

  <ItemGroup>
    <Content Include="..\.dockerignore">
      <Link>.dockerignore</Link>
    </Content>
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.5" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="1.2.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Infrastructure\Infrastructure.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="appsettings.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Create Program.cs**

Create `McpServerSandbox/Program.cs`:

```csharp
using McpServerSandbox.Modules;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);
var settings = builder.Configuration.GetSettings();
builder.Services.ConfigureMcp(settings);

var app = builder.Build();
app.MapMcp("/mcp");

await app.RunAsync();
```

- [ ] **Step 3: Create appsettings.json**

Create `McpServerSandbox/appsettings.json`:

```json
{
  "ContainerRoot": "/",
  "HomeDir": "/home/sandbox_user",
  "DefaultTimeoutSeconds": 60,
  "MaxTimeoutSeconds": 1800,
  "OutputCapBytes": 65536,
  "AllowedExtensions": [
    "",
    ".md", ".txt", ".json", ".yaml", ".yml", ".toml", ".ini", ".conf", ".cfg",
    ".py", ".sh", ".bash", ".zsh",
    ".js", ".jsx", ".ts", ".tsx", ".mjs", ".cjs",
    ".html", ".htm", ".css", ".scss", ".sass", ".less",
    ".csv", ".tsv", ".xml", ".sql",
    ".log", ".env", ".gitignore", ".gitattributes", ".dockerignore",
    ".c", ".h", ".cpp", ".hpp", ".cs", ".java", ".kt", ".go", ".rs", ".rb", ".php",
    ".lua", ".pl", ".swift", ".scala", ".clj",
    ".ipynb"
  ]
}
```

- [ ] **Step 4: Add the project to the solution**

Run:
```bash
dotnet sln /home/dethon/repos/agent/agent.sln add /home/dethon/repos/agent/McpServerSandbox/McpServerSandbox.csproj
```
Expected: "Project ... added to the solution."

- [ ] **Step 5: Verify the project is registered (full build will fail until Tasks 8-14 land — that's OK)**

Run: `dotnet build /home/dethon/repos/agent/McpServerSandbox/McpServerSandbox.csproj`
Expected: FAIL — `McpServerSandbox.Modules` not found, `Settings` not found. We'll fill those in.

- [ ] **Step 6: Do NOT commit yet**

Tasks 7–14 build the server piece by piece; we will commit them as one logical "skeleton + server" commit at the end of Task 14.

---

## Task 8: McpSettings record

**Files:**
- Create: `McpServerSandbox/Settings/McpSettings.cs`

- [ ] **Step 1: Create settings record**

Create `McpServerSandbox/Settings/McpSettings.cs`:

```csharp
namespace McpServerSandbox.Settings;

public record McpSettings
{
    public required string ContainerRoot { get; init; }
    public required string HomeDir { get; init; }
    public required int DefaultTimeoutSeconds { get; init; }
    public required int MaxTimeoutSeconds { get; init; }
    public required int OutputCapBytes { get; init; }
    public required string[] AllowedExtensions { get; init; }
}
```

- [ ] **Step 2: No build/commit yet**

Continue to Task 9.

---

## Task 9: BashRunner service (TDD)

**Files:**
- Create: `Tests/Unit/McpServerSandbox/BashRunnerTests.cs`
- Create: `McpServerSandbox/Services/BashRunner.cs`
- Modify: `Tests/Tests.csproj` (add ProjectReference to McpServerSandbox)

These tests run real `bash` and `python3` on the host. They will be skipped on Windows (`SkippableFact`) and run on Linux (CI / local Linux/WSL).

- [ ] **Step 1: Add project reference to Tests.csproj**

Open `Tests/Tests.csproj` and add a `<ProjectReference>` to McpServerSandbox alongside the other server references. Locate the `<ItemGroup>` containing existing `<ProjectReference>` lines (e.g., for `McpServerVault`). Add:

```xml
    <ProjectReference Include="..\McpServerSandbox\McpServerSandbox.csproj" />
```

- [ ] **Step 2: Write the failing tests**

Create `Tests/Unit/McpServerSandbox/BashRunnerTests.cs`:

```csharp
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using McpServerSandbox.Services;
using McpServerSandbox.Settings;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerSandbox;

public class BashRunnerTests
{
    private readonly McpSettings _settings = new()
    {
        ContainerRoot = "/",
        HomeDir = "/tmp",
        DefaultTimeoutSeconds = 2,
        MaxTimeoutSeconds = 3,
        OutputCapBytes = 1024,
        AllowedExtensions = []
    };

    private static void SkipIfNotLinux()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "BashRunner requires Linux");
    }

    [SkippableFact]
    public async Task RunAsync_SimpleCommand_ReturnsStdoutAndZeroExit()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("", "echo hello", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(0);
        result["stdout"]!.GetValue<string>().ShouldBe("hello\n");
        result["timedOut"]!.GetValue<bool>().ShouldBeFalse();
        result["truncated"]!.GetValue<bool>().ShouldBeFalse();
    }

    [SkippableFact]
    public async Task RunAsync_NonZeroExit_ReturnedAsData()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("", "false", null, CancellationToken.None);

        result["exitCode"]!.GetValue<int>().ShouldBe(1);
        result["timedOut"]!.GetValue<bool>().ShouldBeFalse();
    }

    [SkippableFact]
    public async Task RunAsync_EmptyPath_UsesHomeDirAsCwd()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("", "pwd", null, CancellationToken.None);

        result["stdout"]!.GetValue<string>().Trim().ShouldBe("/tmp");
        result["cwd"]!.GetValue<string>().ShouldBe("/tmp");
    }

    [SkippableFact]
    public async Task RunAsync_DotPath_UsesHomeDirAsCwd()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync(".", "pwd", null, CancellationToken.None);

        result["stdout"]!.GetValue<string>().Trim().ShouldBe("/tmp");
    }

    [SkippableFact]
    public async Task RunAsync_RelativePath_UsesContainerRootCombined()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("tmp", "pwd", null, CancellationToken.None);

        result["stdout"]!.GetValue<string>().Trim().ShouldBe("/tmp");
    }

    [SkippableFact]
    public async Task RunAsync_NonExistentCwd_ReturnsErrorJson()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("does/not/exist", "echo hi", null, CancellationToken.None);

        result["error"]!.GetValue<bool>().ShouldBeTrue();
        result["message"]!.GetValue<string>().ShouldContain("does not exist");
    }

    [SkippableFact]
    public async Task RunAsync_Timeout_KillsProcessAndReportsTimedOut()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("", "sleep 30", timeoutSeconds: 1, CancellationToken.None);

        result["timedOut"]!.GetValue<bool>().ShouldBeTrue();
        // After SIGKILL, exit code is typically 137 (128+SIGKILL=9) or -1 sentinel
    }

    [SkippableFact]
    public async Task RunAsync_OutputExceedsCap_TruncatedTrue()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        // 1024 byte cap: emit 4096 bytes
        var result = await runner.RunAsync("", "yes a | head -c 4096", null, CancellationToken.None);

        result["truncated"]!.GetValue<bool>().ShouldBeTrue();
        result["stdout"]!.GetValue<string>().Length.ShouldBeLessThanOrEqualTo(_settings.OutputCapBytes);
    }

    [SkippableFact]
    public async Task RunAsync_TimeoutExceedsMax_ClampedToMax()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        // Max is 3s. Request 999s. Then `sleep 30` should still time out (clamped to 3s).
        var result = await runner.RunAsync("", "sleep 30", timeoutSeconds: 999, CancellationToken.None);

        result["timedOut"]!.GetValue<bool>().ShouldBeTrue();
    }

    [SkippableFact]
    public async Task RunAsync_NullTimeout_UsesDefault()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        // Default is 2s. `sleep 30` should time out.
        var result = await runner.RunAsync("", "sleep 30", null, CancellationToken.None);

        result["timedOut"]!.GetValue<bool>().ShouldBeTrue();
    }

    [SkippableFact]
    public async Task RunAsync_StderrCaptured()
    {
        SkipIfNotLinux();
        var runner = new BashRunner(_settings);

        var result = await runner.RunAsync("", "echo oops 1>&2", null, CancellationToken.None);

        result["stderr"]!.GetValue<string>().ShouldBe("oops\n");
    }
}
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~BashRunnerTests" --no-restore`
Expected: BUILD FAIL — `BashRunner` type not found.

- [ ] **Step 4: Implement BashRunner**

Create `McpServerSandbox/Services/BashRunner.cs`:

```csharp
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using McpServerSandbox.Settings;

namespace McpServerSandbox.Services;

public class BashRunner(McpSettings settings)
{
    public async Task<JsonNode> RunAsync(string path, string command, int? timeoutSeconds, CancellationToken ct)
    {
        var cwd = ResolveCwd(path);
        if (!Directory.Exists(cwd))
        {
            return new JsonObject
            {
                ["error"] = true,
                ["message"] = $"Working directory '{cwd}' does not exist or is not a directory."
            };
        }

        var effectiveTimeout = TimeSpan.FromSeconds(
            Math.Clamp(timeoutSeconds ?? settings.DefaultTimeoutSeconds, 1, settings.MaxTimeoutSeconds));

        var psi = new ProcessStartInfo("bash")
        {
            ArgumentList = { "-lc", command },
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        var sw = Stopwatch.StartNew();
        process.Start();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(effectiveTimeout);

        var stdoutTask = ReadCappedAsync(process.StandardOutput, settings.OutputCapBytes, timeoutCts.Token);
        var stderrTask = ReadCappedAsync(process.StandardError, settings.OutputCapBytes, timeoutCts.Token);

        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            await process.WaitForExitAsync(CancellationToken.None);
        }

        var stdoutResult = await stdoutTask;
        var stderrResult = await stderrTask;

        return new JsonObject
        {
            ["stdout"] = stdoutResult.Text,
            ["stderr"] = stderrResult.Text,
            ["exitCode"] = timedOut ? -1 : process.ExitCode,
            ["timedOut"] = timedOut,
            ["truncated"] = stdoutResult.Truncated || stderrResult.Truncated,
            ["durationMs"] = sw.ElapsedMilliseconds,
            ["cwd"] = cwd
        };
    }

    private string ResolveCwd(string path)
    {
        if (string.IsNullOrEmpty(path) || path == ".")
        {
            return settings.HomeDir;
        }

        var combined = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(settings.ContainerRoot, path));

        return combined;
    }

    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(
        StreamReader reader, int capBytes, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var byteCount = 0;
        var truncated = false;
        var buffer = new char[4096];

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer.AsMemory(), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (read == 0)
            {
                break;
            }

            if (truncated)
            {
                continue;
            }

            var chunkBytes = Encoding.UTF8.GetByteCount(buffer, 0, read);
            if (byteCount + chunkBytes <= capBytes)
            {
                sb.Append(buffer, 0, read);
                byteCount += chunkBytes;
                continue;
            }

            var remaining = capBytes - byteCount;
            for (var i = 0; i < read && remaining > 0; i++)
            {
                var charBytes = Encoding.UTF8.GetByteCount(buffer, i, 1);
                if (charBytes > remaining)
                {
                    break;
                }
                sb.Append(buffer[i]);
                remaining -= charBytes;
                byteCount += charBytes;
            }
            truncated = true;
        }

        return (sb.ToString(), truncated);
    }
}
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~BashRunnerTests" --no-restore`
Expected: PASS — all 10 tests pass on Linux (skipped on Windows). If running on Windows/WSL, ensure WSL is the test host.

- [ ] **Step 6: Do NOT commit yet**

Continue to Task 10.

---

## Task 10: FsExecTool MCP wrapper

**Files:**
- Create: `McpServerSandbox/McpTools/FsExecTool.cs`

- [ ] **Step 1: Create the MCP wrapper**

Create `McpServerSandbox/McpTools/FsExecTool.cs`:

```csharp
using System.ComponentModel;
using Infrastructure.Utils;
using McpServerSandbox.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsExecTool(BashRunner runner)
{
    private const string Description = """
        Execute a bash command (`bash -lc <command>`) inside the sandbox container.
        The path argument is a relative path under the sandbox root that becomes the CWD;
        empty string or "." use the home directory.
        Output is truncated at the configured cap. On timeout the process tree is killed.
        Non-zero exit codes are returned in the result, not as errors.
        """;

    [McpServerTool(Name = "fs_exec")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string filesystem,
        string path,
        string command,
        int? timeoutSeconds = null,
        CancellationToken cancellationToken = default)
    {
        var result = await runner.RunAsync(path, command, timeoutSeconds, cancellationToken);
        return ToolResponse.Create(result);
    }
}
```

- [ ] **Step 2: Continue to Task 11**

---

## Task 11: Other Fs* MCP tools (Read/Create/Edit/Glob/Search/Move/Delete)

**Files:**
- Create: `McpServerSandbox/McpTools/FsReadTool.cs`
- Create: `McpServerSandbox/McpTools/FsCreateTool.cs`
- Create: `McpServerSandbox/McpTools/FsEditTool.cs`
- Create: `McpServerSandbox/McpTools/FsGlobTool.cs`
- Create: `McpServerSandbox/McpTools/FsSearchTool.cs`
- Create: `McpServerSandbox/McpTools/FsMoveTool.cs`
- Create: `McpServerSandbox/McpTools/FsDeleteTool.cs`

These mirror the McpServerVault tools but root at `ContainerRoot` (`/`) instead of `VaultPath`. Identical signatures so the agent's `Vfs*` tools route through them transparently.

- [ ] **Step 1: FsReadTool**

Create `McpServerSandbox/McpTools/FsReadTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerSandbox.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsReadTool(McpSettings settings)
    : TextReadTool(settings.ContainerRoot, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_read")]
    [Description(Description)]
    public CallToolResult McpRun(
        string filesystem,
        string path,
        int? offset = null,
        int? limit = null)
    {
        return ToolResponse.Create(Run(path, offset, limit));
    }
}
```

- [ ] **Step 2: FsCreateTool**

Create `McpServerSandbox/McpTools/FsCreateTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerSandbox.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsCreateTool(McpSettings settings)
    : TextCreateTool(settings.ContainerRoot, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_create")]
    [Description(Description)]
    public CallToolResult McpRun(
        string filesystem,
        string path,
        string content,
        bool overwrite = false,
        bool createDirectories = true)
    {
        return ToolResponse.Create(Run(path, content, overwrite, createDirectories));
    }
}
```

- [ ] **Step 3: FsEditTool**

Create `McpServerSandbox/McpTools/FsEditTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerSandbox.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsEditTool(McpSettings settings)
    : TextEditTool(settings.ContainerRoot, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_edit")]
    [Description(Description)]
    public CallToolResult McpRun(
        string filesystem,
        string path,
        string oldString,
        string newString,
        bool replaceAll = false)
    {
        return ToolResponse.Create(Run(path, oldString, newString, replaceAll));
    }
}
```

- [ ] **Step 4: FsGlobTool**

Create `McpServerSandbox/McpTools/FsGlobTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsGlobTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : GlobFilesTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_glob")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string filesystem,
        string pattern,
        string mode = "directories",
        string basePath = "",
        CancellationToken cancellationToken = default)
    {
        var globMode = mode.Equals("files", StringComparison.OrdinalIgnoreCase)
            ? GlobMode.Files
            : GlobMode.Directories;

        var effectivePattern = string.IsNullOrEmpty(basePath)
            ? pattern
            : $"{basePath.TrimEnd('/')}/{pattern}";

        return ToolResponse.Create(await Run(effectivePattern, globMode, cancellationToken));
    }
}
```

- [ ] **Step 5: FsSearchTool**

Create `McpServerSandbox/McpTools/FsSearchTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Tools.Text;
using Infrastructure.Utils;
using McpServerSandbox.Settings;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsSearchTool(McpSettings settings)
    : TextSearchTool(settings.ContainerRoot, settings.AllowedExtensions)
{
    [McpServerTool(Name = "fs_search")]
    [Description(Description)]
    public CallToolResult McpRun(
        string filesystem,
        string query,
        bool regex = false,
        string? path = null,
        string? directoryPath = null,
        string? filePattern = null,
        int maxResults = 50,
        int contextLines = 1,
        string outputMode = "content")
    {
        var searchOutputMode = outputMode.Equals("filesOnly", StringComparison.OrdinalIgnoreCase)
            ? SearchOutputMode.FilesOnly
            : SearchOutputMode.Content;

        var effectiveDirectoryPath = directoryPath ?? "/";

        return ToolResponse.Create(Run(query, regex, path, filePattern, effectiveDirectoryPath, maxResults, contextLines, searchOutputMode));
    }
}
```

- [ ] **Step 6: FsMoveTool**

Create `McpServerSandbox/McpTools/FsMoveTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsMoveTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : MoveTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_move")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string filesystem,
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await Run(sourcePath, destinationPath, cancellationToken));
    }
}
```

- [ ] **Step 7: FsDeleteTool**

Create `McpServerSandbox/McpTools/FsDeleteTool.cs`:

```csharp
using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Config;
using Domain.Tools.Files;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpTools;

[McpServerToolType]
public class FsDeleteTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : RemoveTool(client, libraryPath)
{
    [McpServerTool(Name = "fs_delete")]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        string filesystem,
        string path,
        CancellationToken cancellationToken = default)
    {
        return ToolResponse.Create(await Run(path, cancellationToken));
    }
}
```

- [ ] **Step 8: Continue to Task 12**

---

## Task 12: FileSystemResource for sandbox

**Files:**
- Create: `McpServerSandbox/McpResources/FileSystemResource.cs`

- [ ] **Step 1: Create the resource**

Create `McpServerSandbox/McpResources/FileSystemResource.cs`:

```csharp
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpResources;

[McpServerResourceType]
public class FileSystemResource
{
    [McpServerResource(
        UriTemplate = "filesystem://sandbox",
        Name = "Sandbox Filesystem",
        MimeType = "application/json")]
    [Description("Linux sandbox container filesystem with bash + Python execution")]
    public string GetSandboxInfo()
    {
        return JsonSerializer.Serialize(new
        {
            name = "sandbox",
            mountPoint = "/sandbox",
            description = "Linux sandbox: persistent /home/sandbox_user, ephemeral system dirs, full network, bash + Python via fs_exec"
        });
    }
}
```

- [ ] **Step 2: Continue to Task 13**

---

## Task 13: Sandbox MCP system prompt

**Files:**
- Create: `McpServerSandbox/McpPrompts/McpSystemPrompt.cs`

- [ ] **Step 1: Create the MCP prompt wrapper**

Create `McpServerSandbox/McpPrompts/McpSystemPrompt.cs`:

```csharp
using System.ComponentModel;
using Domain.Prompts;
using ModelContextProtocol.Server;

namespace McpServerSandbox.McpPrompts;

[McpServerPromptType]
public class McpSystemPrompt
{
    private const string Name = "sandbox_prompt";

    [McpServerPrompt(Name = Name)]
    [Description("Explains the sandbox filesystem layout, capabilities, and limits")]
    public static string GetSandboxPrompt()
    {
        return SandboxPrompt.Prompt;
    }
}
```

- [ ] **Step 2: Continue to Task 14**

---

## Task 14: ConfigModule (DI wiring)

**Files:**
- Create: `McpServerSandbox/Modules/ConfigModule.cs`

- [ ] **Step 1: Create ConfigModule**

Create `McpServerSandbox/Modules/ConfigModule.cs`:

```csharp
using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.Clients;
using Infrastructure.Utils;
using McpServerSandbox.McpPrompts;
using McpServerSandbox.McpResources;
using McpServerSandbox.McpTools;
using McpServerSandbox.Services;
using McpServerSandbox.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpServerSandbox.Modules;

public static class ConfigModule
{
    public static McpSettings GetSettings(this IConfigurationBuilder configBuilder)
    {
        var config = configBuilder
            .AddEnvironmentVariables()
            .AddUserSecrets<Program>()
            .Build();

        var settings = config.Get<McpSettings>();
        return settings ?? throw new InvalidOperationException("Settings not found");
    }

    public static IServiceCollection ConfigureMcp(this IServiceCollection services, McpSettings settings)
    {
        services
            .AddSingleton(settings)
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.ContainerRoot))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton<BashRunner>()
            .AddMcpServer()
            .WithHttpTransport()
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
                    return ToolResponse.Create(ex);
                }
            }))
            // Filesystem backend tools
            .WithTools<FsReadTool>()
            .WithTools<FsCreateTool>()
            .WithTools<FsEditTool>()
            .WithTools<FsGlobTool>()
            .WithTools<FsSearchTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsExecTool>()
            // Filesystem resource
            .WithResources<FileSystemResource>()
            // Prompts
            .WithPrompts<McpSystemPrompt>();

        return services;
    }
}
```

- [ ] **Step 2: Verify the project builds**

Run: `dotnet build /home/dethon/repos/agent/McpServerSandbox/McpServerSandbox.csproj`
Expected: PASS.

- [ ] **Step 3: Verify the full solution builds**

Run: `dotnet build /home/dethon/repos/agent/agent.sln`
Expected: PASS.

- [ ] **Step 4: Run all unit tests**

Run: `dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName!~Integration&FullyQualifiedName!~E2E" --no-restore`
Expected: PASS — including the new `BashRunnerTests` (skipped on non-Linux).

- [ ] **Step 5: Commit Tasks 7–14 together**

```bash
git -C /home/dethon/repos/agent add McpServerSandbox/ Tests/Unit/McpServerSandbox/ Tests/Tests.csproj agent.sln
git -C /home/dethon/repos/agent commit -m "feat: add McpServerSandbox MCP server with bash exec capability"
```

---

## Task 15: Dockerfile for the sandbox container

**Files:**
- Create: `McpServerSandbox/Dockerfile`

- [ ] **Step 1: Create the Dockerfile**

Create `McpServerSandbox/Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base

# Install bash, python3, pip, common CLI tooling
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
        bash \
        python3 \
        python3-pip \
        python3-venv \
        curl \
        ca-certificates \
        git \
        jq \
        unzip \
 && rm -rf /var/lib/apt/lists/*

# Create non-root sandbox_user with home at /home/sandbox_user
RUN useradd --create-home --home-dir /home/sandbox_user --shell /bin/bash sandbox_user \
 && mkdir -p /home/sandbox_user/.local/bin \
 && chown -R sandbox_user:sandbox_user /home/sandbox_user \
 && mkdir -p /.trash \
 && chown sandbox_user:sandbox_user /.trash

WORKDIR /app

FROM base-sdk:latest AS dependencies
COPY ["McpServerSandbox/McpServerSandbox.csproj", "McpServerSandbox/"]
RUN dotnet restore "McpServerSandbox/McpServerSandbox.csproj"

FROM dependencies AS publish
ARG BUILD_CONFIGURATION=Release
COPY ["McpServerSandbox/", "McpServerSandbox/"]
WORKDIR "McpServerSandbox"
RUN dotnet publish "./McpServerSandbox.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false /p:BuildProjectReferences=false --no-restore

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Switch to non-root sandbox_user; bash exec inherits this user
USER sandbox_user
ENV HOME=/home/sandbox_user
ENV PATH=/home/sandbox_user/.local/bin:$PATH

ENTRYPOINT ["dotnet", "McpServerSandbox.dll"]
```

- [ ] **Step 2: Verify the image builds (slow — first build pulls base layers)**

Run:
```bash
docker compose -f /home/dethon/repos/agent/DockerCompose/docker-compose.yml build base-sdk
```
Then build the sandbox image (we'll add the compose entry in Task 16, but we can preemptively test the Dockerfile in isolation):
```bash
cd /home/dethon/repos/agent && docker build -f McpServerSandbox/Dockerfile -t mcp-sandbox:test .
```
Expected: PASS — image builds successfully. (Note: this requires `base-sdk:latest` image to exist locally; build it first with the compose command above.)

- [ ] **Step 3: Quick smoke test inside the image**

Run:
```bash
docker run --rm --entrypoint bash mcp-sandbox:test -lc 'whoami && pwd && python3 --version'
```
Expected output:
```
sandbox_user
/app
Python 3.x.y
```

- [ ] **Step 4: Do NOT commit yet — Task 16 wires up compose**

---

## Task 16: Wire mcp-sandbox into docker-compose.yml

**Files:**
- Modify: `DockerCompose/docker-compose.yml`

- [ ] **Step 1: Add the service**

In `DockerCompose/docker-compose.yml`, add the following service block immediately after the `mcp-vault` block (around line 204, before the `camoufox:` block):

```yaml
  mcp-sandbox:
    image: mcp-sandbox:latest
    logging:
      options:
        max-size: "5m"
        max-file: "3"
    container_name: mcp-sandbox
    ports:
      - "6004:8080"
    build:
      context: ${REPOSITORY_PATH}
      dockerfile: McpServerSandbox/Dockerfile
      cache_from:
        - mcp-sandbox:latest
      args:
        - BUILDKIT_INLINE_CACHE=1
    volumes:
      - sandbox-data:/home/sandbox_user
    restart: unless-stopped
    env_file:
      - .env
    networks:
      - jackbot
    depends_on:
      - base-sdk
```

- [ ] **Step 2: Add the named volume**

At the bottom of the file, replace:

```yaml
networks:
  jackbot:
```

with:

```yaml
volumes:
  sandbox-data:

networks:
  jackbot:
```

- [ ] **Step 3: Update the agent service's depends_on**

In the `agent:` service block, the `depends_on:` list currently lists `mcp-library`, `mcp-vault`, etc. Add `mcp-sandbox` to that list:

```yaml
    depends_on:
      - mcp-library
      - mcp-vault
      - mcp-sandbox
      - mcp-websearch
      - mcp-idealista
      - mcp-channel-signalr
      - mcp-channel-telegram
      - mcp-channel-servicebus
      - redis
      - base-sdk
```

- [ ] **Step 4: Verify the compose file parses**

Run:
```bash
docker compose -f /home/dethon/repos/agent/DockerCompose/docker-compose.yml -f /home/dethon/repos/agent/DockerCompose/docker-compose.override.linux.yml config --quiet
```
Expected: silent success (config valid).

- [ ] **Step 5: Build the new service through compose**

Run:
```bash
docker compose -f /home/dethon/repos/agent/DockerCompose/docker-compose.yml -f /home/dethon/repos/agent/DockerCompose/docker-compose.override.linux.yml -p jackbot build mcp-sandbox
```
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git -C /home/dethon/repos/agent add McpServerSandbox/Dockerfile DockerCompose/docker-compose.yml
git -C /home/dethon/repos/agent commit -m "feat: deploy mcp-sandbox container with bash + python"
```

---

## Task 17: Update Agent appsettings to connect to mcp-sandbox

**Files:**
- Modify: `Agent/appsettings.json`
- Modify: `Agent/appsettings.Local.json`

- [ ] **Step 1: Add the endpoint to Jonas (the agent that already uses filesystem-rich access)**

Open `Agent/appsettings.json`. In the `agents` array, locate the `jonas` agent and update its `mcpServerEndpoints` to include sandbox, and its `whitelistPatterns` to allow `mcp:mcp-sandbox:*`:

```json
        {
            "id": "jonas",
            "name": "Jonas",
            "model": "z-ai/glm-5.1",
            "mcpServerEndpoints": [
                "http://mcp-vault:8080/mcp",
                "http://mcp-sandbox:8080/mcp",
                "http://mcp-websearch:8080/mcp",
                "http://mcp-idealista:8080/mcp"
            ],
            "enabledFeatures": [
                "filesystem",
                "scheduling",
                "subagents",
                "memory"
            ],
            "whitelistPatterns": [
                "domain:filesystem:*",
                "mcp:mcp-vault:*",
                "mcp:mcp-sandbox:*",
                "mcp:mcp-websearch:*",
                "mcp:mcp-idealista:*",
                "domain:scheduling:*",
                "domain:subagents:*",
                "domain:memory:*"
            ]
        }
```

Apply the same updates to the matching `subAgents` entry (`jonas-worker`).

- [ ] **Step 2: Mirror the local override**

Open `Agent/appsettings.Local.json`. The second agent (Jonas) currently lists local endpoints. Add `http://localhost:6004/mcp` for sandbox so local development reaches the host-published port:

```json
        {
            "mcpServerEndpoints": [
                "http://localhost:6002/mcp",
                "http://localhost:6004/mcp",
                "http://localhost:6003/mcp",
                "http://localhost:6005/mcp"
            ],
            "whitelistPatterns": [
                "*"
            ]
        }
```

Apply the same change to the local `subAgents` entry (the one matching `jonas-worker`).

- [ ] **Step 3: Verify config still loads**

Run: `dotnet build /home/dethon/repos/agent/Agent/Agent.csproj`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git -C /home/dethon/repos/agent add Agent/appsettings.json Agent/appsettings.Local.json
git -C /home/dethon/repos/agent commit -m "chore: wire jonas/jonas-worker to mcp-sandbox endpoint"
```

---

## Task 18: Update CLAUDE.md launch command

**Files:**
- Modify: `CLAUDE.md`

- [ ] **Step 1: Add mcp-sandbox to the up -d invocations**

Open `CLAUDE.md`. Locate the two `docker compose ... up -d ... --build agent webui ...` commands (one for Linux, one for Windows). In both, insert `mcp-sandbox` into the service list, just after `mcp-vault`:

Current (Linux line):
```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-websearch mcp-idealista mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus qbittorrent jackett redis caddy camoufox
```

New (Linux line):
```bash
docker compose -f DockerCompose/docker-compose.yml -f DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build agent webui observability mcp-vault mcp-sandbox mcp-websearch mcp-idealista mcp-library mcp-channel-signalr mcp-channel-telegram mcp-channel-servicebus qbittorrent jackett redis caddy camoufox
```

Apply the same insertion to the Windows line.

- [ ] **Step 2: Commit**

```bash
git -C /home/dethon/repos/agent add CLAUDE.md
git -C /home/dethon/repos/agent commit -m "docs: include mcp-sandbox in compose up command"
```

---

## Task 19: Integration test fixture (McpSandboxServerFixture)

**Files:**
- Create: `Tests/Integration/Fixtures/McpSandboxServerFixture.cs`

- [ ] **Step 1: Create the fixture**

Create `Tests/Integration/Fixtures/McpSandboxServerFixture.cs`:

```csharp
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Domain.Contracts;
using Domain.Tools.Config;
using Infrastructure.Clients;
using Infrastructure.Utils;
using McpServerSandbox.McpResources;
using McpServerSandbox.McpTools;
using McpServerSandbox.Services;
using McpServerSandbox.Settings;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Tests.Integration.Fixtures;

public class McpSandboxServerFixture : IAsyncLifetime
{
    private IHost _host = null!;

    public string McpEndpoint { get; private set; } = null!;
    public string SandboxRoot { get; private set; } = null!;
    public string HomeDir { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Skip.IfNot(RuntimeInformation.IsOSPlatform(OSPlatform.Linux), "Sandbox integration tests require Linux bash");

        SandboxRoot = "/";
        HomeDir = Path.Combine(Path.GetTempPath(), $"mcp-sandbox-{Guid.NewGuid()}");
        Directory.CreateDirectory(HomeDir);

        var port = GetAvailablePort();
        var settings = new McpSettings
        {
            ContainerRoot = SandboxRoot,
            HomeDir = HomeDir,
            DefaultTimeoutSeconds = 30,
            MaxTimeoutSeconds = 120,
            OutputCapBytes = 65536,
            AllowedExtensions = [".md", ".txt", ".py", ".sh", ".json"]
        };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
        });

        builder.Services
            .AddSingleton(settings)
            .AddTransient<LibraryPathConfig>(_ => new LibraryPathConfig(settings.ContainerRoot))
            .AddTransient<IFileSystemClient, LocalFileSystemClient>()
            .AddSingleton<BashRunner>()
            .AddMcpServer()
            .WithHttpTransport()
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                try
                {
                    return await next(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    return ToolResponse.Create(ex);
                }
            }))
            .WithTools<FsReadTool>()
            .WithTools<FsCreateTool>()
            .WithTools<FsEditTool>()
            .WithTools<FsGlobTool>()
            .WithTools<FsSearchTool>()
            .WithTools<FsMoveTool>()
            .WithTools<FsDeleteTool>()
            .WithTools<FsExecTool>()
            .WithResources<FileSystemResource>();

        var app = builder.Build();
        app.MapMcp("/mcp");

        _host = app;
        await _host.StartAsync();

        McpEndpoint = $"http://localhost:{port}/mcp";
    }

    private static int GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        try
        {
            if (HomeDir is not null && Directory.Exists(HomeDir))
            {
                Directory.Delete(HomeDir, true);
            }
        }
        catch
        {
            // Ignore cleanup errors
        }
    }
}
```

Note: The fixture uses a host-side temp directory as `HomeDir` to avoid touching `/home/sandbox_user` on the test machine. `ContainerRoot` is `/` (real host root) so `bash -lc` runs against the actual filesystem; tests must only operate within `HomeDir`.

- [ ] **Step 2: Continue to Task 20**

---

## Task 20: Integration tests (McpAgentSandboxTests)

**Files:**
- Create: `Tests/Integration/Agents/McpAgentSandboxTests.cs`

- [ ] **Step 1: Create the test class**

Create `Tests/Integration/Agents/McpAgentSandboxTests.cs`:

```csharp
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.Agents;

public class McpAgentSandboxTests(McpSandboxServerFixture fixture) : IClassFixture<McpSandboxServerFixture>
{
    private async Task<McpClient> ConnectAsync(CancellationToken ct)
    {
        var transport = new SseClientTransport(new SseClientTransportOptions
        {
            Endpoint = new Uri(fixture.McpEndpoint),
            Name = "sandbox-test"
        });
        return await McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private static JsonDocument ParseToolJson(CallToolResult result)
    {
        var text = string.Join("\n", result.Content
            .OfType<TextContentBlock>()
            .Select(c => c.Text));
        return JsonDocument.Parse(text);
    }

    [SkippableFact]
    public async Task Exec_PythonInline_ReturnsExpectedOutput()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["filesystem"] = "sandbox",
            ["path"] = "",
            ["command"] = "python3 -c 'print(2+2)'"
        }, cancellationToken: cts.Token);

        result.IsError.ShouldNotBe(true);
        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(0);
        json.RootElement.GetProperty("stdout").GetString()!.Trim().ShouldBe("4");
    }

    [SkippableFact]
    public async Task Exec_RoundTripWithFsCreate_RunsAgentWrittenFile()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var client = await ConnectAsync(cts.Token);

        var relHome = Path.GetRelativePath("/", fixture.HomeDir);

        // Write hello.py via fs_create
        await client.CallToolAsync("fs_create", new Dictionary<string, object?>
        {
            ["filesystem"] = "sandbox",
            ["path"] = Path.Combine(relHome, "hello.py"),
            ["content"] = "print('hi from agent')",
            ["overwrite"] = true,
            ["createDirectories"] = true
        }, cancellationToken: cts.Token);

        // Run it via fs_exec
        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["filesystem"] = "sandbox",
            ["path"] = relHome,
            ["command"] = "python3 hello.py"
        }, cancellationToken: cts.Token);

        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(0);
        json.RootElement.GetProperty("stdout").GetString()!.Trim().ShouldBe("hi from agent");
    }

    [SkippableFact]
    public async Task Exec_NonZeroExit_ReturnedAsData()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["filesystem"] = "sandbox",
            ["path"] = "",
            ["command"] = "false"
        }, cancellationToken: cts.Token);

        result.IsError.ShouldNotBe(true);
        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("exitCode").GetInt32().ShouldBe(1);
    }

    [SkippableFact]
    public async Task Exec_Timeout_TruncatesAndKills()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["filesystem"] = "sandbox",
            ["path"] = "",
            ["command"] = "sleep 30",
            ["timeoutSeconds"] = 1
        }, cancellationToken: cts.Token);

        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("timedOut").GetBoolean().ShouldBeTrue();
    }

    [SkippableFact]
    public async Task Exec_OutputExceedsCap_TruncatedReported()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["filesystem"] = "sandbox",
            ["path"] = "",
            ["command"] = "yes a | head -c 200000"
        }, cancellationToken: cts.Token);

        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
    }

    [SkippableFact]
    public async Task Exec_NonExistentCwd_ReturnsErrorJson()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var result = await client.CallToolAsync("fs_exec", new Dictionary<string, object?>
        {
            ["filesystem"] = "sandbox",
            ["path"] = "this/does/not/exist",
            ["command"] = "echo hi"
        }, cancellationToken: cts.Token);

        using var json = ParseToolJson(result);
        json.RootElement.GetProperty("error").GetBoolean().ShouldBeTrue();
    }

    [SkippableFact]
    public async Task ListTools_IncludesFsExec()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var tools = await client.ListToolsAsync(cancellationToken: cts.Token);

        tools.Select(t => t.Name).ShouldContain("fs_exec");
    }

    [SkippableFact]
    public async Task ListResources_ExposesSandboxFilesystem()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await ConnectAsync(cts.Token);

        var resources = await client.ListResourcesAsync(cancellationToken: cts.Token);

        resources.Any(r => r.Uri == "filesystem://sandbox").ShouldBeTrue();
    }
}
```

- [ ] **Step 2: Run the integration tests**

Run: `dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~McpAgentSandboxTests" --no-restore`
Expected: PASS — 8/8 (Linux) or all skipped (Windows).

- [ ] **Step 3: Commit**

```bash
git -C /home/dethon/repos/agent add Tests/Integration/Fixtures/McpSandboxServerFixture.cs Tests/Integration/Agents/McpAgentSandboxTests.cs
git -C /home/dethon/repos/agent commit -m "test: add integration tests for sandbox MCP server"
```

---

## Task 21: Final verification

**Files:**
- None (verification only)

- [ ] **Step 1: Full clean build**

Run: `dotnet build /home/dethon/repos/agent/agent.sln --no-incremental`
Expected: PASS, no warnings related to new code.

- [ ] **Step 2: All unit tests**

Run: `dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName!~Integration&FullyQualifiedName!~E2E" --no-restore`
Expected: PASS.

- [ ] **Step 3: Sandbox integration tests**

Run: `dotnet test /home/dethon/repos/agent/Tests/Tests.csproj --filter "FullyQualifiedName~McpAgentSandboxTests|FullyQualifiedName~BashRunnerTests" --no-restore`
Expected: PASS on Linux (skipped on Windows).

- [ ] **Step 4: Compose validation**

Run:
```bash
docker compose -f /home/dethon/repos/agent/DockerCompose/docker-compose.yml -f /home/dethon/repos/agent/DockerCompose/docker-compose.override.linux.yml config --quiet
```
Expected: silent success.

- [ ] **Step 5: Live sandbox smoke test (optional but recommended)**

Run:
```bash
docker compose -f /home/dethon/repos/agent/DockerCompose/docker-compose.yml -f /home/dethon/repos/agent/DockerCompose/docker-compose.override.linux.yml -p jackbot up -d --build mcp-sandbox
```
Then verify the MCP endpoint responds:
```bash
curl -sf http://localhost:6004/mcp -H 'Accept: text/event-stream' --max-time 3 || true
```
Expected: connection accepts SSE handshake (output may be empty after 3s — that's fine; we're verifying the server is up).

Tear down:
```bash
docker compose -f /home/dethon/repos/agent/DockerCompose/docker-compose.yml -f /home/dethon/repos/agent/DockerCompose/docker-compose.override.linux.yml -p jackbot stop mcp-sandbox
```

- [ ] **Step 6: Confirm clean git status**

Run: `git -C /home/dethon/repos/agent status`
Expected: working tree clean (or only this plan file if you wrote anything else during execution).

- [ ] **Step 7: Commit log review**

Run: `git -C /home/dethon/repos/agent log --oneline master..HEAD`
Expected: ~7-9 commits matching the conventional-commit messages above.
