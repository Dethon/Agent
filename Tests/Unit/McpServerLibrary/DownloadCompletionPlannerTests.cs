using Domain.DTOs;
using Domain.DTOs.Channel;
using McpServerLibrary.Services;
using Shouldly;
using Xunit;

namespace Tests.Unit.McpServerLibrary;

public class DownloadCompletionPlannerTests
{
    [Fact]
    public void BuildPayload_TargetsTheOriginatingConversation()
    {
        var routing = new DownloadRouting
        {
            DownloadId = 42,
            Title = "The Lost City of Z 1080p",
            Context = new ConversationContext("jack", "conv-7", "fran", new ReplyTarget("signalr", "conv-7"))
        };

        var payload = DownloadCompletionPlanner.BuildPayload(routing);

        payload.ConversationId.ShouldBe("conv-7");
        payload.AgentId.ShouldBe("jack");
        payload.Sender.ShouldBe("fran");
        payload.ReplyTo.ShouldBe([new ReplyTarget("signalr", "conv-7")]);
        payload.Origin.ShouldBe(new MessageOrigin(MessageOriginKind.Download, null));
        payload.Content.ShouldContain("The Lost City of Z 1080p");
        payload.Content.ShouldContain("/media/downloads/42");
    }
}