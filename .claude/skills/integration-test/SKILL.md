---
name: integration-test
description: Create integration tests. Use when writing tests, testing features, adding test coverage, or verifying functionality.
allowed-tools: Read, Glob, Grep, Edit, Write
---

# Creating Integration Tests

Prefer integration tests over mocks. Test real behavior with real dependencies.

## Principles

- **Avoid mocks** - Use real dependencies or testcontainers
- **Test behavior** - Not implementation details
- **Use Shouldly** - For readable assertions

## Test Location

| Type | Path | When to Use |
|------|------|-------------|
| Unit | `Tests/Unit/{Layer}/` | Pure logic, no external deps |
| Integration | `Tests/Integration/` | External services, Redis, HTTP |

## Patterns

See [patterns.md](patterns.md) for common test patterns:
- File system tests with temp directories
- Redis tests with `RedisFixture`
- HTTP client tests
- Testable wrapper pattern

## Naming

- Class: `{ClassUnderTest}Tests.cs`
- Method: `{Method}_{Scenario}_{ExpectedResult}`

```csharp
[Fact]
public async Task Run_ValidInput_ReturnsExpectedResult()

[Fact]
public void Parse_InvalidFormat_ThrowsArgumentException()
```

## Checklist

- [ ] Using real dependencies (not mocks)
- [ ] Proper cleanup with `IDisposable`
- [ ] Assertions use Shouldly
- [ ] CancellationToken passed where applicable
- [ ] Test method name describes scenario and expectation
