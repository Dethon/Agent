# Coding Conventions

**Analysis Date:** 2025-01-19

## Naming Patterns

**Files:**
- Classes: PascalCase matching class name (e.g., `BraveSearchClient.cs`)
- Interfaces: `I` prefix with PascalCase (e.g., `IDownloadClient.cs`)
- Tests: `{ClassUnderTest}Tests.cs` (e.g., `ToolPatternMatcherTests.cs`)
- Fixtures: `{Name}Fixture.cs` (e.g., `RedisFixture.cs`)

**Functions/Methods:**
- Public methods: PascalCase (e.g., `SearchAsync`, `CreateClient`)
- Private methods: PascalCase (e.g., `BuildSearchUrl`, `ExtractDomain`)
- Async methods: Suffix with `Async` (e.g., `GetMessagesAsync`)

**Variables:**
- Private fields: Underscore prefix with camelCase (e.g., `_container`, `_apiKey`)
- Local variables: camelCase (e.g., `httpClient`, `response`)
- Constants: PascalCase (e.g., `SearchEndpoint`, `JackettPort`)
- Static readonly: Underscore prefix with camelCase (e.g., `_sourceArray`)

**Types:**
- Classes/Records: PascalCase (e.g., `AgentDefinition`, `ChatPrompt`)
- Interfaces: `I` prefix (e.g., `IDownloadClient`, `ISearchClient`)
- Enums: PascalCase (e.g., `DateRange`, `ChatCommand`)

## Code Style

**Formatting:**
- Configured via `.editorconfig`
- 4-space indentation for C# files
- 2-space indentation for XML/config files
- Max line length: 120 characters
- Braces on new line (Allman style)
- No trailing whitespace

**Linting:**
- EditorConfig rules enforced
- ReSharper/Rider integration for additional style checks
- File-scoped namespaces required (`csharp_style_namespace_declarations = file_scoped:warning`)
- Braces required for all control structures (`csharp_prefer_braces = true:warning`)

## Import Organization

**Order:**
1. System namespaces (sorted alphabetically)
2. Third-party namespaces (sorted alphabetically)
3. Project namespaces (Domain, Infrastructure, etc.)

**Example from `G:\repos\agent\Infrastructure\Clients\BraveSearchClient.cs`:**
```csharp
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Web;
using Domain.Contracts;
using JetBrains.Annotations;
```

**Path Aliases:**
- None configured; use full namespace paths

## Modern C# Patterns

**Use These Patterns:**
- File-scoped namespaces: `namespace Domain.Contracts;`
- Primary constructors for DI: `public class BraveSearchClient(HttpClient httpClient, string apiKey)`
- Record types for DTOs: `public record AgentDefinition { ... }`
- `required` modifier for mandatory properties
- Collection expressions: `[]` instead of `Array.Empty<T>()`
- Nullable reference types enabled throughout
- `var` for type-apparent declarations
- Pattern matching with `switch` expressions

**Example from `G:\repos\agent\Domain\DTOs\AgentDefinition.cs`:**
```csharp
public record AgentDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public string[] WhitelistPatterns { get; init; } = [];
}
```

## Error Handling

**Patterns:**
- Use `ArgumentNullException.ThrowIfNull()` for guard clauses
- Use `ObjectDisposedException.ThrowIf()` for disposed checks
- Throw specific exception types with descriptive messages
- Return error information in tool responses as JSON with `status` field

**Example from `G:\repos\agent\Domain\Tools\Text\TextPatchTool.cs`:**
```csharp
if (start < 1 || start > totalLines)
{
    throw new ArgumentException($"Start line {start} out of range. File has {totalLines} lines.");
}

if (string.IsNullOrEmpty(url))
{
    throw new InvalidOperationException($"Text '{searchText}' not found in file.");
}
```

**Async Error Handling:**
- Use `try-catch` at appropriate boundaries
- Log exceptions with structured logging
- Convert exceptions to user-friendly messages in streaming pipelines

**Example from `G:\repos\agent\Domain\Extensions\IAsyncEnumerableExtensions.cs`:**
```csharp
public static async IAsyncEnumerable<AgentRunResponseUpdate> WithErrorHandling(
    this IAsyncEnumerable<AgentRunResponseUpdate> source,
    CancellationToken ct = default)
{
    // ... enumerator setup ...
    catch (Exception ex)
    {
        errorResponse = new AgentRunResponseUpdate
        {
            Contents = [new ErrorContent($"An error occurred: {ex.Message}")]
        };
        break;
    }
}
```

## Logging

**Framework:** Microsoft.Extensions.Logging via `ILogger<T>`

**Patterns:**
- Inject `ILogger<T>` via constructor
- Use structured logging with message templates
- Log at appropriate levels: Error for exceptions, Info for significant events

**Example from `G:\repos\agent\Domain\Monitor\ChatMonitor.cs`:**
```csharp
public class ChatMonitor(
    IChatMessengerClient chatMessengerClient,
    IAgentFactory agentFactory,
    ChatThreadResolver threadResolver,
    ILogger<ChatMonitor> logger)
{
    // ...
    catch (Exception ex)
    {
        logger.LogError(ex, "ChatMonitor exception: {exceptionMessage}", ex.Message);
    }
}
```

## Comments

**When to Comment:**
- Explain "why" not "what"
- Complex algorithms or non-obvious behavior
- ReSharper suppression reasons

**JSDoc/XML Documentation:**
- No XML documentation comments (per style rules)
- Use `[UsedImplicitly]` attribute for reflection/serialization properties

**Example comment style:**
```csharp
// ReSharper disable once AccessToDisposedClosure - agent and threadCts are disposed after await foreach completes
var aiResponses = group.Prepend(firstPrompt)
    .Select(async (x, _, _) => ...);
```

## Function Design

**Size:**
- Methods ideally < 20 lines
- Extract helper methods for complex logic

**Parameters:**
- Use primary constructors for dependency injection
- Use `CancellationToken` for all async operations (with default value)
- Use records with `init` properties for complex parameter objects

**Return Values:**
- Use `IReadOnlyList<T>` / `IReadOnlyCollection<T>` for collection returns
- Use `Task<T>` / `ValueTask<T>` for async operations
- Return `null` or use `?` nullable types instead of throwing for "not found"

## LINQ Over Loops

**STRONGLY prefer LINQ over traditional loops:**

```csharp
// GOOD: Use LINQ
var results = items.Where(x => x.IsValid).Select(x => x.Name).ToList();

// BAD: Traditional loop
var results = new List<string>();
foreach (var item in items)
{
    if (item.IsValid)
        results.Add(item.Name);
}
```

**Use LINQ for:**
- `.Where()` for filtering
- `.Select()` for transformation
- `.Any()`, `.All()` for existence checks
- `.First()`, `.FirstOrDefault()` for finding elements
- `.GroupBy()`, `.ToLookup()` for grouping
- `.OrderBy()`, `.ThenBy()` for ordering

**Only use loops when:**
- Mutating external state that can't be avoided
- Performance-critical hot paths (rare)

## Module Design

**Exports:**
- Public API surfaces are minimal
- Use `internal` for implementation details
- Use `private` nested classes for response DTOs

**Example from `G:\repos\agent\Infrastructure\Clients\BraveSearchClient.cs`:**
```csharp
public class BraveSearchClient : IWebSearchClient
{
    // Public interface method
    public async Task<WebSearchResult> SearchAsync(...)

    // Private helper methods
    private static string BuildSearchUrl(...)
    private static WebSearchResult MapToWebSearchResult(...)

    // Private nested types for JSON deserialization
    private record BraveSearchResponse { ... }
    private record BraveWebResult { ... }
}
```

## Async Patterns

**Guidelines:**
- Use `async`/`await` throughout
- Always pass `CancellationToken` and check cancellation
- Use `ValueTask` for hot paths that often complete synchronously
- Use `IAsyncEnumerable<T>` for streaming data

**Cancellation Pattern:**
```csharp
public async Task<T> DoWorkAsync(CancellationToken ct = default)
{
    ct.ThrowIfCancellationRequested();
    await SomeOperation(ct);
    return result;
}
```

## Disposal Patterns

**Use these patterns:**
- Implement `IAsyncDisposable` for async cleanup
- Use `Interlocked.Exchange` for thread-safe disposal flags
- Always dispose MCP clients, HttpClient wrappers

**Example from `G:\repos\agent\Infrastructure\Agents\McpAgent.cs`:**
```csharp
private int _isDisposed;

public override async ValueTask DisposeAsync()
{
    if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
    {
        return;
    }
    // cleanup...
}
```

---

*Convention analysis: 2025-01-19*
