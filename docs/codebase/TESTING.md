# Testing

## Framework & Tools

| Tool | Version | Purpose |
|------|---------|---------|
| xUnit | 2.9.3 | Test framework |
| Moq | 4.20.72 | Mocking (interfaces and virtual methods) |
| Shouldly | 4.3.0 | Fluent assertions |
| Testcontainers | 4.10.0 | Docker containers for integration tests |
| Testcontainers.ServiceBus | 4.10.0 | Azure Service Bus emulator container |
| WireMock.Net | 1.25.0 | HTTP API mocking |
| Microsoft.AspNetCore.Mvc.Testing | 10.0.2 | WebApplicationFactory for integration tests |
| Xunit.SkippableFact | 1.5.61 | Conditional test skipping (e.g., missing API keys) |
| coverlet.collector | 6.0.4 | Code coverage collection |
| ModelContextProtocol.AspNetCore | 0.8.0-preview.1 | MCP server testing |
| Microsoft.AspNetCore.SignalR.Client | 10.0.2 | SignalR hub integration tests |

## Test Configuration

```json
// xunit.runner.json
{
  "parallelizeAssembly": true,
  "parallelizeTestCollections": true,
  "maxParallelThreads": 0
}
```

Global using in `Tests.csproj`:
```xml
<Using Include="Xunit"/>
```

## Test Organization

```
Tests/
+-- Integration/                # Tests with real dependencies
|   +-- Agents/                 # McpAgent, ThreadSession, ToolApproval tests
|   +-- Clients/                # External client tests (Telegram, Playwright, qBittorrent, Jackett)
|   +-- Domain/                 # Domain integration tests (state managers)
|   +-- Fixtures/               # Shared test fixtures
|   +-- Infrastructure/         # Infrastructure integration tests (LocalFileSystem)
|   +-- Jack/                   # Dependency injection tests
|   +-- McpServerTests/         # MCP server integration tests (Library, Subscription)
|   +-- McpTools/               # MCP tool integration tests
|   +-- Memory/                 # Redis memory store, embedding tests
|   +-- Messaging/              # Service Bus end-to-end integration tests
|   +-- StateManagers/          # Redis schedule store tests
|   +-- WebChat/                # SignalR hub, session persistence, stream resume tests
|       +-- Client/             # WebChat.Client integration tests (streaming, adapters)
|
+-- Unit/                       # Isolated unit tests
    +-- Domain/                 # Domain logic tests
    |   +-- DTOs/               # DTO tests (WebChat)
    |   +-- Scheduling/         # Schedule tool tests
    |   +-- Text/               # Text tool tests (create, read, edit, search)
    +-- Infrastructure/         # Infrastructure tests
    |   +-- Cli/                # CLI routing tests
    |   +-- Memory/             # Memory entry, embedding mock tests
    |   +-- Messaging/          # ServiceBus, Composite, WebChat client unit tests
    +-- McpServerLibrary/       # MCP server unit tests (SubscriptionTracker)
    +-- Tools/                  # Shared tool tests (GlobFiles)
    +-- WebChat/                # WebChat tests
    |   +-- Client/             # Reducers, streaming, buffer, toast, model tests
    |   +-- Fixtures/           # WebChat test fixtures
    +-- WebChat.Client/         # Client state management tests
        +-- Services/           # SignalR event subscriber tests
        +-- State/              # Store tests (Messages, Topics, Connection, Streaming, Approval)
            +-- Pipeline/       # Message pipeline tests
```

## Test Fixtures

### RedisFixture
Starts a Redis Stack container via Testcontainers for integration tests needing Redis.

```csharp
public class RedisFixture : IAsyncLifetime
{
    public IConnectionMultiplexer Connection { get; }
    public string ConnectionString { get; }
    // Starts redis/redis-stack:latest container
}
```

Used by: `RedisMemoryStoreTests`, `RedisScheduleStoreTests`, `McpAgentIntegrationTests`, `WebChatServerFixture`, `ServiceBusFixture`

### ThreadSessionServerFixture
Starts a real MCP server with HTTP transport for thread session integration tests.

```csharp
public class ThreadSessionServerFixture : IAsyncLifetime
{
    public string McpEndpoint { get; }
    public TestDownloadClient DownloadClient { get; }
    // Starts Kestrel with MCP server, test tools, prompts, resources
}
```

Includes embedded test tools (`TestEchoTool`, `TestResubscribeDownloadsTool`), test prompts (`TestPrompt`), and test resources (`TestDownloadResource`).

### McpLibraryServerFixture
Starts an MCP Library server with real Jackett and qBittorrent Docker containers for full-stack integration tests.

```csharp
public class McpLibraryServerFixture : IAsyncLifetime
{
    public string McpEndpoint { get; }
    public string LibraryPath { get; }
    public string DownloadPath { get; }
    // Starts Jackett, qBittorrent containers + Kestrel MCP server
}
```

Provides helper methods: `CreateLibraryFile()`, `CreateLibraryStructure()`, `FileExistsInLibrary()`

### WebChatServerFixture
Starts a full WebChat server with SignalR hub, Redis, and fake agent factory.

```csharp
public sealed class WebChatServerFixture : IAsyncLifetime
{
    public FakeAgentFactory FakeAgentFactory { get; }
    public HubConnection CreateHubConnection();
    // Starts Kestrel with SignalR hub, Redis, ChatMonitor
}
```

### ServiceBusFixture
Starts Azure Service Bus emulator and Redis containers for end-to-end messaging tests.

```csharp
public class ServiceBusFixture : IAsyncLifetime
{
    public string ConnectionString { get; }
    public IConnectionMultiplexer RedisConnection { get; }
    public Task SendPromptAsync(string prompt, string sender, ...);
    public Task<ServiceBusReceivedMessage?> ReceiveResponseAsync(TimeSpan timeout);
    public (ServiceBusChatMessengerClient, ServiceBusProcessorHost) CreateClientAndHost();
}
```

### TelegramBotFixture
Uses WireMock.Net to simulate Telegram Bot API without a real bot.

```csharp
public class TelegramBotFixture : IAsyncLifetime
{
    public TelegramChatClient CreateClient();
    public void SetupGetUpdates(object[] updates);
    public void SetupSendMessage(long chatId);
    public static object CreateTextMessageUpdate(...);
}
```

### PlaywrightWebBrowserFixture
Manages Playwright browser lifecycle, with fallback from local to containerized browser.

```csharp
public class PlaywrightWebBrowserFixture : IAsyncLifetime
{
    public PlaywrightWebBrowser Browser { get; }
    public bool IsAvailable { get; }
    // Tries local Playwright first, falls back to browserless/chrome container
}
```

Uses `[CollectionDefinition]` for shared browser context across tests.

### FakeAgentFactory
In-memory fake for `IAgentFactory` that allows enqueuing responses, tool calls, reasoning, and errors for WebChat integration tests.

```csharp
public sealed class FakeAgentFactory : IAgentFactory
{
    public void EnqueueResponses(params string[] responses);
    public void EnqueueToolCall(string toolName, ...);
    public void EnqueueReasoning(string reasoning);
    public void EnqueueError(string errorMessage);
}
```

## Test Naming Convention

- Test classes: `{ClassUnderTest}Tests`
- Test methods: `{Method}_{Scenario}_{ExpectedResult}`

```csharp
public class ToolPatternMatcherTests
{
    [Fact]
    public void IsMatch_ExactPattern_MatchesCorrectly() { ... }

    [Fact]
    public void IsMatch_EmptyPatterns_MatchesNothing() { ... }
}
```

For async tests:
```csharp
[Fact]
public async Task Run_WithValidPath_MovesToTrash() { ... }
```

## Writing Tests

### Unit Test Pattern
```csharp
public class ToolPatternMatcherTests
{
    [Fact]
    public void IsMatch_ExactPattern_MatchesCorrectly()
    {
        // Arrange
        var matcher = new ToolPatternMatcher(["mcp:server:Tool"]);

        // Act
        var result = matcher.IsMatch("mcp:server:Tool");

        // Assert
        result.ShouldBeTrue();
    }
}
```

### Theory/InlineData Pattern
```csharp
[Theory]
[InlineData("mcp:server:Tool", "mcp:server:Tool", true)]
[InlineData("mcp:server:Tool", "mcp:server:tool", true)]  // case insensitive
[InlineData("mcp:server:Tool", "mcp:server:OtherTool", false)]
public void IsMatch_ExactPattern_MatchesCorrectly(string toolName, string pattern, bool expected)
{
    var matcher = new ToolPatternMatcher([pattern]);
    matcher.IsMatch(toolName).ShouldBe(expected);
}
```

### Testable Wrapper Pattern
For Domain tools with `protected` methods, create private nested testable wrappers:

```csharp
public class TextEditToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTextEditTool _tool;

    public TextEditToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTextEditTool(_testDir, [".md", ".txt"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }

    [Fact]
    public void Run_SingleOccurrence_ReplacesText()
    {
        var filePath = CreateTestFile("test.txt", "Hello World");
        var result = _tool.TestRun(filePath, "World", "Universe");
        result["status"]!.ToString().ShouldBe("success");
    }

    private class TestableTextEditTool(string vaultPath, string[] extensions)
        : TextEditTool(vaultPath, extensions)
    {
        public JsonNode TestRun(string path, string old, string @new, bool all = false)
            => Run(path, old, @new, all);
    }
}
```

### Integration Test with IClassFixture
```csharp
public class RedisMemoryStoreTests(RedisFixture redisFixture) : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task StoreAsync_AndGetById_ReturnsStoredMemory()
    {
        var store = new RedisStackMemoryStore(redisFixture.Connection);
        var userId = $"user_{Guid.NewGuid():N}";
        var memory = CreateMemory(userId, "User prefers TypeScript");

        await store.StoreAsync(memory);
        var retrieved = await store.GetByIdAsync(userId, memory.Id);

        retrieved.ShouldNotBeNull();
        retrieved.Content.ShouldBe("User prefers TypeScript");
    }
}
```

### Integration Test with IAsyncLifetime
For tests needing per-test setup/teardown:

```csharp
public class ServiceBusIntegrationTests(ServiceBusFixture fixture)
    : IClassFixture<ServiceBusFixture>, IAsyncLifetime
{
    private ServiceBusChatMessengerClient _messengerClient = null!;
    private CancellationTokenSource _cts = null!;

    public async Task InitializeAsync()
    {
        (_messengerClient, _) = fixture.CreateClientAndHost();
        _cts = new CancellationTokenSource();
    }

    public async Task DisposeAsync()
    {
        await _cts.CancelAsync();
        _cts.Dispose();
    }
}
```

### Skippable Tests
For tests requiring external resources (API keys, services):

```csharp
[SkippableFact]
public async Task Agent_WithGlobFilesTool_CanFindFiles()
{
    var apiKey = _configuration["openRouter:apiKey"]
        ?? throw new SkipException("openRouter:apiKey not set in user secrets");
    // ...
}
```

### WebChat State Tests (Redux-like Pattern)
```csharp
public class MessagesStoreTests : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly MessagesStore _store;

    public MessagesStoreTests()
    {
        _dispatcher = new Dispatcher();
        _store = new MessagesStore(_dispatcher);
    }

    public void Dispose() => _store.Dispose();

    [Fact]
    public void MessagesLoaded_PopulatesMessagesByTopic()
    {
        var messages = new List<ChatMessageModel> { new() { Role = "user", Content = "Hello" } };
        _dispatcher.Dispatch(new MessagesLoaded("topic-1", messages));
        _store.State.MessagesByTopic["topic-1"].Count.ShouldBe(1);
    }
}
```

## Assertions

Use Shouldly for all assertions:

```csharp
// Equality
result.ShouldBe(expected);
result.ShouldNotBe(unexpected);

// Null checks
result.ShouldNotBeNull();
result.ShouldBeNull();

// Boolean
result.ShouldBeTrue();
result.ShouldBeFalse();

// Collections
items.ShouldContain(item);
items.ShouldNotBeEmpty();
items.Count.ShouldBe(3);
items.ShouldHaveSingleItem();

// Type checks
result.ShouldBeOfType<ParseSuccess>();

// Comparisons
value.ShouldBeGreaterThan(min);
value.ShouldBeGreaterThanOrEqualTo(min);

// String
text.ShouldContain("substring");
text.ShouldNotBeNullOrEmpty();

// Exceptions
Should.Throw<InvalidOperationException>(() => action());
await Should.ThrowAsync<InvalidOperationException>(async () => await asyncAction());

// Lambda predicates
items.ShouldContain(p => p.Name == "expected");
items.All(r => r.Category == expected).ShouldBeTrue();
```

## Mocking Patterns

### Interface Mocking with Moq
```csharp
var mockStore = new Mock<IMemoryStore>();
mockStore
    .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, null, null, 10, default))
    .ReturnsAsync(new List<MemorySearchResult>());

// Verify calls
_clientMock.Verify(m => m.MoveToTrash(filePath, It.IsAny<CancellationToken>()), Times.Once);
_clientMock.Verify(m => m.Move(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
```

### Mock field initialization pattern
```csharp
public class RemoveToolTests
{
    private readonly Mock<IFileSystemClient> _fileSystemClientMock = new();
    // Mocks initialized inline as class fields
}
```

### HTTP Mocking with WireMock
```csharp
var server = WireMockServer.Start();
server.Given(
        Request.Create()
            .WithPath("/bot{token}/getUpdates")
            .UsingAnyMethod())
    .RespondWith(
        Response.Create()
            .WithStatusCode(200)
            .WithHeader("Content-Type", "application/json")
            .WithBody(JsonSerializer.Serialize(response)));
```

### Test Helper Methods
Factory methods for creating test data:

```csharp
private static MemoryEntry CreateMemory(
    string userId,
    string content,
    MemoryCategory category = MemoryCategory.Fact,
    double importance = 0.5)
{
    return new MemoryEntry
    {
        Id = $"mem_{Guid.NewGuid():N}",
        UserId = userId,
        Category = category,
        Content = content,
        // ...
    };
}
```

## Test-Driven Development

### Workflow
1. **Red** - Write a failing test first that defines the expected behavior
2. **Green** - Write the minimum implementation code to make the test pass
3. **Refactor** - Clean up the code while keeping all tests green

### Rules
- Never write implementation code without a failing test first
- Write one test at a time, then make it pass before writing the next
- Run the test suite after each change to confirm the cycle
- Tests must actually fail before implementation (verify the "red" step)
- Keep implementation minimal -- only write enough code to pass the current test

### When to Apply TDD
- **New features**: Start with a test describing the desired behavior
- **Bug fixes**: Start with a test that reproduces the bug, then fix it
- **Refactoring**: Ensure tests exist and pass before and after changes

### Exceptions
- Pure configuration changes (appsettings, DI registration)
- Trivial one-line changes where the risk is negligible

## Test Patterns Summary

| Pattern | When to Use | Example |
|---------|------------|---------|
| `IClassFixture<T>` | Shared expensive resource across test class | Redis, MCP server |
| `IAsyncLifetime` | Per-test async setup/teardown | ServiceBus processor start/stop |
| `IDisposable` | Sync cleanup (temp files) | TextEditToolTests, MessagesStoreTests |
| `ICollectionFixture<T>` | Shared resource across multiple test classes | PlaywrightWebBrowserFixture |
| `[Fact]` | Single test case | Most unit tests |
| `[Theory] + [InlineData]` | Parameterized tests | ToolPatternMatcherTests |
| `[SkippableFact]` | Tests requiring optional external resources | OpenRouter API tests |
| Testable wrapper | Testing protected Domain tool methods | TextEditTool, RemoveTool |
| FakeAgentFactory | Controllable agent behavior in WebChat tests | ChatHubIntegrationTests |

## Running Tests

```bash
# All tests
dotnet test

# Unit tests only
dotnet test --filter "FullyQualifiedName~Unit"

# Integration tests only
dotnet test --filter "FullyQualifiedName~Integration"

# Specific test class
dotnet test --filter "FullyQualifiedName~ToolPatternMatcherTests"

# With coverage
dotnet test --collect:"XPlat Code Coverage"
```

## Integration Test Infrastructure Requirements

- **Docker**: Required for Testcontainers (Redis, Service Bus emulator, Jackett, qBittorrent, browserless/chrome)
- **User Secrets**: Some integration tests require API keys (OpenRouter) stored in user secrets
- **Network**: Some tests connect to external services (Brave Search, OpenRouter)

## Continuous Integration

- Tests run on every PR
- Coverage collected via coverlet
- Integration tests require Docker for containers
- Tests parallelized at both assembly and collection level
