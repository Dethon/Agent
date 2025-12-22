using Domain.Agents;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatThreadResolverTests
{
    [Fact]
    public void Resolve_WithNewKey_ReturnsNewContext()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(1, 1);

        // Act
        var context = resolver.Resolve(key);

        // Assert
        context.ShouldNotBeNull();
    }

    [Fact]
    public void Resolve_WithExistingKey_ReturnsSameContext()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(1, 1);
        var firstContext = resolver.Resolve(key);

        // Act
        var secondContext = resolver.Resolve(key);

        // Assert
        secondContext.ShouldBeSameAs(firstContext);
    }

    [Fact]
    public void Resolve_WithDifferentKeys_ReturnsDifferentContexts()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key1 = new AgentKey(1, 1);
        var key2 = new AgentKey(2, 2);

        // Act
        var context1 = resolver.Resolve(key1);
        var context2 = resolver.Resolve(key2);

        // Assert
        context1.ShouldNotBeSameAs(context2);
    }

    [Fact]
    public void AgentKeys_ReturnsAllResolvedKeys()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key1 = new AgentKey(1, 1);
        var key2 = new AgentKey(2, 2);
        resolver.Resolve(key1);
        resolver.Resolve(key2);

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
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(1, 1);
        var context = resolver.Resolve(key);

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
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(999, 999);

        // Act & Assert
        await Should.NotThrowAsync(() => resolver.CleanAsync(key));
    }

    [Fact]
    public async Task CleanAsync_ThenResolve_ReturnsNewContext()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(1, 1);
        var firstContext = resolver.Resolve(key);
        await resolver.CleanAsync(key);

        // Act
        var secondContext = resolver.Resolve(key);

        // Assert
        secondContext.ShouldNotBeSameAs(firstContext);
    }

    [Fact]
    public async Task Resolve_AfterDispose_Throws()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        await resolver.DisposeAsync();

        // Act & Assert
        Should.Throw<ObjectDisposedException>(() => resolver.Resolve(new AgentKey(1, 1)));
    }

    [Fact]
    public async Task Resolve_ConcurrentCalls_ReturnsConsistentResults()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(1, 1);
        var results = new List<ChatThreadContext>();
        var lockObj = new object();

        // Act - simulate concurrent access
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(() =>
        {
            var result = resolver.Resolve(key);
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