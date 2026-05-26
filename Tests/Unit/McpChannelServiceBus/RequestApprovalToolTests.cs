using Domain.DTOs;
using McpChannelServiceBus.McpTools;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class RequestApprovalToolTests
{
    private static readonly IReadOnlyList<ToolApprovalRequest> _singleTool =
        [new ToolApprovalRequest(null, "tool", new Dictionary<string, object?>())];

    private static readonly IReadOnlyList<ToolApprovalRequest> _multiTool =
    [
        new ToolApprovalRequest(null, "a", new Dictionary<string, object?>()),
        new ToolApprovalRequest(null, "b", new Dictionary<string, object?>())
    ];

    [Fact]
    public void McpRun_RequestMode_SingleTool_ReturnsApproved()
        => RequestApprovalTool.McpRun("corr-1", ApprovalMode.Request, _singleTool).ShouldBe("approved");

    [Fact]
    public void McpRun_NotifyMode_SingleTool_ReturnsNotified()
        => RequestApprovalTool.McpRun("corr-1", ApprovalMode.Notify, _singleTool).ShouldBe("notified");

    [Fact]
    public void McpRun_RequestMode_MultipleTools_ReturnsApproved()
        => RequestApprovalTool.McpRun("corr-1", ApprovalMode.Request, _multiTool).ShouldBe("approved");
}