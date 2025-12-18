using System.Diagnostics.CodeAnalysis;
using Domain.DTOs;
using Infrastructure.Clients.Cli;
using Shouldly;

namespace Tests.Unit.Infrastructure.Cli;

[SuppressMessage("ReSharper", "AccessToDisposedClosure")]
public class CliChatMessageRouterTests : IDisposable
{
    private readonly FakeTerminalAdapter _terminalAdapter = new();
    private readonly CliChatMessageRouter _router;

    public CliChatMessageRouterTests()
    {
        _router = new CliChatMessageRouter("TestAgent", "TestUser", _terminalAdapter);
    }

    public void Dispose()
    {
        _router.Dispose();
        _terminalAdapter.Dispose();
    }

    [Fact]
    public async Task ReadPrompts_StartsTerminalOnFirstCall()
    {
        // Arrange
        using var cts = new CancellationTokenSource(100);

        // Act
        await foreach (var _ in _router.ReadPrompts(cts.Token))
        {
            break;
        }

        // Assert
        _terminalAdapter.IsStarted.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadPrompts_YieldsPromptWhenInputReceived()
    {
        // Arrange
        using var cts = new CancellationTokenSource(1000);
        var prompts = new List<ChatPrompt>();

        // Start reading prompts in background
        var readTask = Task.Run(async () =>
        {
            // ReSharper disable once AccessToDisposedClosure
            await foreach (var prompt in _router.ReadPrompts(cts.Token))
            {
                prompts.Add(prompt);
                if (prompts.Count >= 1)
                {
                    break;
                }
            }
        }, cts.Token);

        // Give time for router to start
        await Task.Delay(50, cts.Token);

        // Act
        _terminalAdapter.SimulateInput("Hello, agent!");

        await readTask;

        // Assert
        prompts.Count.ShouldBe(1);
        prompts[0].Prompt.ShouldBe("Hello, agent!");
        prompts[0].Sender.ShouldBe("TestUser");
    }

    [Fact]
    public async Task ReadPrompts_DisplaysUserMessageInHistory()
    {
        // Arrange
        using var cts = new CancellationTokenSource(1000);

        var readTask = Task.Run(async () =>
        {
            await foreach (var _ in _router.ReadPrompts(cts.Token))
            {
                break;
            }
        }, cts.Token);

        await Task.Delay(50, cts.Token);

        // Act
        _terminalAdapter.SimulateInput("Test message");
        await readTask;

        // Assert
        _terminalAdapter.DisplayedMessages.Count.ShouldBeGreaterThan(0);
    }

    [Fact]
    public void SendResponse_DisplaysAgentMessage()
    {
        // Arrange
        var response = new ChatResponseMessage { Message = "Hello, user!" };

        // Act
        _router.SendResponse(response);

        // Assert
        _terminalAdapter.DisplayedMessages.Count.ShouldBe(1);
        var lines = _terminalAdapter.DisplayedMessages[0];
        lines.ShouldContain(l => l.Text.Contains("TestAgent"));
    }

    [Fact]
    public void SendResponse_DisplaysToolCalls()
    {
        // Arrange
        var response = new ChatResponseMessage { CalledTools = "search_files(pattern: \"*.cs\")" };

        // Act
        _router.SendResponse(response);

        // Assert
        _terminalAdapter.DisplayedMessages.Count.ShouldBe(1);
        var lines = _terminalAdapter.DisplayedMessages[0];
        lines.ShouldContain(l => l.Type == ChatLineType.ToolHeader || l.Type == ChatLineType.ToolContent);
    }

    [Fact]
    public void SendResponse_DisplaysBothToolsAndMessage()
    {
        // Arrange
        var response = new ChatResponseMessage
        {
            CalledTools = "get_weather(city: \"London\")",
            Message = "The weather in London is sunny."
        };

        // Act
        _router.SendResponse(response);

        // Assert
        _terminalAdapter.DisplayedMessages.Count.ShouldBe(2);
    }

    [Fact]
    public void CreateThread_DisplaysSystemMessage()
    {
        // Act
        _router.CreateThread("New Conversation");

        // Assert
        _terminalAdapter.DisplayedMessages.Count.ShouldBe(1);
        var lines = _terminalAdapter.DisplayedMessages[0];
        lines.ShouldContain(l => l.Text.Contains("New Conversation"));
    }

    [Fact]
    public async Task ReadPrompts_HelpCommandDoesNotYieldPrompt()
    {
        // Arrange
        using var cts = new CancellationTokenSource(500);
        var prompts = new List<ChatPrompt>();

        var readTask = Task.Run(async () =>
        {
            await foreach (var prompt in _router.ReadPrompts(cts.Token))
            {
                prompts.Add(prompt);
            }
        }, cts.Token);

        await Task.Delay(50, cts.Token);

        // Act
        _terminalAdapter.SimulateInput("/help");
        await Task.Delay(100, cts.Token);
        await cts.CancelAsync();

        try { await readTask; }
        catch (OperationCanceledException) { }

        // Assert
        prompts.ShouldBeEmpty();
    }

    [Fact]
    public async Task ReadPrompts_ClearCommandDoesNotYieldPrompt()
    {
        // Arrange
        using var cts = new CancellationTokenSource(500);
        var prompts = new List<ChatPrompt>();

        var readTask = Task.Run(async () =>
        {
            await foreach (var prompt in _router.ReadPrompts(cts.Token))
            {
                prompts.Add(prompt);
            }
        }, cts.Token);

        await Task.Delay(50, cts.Token);

        // Act
        _terminalAdapter.SimulateInput("/clear");
        await Task.Delay(100, cts.Token);
        await cts.CancelAsync();

        try { await readTask; }
        catch (OperationCanceledException) { }

        // Assert - /clear sends /cancel internally but not as a user prompt
        prompts.ShouldNotContain(p => p.Prompt == "/clear");
    }

    [Fact]
    public void ShutdownRequested_RaisedWhenTerminalRequestsShutdown()
    {
        // Arrange
        var shutdownRequested = false;
        _router.ShutdownRequested += () => shutdownRequested = true;

        // Act
        _terminalAdapter.SimulateShutdown();

        // Assert
        shutdownRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadPrompts_StopsWhenCancelled()
    {
        // Arrange
        using var cts = new CancellationTokenSource();

        _ = Task.Run(async () =>
        {
            await foreach (var _ in _router.ReadPrompts(cts.Token))
            {
            }
        }, cts.Token);

        await Task.Delay(50, cts.Token);

        // Act
        await cts.CancelAsync();
        await Task.Delay(100, CancellationToken.None);

        // Assert
        _terminalAdapter.IsStopped.ShouldBeTrue();
    }

    [Fact]
    public async Task ReadPrompts_IncrementsMessageId()
    {
        // Arrange
        using var cts = new CancellationTokenSource(1000);
        var prompts = new List<ChatPrompt>();

        var readTask = Task.Run(async () =>
        {
            await foreach (var prompt in _router.ReadPrompts(cts.Token))
            {
                prompts.Add(prompt);
                if (prompts.Count >= 2)
                {
                    break;
                }
            }
        }, cts.Token);

        await Task.Delay(50, cts.Token);

        // Act
        _terminalAdapter.SimulateInput("First");
        await Task.Delay(50, cts.Token);
        _terminalAdapter.SimulateInput("Second");
        await readTask;

        // Assert
        prompts[0].MessageId.ShouldBe(1);
        prompts[1].MessageId.ShouldBe(2);
    }
}