using System.Text.Json;
using Domain.Agents;
using Infrastructure.StateManagers;
using Shouldly;
using Tests.Integration.Fixtures;

namespace Tests.Integration.StateManagers;

public class RedisThreadStateStoreIntegrationTests(RedisFixture fixture)
    : IClassFixture<RedisFixture>
{
    [Fact]
    public async Task SaveAsync_ThenLoadAsync_ReturnsPersistedThread()
    {
        // Arrange
        var store = new RedisThreadStateStore(fixture.Connection);
        var key = new AgentKey(100, 200);
        var threadData = JsonDocument.Parse("""{"messages":[{"role":"user","content":"Hello"}]}""").RootElement;

        // Act
        await store.SaveAsync(key, threadData, CancellationToken.None);
        var loaded = await store.LoadAsync(key, CancellationToken.None);

        // Assert
        loaded.ShouldNotBeNull();
        loaded.Value.GetRawText().ShouldBe(threadData.GetRawText());
    }

    [Fact]
    public async Task LoadAsync_WithNonExistentKey_ReturnsNull()
    {
        // Arrange
        var store = new RedisThreadStateStore(fixture.Connection);
        var key = new AgentKey(999999, 999999);

        // Act
        var loaded = await store.LoadAsync(key, CancellationToken.None);

        // Assert
        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task DeleteAsync_RemovesPersistedThread()
    {
        // Arrange
        var store = new RedisThreadStateStore(fixture.Connection);
        var key = new AgentKey(101, 201);
        var threadData = JsonDocument.Parse("""{"test":"data"}""").RootElement;
        await store.SaveAsync(key, threadData, CancellationToken.None);

        // Act
        await store.DeleteAsync(key, CancellationToken.None);
        var loaded = await store.LoadAsync(key, CancellationToken.None);

        // Assert
        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingThread()
    {
        // Arrange
        var store = new RedisThreadStateStore(fixture.Connection);
        var key = new AgentKey(102, 202);
        var originalData = JsonDocument.Parse("""{"version":1}""").RootElement;
        var updatedData = JsonDocument.Parse("""{"version":2}""").RootElement;

        // Act
        await store.SaveAsync(key, originalData, CancellationToken.None);
        await store.SaveAsync(key, updatedData, CancellationToken.None);
        var loaded = await store.LoadAsync(key, CancellationToken.None);

        // Assert
        loaded.ShouldNotBeNull();
        loaded.Value.GetProperty("version").GetInt32().ShouldBe(2);
    }

    [Fact]
    public async Task DeleteAsync_WithNonExistentKey_DoesNotThrow()
    {
        // Arrange
        var store = new RedisThreadStateStore(fixture.Connection);
        var key = new AgentKey(888888, 888888);

        // Act & Assert
        await Should.NotThrowAsync(() => store.DeleteAsync(key, CancellationToken.None));
    }

    [Fact]
    public async Task MultipleKeys_AreIsolated()
    {
        // Arrange
        var store = new RedisThreadStateStore(fixture.Connection);
        var key1 = new AgentKey(103, 203);
        var key2 = new AgentKey(104, 204);
        var data1 = JsonDocument.Parse("""{"key":"one"}""").RootElement;
        var data2 = JsonDocument.Parse("""{"key":"two"}""").RootElement;

        // Act
        await store.SaveAsync(key1, data1, CancellationToken.None);
        await store.SaveAsync(key2, data2, CancellationToken.None);

        var loaded1 = await store.LoadAsync(key1, CancellationToken.None);
        var loaded2 = await store.LoadAsync(key2, CancellationToken.None);

        // Assert
        loaded1!.Value.GetProperty("key").GetString().ShouldBe("one");
        loaded2!.Value.GetProperty("key").GetString().ShouldBe("two");
    }

    [Fact]
    public async Task SaveAsync_ComplexThreadData_RoundTripsCorrectly()
    {
        // Arrange
        var store = new RedisThreadStateStore(fixture.Connection);
        var key = new AgentKey(105, 205);
        var complexData = JsonDocument.Parse("""
                                             {
                                                 "messages": [
                                                     {"role": "system", "content": "You are a helpful assistant"},
                                                     {"role": "user", "content": "Hello"},
                                                     {"role": "assistant", "content": "Hi there!"}
                                                 ],
                                                 "metadata": {
                                                     "createdAt": "2024-01-01T00:00:00Z",
                                                     "toolCalls": 5
                                                 }
                                             }
                                             """).RootElement;

        // Act
        await store.SaveAsync(key, complexData, CancellationToken.None);
        var loaded = await store.LoadAsync(key, CancellationToken.None);

        // Assert
        loaded.ShouldNotBeNull();
        var messages = loaded.Value.GetProperty("messages");
        messages.GetArrayLength().ShouldBe(3);
        messages[1].GetProperty("content").GetString().ShouldBe("Hello");
    }
}