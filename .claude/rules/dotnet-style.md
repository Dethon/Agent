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

## LINQ Over Loops

**STRONGLY prefer LINQ over `for`/`foreach`/`while` loops.** Traditional loops should only be used when truly necessary.

Use LINQ for:
- Filtering: `.Where()` not `if` inside loop
- Transformation: `.Select()` not building new list in loop
- Aggregation: `.Sum()`, `.Count()`, `.Aggregate()` not manual accumulation
- Existence checks: `.Any()`, `.All()` not flag variables
- Finding elements: `.First()`, `.FirstOrDefault()`, `.Single()` not loop-and-break
- Grouping: `.GroupBy()`, `.ToLookup()` not dictionary building in loop
- Ordering: `.OrderBy()`, `.ThenBy()` not manual sorting
- Set operations: `.Distinct()`, `.Union()`, `.Intersect()`, `.Except()`

Only use traditional loops when:
- Mutating external state that can't be avoided
- Complex control flow with `break`/`continue` that LINQ can't express cleanly
- Performance-critical hot paths where LINQ overhead matters (rare)
- Index manipulation that `.Select((item, index) => ...)` can't handle

```csharp
// BAD
var results = new List<string>();
foreach (var item in items)
{
    if (item.IsValid)
        results.Add(item.Name);
}

// GOOD
var results = items.Where(x => x.IsValid).Select(x => x.Name).ToList();
```

## Documentation

- Prioritize readable code over comments
- No XML documentation comments
- Only comment to explain "why", never "what"
