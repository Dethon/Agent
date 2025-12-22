using Domain.Agents;
using Shouldly;

namespace Tests.Unit.Domain;

public class ChatThreadContextTests
{
    [Fact]
    public void Constructor_InitializesPropertiesCorrectly()
    {
        // Act
        var context = new ChatThreadContext();

        // Assert
        context.Cts.ShouldNotBeNull();
        context.Cts.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void GetLinkedTokenSource_ReturnsLinkedSource()
    {
        // Arrange
        var context = new ChatThreadContext();
        using var externalCts = new CancellationTokenSource();

        // Act
        using var linked = context.GetLinkedTokenSource(externalCts.Token);

        // Assert
        linked.ShouldNotBeNull();
        linked.Token.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public void GetLinkedTokenSource_CancelsWhenContextCts_IsCancelled()
    {
        // Arrange
        var context = new ChatThreadContext();
        using var externalCts = new CancellationTokenSource();
        using var linked = context.GetLinkedTokenSource(externalCts.Token);

        // Act
        context.Cts.Cancel();

        // Assert
        linked.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void GetLinkedTokenSource_CancelsWhenExternalToken_IsCancelled()
    {
        // Arrange
        var context = new ChatThreadContext();
        using var externalCts = new CancellationTokenSource();
        using var linked = context.GetLinkedTokenSource(externalCts.Token);

        // Act
        externalCts.Cancel();

        // Assert
        linked.Token.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Dispose_CancelsCts()
    {
        // Arrange
        var context = new ChatThreadContext();

        // Act
        context.Dispose();

        // Assert
        context.Cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Dispose_InvokesRegisteredCallback()
    {
        // Arrange
        var context = new ChatThreadContext();
        var callbackInvoked = false;
        context.RegisterCompletionCallback(() => callbackInvoked = true);

        // Act
        context.Dispose();

        // Assert
        callbackInvoked.ShouldBeTrue();
    }

    [Fact]
    public void Dispose_WithNoCallback_DoesNotThrow()
    {
        // Arrange
        var context = new ChatThreadContext();

        // Act & Assert
        Should.NotThrow(() => context.Dispose());
    }
}