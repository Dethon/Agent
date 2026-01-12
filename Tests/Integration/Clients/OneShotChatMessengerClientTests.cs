using Domain.DTOs;
using Infrastructure.Clients;
using Microsoft.Extensions.Hosting;
using Moq;
using Shouldly;

namespace Tests.Integration.Clients;

public class OneShotChatMessengerClientTests
{
    [Fact]
    public async Task ReadPrompts_FirstCall_YieldsConfiguredPrompt()
    {
        // Arrange
        const string expectedPrompt = "Test prompt";
        var lifetime = new Mock<IHostApplicationLifetime>();
        var client = new OneShotChatMessengerClient(expectedPrompt, false, lifetime.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        var prompts = new List<ChatPrompt>();
        await foreach (var prompt in client.ReadPrompts(0, cts.Token))
        {
            prompts.Add(prompt);
            break;
        }

        // Assert
        prompts.ShouldHaveSingleItem();
        prompts[0].Prompt.ShouldBe(expectedPrompt);
        prompts[0].ChatId.ShouldBe(1);
        prompts[0].ThreadId.ShouldBe(1);
        prompts[0].Sender.ShouldBe(Environment.UserName);
    }

    [Fact]
    public async Task ReadPrompts_SecondCall_BlocksIndefinitely()
    {
        // Arrange
        const string prompt = "Test prompt";
        var lifetime = new Mock<IHostApplicationLifetime>();
        var client = new OneShotChatMessengerClient(prompt, false, lifetime.Object);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        // First call - consume the prompt
        await foreach (var _ in client.ReadPrompts(0, CancellationToken.None))
        {
            break;
        }

        // Act - Second call should block until canceled
        var prompts = new List<ChatPrompt>();
        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await foreach (var p in client.ReadPrompts(0, cts.Token))
            {
                prompts.Add(p);
            }
        });

        // Assert
        prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task SendResponse_WithMessage_AccumulatesContent()
    {
        // Arrange
        var lifetime = new Mock<IHostApplicationLifetime>();
        var client = new OneShotChatMessengerClient("test", false, lifetime.Object);

        var response1 = new ChatResponseMessage { Message = "Hello " };
        var response2 = new ChatResponseMessage { Message = "World" };

        // Capture console output
        var originalOut = Console.Out;
        await using var sw = new StringWriter();
        Console.SetOut(sw);

        try
        {
            // Act
            await client.SendResponse(1, response1, 1, null, CancellationToken.None);
            await client.SendResponse(1, response2, 1, null, CancellationToken.None);

            // Assert - Content should be written to console
            var output = sw.ToString();
            output.ShouldContain("Hello ");
            output.ShouldContain("World");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task SendResponse_WithReasoning_WhenShowReasoningTrue_OutputsReasoning()
    {
        // Arrange
        var lifetime = new Mock<IHostApplicationLifetime>();
        var client = new OneShotChatMessengerClient("test", showReasoning: true, lifetime.Object);

        var response = new ChatResponseMessage
        {
            Message = "Result",
            Reasoning = "Thinking..."
        };

        var originalOut = Console.Out;
        await using var sw = new StringWriter();
        Console.SetOut(sw);

        try
        {
            // Act
            await client.SendResponse(1, response, 1, null, CancellationToken.None);

            // Assert
            var output = sw.ToString();
            output.ShouldContain("Thinking...");
            output.ShouldContain("Result");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task SendResponse_WithReasoning_WhenShowReasoningFalse_OmitsReasoning()
    {
        // Arrange
        var lifetime = new Mock<IHostApplicationLifetime>();
        var client = new OneShotChatMessengerClient("test", showReasoning: false, lifetime.Object);

        var response = new ChatResponseMessage
        {
            Message = "Result",
            Reasoning = "Thinking..."
        };

        var originalOut = Console.Out;
        await using var sw = new StringWriter();
        Console.SetOut(sw);

        try
        {
            // Act
            await client.SendResponse(1, response, 1, null, CancellationToken.None);

            // Assert
            var output = sw.ToString();
            output.ShouldNotContain("Thinking...");
            output.ShouldContain("Result");
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task SendResponse_AfterCompletionTimeout_StopsApplication()
    {
        // Arrange
        var lifetime = new Mock<IHostApplicationLifetime>();
        var client = new OneShotChatMessengerClient("test", false, lifetime.Object);

        var response = new ChatResponseMessage { Message = "Done" };

        var originalOut = Console.Out;
        Console.SetOut(new StringWriter());

        try
        {
            // Act
            await client.SendResponse(1, response, 1, null, CancellationToken.None);

            // Wait for completion timer (500ms + some buffer)
            await Task.Delay(700);

            // Assert
            lifetime.Verify(l => l.StopApplication(), Times.Once);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task SendResponse_WithMultipleChunks_ResetsCompletionTimer()
    {
        // Arrange
        var lifetime = new Mock<IHostApplicationLifetime>();
        var client = new OneShotChatMessengerClient("test", false, lifetime.Object);

        var originalOut = Console.Out;
        Console.SetOut(new StringWriter());

        try
        {
            // Act - Send chunks with delays shorter than completion timeout
            await client.SendResponse(1, new ChatResponseMessage { Message = "Chunk1" }, 1, null,
                CancellationToken.None);
            await Task.Delay(200);
            await client.SendResponse(1, new ChatResponseMessage { Message = "Chunk2" }, 1, null,
                CancellationToken.None);
            await Task.Delay(200);
            await client.SendResponse(1, new ChatResponseMessage { Message = "Chunk3" }, 1, null,
                CancellationToken.None);

            // Assert - Should not have stopped yet (timer resets with each chunk)
            lifetime.Verify(l => l.StopApplication(), Times.Never);

            // Wait for final completion
            await Task.Delay(700);
            lifetime.Verify(l => l.StopApplication(), Times.Once);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task CreateThread_ReturnsOne()
    {
        // Arrange
        var lifetime = new Mock<IHostApplicationLifetime>();
        var client = new OneShotChatMessengerClient("test", false, lifetime.Object);

        // Act
        var threadId = await client.CreateThread(1, "Test", null, CancellationToken.None);

        // Assert
        threadId.ShouldBe(1);
    }

    [Fact]
    public async Task DoesThreadExist_ReturnsTrue()
    {
        // Arrange
        var lifetime = new Mock<IHostApplicationLifetime>();
        var client = new OneShotChatMessengerClient("test", false, lifetime.Object);

        // Act
        var exists = await client.DoesThreadExist(1, 1, null, CancellationToken.None);

        // Assert
        exists.ShouldBeTrue();
    }
}