using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class ToolApprovalChatClientTests
{
    [Fact]
    public async Task InvokeFunctionAsync_WhenNotWhitelisted_RequestsApproval()
    {
        // Arrange
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "mcp:server:TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:server:TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldNotBeEmpty();
        handler.RequestedApprovals[0][0].ToolName.ShouldBe("mcp:server:TestTool");
        invoked.ShouldBeTrue("Tool should have been invoked after approval");
    }

    [Fact]
    public async Task InvokeFunctionAsync_WhenExactMatchWhitelisted_SkipsApproval()
    {
        // Arrange
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "mcp:server:TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:server:TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler, whitelistPatterns: ["mcp:server:TestTool"]);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert - whitelisted tool should not request approval but should notify
        handler.RequestedApprovals.ShouldBeEmpty();
        handler.AutoApprovedNotifications.ShouldHaveSingleItem();
        handler.AutoApprovedNotifications[0][0].ToolName.ShouldBe("mcp:server:TestTool");
        invoked.ShouldBeTrue("Whitelisted tool should be invoked without approval");
    }

    [Fact]
    public async Task InvokeFunctionAsync_WhenServerWildcardWhitelisted_SkipsApproval()
    {
        // Arrange
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "mcp:mcp-library:FileSearch");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:mcp-library:FileSearch", "call1"));

        // Whitelist all tools from mcp-library server
        var client = new ToolApprovalChatClient(fakeClient, handler, whitelistPatterns: ["mcp:mcp-library:*"]);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldBeEmpty();
        handler.AutoApprovedNotifications.ShouldHaveSingleItem();
        invoked.ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeFunctionAsync_WhenAllMcpWildcardWhitelisted_SkipsApproval()
    {
        // Arrange
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "mcp:any-server:AnyTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:any-server:AnyTool", "call1"));

        // Whitelist all MCP tools
        var client = new ToolApprovalChatClient(fakeClient, handler, whitelistPatterns: ["mcp:*"]);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldBeEmpty();
        invoked.ShouldBeTrue();
    }

    [Fact]
    public async Task InvokeFunctionAsync_WhenRejected_TerminatesAndReturnsRejectionMessage()
    {
        // Arrange
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Rejected);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, "mcp:server:TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp:server:TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler);
        var options = new ChatOptions { Tools = [function] };

        // Act
        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldNotBeEmpty();
        invoked.ShouldBeFalse("Rejected tool should not be invoked");

        // The rejection message is returned as function result
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
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var whitelistedInvoked = false;
        var nonWhitelistedInvoked = false;

        var whitelistedFunc = AIFunctionFactory.Create(() =>
        {
            whitelistedInvoked = true;
            return "whitelisted result";
        }, "mcp:trusted-server:WhitelistedTool");

        var nonWhitelistedFunc = AIFunctionFactory.Create(() =>
        {
            nonWhitelistedInvoked = true;
            return "non-whitelisted result";
        }, "mcp:untrusted-server:NonWhitelistedTool");

        var fakeClient = new FakeChatClient();
        // Return tool calls for both tools
        fakeClient.SetNextResponse(CreateMultiToolCallResponse(
            ("mcp:trusted-server:WhitelistedTool", "call1"),
            ("mcp:untrusted-server:NonWhitelistedTool", "call2")));

        // Whitelist all tools from trusted-server
        var client = new ToolApprovalChatClient(fakeClient, handler, whitelistPatterns: ["mcp:trusted-server:*"]);
        var options = new ChatOptions { Tools = [whitelistedFunc, nonWhitelistedFunc] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldHaveSingleItem();
        handler.RequestedApprovals[0][0].ToolName.ShouldBe("mcp:untrusted-server:NonWhitelistedTool");
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

    private sealed class TestApprovalHandler(ToolApprovalResult result) : IToolApprovalHandler
    {
        public List<IReadOnlyList<ToolApprovalRequest>> RequestedApprovals { get; } = [];
        public List<IReadOnlyList<ToolApprovalRequest>> AutoApprovedNotifications { get; } = [];

        public Task<ToolApprovalResult> RequestApprovalAsync(
            IReadOnlyList<ToolApprovalRequest> requests,
            CancellationToken cancellationToken)
        {
            RequestedApprovals.Add(requests);
            return Task.FromResult(result);
        }

        public Task NotifyAutoApprovedAsync(
            IReadOnlyList<ToolApprovalRequest> requests,
            CancellationToken cancellationToken)
        {
            AutoApprovedNotifications.Add(requests);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Queue<ChatResponse> _responses = new();

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

        public void SetNextResponse(ChatResponse response)
        {
            _responses.Enqueue(response);
        }
    }
}