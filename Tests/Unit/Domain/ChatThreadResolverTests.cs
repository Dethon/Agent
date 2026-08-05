using Domain.Agents;
using Domain.Contracts;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatThreadResolverTests
{
    [Fact]
    public void Resolve_HandlesKeyIdentityCorrectly()
    {
        var resolver = new ChatThreadResolver();
        var key1 = new AgentKey("1:1");
        var key2 = new AgentKey("2:2");

        var context1 = resolver.Resolve(key1);
        context1.ShouldNotBeNull();

        var context1Again = resolver.Resolve(key1);
        context1Again.ShouldBeSameAs(context1);

        var context2 = resolver.Resolve(key2);
        context2.ShouldNotBeSameAs(context1);
    }

    [Fact]
    public async Task CleanAsync_WithExistingKey_RemovesKeyAndCompletesContext()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey("1:1");
        var context = resolver.Resolve(key);

        // Act
        await resolver.ClearAsync(key);

        // Assert
        resolver.AgentKeys.ShouldNotContain(key);
        context.Cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task ClearAsync_AfterACancelRemovedTheContext_StillDeletesPersistedState()
    {
        // /cancel immediately followed by /clear: the cancel already removed and disposed the
        // context, so a delete conditional on finding one would leave the history the user just
        // asked to wipe sitting in the store.
        var stateStore = new Mock<IThreadStateStore>();
        stateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>())).Returns(Task.CompletedTask);
        var resolver = new ChatThreadResolver(stateStore.Object);
        var key = new AgentKey("1:1");
        resolver.Resolve(key);
        resolver.Cancel(key);

        await resolver.ClearAsync(key);

        stateStore.Verify(s => s.DeleteAsync(key), Times.Once);
    }

    [Fact]
    public async Task ClearAsync_WithNoLiveContext_StillDeletesPersistedState()
    {
        // A /clear on a conversation with no live group — routine after a restart — wipes the
        // persisted thread too; the delete is a single idempotent key delete.
        var stateStore = new Mock<IThreadStateStore>();
        stateStore.Setup(s => s.DeleteAsync(It.IsAny<AgentKey>())).Returns(Task.CompletedTask);
        var resolver = new ChatThreadResolver(stateStore.Object);
        var key = new AgentKey("1:1");

        await resolver.ClearAsync(key);

        stateStore.Verify(s => s.DeleteAsync(key), Times.Once);
    }

    [Fact]
    public async Task CleanAsync_WithNonExistentKey_DoesNotThrow()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey("999:999");

        // Act & Assert
        await Should.NotThrowAsync(() => resolver.ClearAsync(key));
    }

    [Fact]
    public async Task CleanAsync_ThenResolve_ReturnsNewContext()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey("1:1");
        var firstContext = resolver.Resolve(key);
        await resolver.ClearAsync(key);

        // Act
        var secondContext = resolver.Resolve(key);

        // Assert
        secondContext.ShouldNotBeSameAs(firstContext);
    }

    [Fact]
    public void Resolve_AfterDispose_Throws()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        resolver.Dispose();

        // Act & Assert
        Should.Throw<ObjectDisposedException>(() => resolver.Resolve(new AgentKey("1:1")));
    }

    [Fact]
    public async Task Resolve_ConcurrentCalls_ReturnsConsistentResults()
    {
        // Arrange
        var resolver = new ChatThreadResolver();
        var key = new AgentKey("1:1");
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