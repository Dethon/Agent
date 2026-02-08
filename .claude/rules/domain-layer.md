---
paths:
  - "Domain/**/*.cs"
---

# Domain Layer Rules

This is the innermost layer - pure business logic with no external dependencies.

## Forbidden Dependencies

- NEVER import from `Infrastructure` or `Agent` namespaces
- NEVER reference framework-specific types (HttpClient, DbContext, etc.)
- NEVER depend on concrete implementations

```csharp
// VIOLATION
using Infrastructure.Clients;
private readonly HttpClient _client;

// CORRECT
public interface IHttpService { }
```

## What Belongs Here

- Interfaces/contracts (`Domain/Contracts/`)
- DTOs and value objects (`Domain/DTOs/`)
- Domain tools with pure logic (`Domain/Tools/`)
- Domain services and agents (`Domain/Agents/`)
- Prompts (`Domain/Prompts/`)
- Exceptions

## Patterns

- Use `record` types for DTOs
- Interfaces should be minimal and focused
- No state management - that's Infrastructure's job
- Only define interfaces for services that Domain needs to consume; single-implementation services used only by Agent layer do not need interfaces here
