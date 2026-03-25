---
paths:
  - "Tests/**/*.cs"
---

# Testing Rules

## Directory Structure

- `Tests/Unit/` - Fast, isolated unit tests
- `Tests/Integration/` - Tests requiring external resources (Redis, APIs)
- `Tests/Integration/Fixtures/` - Shared test fixtures
- `Tests/E2E/` - End-to-end Playwright browser tests
- `Tests/E2E/Fixtures/` - E2E fixtures and helpers

## Naming Convention

- Unit/integration test classes: `{ClassUnderTest}Tests.cs`
- E2E test classes: `{Feature}E2ETests.cs`
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

## E2E Tests

- E2E tests use Playwright via `Microsoft.Playwright` and run against the full Docker Compose stack
- Fixtures extend `E2EFixtureBase` (`IAsyncLifetime`) which manages browser lifecycle and container startup
- Specific fixtures (`WebChatE2EFixture`, `DashboardE2EFixture`) handle stack-specific setup
- Use `[Collection("...")]` to share a fixture across test classes in the same feature area
- Use `[Trait("Category", "E2E")]` to tag E2E tests
- Use `[SkippableFact]` with `Skip.If(...)` to skip when the required stack is unavailable
- Set `PLAYWRIGHT_HEADLESS=false` to run with a visible browser for debugging
- Each test gets a unique user identity via `fixture.NextUserIndex()` to avoid state collisions

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
