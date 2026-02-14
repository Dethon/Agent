using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Shouldly;
using Tests.Integration.Fixtures;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;

namespace Tests.Integration.McpServerTests;

public class McpCalendarServerTests(McpCalendarServerFixture fixture) : IClassFixture<McpCalendarServerFixture>
{
    private static string GetTextContent(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Select(t => t.Text).FirstOrDefault() ?? "";

    [Fact]
    public async Task McpServer_ListsAllCalendarTools()
    {
        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var tools = await client.ListToolsAsync();

        tools.ShouldNotBeEmpty();
        var toolNames = tools.Select(t => t.Name).ToList();
        toolNames.ShouldContain("calendar_list");
        toolNames.ShouldContain("event_list");
        toolNames.ShouldContain("event_get");
        toolNames.ShouldContain("event_create");
        toolNames.ShouldContain("event_update");
        toolNames.ShouldContain("event_delete");
        toolNames.ShouldContain("check_availability");

        await client.DisposeAsync();
    }

    [Fact]
    public async Task CalendarListTool_ReturnsCalendarsFromGraphApi()
    {
        var graphResponse = new
        {
            value = new[]
            {
                new { id = "cal-1", name = "Calendar", color = "auto", isDefaultCalendar = true, canEdit = true },
                new { id = "cal-2", name = "Work", color = "lightBlue", isDefaultCalendar = false, canEdit = true }
            }
        };

        fixture.GraphApiMock.Given(
            Request.Create().WithPath("/me/calendars").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody(JsonSerializer.Serialize(graphResponse)));

        var client = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(fixture.McpEndpoint)
            }),
            cancellationToken: CancellationToken.None);

        var result = await client.CallToolAsync(
            "calendar_list",
            new Dictionary<string, object?> { ["accessToken"] = "test-token" },
            cancellationToken: CancellationToken.None);

        result.ShouldNotBeNull();
        result.IsError.ShouldBe(false);
        var text = GetTextContent(result);
        text.ShouldContain("cal-1");
        text.ShouldContain("Calendar");

        await client.DisposeAsync();
    }
}
