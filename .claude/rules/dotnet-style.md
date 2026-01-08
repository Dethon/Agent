---
paths: "**/*.cs"
---

# .NET Coding Style

## Modern C# Patterns

- File-scoped namespaces: `namespace Foo;`
- Primary constructors for DI: `public class Foo(IBar bar)`
- `record` types for DTOs and immutable data
- Nullable reference types with proper null handling
- `async`/`await` throughout for async operations
- `ArgumentNullException.ThrowIfNull()` for guard clauses
- `IReadOnlyList<T>` / `IReadOnlyCollection<T>` for return types
- `TimeProvider` for testable time-dependent code

## Clean Code

- Single Responsibility - one reason to change per class
- Prefer composition over inheritance
- Methods < 20 lines ideal
- Meaningful names that reveal intent
- Minimize mutable state and side effects

## Documentation

- Prioritize readable code over comments
- No XML documentation comments
- Only comment to explain "why", never "what"
