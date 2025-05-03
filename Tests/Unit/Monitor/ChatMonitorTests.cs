using System.Reflection;
using System.Text.Json.Nodes;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Monitor;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Tests.Unit.Monitor;

public class ChatMonitorTests
{
    private readonly Mock<IChatClient> _chatClientMock;
    private readonly Mock<ILogger<ChatMonitor>> _loggerMock;
    private readonly Mock<IAgentResolver> _agentResolverMock;
    private readonly Mock<IAgent> _agentMock;
    private readonly TaskQueue _taskQueue;
    private readonly ChatMonitor _chatMonitor;
    private readonly CancellationToken _cancellationToken = CancellationToken.None;

    public ChatMonitorTests()
    {
        var serviceProviderMock = new Mock<IServiceProvider>();
        var serviceScopeMock = new Mock<IServiceScope>();
        var serviceScopeFactoryMock = new Mock<IServiceScopeFactory>();
        _chatClientMock = new Mock<IChatClient>();
        _loggerMock = new Mock<ILogger<ChatMonitor>>();
        _agentResolverMock = new Mock<IAgentResolver>();
        _agentMock = new Mock<IAgent>();

        serviceScopeFactoryMock.Setup(x => x.CreateScope()).Returns(serviceScopeMock.Object);
        serviceScopeMock.Setup(x => x.ServiceProvider).Returns(serviceProviderMock.Object);
        serviceProviderMock.Setup(x => x.GetService(typeof(IServiceScopeFactory)))
            .Returns(serviceScopeFactoryMock.Object);
        serviceProviderMock.Setup(x => x.GetService(typeof(IAgentResolver))).Returns(_agentResolverMock.Object);

        _taskQueue = new TaskQueue();

        _chatMonitor = new ChatMonitor(
            serviceProviderMock.Object,
            _taskQueue,
            _chatClientMock.Object,
            _loggerMock.Object);
    }

    [Fact]
    public async Task Monitor_ReadPrompts_QueuesTaskForEachPrompt()
    {
        // given
        var prompts = new List<ChatPrompt>
        {
            new()
            {
                ChatId = 1,
                MessageId = 1,
                Prompt = "Test prompt 1",
                Sender = "user1"
            },
            new()
            {
                ChatId = 2,
                MessageId = 2,
                Prompt = "Test prompt 2",
                Sender = "user2"
            }
        };

        _chatClientMock.Setup(x => x.ReadPrompts(It.IsAny<int>(), _cancellationToken))
            .Returns(prompts.ToAsyncEnumerable());

        // when
        await _chatMonitor.Monitor(_cancellationToken);

        // then
        Assert.Equal(2, _taskQueue.Count);
        var task1 = await _taskQueue.DequeueTask(_cancellationToken);
        var task2 = await _taskQueue.DequeueTask(_cancellationToken);
        Assert.NotNull(task1);
        Assert.NotNull(task2);
        Assert.Equal(0, _taskQueue.Count);
    }

    [Fact]
    public async Task AgentTask_ProcessesPromptAndSendsResponse()
    {
        // given
        var prompt = CreateChatPrompt();
        var agentResponse = CreateAgentResponse("Response content", includeToolCalls: true);

        SetupAgentResolver();
        SetupAgentRun(prompt.Prompt, agentResponse);
        SetupChatClientSendResponse(789);

        // when
        await ExecuteAgentTask(prompt);

        // then
        VerifyAgentResolver(null);
        VerifyAgentRun(prompt.Prompt);
        VerifyResponseSent(prompt);
        VerifyMessageAssociatedWithAgent();
    }

    [Fact]
    public async Task AgentTask_WithReplyToMessageId_ResolvesAgentWithReferenceId()
    {
        // given
        var prompt = CreateChatPrompt(replyToMessageId: 100);
        var referenceId = prompt.ReplyToMessageId + prompt.Sender.GetHashCode();
        var agentResponse = CreateAgentResponse("Response content");

        SetupAgentResolver(referenceId);
        SetupAgentRun(prompt.Prompt, agentResponse);
        SetupChatClientSendResponse(789);

        // when
        await ExecuteAgentTask(prompt);

        // then
        VerifyAgentResolver(referenceId);
    }

    [Fact]
    public async Task AgentTask_ExceptionHandling_LogsError()
    {
        // given
        var prompt = CreateChatPrompt();

        _agentResolverMock.Setup(x => x.Resolve(AgentType.Download, null))
            .Throws(new Exception("Test exception"));

        // when
        await ExecuteAgentTask(prompt);

        // then
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.Once);
    }

    #region Helper Methods

    private static ChatPrompt CreateChatPrompt(long chatId = 123, int messageId = 456,
        string promptText = "Test prompt",
        string sender = "user123", int? replyToMessageId = null)
    {
        return new ChatPrompt
        {
            ChatId = chatId,
            MessageId = messageId,
            Prompt = promptText,
            Sender = sender,
            ReplyToMessageId = replyToMessageId
        };
    }

    private static AgentResponse CreateAgentResponse(string content, bool includeToolCalls = false)
    {
        var toolCall = new ToolCall
        {
            Name = "TestTool",
            Parameters = new JsonObject
            {
                ["arg1"] = "value1",
                ["arg2"] = "value2"
            },
            Id = "434"
        };
        return new AgentResponse
        {
            Content = content,
            ToolCalls = includeToolCalls ? [toolCall] : [],
            StopReason = StopReason.Error,
            Role = Role.User
        };
    }

    private void SetupAgentResolver(int? referenceId = null)
    {
        _agentResolverMock.Setup(x => x.Resolve(AgentType.Download, referenceId))
            .Returns(_agentMock.Object);
    }

    private void SetupAgentRun(string promptText, AgentResponse response)
    {
        _agentMock.Setup(x => x.Run(promptText, _cancellationToken))
            .Returns(new[] { response }.ToAsyncEnumerable());
    }

    private void SetupChatClientSendResponse(int responseId)
    {
        _chatClientMock.Setup(x => x.SendResponse(
                It.IsAny<long>(),
                It.IsAny<string>(),
                It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(responseId);
    }

    private async Task ExecuteAgentTask(ChatPrompt prompt)
    {
        await _taskQueue.QueueTask(c => InvokeAgentTaskMethod(prompt, c));
        var task = await _taskQueue.DequeueTask(_cancellationToken);
        await task(_cancellationToken);
    }

    private void VerifyAgentResolver(int? referenceId)
    {
        _agentResolverMock.Verify(
            x => x.Resolve(AgentType.Download, referenceId),
            Times.Once);
    }

    private void VerifyAgentRun(string promptText)
    {
        _agentMock.Verify(
            x => x.Run(promptText, _cancellationToken),
            Times.Once);
    }

    private void VerifyResponseSent(ChatPrompt prompt)
    {
        _chatClientMock.Verify(
            x => x.SendResponse(
                prompt.ChatId,
                It.IsAny<string>(),
                prompt.MessageId,
                _cancellationToken),
            Times.Once);
    }

    private void VerifyMessageAssociatedWithAgent()
    {
        _agentResolverMock.Verify(
            x => x.AssociateMessageToAgent(It.IsAny<int>(), _agentMock.Object),
            Times.Once);
    }

    private async Task InvokeAgentTaskMethod(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        var method = typeof(ChatMonitor)
            .GetMethod("AgentTask", BindingFlags.NonPublic | BindingFlags.Instance);
        var result = method?.Invoke(_chatMonitor, [prompt, cancellationToken]);
        if (result == null)
        {
            throw new InvalidOperationException("Method invocation failed.");
        }

        await (Task)result;
    }

    #endregion
}