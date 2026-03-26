using McpChannelServiceBus.McpTools;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class RequestApprovalToolTests
{
    [Theory]
    [InlineData("request", """[{"toolName":"tool","arguments":{}}]""", "approved")]
    [InlineData("notify", """[{"toolName":"tool","arguments":{}}]""", "notified")]
    [InlineData("something_else", """[{"toolName":"tool","arguments":{}}]""", "approved")]
    [InlineData("request", """[{"toolName":"a","arguments":{}},{"toolName":"b","arguments":{}}]""", "approved")]
    public void McpRun_AlwaysAutoApproves(string mode, string requests, string expected)
    {
        var result = RequestApprovalTool.McpRun("corr-1", mode, requests);

        result.ShouldBe(expected);
    }
}
