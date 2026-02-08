# Code Conventions

## C# Language & Style

### Target Framework
- .NET 10 LTS (`net10.0`)
- C# 14 (`<LangVersion>14</LangVersion>`)
- Implicit usings enabled (`<ImplicitUsings>enable</ImplicitUsings>`)
- Nullable reference types enabled (`<Nullable>enable</Nullable>`)

### Namespaces
- File-scoped namespaces: `namespace Domain.Contracts;`
- Enforced via EditorConfig: `csharp_style_namespace_declarations = file_scoped:warning`

### Primary Constructors
- Use primary constructors for dependency injection:
```csharp
public class MyService(ILogger<MyService> logger, IRepository repo)
{
    // No explicit field declarations needed
}
```
- Also used in test helper classes:
```csharp
private class TestableExampleTool(string basePath, string[] extensions)
    : ExampleTool(basePath, extensions)
{
    public JsonNode TestRun(string path) => Run(path);
}
```

### Data Types
- `record` types for DTOs and immutable data:
```csharp
public record AgentDefinition
{
    public required string Id { get; init; }
    public string? Description { get; init; }
}
```
- Positional `record` types for simple value types:
```csharp
public record ToolApprovalRequest(
    string? MessageId,
    string ToolName,
    IReadOnlyDictionary<string, object?> Arguments);
```
- Discriminated unions via abstract records:
```csharp
public abstract record ParseResult;
public sealed record ParseSuccess(ParsedServiceBusMessage Message) : ParseResult;
public sealed record ParseFailure(string Reason, string Details) : ParseResult;
```

### Nullable Reference Types
- Always enabled (`<Nullable>enable</Nullable>`)
- Use `?` for nullable properties and parameters
- Use null-conditional operators (`?.`, `??`)
- Use pattern matching null checks: `if (value is not null)`

### Async/Await
- All async operations use `async`/`await`
- Pass `CancellationToken` through all async chains
- Use `CancellationToken ct = default` for optional cancellation parameters
- No `Async` suffix convention enforced -- some methods use it (`RunAsync`, `StoreAsync`), others don't (`Run`, `Parse`)

### Guard Clauses
```csharp
ArgumentNullException.ThrowIfNull(parameter);
ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
```

### Collections
- Return `IReadOnlyList<T>` or `IReadOnlyCollection<T>`
- Accept `IEnumerable<T>` as parameters
- Use collection expressions (`[]`) for empty or inline collections:
```csharp
public string[] EnabledFeatures { get; init; } = [];
var parser = new ServiceBusMessageParser(["agent1", "agent2"]);
```

### C# 14 Extension Members
- Extension methods use the new C# 14 `extension(Type)` block syntax:
```csharp
public static class StringExtensions
{
    extension(string str)
    {
        public string Left(int count) => str.Length <= count ? str : str[..count];
    }
}
```
- Also used in DI modules for `IServiceCollection` extensions:
```csharp
public static class InjectorModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAgent(AgentSettings settings) { ... }
    }
}
```

### `var` Preference
- Use `var` whenever the type is apparent from context
- Enforced via EditorConfig: `csharp_style_var_when_type_is_apparent = true:suggestion`

## LINQ Preference

**Strongly prefer LINQ over traditional loops:**

```csharp
// Preferred
var results = items
    .Where(x => x.IsValid)
    .Select(x => x.Name)
    .ToList();

// Avoid
var results = new List<string>();
foreach (var item in items)
{
    if (item.IsValid)
        results.Add(item.Name);
}
```

Use LINQ for filtering (`.Where()`), transformation (`.Select()`), aggregation (`.Sum()`, `.Aggregate()`), existence checks (`.Any()`, `.All()`), finding elements (`.First()`, `.FirstOrDefault()`), grouping (`.GroupBy()`), ordering (`.OrderBy()`), and set operations (`.Distinct()`, `.Union()`).

Only use traditional loops when mutating external state, for complex control flow LINQ cannot express cleanly, or for performance-critical hot paths.

## Naming Conventions

### EditorConfig-Enforced Rules

| Element | Convention | Example |
|---------|-----------|---------|
| Types (class, struct, enum) | PascalCase | `ChatMonitor`, `AgentDefinition` |
| Interfaces | PascalCase with `I` prefix | `IChatMessengerClient`, `IMemoryStore` |
| Methods and properties | PascalCase | `RunAsync`, `IsValid` |
| Constants | PascalCase | `Name`, `Description`, `DefaultAccessCount` |
| Private fields | `_camelCase` (underscore prefix) | `_container`, `_fileSystemClientMock` |
| Public fields | PascalCase | rare; prefer properties |
| Parameters and locals | camelCase | `cancellationToken`, `result` |

### Project Naming
- Test classes: `{ClassUnderTest}Tests.cs`
- Test methods: `{Method}_{Scenario}_{ExpectedResult}`
- MCP tool classes: `Mcp{DomainToolName}` (e.g., `McpRemoveTool`)
- Domain tool classes: `{Verb}{Noun}Tool` (e.g., `MemoryStoreTool`, `FileSearchTool`)
- Fixture classes: `{Feature}Fixture` (e.g., `RedisFixture`, `ServiceBusFixture`)

## Import Organization

1. System namespaces
2. Microsoft namespaces
3. Third-party packages (Moq, Shouldly, StackExchange, etc.)
4. Project namespaces (Domain, Infrastructure, McpServer*)

EditorConfig enforces: `dotnet_sort_system_directives_first = true`

## Braces
- Always required for `if`, `else`, `for`, `foreach`, `while`, `using`, `lock`, `fixed`
- Enforced via EditorConfig: `csharp_prefer_braces = true:warning`
- New line before open brace (Allman style): `csharp_new_line_before_open_brace = all`

## Domain Layer Rules

- No external framework dependencies (only abstractions packages)
- Interfaces only for services Domain consumes
- Pure business logic, no state management
- DTOs as records with `required` init properties
- Domain tools contain business logic; MCP tools are thin wrappers
- Only define interfaces for services consumed by Domain; single-implementation services used only by Agent layer do not need interfaces
- Tool `Name` and `Description` as `protected const string` in base Domain tool

## Infrastructure Layer Rules

- Implements Domain interfaces
- Handles external communication (Redis, HTTP, Telegram, ServiceBus, Playwright)
- Uses primary constructors for DI
- Graceful error handling with proper error messages
- `InternalsVisibleTo` for Tests project: `<InternalsVisibleTo Include="Tests"/>`
- Never imports from `Agent` namespace

## MCP Tool Pattern

MCP tools wrap Domain tools and expose them via Model Context Protocol:

```csharp
[McpServerToolType]
public class McpRemoveTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath) : RemoveTool(client, libraryPath)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        [Description("Path to the file or directory")]
        string path,
        CancellationToken cancellationToken)
    {
        return ToolResponse.Create(await Run(path, cancellationToken));
    }
}
```

Key conventions:
- Inherit from the corresponding Domain tool
- Use `[McpServerToolType]` class attribute
- Use `[McpServerTool(Name = Name)]` and `[Description(Description)]` method attributes
- Use `[Description(...)]` on each parameter for LLM documentation
- Return `CallToolResult` via `ToolResponse.Create(result)`
- `Name` and `Description` constants come from the base Domain tool
- No try/catch -- global filter via `AddCallToolFilter` handles errors
- No `ILogger<T>` -- the global filter logs errors
- Use `context.Server.StateKey` for session identification (when needed)

## Domain Tool Pattern

Domain tools contain the business logic:

```csharp
public class RemoveTool(IFileSystemClient client, LibraryPathConfig libraryPath)
{
    protected const string Name = "Remove";
    protected const string Description = """
        Removes a file or directory by moving it to a trash folder.
        """;

    protected async Task<JsonNode> Run(string path, CancellationToken cancellationToken)
    {
        // Business logic here
        return new JsonObject { ["status"] = "success" };
    }
}
```

Key conventions:
- Use `protected const` for `Name` and `Description`
- Return `JsonNode` (typically `JsonObject`) for tool results
- Protected `Run` method containing business logic
- Use raw string literals (`"""..."""`) for multi-line descriptions
- Validate inputs and throw exceptions for invalid states

## Domain Tool Feature Pattern

For tools registered directly as domain tools (not via MCP):

```csharp
public class SchedulingToolFeature(
    ScheduleCreateTool createTool,
    ScheduleListTool listTool,
    ScheduleDeleteTool deleteTool) : IDomainToolFeature
{
    public string FeatureName => "scheduling";

    public IEnumerable<AIFunction> GetTools()
    {
        yield return AIFunctionFactory.Create(
            createTool.RunAsync,
            name: $"domain:{FeatureName}:{ScheduleCreateTool.Name}");
    }
}
```

## DI Registration Patterns

### Module Organization
- DI registration is organized into static module classes in `Agent/Modules/` and `McpServer*/Modules/`
- Uses C# 14 extension members on `IServiceCollection`:
```csharp
public static class InjectorModule
{
    extension(IServiceCollection services)
    {
        public IServiceCollection AddAgent(AgentSettings settings) { ... }
        public IServiceCollection AddChatMonitoring(...) { ... }
    }
}
```

### MCP Server Configuration
Each MCP server has a `ConfigModule.cs` with:
- `GetSettings()` extension method on `IConfigurationBuilder`
- `ConfigureMcp()` extension method on `IServiceCollection`
- Global error filter via `AddCallToolFilter`:
```csharp
.AddCallToolFilter(next => async (context, cancellationToken) =>
{
    try { return await next(context, cancellationToken); }
    catch (Exception ex)
    {
        var logger = context.Services?.GetRequiredService<ILogger<Program>>();
        logger?.LogError(ex, "Error in {ToolName} tool", context.Params?.Name);
        return ToolResponse.Create(ex);
    }
})
```

### Common Registration Patterns
- `AddSingleton` for stateful services, managers, and stores
- `AddTransient` for tools (e.g., `services.AddTransient<ScheduleCreateTool>()`)
- `AddHostedService` for background services
- Factory lambdas for complex construction: `services.AddSingleton<IAgentFactory>(sp => ...)`

## Error Handling

- Use `FluentResults` for result types in select areas (download cleanup, chat message store)
- Throw exceptions for truly exceptional/validation cases
- Log errors at Infrastructure layer
- MCP tools rely on global `AddCallToolFilter` for centralized error handling
- Domain tools return `JsonObject` with `["error"]` key for expected validation failures:
```csharp
return new JsonObject { ["error"] = $"Invalid category: {category}" };
```
- Use `InvalidOperationException` for constraint violations in tools

## Documentation

- Prioritize readable code over comments
- No XML documentation comments
- Comment only to explain "why", never "what"
- No emoji in code
- Use `[Description(...)]` attributes on MCP tool methods and parameters for LLM documentation
- Use raw string literals for multi-line tool descriptions

## Code Formatting

- Indent size: 4 spaces (code), 2 spaces (XML/csproj)
- Max line length: 120 characters
- End of line: CRLF
- No final newline
- Charset: UTF-8
- Braces: Allman style (next line)
- Using directives: outside namespace
