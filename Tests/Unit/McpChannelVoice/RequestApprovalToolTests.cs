using Domain.DTOs;
using McpChannelVoice.McpTools;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class RequestApprovalToolTests
{
    [Theory]
    [InlineData(ApprovalMode.Notify, "notified")]
    [InlineData(ApprovalMode.Request, "declined")]
    public async Task McpRun_PlaceholderReturnsBranchExpectedString(ApprovalMode mode, string expected)
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var result = await RequestApprovalTool.McpRun(
            "kitchen-01",
            mode,
            [],
            services);

        result.ShouldBe(expected);
    }
}