using Domain.Agents;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatThreadResolverTests
{
    [Fact]
    public void Resolve_WithNewKey_ReturnsNewContextAndIsNewTrue()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(1, 1);

        // Act
        var (context, isNew) = resolver.Resolve(key);

        // Assert
        context.ShouldNotBeNull();
        isNew.ShouldBeTrue();
    }

    [Fact]
    public void Resolve_WithExistingKey_ReturnsSameContextAndIsNewFalse()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(1, 1);
        var (firstContext, _) = resolver.Resolve(key);

        // Act
        var (secondContext, isNew) = resolver.Resolve(key);

        // Assert
        secondContext.ShouldBeSameAs(firstContext);
        isNew.ShouldBeFalse();
    }

    [Fact]
    public void Resolve_WithDifferentKeys_ReturnsDifferentContexts()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key1 = new AgentKey(1, 1);
        var key2 = new AgentKey(2, 2);

        // Act
        var (context1, isNew1) = resolver.Resolve(key1);
        var (context2, isNew2) = resolver.Resolve(key2);

        // Assert
        context1.ShouldNotBeSameAs(context2);
        isNew1.ShouldBeTrue();
        isNew2.ShouldBeTrue();
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
    public void Clean_WithExistingKey_RemovesKeyAndCompletesContext()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(1, 1);
        var (context, _) = resolver.Resolve(key);

        // Act
        resolver.Clean(key);

        // Assert
        resolver.AgentKeys.ShouldNotContain(key);
        context.Cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Clean_WithNonExistentKey_DoesNotThrow()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(999, 999);

        // Act & Assert
        Should.NotThrow(() => resolver.Clean(key));
    }

    [Fact]
    public void Clean_ThenResolve_ReturnsNewContext()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(1, 1);
        var (firstContext, _) = resolver.Resolve(key);
        resolver.Clean(key);

        // Act
        var (secondContext, isNew) = resolver.Resolve(key);

        // Assert
        secondContext.ShouldNotBeSameAs(firstContext);
        isNew.ShouldBeTrue();
    }

    [Fact]
    public async Task Resolve_ConcurrentCalls_ReturnsConsistentResults()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey(1, 1);
        var results = new List<(ChatThreadContext context, bool isNew)>();
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

        // Assert - only one should be new, all should reference same context
        results.Count(r => r.isNew).ShouldBe(1);
        var firstContext = results.First().context;
        results.ShouldAllBe(r => ReferenceEquals(r.context, firstContext));
    }
}