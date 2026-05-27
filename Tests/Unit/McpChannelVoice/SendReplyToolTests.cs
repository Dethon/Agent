using Domain.DTOs;
using McpChannelVoice.McpTools;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Tests.Unit.McpChannelVoice;

public class SendReplyToolTests
{
    [Fact]
    public async Task McpRun_ReturnsOkPlaceholder()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        var result = await SendReplyTool.McpRun(
            "kitchen-01",
            "hello",
            ReplyContentType.Text,
            true,
            null,
            services);

        result.ShouldBe("ok");
    }
}