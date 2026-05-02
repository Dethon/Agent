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
        }, "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

        var client = new ToolApprovalChatClient(fakeClient, handler);
        var options = new ChatOptions { Tools = [function] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldNotBeEmpty();
        handler.RequestedApprovals[0][0].ToolName.ShouldBe("mcp__server__TestTool");
        invoked.ShouldBeTrue("Tool should have been invoked after approval");
    }

    [Theory]
    [InlineData("mcp__server__TestTool", "mcp__server__TestTool", "mcp__server__TestTool")]
    [InlineData("mcp__mcp-library__*", "mcp__mcp-library__FileSearch", "mcp__mcp-library__FileSearch")]
    [InlineData("mcp__*", "mcp__any-server__AnyTool", "mcp__any-server__AnyTool")]
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
        }, "mcp__server__TestTool");

        var fakeClient = new FakeChatClient();
        fakeClient.SetNextResponse(CreateToolCallResponse("mcp__server__TestTool", "call1"));

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
        }, "mcp__trusted-server__WhitelistedTool");

        var nonWhitelistedFunc = AIFunctionFactory.Create(() =>
        {
            nonWhitelistedInvoked = true;
            return "non-whitelisted result";
        }, "mcp__untrusted-server__NonWhitelistedTool");

        var fakeClient = new FakeChatClient();
        // Return tool calls for both tools
        fakeClient.SetNextResponse(CreateMultiToolCallResponse(
            ("mcp__trusted-server__WhitelistedTool", "call1"),
            ("mcp__untrusted-server__NonWhitelistedTool", "call2")));

        // Whitelist all tools from trusted-server
        var client = new ToolApprovalChatClient(fakeClient, handler, whitelistPatterns: ["mcp__trusted-server__*"]);
        var options = new ChatOptions { Tools = [whitelistedFunc, nonWhitelistedFunc] };

        // Act
        await client.GetResponseAsync([new ChatMessage(ChatRole.User, "test")], options);

        // Assert
        handler.RequestedApprovals.ShouldHaveSingleItem();
        handler.RequestedApprovals[0][0].ToolName.ShouldBe("mcp__untrusted-server__NonWhitelistedTool");
        whitelistedInvoked.ShouldBeTrue();
        nonWhitelistedInvoked.ShouldBeTrue();
    }

}