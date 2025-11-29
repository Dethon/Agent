using Domain.Agents;
using Domain.Contracts;
using Moq;
using Shouldly;

namespace Tests.Integration.Domain;

public class AgentResolverTests
{
    private readonly AgentResolver _resolver = new();

    [Fact]
    public async Task Resolve_WithNewChat_CreatesAgent()
    {
        // Arrange
        var mockAgent = CreateMockAgent();
        var factoryCalled = false;

        Task<IAgent> Factory(CancellationToken _)
        {
            factoryCalled = true;
            return Task.FromResult(mockAgent.Object);
        }

        // Act
        var result = await _resolver.Resolve(1, 1, Factory, CancellationToken.None);

        // Assert
        result.ShouldBe(mockAgent.Object);
        factoryCalled.ShouldBeTrue();
    }

    [Fact]
    public async Task Resolve_WithExistingChat_ReturnsCachedAgent()
    {
        // Arrange
        var mockAgent = CreateMockAgent();
        var factoryCallCount = 0;

        Task<IAgent> Factory(CancellationToken _)
        {
            factoryCallCount++;
            return Task.FromResult(mockAgent.Object);
        }

        // Act
        await _resolver.Resolve(1, 1, Factory, CancellationToken.None);
        var result = await _resolver.Resolve(1, 1, Factory, CancellationToken.None);

        // Assert
        result.ShouldBe(mockAgent.Object);
        factoryCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task Resolve_WithNullChatId_AlwaysCreatesNewAgent()
    {
        // Arrange
        var mockAgent1 = CreateMockAgent();
        var mockAgent2 = CreateMockAgent();
        var callCount = 0;

        Task<IAgent> Factory(CancellationToken _)
        {
            callCount++;
            return Task.FromResult(callCount == 1 ? mockAgent1.Object : mockAgent2.Object);
        }

        // Act
        var result1 = await _resolver.Resolve(null, 1, Factory, CancellationToken.None);
        var result2 = await _resolver.Resolve(null, 1, Factory, CancellationToken.None);

        // Assert
        callCount.ShouldBe(2);
        result1.ShouldBe(mockAgent1.Object);
        result2.ShouldBe(mockAgent2.Object);
    }

    [Fact]
    public async Task Clean_DisposesAndRemovesAgent()
    {
        // Arrange
        var mockAgent = CreateMockAgent();
        await _resolver.Resolve(1, 1, _ => Task.FromResult(mockAgent.Object), CancellationToken.None);

        // Act
        await _resolver.Clean(1, 1);

        // Assert
        mockAgent.Verify(a => a.DisposeAsync(), Times.Once);
        _resolver.Agents.ShouldNotContain(x => x.ChatId == 1 && x.ThreadId == 1);
    }

    [Fact]
    public async Task Agents_ReturnsAllCachedAgents()
    {
        // Arrange
        var mockAgent1 = CreateMockAgent();
        var mockAgent2 = CreateMockAgent();
        await _resolver.Resolve(1, 10, _ => Task.FromResult(mockAgent1.Object), CancellationToken.None);
        await _resolver.Resolve(2, 20, _ => Task.FromResult(mockAgent2.Object), CancellationToken.None);

        // Act
        var agents = _resolver.Agents;

        // Assert
        agents.Length.ShouldBe(2);
        agents.ShouldContain(x => x.ChatId == 1 && x.ThreadId == 10);
        agents.ShouldContain(x => x.ChatId == 2 && x.ThreadId == 20);
    }

    [Fact]
    public async Task Clean_WithNonExistentAgent_DoesNotThrow()
    {
        // Act & Assert
        await Should.NotThrowAsync(async () => await _resolver.Clean(999, 999));
    }

    [Fact]
    public async Task Resolve_WithDifferentThreads_CreatesSeparateAgents()
    {
        // Arrange
        var mockAgent1 = CreateMockAgent();
        var mockAgent2 = CreateMockAgent();
        var callCount = 0;

        Task<IAgent> Factory(CancellationToken _)
        {
            callCount++;
            return Task.FromResult(callCount == 1 ? mockAgent1.Object : mockAgent2.Object);
        }

        // Act
        var result1 = await _resolver.Resolve(1, 1, Factory, CancellationToken.None);
        var result2 = await _resolver.Resolve(1, 2, Factory, CancellationToken.None);

        // Assert
        callCount.ShouldBe(2);
        result1.ShouldBe(mockAgent1.Object);
        result2.ShouldBe(mockAgent2.Object);
    }

    private static Mock<IAgent> CreateMockAgent()
    {
        var mock = new Mock<IAgent>();
        mock.Setup(a => a.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return mock;
    }
}