using Domain.DTOs;
using Infrastructure.Agents.ChatClients;
using Microsoft.Extensions.AI;
using Shouldly;
using Tests.Unit.Infrastructure.Helpers;
using static Tests.Unit.Infrastructure.Helpers.ToolApprovalResponseFactory;

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

    [Theory]
    [InlineData("mcp:server:TestTool", "mcp:server:TestTool", "mcp:server:TestTool")]
    [InlineData("mcp:mcp-library:*", "mcp:mcp-library:FileSearch", "mcp:mcp-library:FileSearch")]
    [InlineData("mcp:*", "mcp:any-server:AnyTool", "mcp:any-server:AnyTool")]
    public async Task SendAsync_WhitelistedTool_SkipsApproval(string whitelistPattern, string toolName, string callToolName)
    {
        // Arrange
        var handler = new TestApprovalHandler(result: ToolApprovalResult.Approved);
        var invoked = false;
        var function = AIFunctionFactory.Create(() =>
        {
            invoked = true;
            return "result";
        }, toolName);

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse(callToolName, "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler, whitelistPatterns: [whitelistPattern]);
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

}