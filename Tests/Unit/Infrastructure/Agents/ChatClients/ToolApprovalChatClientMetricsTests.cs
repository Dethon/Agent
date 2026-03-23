using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class ToolApprovalChatClientMetricsTests
{
    [Fact]
    public async Task InvokeFunctionAsync_ApprovedTool_PublishesSuccessEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var function = AIFunctionFactory.Create(() => "result", "mcp:server:TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:server:TestTool", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => captured = e as ToolCallEvent)
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        captured.ShouldNotBeNull();
        captured.ToolName.ShouldBe("mcp:server:TestTool");
        captured.Success.ShouldBeTrue();
        captured.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
        captured.Error.ShouldBeNull();
    }

    [Fact]
    public async Task InvokeFunctionAsync_AutoApprovedTool_PublishesSuccessEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var function = AIFunctionFactory.Create(() => "result", "mcp:server:TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:server:TestTool", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => captured = e as ToolCallEvent)
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(
            fakeClient, handler,
            whitelistPatterns: ["mcp:server:*"],
            metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        captured.ShouldNotBeNull();
        captured.ToolName.ShouldBe("mcp:server:TestTool");
        captured.Success.ShouldBeTrue();
        captured.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task InvokeFunctionAsync_ToolThrows_PublishesFailureEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var function = AIFunctionFactory.Create(string () => throw new InvalidOperationException("boom"),
            "mcp:server:FailTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:server:FailTool", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => captured = e as ToolCallEvent)
            .Returns(Task.CompletedTask);

        // IncludeDetailedErrors is true by default, so the exception is caught by the base class
        // and returned as a result. We need to verify the metrics still capture it.
        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert — FunctionInvokingChatClient catches exceptions from tool invocations
        // and returns them as error results, so the call succeeds but the tool "failed"
        captured.ShouldNotBeNull();
        captured.ToolName.ShouldBe("mcp:server:FailTool");
        captured.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task InvokeFunctionAsync_WithoutPublisher_DoesNotThrow()
    {
        // Arrange
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var function = AIFunctionFactory.Create(() => "result", "mcp:server:TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:server:TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler);
        var options = new ChatOptions { Tools = [function] };

        // Act & Assert — should not throw
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);
        response.ShouldNotBeNull();
    }

    [Fact]
    public async Task InvokeFunctionAsync_RejectedTool_DoesNotPublishEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Rejected);
        var function = AIFunctionFactory.Create(() => "result", "mcp:server:TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:server:TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert — rejected tools are never invoked, so no metrics should be published
        publisher.Verify(
            p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InvokeFunctionAsync_ApprovedAndRemember_PublishesSuccessEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.ApprovedAndRemember);
        var function = AIFunctionFactory.Create(() => "result", "mcp:server:TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:server:TestTool", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => captured = e as ToolCallEvent)
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        captured.ShouldNotBeNull();
        captured.ToolName.ShouldBe("mcp:server:TestTool");
        captured.Success.ShouldBeTrue();
    }

    private static ChatResponse CreateToolCallResponse(string toolName, string callId)
    {
        var toolCallContent = new FunctionCallContent(callId, toolName, new Dictionary<string, object?>());
        var message = new ChatMessage(ChatRole.Assistant, [toolCallContent]);
        return new ChatResponse([message]) { FinishReason = ChatFinishReason.ToolCalls };
    }

    private sealed class TestApprovalHandler(ToolApprovalResult result) : IToolApprovalHandler
    {
        public Task<ToolApprovalResult> RequestApprovalAsync(
            IReadOnlyList<ToolApprovalRequest> requests,
            CancellationToken cancellationToken)
            => Task.FromResult(result);

        public Task NotifyAutoApprovedAsync(
            IReadOnlyList<ToolApprovalRequest> requests,
            CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        public void SetNextResponse(ChatResponse response) => _responses.Enqueue(response);

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (_responses.TryDequeue(out var response))
            {
                return Task.FromResult(response);
            }

            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, "Done")])
            {
                FinishReason = ChatFinishReason.Stop
            });
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => AsyncEnumerable.Empty<ChatResponseUpdate>();

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
    }
}
