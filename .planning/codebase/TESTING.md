# Testing Patterns

**Analysis Date:** 2025-01-19

## Test Framework

**Runner:**
- xUnit 2.9.3
- Config: `Tests/Tests.csproj`

**Assertion Library:**
- Shouldly 4.3.0

**Run Commands:**
```bash
dotnet test                           # Run all tests
dotnet test --filter "Category=Unit"  # Run unit tests only
dotnet test --collect:"XPlat Code Coverage"  # With coverage
```

## Test File Organization

**Location:**
- Unit tests: `Tests/Unit/{Layer}/{ClassUnderTest}Tests.cs`
- Integration tests: `Tests/Integration/{Category}/{TestName}Tests.cs`
- Fixtures: `Tests/Integration/Fixtures/{Name}Fixture.cs`
- Fake implementations: `Tests/Unit/{Layer}/Fixtures/Fake{ServiceName}.cs`

**Naming:**
- Test classes: `{ClassUnderTest}Tests`
- Test methods: `{Method}_{Scenario}_{ExpectedResult}`

**Structure:**
```
Tests/
├── Unit/
│   ├── Domain/
│   │   ├── GroupByStreamingTests.cs
│   │   ├── RemoveFileToolTests.cs
│   │   └── Text/
│   │       ├── TextPatchToolTests.cs
│   │       └── TextReadToolTests.cs
│   ├── Infrastructure/
│   │   ├── BraveSearchClientTests.cs
│   │   ├── ToolPatternMatcherTests.cs
│   │   └── Memory/
│   │       └── MemoryEntryTests.cs
│   └── WebChat/
│       ├── Client/
│       │   ├── StreamResumeServiceTests.cs
│       │   └── ChatStateManagerTests.cs
│       └── Fixtures/
│           ├── FakeChatMessagingService.cs
│           └── FakeTopicService.cs
└── Integration/
    ├── Agents/
    │   ├── McpAgentIntegrationTests.cs
    │   └── ThreadSessionIntegrationTests.cs
    ├── Clients/
    │   ├── BraveSearchClientIntegrationTests.cs
    │   └── JackettSearchClientTests.cs
    ├── Fixtures/
    │   ├── McpLibraryServerFixture.cs
    │   ├── RedisFixture.cs
    │   ├── JackettFixture.cs
    │   └── QBittorrentFixture.cs
    └── WebChat/
        └── ChatHubIntegrationTests.cs
```

## Test Structure

**Suite Organization:**
```csharp
public class ToolPatternMatcherTests
{
    [Theory]
    [InlineData("mcp:server:Tool", "mcp:server:Tool", true)]
    [InlineData("mcp:server:Tool", "mcp:server:tool", true)]
    [InlineData("mcp:server:Tool", "mcp:server:OtherTool", false)]
    public void IsMatch_ExactPattern_MatchesCorrectly(string toolName, string pattern, bool expected)
    {
        var matcher = new ToolPatternMatcher([pattern]);
        matcher.IsMatch(toolName).ShouldBe(expected);
    }

    [Fact]
    public void IsMatch_EmptyPatterns_MatchesNothing()
    {
        var matcher = new ToolPatternMatcher([]);
        matcher.IsMatch("mcp:server:Tool").ShouldBeFalse();
    }
}
```

**Patterns:**
- Arrange-Act-Assert structure
- Clear separation with comments (`// Arrange`, `// Act`, `// Assert`)
- One assertion concept per test (multiple ShouldBe calls for same concept OK)
- Use `#region` for logical grouping of related tests

**Example with regions from `G:\repos\agent\Tests\Unit\WebChat\Client\StreamResumeServiceTests.cs`:**
```csharp
#region Resume Guard Tests

[Fact]
public async Task TryResumeStreamAsync_WhenAlreadyResuming_DoesNotDuplicateResume()
{
    // test implementation
}

#endregion

#region History Loading Tests

[Fact]
public async Task TryResumeStreamAsync_LoadsHistoryIfNeeded()
{
    // test implementation
}

#endregion
```

## Mocking

**Framework:** Moq 4.20.72

**Patterns:**
```csharp
// Setup
private readonly Mock<IFileSystemClient> _fileSystemClientMock = new();

// Configure
_fileSystemClientMock
    .Setup(m => m.MoveToTrash(filePath, It.IsAny<CancellationToken>()))
    .ReturnsAsync(trashPath);

// Verify
_fileSystemClientMock.Verify(
    m => m.MoveToTrash(It.IsAny<string>(), It.IsAny<CancellationToken>()),
    Times.Never);
```

**What to Mock:**
- External services (HTTP clients, databases)
- File system operations when testing business logic
- Time-dependent behavior

**What NOT to Mock:**
- Domain logic under test
- Simple DTOs and value objects
- Prefer integration tests with real dependencies over mocks

**Preference for Real Dependencies:**
The codebase strongly prefers integration tests with Testcontainers over mocking:
```csharp
// Preferred: Real Redis via Testcontainers
public class RedisFixture : IAsyncLifetime
{
    private IContainer _container = null!;
    public IConnectionMultiplexer Connection { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("redis/redis-stack:latest")
            .WithPortBinding(RedisPort, true)
            .Build();
        await _container.StartAsync();
    }
}
```

## Fixtures and Factories

**Test Data:**
```csharp
// Static test data arrays
private static readonly int[] _sourceArray = [1, 2, 3, 4, 5, 6];
private static readonly string[] _sourceArray2 = ["a1", "b1", "a2", "c1", "b2"];

// Factory methods
private static StoredTopic CreateTopic(string? topicId = null)
{
    var id = topicId ?? Guid.NewGuid().ToString();
    return new StoredTopic
    {
        TopicId = id,
        ChatId = (Math.Abs(id.GetHashCode()) % 10000) + 1000,
        ThreadId = (Math.Abs(id.GetHashCode()) % 10000) + 2000,
        AgentId = "test-agent",
        Name = "Test Topic",
        CreatedAt = DateTime.UtcNow
    };
}
```

**Location:**
- Shared fixtures: `Tests/Integration/Fixtures/`
- Test-specific fake services: `Tests/Unit/{Layer}/Fixtures/`

**Fixture Pattern:**
```csharp
public class McpLibraryServerFixture : IAsyncLifetime
{
    private IHost _host = null!;

    public string McpEndpoint { get; private set; } = null!;
    public string LibraryPath { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Start containers, create temp directories, start server
    }

    public void CreateLibraryFile(string relativePath, string content = "test content")
    {
        // Helper method for tests
    }

    public async Task DisposeAsync()
    {
        // Cleanup containers, temp directories
    }
}
```

**Fake Service Pattern:**
```csharp
public sealed class FakeChatMessagingService : IChatMessagingService
{
    private readonly Queue<ChatStreamMessage> _enqueuedMessages = new();

    public void EnqueueMessages(params ChatStreamMessage[] messages)
    {
        foreach (var msg in messages)
            _enqueuedMessages.Enqueue(msg);
    }

    public async IAsyncEnumerable<ChatStreamMessage> SendMessageAsync(string topicId, string message)
    {
        while (_enqueuedMessages.TryDequeue(out var msg))
            yield return msg;
    }
}
```

## Coverage

**Requirements:** None enforced

**Collection:**
- coverlet.collector 6.0.4 configured

**View Coverage:**
```bash
dotnet test --collect:"XPlat Code Coverage"
```

## Test Types

**Unit Tests:**
- Location: `Tests/Unit/`
- Scope: Single class/method in isolation
- Approach: Mock external dependencies, test business logic
- Speed: Fast, no external resources

**Integration Tests:**
- Location: `Tests/Integration/`
- Scope: Multiple components with real dependencies
- Approach: Use Testcontainers for Redis, Docker services
- Speed: Slower, requires Docker

**Skippable Tests:**
- Use `[SkippableFact]` from Xunit.SkippableFact for tests requiring API keys
- Skip with `throw new SkipException("reason")`

```csharp
[SkippableFact]
public async Task Agent_WithListDirectoriesTool_CanListLibraryDirectories()
{
    var llmClient = CreateLlmClient(); // throws SkipException if API key missing
    // ...
}
```

## Common Patterns

**Async Testing:**
```csharp
[Fact]
public async Task GroupByStreaming_WithAsyncKeySelector_AwaitsCorrectly()
{
    // Arrange
    var source = _sourceArray.ToAsyncEnumerable();
    var keySelectorCalled = 0;

    // Act
    var groups = await source
        .GroupByStreaming(async (x, ct) =>
        {
            keySelectorCalled++;
            await Task.Delay(10, ct);
            return x % 2;
        })
        .ToListAsync();

    // Assert
    keySelectorCalled.ShouldBe(3);
    groups.Count.ShouldBe(2);
}
```

**Error/Exception Testing:**
```csharp
[Fact]
public async Task Run_WithPathOutsideLibrary_ThrowsInvalidOperationException()
{
    // Arrange
    var tool = CreateTool();
    var outsidePath = "/other/folder/file.txt";

    // Act & Assert
    var exception = await Should.ThrowAsync<InvalidOperationException>(async () =>
        await tool.TestRun(outsidePath, CancellationToken.None));

    exception.Message.ShouldContain("must be within the library");
    _fileSystemClientMock.Verify(
        m => m.MoveToTrash(It.IsAny<string>(), It.IsAny<CancellationToken>()),
        Times.Never);
}
```

**Cancellation Testing:**
```csharp
[Fact]
public async Task GroupByStreaming_WithCancellation_StopsProcessing()
{
    // Arrange
    var cts = new CancellationTokenSource();
    var processedCount = 0;

    // Act
    var groups = infiniteSource()
        .GroupByStreaming((x, _) =>
        {
            processedCount++;
            if (processedCount >= 5) cts.Cancel();
            return ValueTask.FromResult(x % 2);
        }, cts.Token);

    // Assert
    await Should.ThrowAsync<OperationCanceledException>(async () =>
    {
        await foreach (var _ in groups) { }
    });
    processedCount.ShouldBeGreaterThanOrEqualTo(5);
}
```

**WireMock for HTTP Testing:**
```csharp
public class BraveSearchClientTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly BraveSearchClient _client;

    public BraveSearchClientTests()
    {
        _server = WireMockServer.Start();
        var httpClient = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _client = new BraveSearchClient(httpClient, "test-api-key");
    }

    [Fact]
    public async Task SearchAsync_WithValidQuery_ReturnsResults()
    {
        // Arrange
        _server.Given(Request.Create()
                .WithPath("/web/search")
                .WithHeader("X-Subscription-Token", "test-api-key")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithBody(JsonSerializer.Serialize(response)));

        // Act & Assert
        var result = await _client.SearchAsync(query);
        result.ShouldNotBeNull();
    }

    public void Dispose() => _server.Dispose();
}
```

**Testable Wrapper Pattern:**
```csharp
// For testing protected methods
private class TestableRemoveFileTool(
    IFileSystemClient client,
    LibraryPathConfig libraryPath)
    : RemoveFileTool(client, libraryPath)
{
    public Task<JsonNode> TestRun(string path, CancellationToken ct)
    {
        return Run(path, ct);  // Protected method now accessible
    }
}
```

## Testcontainers Usage

**Supported Services:**
- Redis: `redis/redis-stack:latest`
- Jackett: `lscr.io/linuxserver/jackett:0.24.306`
- qBittorrent: Custom image

**Pattern:**
```csharp
public class RedisFixture : IAsyncLifetime
{
    private IContainer _container = null!;
    public IConnectionMultiplexer Connection { get; private set; } = null!;
    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("redis/redis-stack:latest")
            .WithPortBinding(6379, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("Ready to accept connections"))
            .Build();

        await _container.StartAsync();

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(6379);
        ConnectionString = $"{host}:{port}";
        Connection = await ConnectionMultiplexer.ConnectAsync(ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await Connection.DisposeAsync();
        await _container.DisposeAsync();
    }
}
```

**Using Fixtures in Tests:**
```csharp
public class McpAgentIntegrationTests(McpLibraryServerFixture mcpFixture, RedisFixture redisFixture)
    : IClassFixture<McpLibraryServerFixture>, IClassFixture<RedisFixture>
{
    [SkippableFact]
    public async Task Agent_WithListDirectoriesTool_CanListLibraryDirectories()
    {
        mcpFixture.CreateLibraryStructure("Movies");
        // use mcpFixture.McpEndpoint, redisFixture.Connection
    }
}
```

## Cleanup Patterns

**IDisposable for synchronous cleanup:**
```csharp
public class BraveSearchClientTests : IDisposable
{
    private readonly WireMockServer _server;

    public void Dispose()
    {
        _server.Dispose();
    }
}
```

**IAsyncLifetime for async cleanup:**
```csharp
public class RedisFixture : IAsyncLifetime
{
    public async Task InitializeAsync() { /* setup */ }
    public async Task DisposeAsync() { /* cleanup */ }
}
```

**Temp Directory Cleanup:**
```csharp
public async Task DisposeAsync()
{
    await _host.StopAsync();
    _host.Dispose();

    try
    {
        if (Directory.Exists(LibraryPath))
            Directory.Delete(LibraryPath, true);
    }
    catch { /* ignore cleanup errors */ }
}
```

---

*Testing analysis: 2025-01-19*
