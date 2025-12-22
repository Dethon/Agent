using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatThreadContextTests
{
    private static readonly AgentKey _testKey = new(123, 456);

    private static async Task<ChatThreadContext> CreateContextAsync(IThreadStateStore? store = null)
    {
        store ??= new Mock<IThreadStateStore>().Object;
        return await ChatThreadContext.CreateAsync(_testKey, store, CancellationToken.None);
    }

    [Fact]
    public async Task CreateAsync_InitializesPropertiesCorrectly()
    {
        // Act
        var context = await CreateContextAsync();

        // Assert
        context.Cts.ShouldNotBeNull();
        context.Cts.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task CreateAsync_LoadsPersistedThread()
    {
        // Arrange
        var storeMock = new Mock<IThreadStateStore>();
        var persistedThread = JsonDocument.Parse("{}").RootElement;
        storeMock.Setup(s => s.LoadAsync(_testKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(persistedThread);

        // Act
        var context = await ChatThreadContext.CreateAsync(_testKey, storeMock.Object, CancellationToken.None);

        // Assert
        context.PersistedThread.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetLinkedTokenSource_ReturnsLinkedSource()
    {
        // Arrange
        var context = await CreateContextAsync();
        using var externalCts = new CancellationTokenSource();

        // Act
        using var linked = context.GetLinkedTokenSource(externalCts.Token);

        // Assert
        linked.ShouldNotBeNull();
        linked.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task GetLinkedTokenSource_CancelsWhenContextCts_IsCancelled()
    {
        // Arrange
        var context = await CreateContextAsync();
        using var externalCts = new CancellationTokenSource();
        using var linked = context.GetLinkedTokenSource(externalCts.Token);

        // Act
        await context.Cts.CancelAsync();

        // Assert
        linked.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task GetLinkedTokenSource_CancelsWhenExternalToken_IsCancelled()
    {
        // Arrange
        var context = await CreateContextAsync();
        using var externalCts = new CancellationTokenSource();
        using var linked = context.GetLinkedTokenSource(externalCts.Token);

        // Act
        await externalCts.CancelAsync();

        // Assert
        linked.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeAsync_CancelsCts()
    {
        // Arrange
        var context = await CreateContextAsync();

        // Act
        await context.DisposeAsync();

        // Assert
        context.Cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeAsync_InvokesRegisteredCallback()
    {
        // Arrange
        var context = await CreateContextAsync();
        var callbackInvoked = false;
        context.RegisterCompletionCallback(() => callbackInvoked = true);

        // Act
        await context.DisposeAsync();

        // Assert
        callbackInvoked.ShouldBeTrue();
    }

    [Fact]
    public async Task DisposeAsync_DeletesFromStore()
    {
        // Arrange
        var storeMock = new Mock<IThreadStateStore>();
        var context = await ChatThreadContext.CreateAsync(_testKey, storeMock.Object, CancellationToken.None);

        // Act
        await context.DisposeAsync();

        // Assert
        storeMock.Verify(s => s.DeleteAsync(_testKey, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SaveThreadAsync_SavesAndUpdatesPersistedThread()
    {
        // Arrange
        var storeMock = new Mock<IThreadStateStore>();
        var context = await ChatThreadContext.CreateAsync(_testKey, storeMock.Object, CancellationToken.None);
        var thread = JsonDocument.Parse("{\"test\":true}").RootElement;

        // Act
        await context.SaveThreadAsync(thread, CancellationToken.None);

        // Assert
        storeMock.Verify(s => s.SaveAsync(_testKey, thread, It.IsAny<CancellationToken>()), Times.Once);
        context.PersistedThread.ShouldNotBeNull();
    }
}