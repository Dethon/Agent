# Domain Tool Template

Domain tools live in `Domain/Tools/` and contain pure business logic.

## Template

```csharp
namespace Domain.Tools.{Category};

public class {Name}Tool(IDependency dependency)
{
    public const string Name = "{tool-name}";
    public const string Description = "{Clear description of what the tool does}";

    public async Task<{ResultType}> Run(
        string sessionId,
        {parameters},
        CancellationToken cancellationToken)
    {
        // Pure business logic here
        // No MCP dependencies
        // No logging (that's the wrapper's job)
    }
}
```

## Guidelines

- Use primary constructor for dependencies
- Define `Name` and `Description` as constants (used by MCP wrapper)
- Return domain types, not MCP types
- Throw exceptions for errors (MCP wrapper handles conversion)
- Keep methods focused and testable

## Example: Domain Tool

```csharp
namespace Domain.Tools.Files;

public class ListFilesTool(IFileSystemClient fileSystem)
{
    public const string Name = "list-files";
    public const string Description = "Lists files in a directory with optional filtering";

    public async Task<IReadOnlyList<FileInfo>> Run(
        string sessionId,
        string path,
        string? pattern,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);

        var files = await fileSystem.ListFilesAsync(path, pattern, cancellationToken);
        return files;
    }
}
```
