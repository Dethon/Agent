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
        serviceProviderMock.Setup(x => x.GetService(typeof(IServiceScopeFactory))).Returns(serviceScopeFactoryMock.Object);
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
            new() { ChatId = 1, MessageId = 1, Prompt = "Test prompt 1", Sender = "user1" },
            new() { ChatId = 2, MessageId = 2, Prompt = "Test prompt 2", Sender = "user2" }
        };
        
        _chatClientMock.Setup(x => x.ReadPrompts(It.IsAny<int>(), _cancellationToken))
            .Returns(prompts.ToAsyncEnumerable());
        
        // when
        await _chatMonitor.Monitor(_cancellationToken);
        
        // then - Verify two tasks were queued
        var task1 = await _taskQueue.DequeueTask(_cancellationToken);
        var task2 = await _taskQueue.DequeueTask(_cancellationToken);
        
        Assert.NotNull(task1);
        Assert.NotNull(task2);
    }

    [Fact]
    public async Task AgentTask_ProcessesPromptAndSendsResponse()
    {
        // given
        var prompt = new ChatPrompt
        {
            ChatId = 123,
            MessageId = 456,
            Prompt = "Test prompt",
            Sender = "user123"
        };
        
        var agentResponse = new AgentResponse
        {
            Content = "Response content",
            ToolCalls = [new ToolCall
                {
                    Name = "TestTool",
                    Parameters = new JsonObject
                    {
                        ["arg1"] = "value1",
                        ["arg2"] = "value2"
                    },
                    Id = "434"
                }
            ],
            StopReason = StopReason.Error,
            Role = Role.User
        };

        _agentResolverMock.Setup(x => x.Resolve(AgentType.Download, null))
            .Returns(_agentMock.Object);
        
        _agentMock.Setup(x => x.Run(prompt.Prompt, _cancellationToken))
            .Returns(new[] { agentResponse }.ToAsyncEnumerable());
        
        _chatClientMock.Setup(x => x.SendResponse(
                It.IsAny<long>(), 
                It.IsAny<string>(), 
                It.IsAny<int>(), 
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(789);
        
        // when - Queue and execute the agent task
        await _taskQueue.QueueTask(c => InvokeAgentTaskMethod(prompt, c));
        var task = await _taskQueue.DequeueTask(_cancellationToken);
        await task(_cancellationToken);
        
        // then
        _agentResolverMock.Verify(
            x => x.Resolve(AgentType.Download, null), 
            Times.Once);
        
        _agentMock.Verify(
            x => x.Run(prompt.Prompt, _cancellationToken), 
            Times.Once);
        
        _chatClientMock.Verify(
            x => x.SendResponse(
                prompt.ChatId, 
                It.IsAny<string>(),
                prompt.MessageId, 
                _cancellationToken), 
            Times.Once);
        
        _agentResolverMock.Verify(
            x => x.AssociateMessageToAgent(It.IsAny<int>(), _agentMock.Object),
            Times.Once);
    }
    
    [Fact]
    public async Task AgentTask_WithReplyToMessageId_ResolvesAgentWithReferenceId()
    {
        // given
        var prompt = new ChatPrompt
        {
            ChatId = 123,
            MessageId = 456,
            Prompt = "Test prompt",
            Sender = "user123",
            ReplyToMessageId = 100
        };
        
        var referenceId = prompt.ReplyToMessageId + prompt.Sender.GetHashCode();
        var agentResponse = new AgentResponse
        {
            Content = "Response content",
            ToolCalls = [],
            StopReason = StopReason.Error,
            Role = Role.User
        };

        _agentResolverMock.Setup(x => x.Resolve(AgentType.Download, referenceId))
            .Returns(_agentMock.Object);
        
        _agentMock.Setup(x => x.Run(prompt.Prompt, _cancellationToken))
            .Returns(new[] { agentResponse }.ToAsyncEnumerable());
        
        _chatClientMock.Setup(x => x.SendResponse(
                It.IsAny<long>(), 
                It.IsAny<string>(), 
                It.IsAny<int>(), 
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(789);
        
        // when
        await _taskQueue.QueueTask(c => InvokeAgentTaskMethod(prompt, c));
        var task = await _taskQueue.DequeueTask(_cancellationToken);
        await task(_cancellationToken);
        
        // then
        _agentResolverMock.Verify(
            x => x.Resolve(AgentType.Download, referenceId), 
            Times.Once);
    }
    
    [Fact]
    public async Task AgentTask_ExceptionHandling_LogsError()
    {
        // given
        var prompt = new ChatPrompt
        {
            ChatId = 123,
            MessageId = 456,
            Prompt = "Test prompt",
            Sender = "user123"
        };
        
        _agentResolverMock.Setup(x => x.Resolve(AgentType.Download, null))
            .Throws(new Exception("Test exception"));
        
        // when
        await _taskQueue.QueueTask(c => InvokeAgentTaskMethod(prompt, c));
        var task = await _taskQueue.DequeueTask(_cancellationToken);
        await task(_cancellationToken);
        
        // then - verify exception was logged
        _loggerMock.Verify(
            x => x.Log(
                LogLevel.Error,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception>(),
                (Func<It.IsAnyType, Exception?, string>)It.IsAny<object>()),
            Times.Once);
    }

    [Fact]
    public async Task AgentTask_EmptyResponse_DoesNotSendMessage()
    {
        // given
        var prompt = new ChatPrompt
        {
            ChatId = 123,
            MessageId = 456,
            Prompt = "Test prompt",
            Sender = "user123"
        };
        
        var agentResponse = new AgentResponse
        {
            Content = "",
            ToolCalls = [],
            StopReason = StopReason.Error,
            Role = Role.User
        };

        _agentResolverMock.Setup(x => x.Resolve(AgentType.Download, null))
            .Returns(_agentMock.Object);
        
        _agentMock.Setup(x => x.Run(prompt.Prompt, _cancellationToken))
            .Returns(new[] { agentResponse }.ToAsyncEnumerable());
        
        // when
        await _taskQueue.QueueTask(c => InvokeAgentTaskMethod(prompt, c));
        var task = await _taskQueue.DequeueTask(_cancellationToken);
        await task(_cancellationToken);
        
        // then - verify SendResponse was not called
        _chatClientMock.Verify(
            x => x.SendResponse(
                It.IsAny<long>(), 
                It.IsAny<string>(), 
                It.IsAny<int>(), 
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
    
    // Helper method to invoke the private AgentTask method via reflection
    private async Task InvokeAgentTaskMethod(ChatPrompt prompt, CancellationToken cancellationToken)
    {
        await (Task)typeof(ChatMonitor)
            .GetMethod("AgentTask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Invoke(_chatMonitor, new object[] { prompt, cancellationToken });
    }
}
