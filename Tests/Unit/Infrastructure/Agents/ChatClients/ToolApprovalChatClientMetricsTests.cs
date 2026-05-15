using System.Text.Json;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.DTOs.Metrics;
using Domain.DTOs.Metrics.Enums;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Moq;
using Shouldly;
using Tests.Unit.Infrastructure.Helpers;
using static Tests.Unit.Infrastructure.Helpers.ToolApprovalResponseFactory;

namespace Tests.Unit.Infrastructure.Agents.ChatClients;

public class ToolApprovalChatClientMetricsTests
{
    [Fact]
    public async Task InvokeFunctionAsync_ApprovedTool_PublishesSuccessEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var function = AIFunctionFactory.Create(() => "result", "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is ToolCallEvent t) { captured = t; } })
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        captured.ShouldNotBeNull();
        captured.ToolName.ShouldBe("mcp__server__TestTool");
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
        var function = AIFunctionFactory.Create(() => "result", "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is ToolCallEvent t) { captured = t; } })
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(
            fakeClient, handler,
            whitelistPatterns: ["mcp__server__*"],
            metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        captured.ShouldNotBeNull();
        captured.ToolName.ShouldBe("mcp__server__TestTool");
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
            "mcp__server__FailTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__FailTool", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is ToolCallEvent t) { captured = t; } })
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
        captured.ToolName.ShouldBe("mcp__server__FailTool");
        captured.Success.ShouldBeFalse();
        captured.Error.ShouldNotBeNullOrEmpty();
        captured.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task InvokeFunctionAsync_WithoutPublisher_DoesNotThrow()
    {
        // Arrange
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var function = AIFunctionFactory.Create(() => "result", "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

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
        var function = AIFunctionFactory.Create(() => "result", "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

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
    public async Task InvokeFunctionAsync_McpToolReturnsIsError_PublishesFailureEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);

        // Simulate an MCP tool returning CallToolResult with isError: true (serialized as JsonElement)
        var errorResult = JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = "Connection refused" } },
            isError = true
        });
        var function = AIFunctionFactory.Create(() => errorResult, "mcp__server__FailTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__FailTool", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is ToolCallEvent t) { captured = t; } })
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        captured.ShouldNotBeNull();
        captured.ToolName.ShouldBe("mcp__server__FailTool");
        captured.Success.ShouldBeFalse();
        captured.Error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task InvokeFunctionAsync_DomainToolReturnsErrorEnvelope_PublishesFailureEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);

        // Simulate a domain tool (like SubAgentRunTool) returning the standard error envelope
        var errorResult = new JsonObject
        {
            ["ok"] = false,
            ["errorCode"] = "not_found",
            ["message"] = "Unknown subagent: 'invalid'",
            ["retryable"] = false
        };
        var function = AIFunctionFactory.Create(() => errorResult, "run_subagent");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("run_subagent", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is ToolCallEvent t) { captured = t; } })
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        captured.ShouldNotBeNull();
        captured.ToolName.ShouldBe("run_subagent");
        captured.Success.ShouldBeFalse();
        captured.Error.ShouldBe("Unknown subagent: 'invalid'");
    }

    [Fact]
    public async Task InvokeFunctionAsync_McpToolReturnsIsErrorFalse_PublishesSuccessEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);

        // MCP tool returning isError: false should be treated as success
        var successResult = JsonSerializer.SerializeToElement(new
        {
            content = new[] { new { type = "text", text = "All good" } },
            isError = false
        });
        var function = AIFunctionFactory.Create(() => successResult, "mcp__server__OkTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__OkTool", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is ToolCallEvent t) { captured = t; } })
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        captured.ShouldNotBeNull();
        captured.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeFunctionAsync_DomainToolReturnsStatusCompleted_PublishesSuccessEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);

        var successResult = new JsonObject
        {
            ["status"] = "completed",
            ["result"] = "Task done"
        };
        var function = AIFunctionFactory.Create(() => successResult, "run_subagent");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("run_subagent", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is ToolCallEvent t) { captured = t; } })
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        captured.ShouldNotBeNull();
        captured.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeFunctionAsync_ApprovedAndRemember_PublishesSuccessEvent()
    {
        // Arrange
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.ApprovedAndRemember);
        var function = AIFunctionFactory.Create(() => "result", "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

        ToolCallEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is ToolCallEvent t) { captured = t; } })
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        captured.ShouldNotBeNull();
        captured.ToolName.ShouldBe("mcp__server__TestTool");
        captured.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeFunctionAsync_ApprovedTool_PublishesToolExecLatencyEvent()
    {
        var publisher = new Mock<IMetricsPublisher>();
        var handler = new TestApprovalHandler(ToolApprovalResult.Approved);
        var function = AIFunctionFactory.Create(() => "result", "mcp__server__TestTool");
        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

        LatencyEvent? captured = null;
        publisher
            .Setup(p => p.PublishAsync(It.IsAny<MetricEvent>(), It.IsAny<CancellationToken>()))
            .Callback<MetricEvent, CancellationToken>((e, _) => { if (e is LatencyEvent l) { captured = l; } })
            .Returns(Task.CompletedTask);

        var client = new ToolApprovalChatClient(fakeClient, handler, metricsPublisher: publisher.Object);
        var options = new ChatOptions { Tools = [function] };

        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        captured.ShouldNotBeNull();
        captured.Stage.ShouldBe(LatencyStage.ToolExec);
        captured.DurationMs.ShouldBeGreaterThanOrEqualTo(0);
    }

}