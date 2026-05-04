using Domain.DTOs;
using McpChannelServiceBus.McpTools;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class RequestApprovalToolTests
{
    [Theory]
    [InlineData(ApprovalMode.Request, """[{"toolName":"tool","arguments":{}}]""", "approved")]
    [InlineData(ApprovalMode.Notify, """[{"toolName":"tool","arguments":{}}]""", "notified")]
    [InlineData(ApprovalMode.Request, """[{"toolName":"a","arguments":{}},{"toolName":"b","arguments":{}}]""", "approved")]
    public void McpRun_AlwaysAutoApproves(ApprovalMode mode, string requests, string expected)
    {
        var result = RequestApprovalTool.McpRun("corr-1", mode, requests);

        result.ShouldBe(expected);
    }
}
