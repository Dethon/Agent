# Testing

## Framework & Tools

| Tool | Purpose |
|------|---------|
| xUnit 2.9.3 | Test framework |
| Moq 4.20.72 | Mocking |
| Shouldly 4.3.0 | Fluent assertions |
| Testcontainers 4.10.0 | Docker containers for integration tests |
| WireMock.Net 1.25.0 | HTTP mocking |
| Microsoft.AspNetCore.Mvc.Testing | WebApplicationFactory |
| Xunit.SkippableFact | Conditional test skipping |

## Test Organization

```
Tests/
+-- Integration/          # Tests with real dependencies
|   +-- Agents/           # McpAgent, ThreadSession tests
|   +-- Clients/          # External client tests (Telegram)
|   +-- Domain/           # Domain integration tests
|   +-- Fixtures/         # Shared test fixtures
|   +-- McpServerTests/   # MCP server tests
|   +-- McpTools/         # Tool integration tests
|   +-- Memory/           # Redis memory tests
|   +-- Messaging/        # Service Bus end-to-end integration tests
|   +-- StateManagers/    # State persistence tests
|   +-- WebChat/          # SignalR hub tests
|   |   +-- Client/       # WebChat.Client tests
|
+-- Unit/                 # Isolated unit tests
    +-- Domain/           # Domain logic tests (ChatMonitor, ScheduleExecutor)
    +-- Infrastructure/   # Infrastructure tests
    |   +-- Messaging/    # ServiceBus, Composite, WebChat client unit tests
    +-- McpServerLibrary/ # MCP server unit tests
    +-- WebChat/          # WebChat tests
    +-- WebChat.Client/   # Client state, pipeline, effect tests
        +-- State/
            +-- Pipeline/
```

## Test Fixtures

### RedisFixture
```csharp
public class RedisFixture : IAsyncLifetime
{
    // Starts Redis container via Testcontainers
    public IConnectionMultiplexer Redis { get; }
}
```

### ThreadSessionServerFixture
```csharp
public class ThreadSessionServerFixture : IAsyncLifetime
{
    // Starts MCP server for integration tests
}
```

### PlaywrightWebBrowserFixture
```csharp
public class PlaywrightWebBrowserFixture : IAsyncLifetime
{
    // Manages Playwright browser lifecycle
}
```

### ServiceBusFixture
```csharp
public class ServiceBusFixture : IAsyncLifetime
{
    // Provides test helpers for Service Bus integration tests
    // Creates ServiceBusReceivedMessage instances with correlationId-based payloads
    // Manages ServiceBusProcessor, Parser, PromptReceiver, ResponseWriter
}
```

## Writing Tests

### Unit Test Pattern
```csharp
public class ToolPatternMatcherTests
{
    [Fact]
    public void IsWhitelisted_WhenPatternMatches_ReturnsTrue()
    {
        // Arrange
        var patterns = new[] { "library/*" };
        var matcher = new ToolPatternMatcher(patterns);

        // Act
        var result = matcher.IsWhitelisted("library/download");

        // Assert
        result.ShouldBeTrue();
    }
}
```

### Integration Test Pattern
```csharp
public class McpAgentIntegrationTests : IClassFixture<ThreadSessionServerFixture>
{
    private readonly ThreadSessionServerFixture _fixture;

    public McpAgentIntegrationTests(ThreadSessionServerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunAsync_WithToolCall_ExecutesTool()
    {
        // Uses real MCP server from fixture
    }
}
```

### WebChat State Tests
```csharp
public class MessagesStoreTests
{
    [Fact]
    public void Dispatch_AddMessage_UpdatesState()
    {
        var store = new MessagesStore();
        var action = new AddMessageAction(message);

        store.Dispatch(action, MessagesReducers.Reduce);

        store.State.Messages.ShouldContain(message);
    }
}
```

### Service Bus Parser Tests
```csharp
public class ServiceBusMessageParserTests
{
    [Fact]
    public void Parse_MissingCorrelationId_ReturnsParseFailure()
    {
        var parser = new ServiceBusMessageParser(["agent1"]);
        var message = CreateMessage(agentId: "agent1", prompt: "test");

        var result = parser.Parse(message);

        result.ShouldBeOfType<ParseFailure>();
        ((ParseFailure)result).Reason.ShouldBe("MissingField");
    }

    [Fact]
    public void Parse_InvalidAgentId_ReturnsParseFailure()
    {
        var parser = new ServiceBusMessageParser(["agent1"]);
        var message = CreateMessage(correlationId: "123", agentId: "unknown", prompt: "test");

        var result = parser.Parse(message);

        ((ParseFailure)result).Reason.ShouldBe("InvalidAgentId");
    }
}
```

## Test-Driven Development

### Workflow
1. **Red**: Write failing test defining expected behavior
2. **Green**: Write minimum code to pass
3. **Refactor**: Clean up while tests pass

### When to Apply TDD
- New features: Start with behavior test
- Bug fixes: Start with reproducing test
- Refactoring: Ensure tests exist first

### Exceptions
- Pure configuration changes
- Trivial one-line changes

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
```

### Composite Client Tests
```csharp
public class CompositeChatMessengerClientTests
{
    [Fact]
    public async Task ReadPrompts_MergesFromAllClients()
    {
        var client1 = new Mock<IChatMessengerClient>();
        var client2 = new Mock<IChatMessengerClient>();
        var router = new Mock<IMessageSourceRouter>();
        var composite = new CompositeChatMessengerClient([client1.Object, client2.Object], router.Object);

        // Verifies merged async enumerable from both clients
    }
}
```

### Pipeline Tests
```csharp
public class MessagePipelineTests
{
    [Fact]
    public void FinalizeMessage_DuplicateMessageId_SkipsSecondFinalization()
    {
        // Verifies dedup via finalization tracking
    }

    [Fact]
    public void ClearTopic_RemovesFinalizationState()
    {
        // Verifies cleanup on topic delete
    }
}
```

## Mocking Patterns

### Interface Mocking
```csharp
var mockStore = new Mock<IMemoryStore>();
mockStore
    .Setup(x => x.SearchAsync(It.IsAny<string>(), It.IsAny<string>(), null, null, null, null, 10, default))
    .ReturnsAsync(new List<MemorySearchResult>());
```

### HTTP Mocking with WireMock
```csharp
var server = WireMockServer.Start();
server.Given(Request.Create().WithPath("/api/search"))
    .RespondWith(Response.Create().WithBody("{}"));
```

## Continuous Integration

- Tests run on every PR
- Coverage collected via coverlet
- Integration tests require Docker for containers
