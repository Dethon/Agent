# Hybrid Tools Specification

> Configurable tool sourcing: local execution and/or remote MCP servers

## Problem Statement

The current architecture requires all tools to come from MCP servers:
1. **Unnecessary overhead** - Local file operations go through HTTP/SSE transport
2. **Deployment complexity** - Must run MCP servers even for simple local tools
3. **Latency** - Round-trip to MCP server for every tool call
4. **No flexibility** - Can't mix local tools with remote MCP capabilities

However, MCP servers provide value for:
- **Shared resources** - Multiple agents accessing same torrent client, media library
- **Long-running state** - Download monitoring, subscription tracking
- **Isolation** - Sandboxed execution environments
- **Distributed systems** - Tools running on different machines

## Solution Overview

A hybrid tool system where the agent can be configured with:
1. **Local tools** - Execute directly in the agent process
2. **Remote MCP tools** - Connect to MCP servers as today
3. **Mixed configuration** - Some tools local, some remote

```
┌─────────────────────────────────────────────────────────┐
│                     Agent Process                        │
│  ┌─────────────────┐    ┌─────────────────────────────┐ │
│  │  Local Tools    │    │     MCP Client Manager      │ │
│  │  ─────────────  │    │     ──────────────────      │ │
│  │  • TextInspect  │    │  → McpServerLibrary         │ │
│  │  • TextRead     │    │    (torrents, downloads)    │ │
│  │  • TextPatch    │    │                             │ │
│  │  • CliRun       │    │  → External MCP Servers     │ │
│  │  • CliSession   │    │    (databases, APIs, etc.)  │ │
│  └────────┬────────┘    └──────────────┬──────────────┘ │
│           │                            │                 │
│           └──────────┬─────────────────┘                 │
│                      ▼                                   │
│           ┌─────────────────────┐                        │
│           │   Unified AITool[]  │                        │
│           │   for ChatClient    │                        │
│           └─────────────────────┘                        │
└─────────────────────────────────────────────────────────┘
```

---

## Configuration Schema

### Agent Settings

```json
{
  "Agent": {
    "Name": "jack",
    "Tools": {
      "Local": {
        "Enabled": true,
        "TextTools": {
          "Enabled": true,
          "AllowedExtensions": [".md", ".txt", ".json", ".yaml", ".yml"]
        },
        "CliTools": {
          "Enabled": true,
          "Shell": "auto",
          "AllowedCommands": ["git", "npm", "dotnet", "ls", "cat", "grep"],
          "MaxSessionCount": 5,
          "CommandTimeout": 30
        }
      },
      "McpServers": [
        {
          "Name": "library",
          "Endpoint": "http://localhost:5001/sse",
          "Enabled": true
        },
        {
          "Name": "external-db",
          "Endpoint": "http://db-server:5002/sse",
          "Enabled": true
        }
      ]
    }
  }
}
```

> **Note**: Local tools use the directory where the agent is launched as the working directory. All file paths are relative to this launch directory, and CLI commands execute there by default. This makes the agent behave like a local development tool—run it from your project root and it operates on that project.

### Configuration Options

#### Local Text Tools

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | bool | false | Enable local text tools |
| `AllowedExtensions` | string[] | [".md", ".txt"] | File types that can be accessed |
| `MaxFileSize` | string | "10MB" | Maximum file size for operations |
| `AllowCreate` | bool | true | Allow creating new files |
| `AllowDelete` | bool | false | Allow deleting files |

> Working directory is automatically set to where the agent is launched. All relative paths resolve from there.

#### Local CLI Tools

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Enabled` | bool | false | Enable local CLI tools |
| `Shell` | string | "auto" | Shell to use: "auto", "bash", "pwsh", "cmd", "sh" |
| `AllowedCommands` | string[] | [] | Whitelist of allowed commands (empty = all) |
| `BlockedCommands` | string[] | ["rm -rf", "format", "del /f"] | Blacklist of dangerous patterns |
| `MaxSessionCount` | int | 5 | Maximum concurrent interactive sessions |
| `CommandTimeout` | int | 30 | Default timeout in seconds |
| `CaptureStderr` | bool | true | Include stderr in output |

> Working directory is automatically set to where the agent is launched. CLI commands execute from there.

#### MCP Server Configuration

| Setting | Type | Default | Description |
|---------|------|---------|-------------|
| `Name` | string | required | Display name for the server |
| `Endpoint` | string | required | SSE endpoint URL |
| `Enabled` | bool | true | Whether to connect to this server |
| `RetryAttempts` | int | 3 | Connection retry attempts |
| `RetryDelayMs` | int | 1000 | Initial retry delay (exponential backoff) |

---

## Architecture

### Domain Layer

New contracts for local tool execution:

```csharp
// Domain/Contracts/ILocalToolProvider.cs
public interface ILocalToolProvider
{
    string Name { get; }
    IReadOnlyList<AITool> GetTools();
}

// Domain/Contracts/IToolRegistry.cs
public interface IToolRegistry
{
    IReadOnlyList<AITool> GetAllTools();
    void RegisterProvider(ILocalToolProvider provider);
    void RegisterMcpTools(IReadOnlyList<AITool> tools);
}
```

### Infrastructure Layer

#### Local Tool Providers

```csharp
// Infrastructure/Tools/Local/LocalTextToolProvider.cs
public class LocalTextToolProvider : ILocalToolProvider
{
    public string Name => "LocalText";
    
    public LocalTextToolProvider(LocalTextToolSettings settings)
    {
        // Initialize tools with settings
    }
    
    public IReadOnlyList<AITool> GetTools()
    {
        // Return AITool wrappers around Domain text tools
    }
}

// Infrastructure/Tools/Local/LocalCliToolProvider.cs
public class LocalCliToolProvider : ILocalToolProvider
{
    public string Name => "LocalCli";
    
    public IReadOnlyList<AITool> GetTools()
    {
        // Return AITool wrappers around CLI tools
    }
}
```

#### Tool Registry Implementation

```csharp
// Infrastructure/Tools/ToolRegistry.cs
public class ToolRegistry : IToolRegistry
{
    private readonly List<AITool> _tools = [];
    private readonly List<ILocalToolProvider> _providers = [];
    
    public IReadOnlyList<AITool> GetAllTools() => _tools.AsReadOnly();
    
    public void RegisterProvider(ILocalToolProvider provider)
    {
        _providers.Add(provider);
        _tools.AddRange(provider.GetTools());
    }
    
    public void RegisterMcpTools(IReadOnlyList<AITool> tools)
    {
        _tools.AddRange(tools);
    }
}
```

#### AITool Wrapper for Domain Tools

```csharp
// Infrastructure/Tools/Local/LocalAITool.cs
public class LocalAITool<TInput> : AITool
{
    private readonly Func<TInput, CancellationToken, Task<object>> _execute;
    
    public LocalAITool(
        string name,
        string description,
        Func<TInput, CancellationToken, Task<object>> execute,
        AIJsonSchemaCreateOptions? schemaOptions = null)
    {
        Name = name;
        Description = description;
        _execute = execute;
        
        // Build JSON schema from TInput type
        JsonSchema = AIJsonSchemaCreateOptions.Default.CreateJsonSchema(typeof(TInput));
    }
    
    public override async Task<object?> InvokeAsync(
        AIToolArguments arguments,
        CancellationToken cancellationToken)
    {
        var input = JsonSerializer.Deserialize<TInput>(arguments.Arguments);
        return await _execute(input!, cancellationToken);
    }
}
```

### Session Builder Changes

Modify `ThreadSessionBuilder` to support hybrid tools:

```csharp
// Infrastructure/Agents/ThreadSessionBuilder.cs
internal sealed class ThreadSessionBuilder
{
    private readonly IToolRegistry _toolRegistry;
    private readonly string[] _mcpEndpoints;
    
    public async Task<ThreadSessionData> BuildAsync(CancellationToken ct)
    {
        // Step 1: Local tools are already registered in _toolRegistry
        
        // Step 2: Connect to MCP servers and add their tools
        if (_mcpEndpoints.Length > 0)
        {
            var clientManager = await McpClientManager.CreateAsync(...);
            _toolRegistry.RegisterMcpTools(clientManager.Tools);
        }
        
        // Step 3: Get unified tool list
        var allTools = _toolRegistry.GetAllTools();
        
        // ... rest of setup
    }
}
```

---

## Local Tool Implementations

### Text Tools (Reuse Domain Layer)

The existing `Domain/Tools/Text/*` classes contain the core logic. Create thin AITool wrappers:

```csharp
// Infrastructure/Tools/Local/Text/LocalTextInspectTool.cs
public class LocalTextInspectTool : LocalAITool<TextInspectInput>
{
    public LocalTextInspectTool(LocalTextToolSettings settings, string workingDirectory)
        : base(
            name: "TextInspect",
            description: "Inspects text/markdown file structure...",
            execute: (input, ct) => 
            {
                var tool = new TextInspectTool(workingDirectory, settings.AllowedExtensions);
                return Task.FromResult<object>(tool.Run(input.FilePath, input.Mode, input.Query, input.Regex, input.Context));
            })
    { }
}

public record TextInspectInput(
    string FilePath,
    string Mode = "structure",
    string? Query = null,
    bool Regex = false,
    int Context = 0);
```

### CLI Tools (New Implementation)

```csharp
// Infrastructure/Tools/Local/Cli/LocalCliRunTool.cs
public class LocalCliRunTool : LocalAITool<CliRunInput>
{
    private readonly LocalCliToolSettings _settings;
    private readonly IShellExecutor _executor;
    
    public LocalCliRunTool(LocalCliToolSettings settings, IShellExecutor executor)
        : base(
            name: "CliRun",
            description: "Executes a command and returns output...",
            execute: async (input, ct) => await ExecuteAsync(input, ct))
    {
        _settings = settings;
        _executor = executor;
    }
    
    private async Task<object> ExecuteAsync(CliRunInput input, CancellationToken ct)
    {
        ValidateCommand(input.Command);
        
        var result = await _executor.RunAsync(
            input.Command,
            input.WorkingDirectory ?? _settings.WorkingDirectory,
            TimeSpan.FromSeconds(input.Timeout ?? _settings.CommandTimeout),
            ct);
            
        return new CliRunOutput(
            result.ExitCode == 0 ? "success" : "error",
            result.ExitCode,
            result.Stdout,
            result.Stderr,
            result.Duration.TotalSeconds);
    }
    
    private void ValidateCommand(string command)
    {
        // Check against AllowedCommands whitelist
        // Check against BlockedCommands blacklist
    }
}

public record CliRunInput(
    string Command,
    string? WorkingDirectory = null,
    int? Timeout = null,
    bool CaptureStderr = true);

public record CliRunOutput(
    string Status,
    int? ExitCode,
    string Stdout,
    string Stderr,
    double Duration);
```

---

## Dependency Injection Setup

### Module Registration

```csharp
// Agent/Modules/LocalToolsModule.cs
public static class LocalToolsModule
{
    public static IServiceCollection AddLocalTools(
        this IServiceCollection services,
        LocalToolsSettings settings)
    {
        // Capture launch directory at startup
        var workingDirectory = Directory.GetCurrentDirectory();
        
        services.AddSingleton<IToolRegistry, ToolRegistry>();
        
        if (settings.TextTools?.Enabled == true)
        {
            services.AddSingleton<ILocalToolProvider>(sp => 
                new LocalTextToolProvider(settings.TextTools, workingDirectory));
        }
        
        if (settings.CliTools?.Enabled == true)
        {
            services.AddSingleton<IShellExecutor>(sp => 
                new ShellExecutor(workingDirectory));
            services.AddSingleton<ICliSessionManager, CliSessionManager>();
            services.AddSingleton<ILocalToolProvider>(sp => 
                new LocalCliToolProvider(
                    settings.CliTools,
                    workingDirectory,
                    sp.GetRequiredService<IShellExecutor>(),
                    sp.GetRequiredService<ICliSessionManager>()));
        }
        
        return services;
    }
}
```

### Startup Integration

```csharp
// Agent/Program.cs
var settings = configuration.GetSection("Agent").Get<AgentSettings>();

services.AddLocalTools(settings.Tools.Local);

// Tool registry auto-registers all ILocalToolProvider instances
services.AddHostedService<ToolRegistrationService>();
```

---

## Tool Naming and Conflicts

### Naming Convention

- Local tools: Use simple names (`TextInspect`, `CliRun`)
- MCP tools: Prefix with server name (`library:SearchTorrents`, `library:GetDownloadStatus`)

### Conflict Resolution

If a local tool and MCP tool have the same name:

1. **Default**: Local tool takes precedence (faster)
2. **Configurable**: `PreferMcp: true` to prefer MCP version
3. **Explicit**: User can specify `library:ToolName` to force MCP version

```json
{
  "Tools": {
    "ConflictResolution": "local-first"  // or "mcp-first", "error"
  }
}
```

---

## Security Model

### Local Text Tools

1. **Path containment**: All paths must resolve within working directory (launch dir)
2. **Extension whitelist**: Only configured file types accessible
3. **Size limits**: Prevent reading/writing excessively large files
4. **No symlink following**: Prevent escaping working directory via symlinks
5. **No parent traversal**: Reject paths with `..` that escape working directory

### Local CLI Tools

1. **Command whitelist**: If `AllowedCommands` is non-empty, only those prefixes allowed
2. **Command blacklist**: `BlockedCommands` patterns always rejected
3. **Working directory containment**: Commands run in controlled directory
4. **Resource limits**: Max sessions, timeouts, output size caps
5. **No shell injection**: Commands validated for dangerous patterns

### Validation Examples

```csharp
// Allowed (if "git" in AllowedCommands)
"git status"
"git diff HEAD~1"

// Blocked (dangerous patterns)
"rm -rf /"
"git push; rm -rf /"  // Command chaining with dangerous command
"$(whoami)"           // Command substitution

// Blocked (not in whitelist)
"curl http://evil.com"  // If curl not in AllowedCommands
```

---

## Workflow Examples

### Example 1: Local-Only Text Editing

Configuration:
```json
{
  "Tools": {
    "Local": {
      "TextTools": { "Enabled": true, "VaultPath": "/docs" },
      "CliTools": { "Enabled": false }
    },
    "McpServers": []
  }
}
```

Agent has: `TextInspect`, `TextRead`, `TextPatch`, `TextCreate`, `TextSearch`

Use case: Documentation assistant that only edits markdown files.

### Example 2: Local CLI + Remote Library

Configuration:
```json
{
  "Tools": {
    "Local": {
      "TextTools": { "Enabled": false },
      "CliTools": { 
        "Enabled": true,
        "AllowedCommands": ["git", "npm", "node"]
      }
    },
    "McpServers": [
      { "Name": "library", "Endpoint": "http://localhost:5001/sse" }
    ]
  }
}
```

Agent has: 
- Local: `CliRun`, `CliSession`, `CliRead`, `CliWrite`, `CliStop`, `CliGetPlatform`
- Remote: `SearchTorrents`, `DownloadTorrent`, `GetDownloadStatus`, etc.

Use case: Media agent that manages downloads remotely but runs git/npm locally for automation.

### Example 3: Full Hybrid

Configuration:
```json
{
  "Tools": {
    "Local": {
      "TextTools": { "Enabled": true, "VaultPath": "/workspace" },
      "CliTools": { 
        "Enabled": true,
        "AllowedCommands": ["dotnet", "git"]
      }
    },
    "McpServers": [
      { "Name": "library", "Endpoint": "http://media-server:5001/sse" }
    ]
  }
}
```

Agent has:
- Local text: `TextInspect`, `TextRead`, `TextPatch`, `TextCreate`, `TextSearch`
- Local CLI: `CliRun`, `CliSession`, etc.
- Remote: `library:SearchTorrents`, `library:DownloadTorrent`, etc.

Use case: Development assistant that edits code, runs builds, and can request media downloads.

---

## Migration Path

### Phase 1: Add Local Tool Infrastructure
- Implement `IToolRegistry`, `ILocalToolProvider`
- Create `LocalAITool<T>` wrapper
- Wire up DI without breaking existing MCP flow

### Phase 2: Port Text Tools to Local
- Create `LocalTextToolProvider` using existing Domain tools
- Add configuration for local text tools
- Test alongside MCP text tools

### Phase 3: Add CLI Tools
- Implement `IShellExecutor`, `ICliSessionManager`
- Create `LocalCliToolProvider`
- Add security validation layer

### Phase 4: Deprecate MCP Text Server (Optional)
- If local text tools prove sufficient, McpServerText becomes optional
- Keep for scenarios requiring remote text editing

---

## Implementation Notes

### File Locations

```
Domain/
  Contracts/
    ILocalToolProvider.cs
    IToolRegistry.cs
    IShellExecutor.cs
    ICliSessionManager.cs

Infrastructure/
  Tools/
    ToolRegistry.cs
    Local/
      LocalAITool.cs
      LocalToolProvider.cs
      Text/
        LocalTextToolProvider.cs
        LocalTextInspectTool.cs
        LocalTextReadTool.cs
        LocalTextPatchTool.cs
        LocalTextCreateTool.cs
        LocalTextSearchTool.cs
      Cli/
        LocalCliToolProvider.cs
        LocalCliRunTool.cs
        LocalCliSessionTool.cs
        LocalCliReadTool.cs
        LocalCliWriteTool.cs
        LocalCliStopTool.cs
        LocalCliGetPlatformTool.cs
        LocalCliEnvTool.cs
        ShellExecutor.cs
        CliSessionManager.cs
        CommandValidator.cs

Agent/
  Settings/
    LocalToolsSettings.cs
    LocalTextToolSettings.cs
    LocalCliToolSettings.cs
  Modules/
    LocalToolsModule.cs
```

### Testing Strategy

1. **Unit tests**: Tool logic in isolation
2. **Integration tests**: Full tool execution with real files/shell
3. **Security tests**: Validate path containment, command blocking
4. **Performance tests**: Compare local vs MCP latency

---

## Future Enhancements

1. **Tool discovery**: Auto-detect available tools based on environment
2. **Hot reload**: Change tool configuration without restart
3. **Tool metrics**: Track usage, latency, errors per tool
4. **Tool permissions**: Per-user/per-chat tool restrictions
5. **Tool chaining**: Define composite tools that combine multiple operations
6. **Custom local tools**: Plugin system for user-defined tools
