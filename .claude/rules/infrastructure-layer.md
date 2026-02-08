---
paths:
  - "Infrastructure/**/*.cs"
---

# Infrastructure Layer Rules

This layer implements Domain interfaces and handles external concerns.

## Forbidden Dependencies

- NEVER import from `Agent` namespace
- Agent project handles DI and bootstrapping, not Infrastructure

```csharp
// VIOLATION
using Agent.App;

// CORRECT
using Domain.Contracts;
```

## What Belongs Here

- Interface implementations (`Infrastructure/Agents/`, `Infrastructure/Clients/`)
- External service clients (`Infrastructure/Clients/`)
- State persistence (`Infrastructure/StateManagers/`, `Infrastructure/Memory/`)
- MCP integration (`Infrastructure/Agents/Mcp/`)
- CLI components (`Infrastructure/CliGui/`)

## Patterns

- Use primary constructors for dependency injection
- Implement Domain interfaces when the service is consumed by Domain
- Services with a single expected implementation do not require an interface unless Domain needs to use them
- Handle external failures gracefully with proper error messages
- Use `async`/`await` throughout
- Use `CancellationToken` for all async operations
