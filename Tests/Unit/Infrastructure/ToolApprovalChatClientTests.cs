using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class ToolApprovalChatClientTests
{
    [Fact]
    public async Task InvokeFunctionAsync_WhenNotWhitelisted_RequestsApproval()
    {
        // Arrange
        var handler = new TestApprovalHandler(approved: true);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldNotBeEmpty();
        handler.RequestedApprovals[0][0].ToolName.ShouldBe("TestTool");
        invoked.ShouldBeTrue("Tool should have been invoked after approval");
    }

    [Fact]
    public async Task InvokeFunctionAsync_WhenWhitelisted_SkipsApproval()
    {
        // Arrange
        var handler = new TestApprovalHandler(approved: true);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler, whitelistedTools: ["TestTool"]);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert - whitelisted tool should not request approval
        handler.RequestedApprovals.ShouldBeEmpty();
        invoked.ShouldBeTrue("Whitelisted tool should be invoked without approval");
    }

    [Fact]
    public async Task InvokeFunctionAsync_WhenRejected_ReturnsRejectionMessage()
    {
        // Arrange
        var handler = new TestApprovalHandler(approved: false);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler);
        var options = new ChatOptions { Tools = [function] };

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldNotBeEmpty();
        invoked.ShouldBeFalse("Rejected tool should not be invoked");

        // The rejection is returned as function result
        var resultContent = response.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionResultContent>()
            .FirstOrDefault();
        resultContent.ShouldNotBeNull();
        (resultContent.Result?.ToString() ?? string.Empty).ShouldContain("rejected");
    }

    [Fact]
    public async Task InvokeFunctionAsync_WithMultipleTools_OnlyApprovesNonWhitelisted()
    {
        // Arrange
        var handler = new TestApprovalHandler(approved: true);
        var whitelistedInvoked = false;
        var nonWhitelistedInvoked = false;

        var whitelistedFunc = AIFunctionFactory.Create(() =>
        {
            whitelistedInvoked = true;
            return "whitelisted result";
        }, "WhitelistedTool");

        var nonWhitelistedFunc = AIFunctionFactory.Create(() =>
        {
            nonWhitelistedInvoked = true;
            return "non-whitelisted result";
        }, "NonWhitelistedTool");

        var fakeClient = new FakeChatClient();
        // Return tool calls for both tools
        fakeClient.SetNextResponse(CreateMultiToolCallResponse(
            ("WhitelistedTool", "call1"),
            ("NonWhitelistedTool", "call2")));

        var client = new ToolApprovalChatClient(fakeClient, handler, whitelistedTools: ["WhitelistedTool"]);
        var options = new ChatOptions { Tools = [whitelistedFunc, nonWhitelistedFunc] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldHaveSingleItem();
        handler.RequestedApprovals[0][0].ToolName.ShouldBe("NonWhitelistedTool");
        whitelistedInvoked.ShouldBeTrue();
        nonWhitelistedInvoked.ShouldBeTrue();
    }

    private static ChatResponse CreateToolCallResponse(string toolName, string callId)
    {
        var toolCallContent = new FunctionCallContent(callId, toolName, new Dictionary<string, object?>());
        var message = new ChatMessage(ChatRole.Assistant, [toolCallContent]);
        return new ChatResponse([message]) { FinishReason = ChatFinishReason.ToolCalls };
    }

    private static ChatResponse CreateMultiToolCallResponse(params (string toolName, string callId)[] tools)
    {
        var contents = tools
            .Select(t => new FunctionCallContent(t.callId, t.toolName, new Dictionary<string, object?>()))
            .ToList<AIContent>();
        var message = new ChatMessage(ChatRole.Assistant, contents);
        return new ChatResponse([message]) { FinishReason = ChatFinishReason.ToolCalls };
    }

    private sealed class TestApprovalHandler(bool approved) : IToolApprovalHandler
    {
        public List<IReadOnlyList<ToolApprovalRequest>> RequestedApprovals { get; } = [];

        public Task<bool> RequestApprovalAsync(
            IReadOnlyList<ToolApprovalRequest> requests,
            CancellationToken cancellationToken)
        {
            RequestedApprovals.Add(requests);
            return Task.FromResult(approved);
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

        public void SetNextResponse(ChatResponse response)
        {
            _responses.Enqueue(response);
        }

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
        {
            return AsyncEnumerable.Empty<ChatResponseUpdate>();
        }

        public void Dispose() { }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }
    }
}