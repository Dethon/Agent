using Domain.DTOs;
using McpServerScheduling.McpTools;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerScheduling;

public class RequestApprovalToolTests
{
    [Fact]
    public void McpRun_RequestMode_AutoApproves()
        => RequestApprovalTool.McpRun("c1", ApprovalMode.Request, []).ShouldBe("approved");

    [Fact]
    public void McpRun_NotifyMode_ReturnsNotified()
        => RequestApprovalTool.McpRun("c1", ApprovalMode.Notify, []).ShouldBe("notified");
}