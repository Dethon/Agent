using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools.Calendar;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Calendar;

public class EventDeleteToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();
    private readonly TestableEventDeleteTool _tool;

    public EventDeleteToolTests()
    {
        _tool = new TestableEventDeleteTool(_providerMock.Object);
    }

    [Fact]
    public async Task Run_CallsDeleteEventAsyncOnProvider()
    {
        _providerMock.Setup(p => p.DeleteEventAsync("token", "evt-1", "cal-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _tool.InvokeRun("token", "evt-1", "cal-1");

        _providerMock.Verify(p => p.DeleteEventAsync("token", "evt-1", "cal-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_WithNullCalendarId_PassesNullToProvider()
    {
        _providerMock.Setup(p => p.DeleteEventAsync("token", "evt-2", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _tool.InvokeRun("token", "evt-2");

        _providerMock.Verify(p => p.DeleteEventAsync("token", "evt-2", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Run_ReturnsDeletedConfirmation()
    {
        _providerMock.Setup(p => p.DeleteEventAsync("token", "evt-1", null, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _tool.InvokeRun("token", "evt-1");

        result["status"]!.GetValue<string>().ShouldBe("deleted");
        result["eventId"]!.GetValue<string>().ShouldBe("evt-1");
    }

    [Fact]
    public async Task Run_ConfirmationContainsCorrectEventId()
    {
        _providerMock.Setup(p => p.DeleteEventAsync("token", "evt-abc-123", "cal-x", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _tool.InvokeRun("token", "evt-abc-123", "cal-x");

        result["status"]!.GetValue<string>().ShouldBe("deleted");
        result["eventId"]!.GetValue<string>().ShouldBe("evt-abc-123");
    }

    [Fact]
    public void HasExpectedNameAndDescription()
    {
        EventDeleteTool.ToolName.ShouldNotBeNullOrWhiteSpace();
        EventDeleteTool.ToolDescription.ShouldNotBeNullOrWhiteSpace();
    }
}

internal class TestableEventDeleteTool(ICalendarProvider provider) : EventDeleteTool(provider)
{
    public Task<JsonNode> InvokeRun(
        string accessToken,
        string eventId,
        string? calendarId = null,
        CancellationToken ct = default)
        => Run(accessToken, eventId, calendarId, ct);
}
