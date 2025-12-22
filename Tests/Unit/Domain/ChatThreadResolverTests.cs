using Domain.Agents;
using Domain.Contracts;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatThreadResolverTests
{
    private static ChatThreadResolver CreateResolver(IThreadStateStore? store = null)
    {
        store ??= new Mock<IThreadStateStore>().Object;
        return new ChatThreadResolver(store);
    }

    [Fact]
    public async Task ResolveAsync_WithNewKey_ReturnsNewContext()
    {
        // Arrange
        var resolver = CreateResolver();
        var key = new AgentKey(1, 1);

        // Act
        var context = await resolver.ResolveAsync(key, CancellationToken.None);

        // Assert
        context.ShouldNotBeNull();
    }

    [Fact]
    public async Task ResolveAsync_WithExistingKey_ReturnsSameContext()
    {
        // Arrange
        var resolver = CreateResolver();
        var key = new AgentKey(1, 1);
        var firstContext = await resolver.ResolveAsync(key, CancellationToken.None);

        // Act
        var secondContext = await resolver.ResolveAsync(key, CancellationToken.None);

        // Assert
        secondContext.ShouldBeSameAs(firstContext);
    }

    [Fact]
    public async Task ResolveAsync_WithDifferentKeys_ReturnsDifferentContexts()
    {
        // Arrange
        var resolver = CreateResolver();
        var key1 = new AgentKey(1, 1);
        var key2 = new AgentKey(2, 2);

        // Act
        var context1 = await resolver.ResolveAsync(key1, CancellationToken.None);
        var context2 = await resolver.ResolveAsync(key2, CancellationToken.None);

        // Assert
        context1.ShouldNotBeSameAs(context2);
    }

    [Fact]
    public async Task AgentKeys_ReturnsAllResolvedKeys()
    {
        // Arrange
        var resolver = CreateResolver();
        var key1 = new AgentKey(1, 1);
        var key2 = new AgentKey(2, 2);
        await resolver.ResolveAsync(key1, CancellationToken.None);
        await resolver.ResolveAsync(key2, CancellationToken.None);

        // Act
        var keys = resolver.AgentKeys.ToArray();

        // Assert
        keys.ShouldContain(key1);
        keys.ShouldContain(key2);
        keys.Length.ShouldBe(2);
    }

    [Fact]
    public async Task CleanAsync_WithExistingKey_RemovesKeyAndCompletesContext()
    {
        // Arrange
        var resolver = CreateResolver();
        var key = new AgentKey(1, 1);
        var context = await resolver.ResolveAsync(key, CancellationToken.None);

        // Act
        await resolver.CleanAsync(key);

        // Assert
        resolver.AgentKeys.ShouldNotContain(key);
        context.Cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task CleanAsync_WithNonExistentKey_DoesNotThrow()
    {
        // Arrange
        var resolver = CreateResolver();
        var key = new AgentKey(999, 999);

        // Act & Assert
        await Should.NotThrowAsync(() => resolver.CleanAsync(key));
    }

    [Fact]
    public async Task CleanAsync_ThenResolveAsync_ReturnsNewContext()
    {
        // Arrange
        var resolver = CreateResolver();
        var key = new AgentKey(1, 1);
        var firstContext = await resolver.ResolveAsync(key, CancellationToken.None);
        await resolver.CleanAsync(key);

        // Act
        var secondContext = await resolver.ResolveAsync(key, CancellationToken.None);

        // Assert
        secondContext.ShouldNotBeSameAs(firstContext);
    }

    [Fact]
    public async Task ResolveAsync_AfterDispose_Throws()
    {
        // Arrange
        var resolver = CreateResolver();
        await resolver.DisposeAsync();

        // Act & Assert
        await Should.ThrowAsync<ObjectDisposedException>(() =>
            resolver.ResolveAsync(new AgentKey(1, 1), CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_ConcurrentCalls_ReturnsConsistentResults()
    {
        // Arrange
        var resolver = CreateResolver();
        var key = new AgentKey(1, 1);
        var results = new List<ChatThreadContext>();
        var lockObj = new object();

        // Act - simulate concurrent access
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            var result = await resolver.ResolveAsync(key, CancellationToken.None);
            lock (lockObj)
            {
                results.Add(result);
            }
        }));
        await Task.WhenAll(tasks);

        // Assert - all should reference same context
        var firstContext = results.First();
        results.ShouldAllBe(r => ReferenceEquals(r, firstContext));
    }
}