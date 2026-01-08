# Integration Test Patterns

## File System Tests

Use temp directories with `IDisposable` cleanup:

```csharp
public class FileToolTests : IDisposable
{
    private readonly string _testDir;
    private readonly TestableTool _tool;

    public FileToolTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid()}");
        Directory.CreateDirectory(_testDir);
        _tool = new TestableTool(_testDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
        {
            Directory.Delete(_testDir, true);
        }
    }

    [Fact]
    public void Run_FileExists_ReturnsContent()
    {
        // Arrange
        var filePath = Path.Combine(_testDir, "test.txt");
        File.WriteAllText(filePath, "content");

        // Act
        var result = _tool.TestRun(filePath);

        // Assert
        result.ShouldBe("content");
    }
}
```

## Redis Tests

Use `RedisFixture` for tests requiring Redis:

```csharp
public class RedisStoreTests : IClassFixture<RedisFixture>
{
    private readonly RedisFixture _redis;

    public RedisStoreTests(RedisFixture redis)
    {
        _redis = redis;
    }

    [Fact]
    public async Task Store_ValidData_Persists()
    {
        var store = new RedisStore(_redis.Connection);

        await store.SetAsync("key", "value");
        var result = await store.GetAsync("key");

        result.ShouldBe("value");
    }
}
```

## Testable Wrapper Pattern

For classes with protected methods, create a testable subclass:

```csharp
private class TestableTool : ActualTool
{
    public TestableTool(string path) : base(path) { }

    public string TestRun(string input)
    {
        return Run(input); // Expose protected method
    }
}
```

## HTTP Client Tests

Test with real HttpClient against test servers or actual endpoints:

```csharp
public class ApiClientTests : IAsyncLifetime
{
    private readonly HttpClient _client;

    public ApiClientTests()
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri("https://api.example.com")
        };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Get_ValidEndpoint_ReturnsData()
    {
        var response = await _client.GetAsync("/health");
        response.IsSuccessStatusCode.ShouldBeTrue();
    }
}
```

## Shouldly Assertions

```csharp
// Equality
result.ShouldBe("expected");
result.ShouldNotBe("other");

// Collections
list.ShouldContain("item");
list.ShouldBeEmpty();
list.Count.ShouldBe(3);

// Exceptions
Should.Throw<InvalidOperationException>(() => tool.Run(null));
await Should.ThrowAsync<ArgumentException>(async () => await tool.RunAsync("bad"));

// Null checks
result.ShouldNotBeNull();
result.ShouldBeNull();

// Boolean
condition.ShouldBeTrue();
condition.ShouldBeFalse();

// String
text.ShouldContain("substring");
text.ShouldStartWith("prefix");
text.ShouldBeNullOrEmpty();
```

## Async Test Pattern

```csharp
[Fact]
public async Task RunAsync_WithCancellation_ThrowsOperationCanceled()
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();

    await Should.ThrowAsync<OperationCanceledException>(
        async () => await _tool.RunAsync("input", cts.Token));
}
```
