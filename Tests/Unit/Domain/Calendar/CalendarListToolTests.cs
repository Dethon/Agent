using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.DTOs;
using Domain.Tools.Calendar;
using Moq;
using Shouldly;

namespace Tests.Unit.Domain.Calendar;

public class CalendarListToolTests
{
    private readonly Mock<ICalendarProvider> _providerMock = new();
    private readonly TestableCalendarListTool _tool;

    public CalendarListToolTests()
    {
        _tool = new TestableCalendarListTool(_providerMock.Object);
    }

    [Fact]
    public async Task Run_ReturnsCalendarsAsJsonArray()
    {
        var calendars = new List<CalendarInfo>
        {
            new() { Id = "cal-1", Name = "Personal", IsDefault = true, CanEdit = true, Color = "#0000FF" },
            new() { Id = "cal-2", Name = "Work", IsDefault = false, CanEdit = true, Color = "#FF0000" }
        };
        _providerMock.Setup(p => p.ListCalendarsAsync("token-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendars);

        var result = await _tool.InvokeRun("token-123");

        var array = result.AsArray();
        array.Count.ShouldBe(2);
        array[0]!["id"]!.GetValue<string>().ShouldBe("cal-1");
        array[0]!["name"]!.GetValue<string>().ShouldBe("Personal");
        array[0]!["isDefault"]!.GetValue<bool>().ShouldBeTrue();
        array[0]!["canEdit"]!.GetValue<bool>().ShouldBeTrue();
        array[0]!["color"]!.GetValue<string>().ShouldBe("#0000FF");
    }

    [Fact]
    public async Task Run_WhenNoCalendars_ReturnsEmptyArray()
    {
        _providerMock.Setup(p => p.ListCalendarsAsync("token-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CalendarInfo>());

        var result = await _tool.InvokeRun("token-123");

        result.AsArray().Count.ShouldBe(0);
    }

    [Fact]
    public async Task Run_MapsAllCalendarInfoFields()
    {
        var calendars = new List<CalendarInfo>
        {
            new() { Id = "cal-x", Name = "Shared", IsDefault = false, CanEdit = false, Color = null }
        };
        _providerMock.Setup(p => p.ListCalendarsAsync("t", It.IsAny<CancellationToken>()))
            .ReturnsAsync(calendars);

        var result = await _tool.InvokeRun("t");

        var item = result.AsArray()[0]!;
        item["id"]!.GetValue<string>().ShouldBe("cal-x");
        item["name"]!.GetValue<string>().ShouldBe("Shared");
        item["isDefault"]!.GetValue<bool>().ShouldBeFalse();
        item["canEdit"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Fact]
    public void HasExpectedNameAndDescription()
    {
        CalendarListTool.ToolName.ShouldNotBeNullOrWhiteSpace();
        CalendarListTool.ToolDescription.ShouldNotBeNullOrWhiteSpace();
    }
}

internal class TestableCalendarListTool(ICalendarProvider provider) : CalendarListTool(provider)
{
    public Task<JsonNode> InvokeRun(string accessToken, CancellationToken ct = default)
        => Run(accessToken, ct);
}
