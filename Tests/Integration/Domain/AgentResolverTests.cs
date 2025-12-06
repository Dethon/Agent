using Domain.Agents;
using Microsoft.Agents.AI;
using Shouldly;

namespace Tests.Integration.Domain;

public class ThreadResolverTests
{
    private readonly ThreadResolver _resolver = new();

    [Fact]
    public async Task Resolve_WithNewChat_CreatesThread()
    {
        // Arrange
        var key = new AgentKey(1, 1);
        var factoryCalled = false;
        var mockThread = new FakeAgentThread();

        // Act
        var result = await _resolver.Resolve(key, factory, CancellationToken.None);

        // Assert
        result.ShouldBe(mockThread);
        factoryCalled.ShouldBeTrue();
        return;

        AgentThread factory()
        {
            factoryCalled = true;
            return mockThread;
        }
    }

    [Fact]
    public async Task Resolve_WithExistingChat_ReturnsCachedThread()
    {
        // Arrange
        var key = new AgentKey(1, 1);
        var factoryCallCount = 0;
        var mockThread = new FakeAgentThread();

        // Act
        await _resolver.Resolve(key, factory, CancellationToken.None);
        var result = await _resolver.Resolve(key, factory, CancellationToken.None);

        // Assert
        result.ShouldBe(mockThread);
        factoryCallCount.ShouldBe(1);
        return;

        AgentThread factory()
        {
            factoryCallCount++;
            return mockThread;
        }
    }

    [Fact]
    public async Task Clean_RemovesThread()
    {
        // Arrange
        var key = new AgentKey(1, 1);
        var mockThread = new FakeAgentThread();
        await _resolver.Resolve(key, () => mockThread, CancellationToken.None);

        // Act
        _resolver.Clean(1, 1);

        // Assert
        _resolver.Threads.ShouldNotContain(x => x.ChatId == 1 && x.ThreadId == 1);
    }

    [Fact]
    public async Task Threads_ReturnsAllCachedThreads()
    {
        // Arrange
        var mockThread1 = new FakeAgentThread();
        var mockThread2 = new FakeAgentThread();
        await _resolver.Resolve(new AgentKey(1, 10), () => mockThread1, CancellationToken.None);
        await _resolver.Resolve(new AgentKey(2, 20), () => mockThread2, CancellationToken.None);

        // Act
        var threads = _resolver.Threads;

        // Assert
        threads.Length.ShouldBe(2);
        threads.ShouldContain(x => x.ChatId == 1 && x.ThreadId == 10);
        threads.ShouldContain(x => x.ChatId == 2 && x.ThreadId == 20);
    }

    [Fact]
    public void Clean_WithNonExistentThread_DoesNotThrow()
    {
        // Act & Assert
        Should.NotThrow(() => _resolver.Clean(999, 999));
    }

    [Fact]
    public async Task Resolve_WithDifferentThreads_CreatesSeparateThreads()
    {
        // Arrange
        var mockThread1 = new FakeAgentThread();
        var mockThread2 = new FakeAgentThread();
        var callCount = 0;

        // Act
        var result1 = await _resolver.Resolve(new AgentKey(1, 1), factory, CancellationToken.None);
        var result2 = await _resolver.Resolve(new AgentKey(1, 2), factory, CancellationToken.None);

        // Assert
        callCount.ShouldBe(2);
        result1.ShouldBe(mockThread1);
        result2.ShouldBe(mockThread2);
        return;

        AgentThread factory()
        {
            callCount++;
            return callCount == 1 ? mockThread1 : mockThread2;
        }
    }

    private sealed class FakeAgentThread : AgentThread;
}

public class CancellationResolverTests
{
    private readonly CancellationResolver _resolver = new();

    [Fact]
    public void GetOrCreate_WithNewKey_CreatesCts()
    {
        // Arrange
        var key = new AgentKey(1, 1);

        // Act
        var cts = _resolver.GetOrCreate(key);

        // Assert
        cts.ShouldNotBeNull();
        cts.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void GetOrCreate_WithExistingKey_ReturnsSameCts()
    {
        // Arrange
        var key = new AgentKey(1, 1);

        // Act
        var cts1 = _resolver.GetOrCreate(key);
        var cts2 = _resolver.GetOrCreate(key);

        // Assert
        cts1.ShouldBeSameAs(cts2);
    }

    [Fact]
    public async Task CancelAndRemove_CancelsAndRemovesCts()
    {
        // Arrange
        var key = new AgentKey(1, 1);
        var cts = _resolver.GetOrCreate(key);

        // Act
        await _resolver.CancelAndRemove(key);

        // Assert
        cts.IsCancellationRequested.ShouldBeTrue();

        // Getting again should create a new CTS
        var newCts = _resolver.GetOrCreate(key);
        newCts.ShouldNotBeSameAs(cts);
    }

    [Fact]
    public void Clean_DisposesCts()
    {
        // Arrange
        var key = new AgentKey(1, 1);
        var cts = _resolver.GetOrCreate(key);

        // Act
        _resolver.Clean(1, 1);

        // Assert - Getting again should create a new CTS
        var newCts = _resolver.GetOrCreate(key);
        newCts.ShouldNotBeSameAs(cts);
    }

    [Fact]
    public async Task CancelAndRemove_WithNonExistentKey_DoesNotThrow()
    {
        // Act & Assert
        await Should.NotThrowAsync(async () => await _resolver.CancelAndRemove(new AgentKey(999, 999)));
    }
}