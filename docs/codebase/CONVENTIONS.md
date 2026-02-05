# Code Conventions

## C# Style

### Namespaces
- File-scoped namespaces: `namespace Domain.Contracts;`

### Classes
- Primary constructors for dependency injection:
```csharp
public class MyService(ILogger<MyService> logger, IRepository repo)
{
    // No explicit field declarations needed
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

### Nullable Reference Types
- Always enabled (`<Nullable>enable</Nullable>`)
- Use `?` for nullable, explicit null checks

### Async/Await
- All async operations use `async`/`await`
- Pass `CancellationToken` through all async chains
- Name async methods with `Async` suffix

### Guard Clauses
```csharp
ArgumentNullException.ThrowIfNull(parameter);
ObjectDisposedException.ThrowIf(_isDisposed == 1, this);
```

### Collections
- Return `IReadOnlyList<T>` or `IReadOnlyCollection<T>`
- Accept `IEnumerable<T>` as parameters

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

## Domain Layer Rules

- No external framework dependencies
- Interfaces only for services Domain consumes
- Pure business logic, no state management
- DTOs as records

## Infrastructure Layer Rules

- Implements Domain interfaces
- Handles external communication
- Uses primary constructors for DI
- Graceful error handling

## MCP Tool Pattern

```csharp
[McpServerToolType]
public class McpExampleTool(IDep dep) : ExampleTool(dep)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        string param,
        CancellationToken cancellationToken)
    {
        var sessionId = context.Server.StateKey;
        var result = await Run(sessionId, param, cancellationToken);
        return ToolResponse.Create(result);
    }
}
```

- Inherit from Domain tool
- Use `[McpServerToolType]` and `[McpServerTool]`
- No try/catch (global filter handles errors)
- Use `context.Server.StateKey` for session

## Test Conventions

### Test-Driven Development (TDD)
1. Red - Write failing test first
2. Green - Minimum implementation to pass
3. Refactor - Clean up while tests pass

### Naming
```csharp
public class MyServiceTests
{
    [Fact]
    public async Task MethodName_Condition_ExpectedResult()
    {
        // Arrange
        // Act
        // Assert
    }
}
```

### Assertions
- Use Shouldly for assertions:
```csharp
result.ShouldBe(expected);
result.ShouldNotBeNull();
items.ShouldContain(item);
```

## Documentation

- Prioritize readable code over comments
- No XML documentation comments
- Comment only to explain "why", never "what"
- No emoji in code

## Import Organization

1. System namespaces
2. Microsoft namespaces
3. Third-party packages
4. Project namespaces (Domain, Infrastructure)

## Error Handling

- Use `FluentResults` for result types where appropriate
- Throw exceptions for truly exceptional cases
- Log errors at Infrastructure layer
- MCP tools use global filter via `AddCallToolFilter`
