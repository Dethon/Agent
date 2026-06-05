using Domain.DTOs;
using McpServerScheduling.McpTools;
using Shouldly;

namespace Tests.Unit.McpServerScheduling;

public class RequestApprovalToolTests
{
    [Theory]
    [InlineData(ApprovalMode.Request, "approved")]
    [InlineData(ApprovalMode.Notify, "notified")]
    public void McpRun_AutoApprovesOrNotifies(ApprovalMode mode, string expected)
        => RequestApprovalTool.McpRun("c1", mode, []).ShouldBe(expected);
}