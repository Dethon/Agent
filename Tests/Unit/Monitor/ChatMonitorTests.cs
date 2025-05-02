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
}



