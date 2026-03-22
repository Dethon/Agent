using McpChannelServiceBus.McpTools;
using Shouldly;

namespace Tests.Unit.McpChannelServiceBus;

public class RequestApprovalToolTests
{
    [Fact]
    public void McpRun_RequestMode_ReturnsApproved()
    {
        const string requests = """[{"toolName":"tool","arguments":{}}]""";

        var result = RequestApprovalTool.McpRun("corr-1", "request", requests);

        result.ShouldBe("approved");
    }

    [Fact]
    public void McpRun_NotifyMode_ReturnsNotified()
    {
        const string requests = """[{"toolName":"tool","arguments":{}}]""";

        var result = RequestApprovalTool.McpRun("corr-1", "notify", requests);

        result.ShouldBe("notified");
    }

    [Fact]
    public void McpRun_UnknownMode_ReturnsApproved()
    {
        const string requests = """[{"toolName":"tool","arguments":{}}]""";

        var result = RequestApprovalTool.McpRun("corr-1", "something_else", requests);

        result.ShouldBe("approved");
    }

    [Fact]
    public void McpRun_MultipleRequests_StillAutoApproves()
    {
        const string requests = """[{"toolName":"a","arguments":{}},{"toolName":"b","arguments":{}}]""";

        var result = RequestApprovalTool.McpRun("corr-1", "request", requests);

        result.ShouldBe("approved");
    }
}
