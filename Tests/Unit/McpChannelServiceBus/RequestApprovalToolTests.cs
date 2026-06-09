using Domain.DTOs;
using McpChannelServiceBus.McpTools;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class RequestApprovalToolTests
{
    private static readonly IReadOnlyList<ToolApprovalRequest> _singleTool =
        [new ToolApprovalRequest(null, "tool", new Dictionary<string, object?>())];

    [Theory]
    [InlineData(ApprovalMode.Request, "approved")]
    [InlineData(ApprovalMode.Notify, "notified")]
    public void McpRun_AutoApprovesOrNotifies(ApprovalMode mode, string expected)
        => RequestApprovalTool.McpRun("corr-1", mode, _singleTool).ShouldBe(expected);
}