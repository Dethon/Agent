using Domain.Agents;
using Domain.DTOs;
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
        context.PromptChannel.ShouldNotBeNull();
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
    public void Complete_CancelsCts()
    {
        // Arrange
        var context = new ChatThreadContext();

        // Act
        context.Complete();

        // Assert
        context.Cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public void Complete_CompletesPromptChannel()
    {
        // Arrange
        var context = new ChatThreadContext();

        // Act
        context.Complete();

        // Assert
        context.PromptChannel.Reader.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact]
    public async Task PromptChannel_CanWriteAndRead()
    {
        // Arrange
        var context = new ChatThreadContext();
        var prompt = new ChatPrompt
        {
            ChatId = 1,
            ThreadId = 1,
            Prompt = "Test",
            MessageId = 1,
            Sender = "test"
        };

        // Act
        await context.PromptChannel.Writer.WriteAsync(prompt);
        var result = await context.PromptChannel.Reader.ReadAsync();

        // Assert
        result.ShouldBe(prompt);
    }

    [Fact]
    public void PromptChannel_AfterComplete_CannotWrite()
    {
        // Arrange
        var context = new ChatThreadContext();
        context.Complete();

        // Act & Assert
        var writeResult = context.PromptChannel.Writer.TryWrite(new ChatPrompt
        {
            ChatId = 1,
            ThreadId = 1,
            Prompt = "Test",
            MessageId = 1,
            Sender = "test"
        });
        writeResult.ShouldBeFalse();
    }
}