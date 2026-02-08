---
paths:
  - "Tests/**/*.cs"
---

# Testing Rules

## Directory Structure

- `Tests/Unit/` - Fast, isolated unit tests
- `Tests/Integration/` - Tests requiring external resources (Redis, APIs)
- `Tests/Integration/Fixtures/` - Shared test fixtures

## Naming Convention

- Test classes: `{ClassUnderTest}Tests.cs`
- Test methods: `{Method}_{Scenario}_{ExpectedResult}`

```csharp
[Fact]
public void Run_TextNotFound_ThrowsWithSuggestion()
```

## Patterns

- **Prefer integration tests over mocks** - Test real behavior, not mock implementations
- Avoid mocking when possible; use real dependencies with testcontainers
- Use `Shouldly` for assertions (`result.ShouldBe()`, `Should.Throw<>()`)
- Create testable wrappers for classes with protected methods
- Use `IDisposable` for cleanup of temp files/directories
- Integration tests requiring Redis use `RedisFixture`

## Example Structure

```csharp
public class ExampleToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableExampleTool _tool;

    public ExampleToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableExampleTool(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void Run_ValidInput_ReturnsExpected()
    {
        var result = _tool.TestRun("input");
        result.ShouldBe("expected");
    }
}
```
