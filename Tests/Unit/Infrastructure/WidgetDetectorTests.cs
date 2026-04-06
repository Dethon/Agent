using Infrastructure.Clients.Browser;
using Shouldly;

namespace Tests.Unit.Infrastructure;

public class WidgetDetectorTests
{
    [Fact]
    public void FormatWidgetContent_Datepicker_FormatsCorrectly()
    {
        var widget = new DetectedWidget(
            WidgetType.Datepicker,
            Label: "Check-in Date",
            CurrentValue: null,
            Options:
            [
                new WidgetOption("15", ".day[data-date='2026-04-15']"),
                new WidgetOption("16", ".day[data-date='2026-04-16']"),
                new WidgetOption("17", ".day[data-date='2026-04-17']")
            ],
            Metadata: new Dictionary<string, string> { ["visibleMonth"] = "April 2026" },
            NearbyActions:
            [
                new NearbyAction("Check-out Date", "input[name='checkout']", "input"),
                new NearbyAction("Search", "button.search-submit", "button")
            ]);

        var content = WidgetDetector.FormatWidgetContent(widget);

        content.ShouldContain("[Widget: datepicker]");
        content.ShouldContain("Check-in Date");
        content.ShouldContain("\"15\"");
        content.ShouldContain(".day[data-date='2026-04-15']");
        content.ShouldContain("[Nearby actions]");
        content.ShouldContain("Check-out Date");
    }

    [Fact]
    public void FormatWidgetContent_Autocomplete_FormatsCorrectly()
    {
        var widget = new DetectedWidget(
            WidgetType.Autocomplete,
            Label: "City",
            CurrentValue: "New",
            Options:
            [
                new WidgetOption("New York, NY", ".suggestion-item:nth-child(1)"),
                new WidgetOption("New Jersey", ".suggestion-item:nth-child(2)")
            ],
            Metadata: null,
            NearbyActions: []);

        var content = WidgetDetector.FormatWidgetContent(widget);

        content.ShouldContain("[Widget: autocomplete]");
        content.ShouldContain("2 suggestions");
        content.ShouldContain("New York, NY");
        content.ShouldContain(".suggestion-item:nth-child(1)");
    }

    [Fact]
    public void FormatWidgetContent_Dropdown_FormatsCorrectly()
    {
        var widget = new DetectedWidget(
            WidgetType.Dropdown,
            Label: "Country",
            CurrentValue: "United States",
            Options:
            [
                new WidgetOption("Afghanistan", "[role='option']:nth-child(1)"),
                new WidgetOption("Albania", "[role='option']:nth-child(2)")
            ],
            Metadata: new Dictionary<string, string> { ["totalOptions"] = "195" },
            NearbyActions: []);

        var content = WidgetDetector.FormatWidgetContent(widget);

        content.ShouldContain("[Widget: dropdown]");
        content.ShouldContain("Country");
        content.ShouldContain("United States");
        content.ShouldContain("Afghanistan");
    }

    [Fact]
    public void FormatWidgetContent_Slider_FormatsCorrectly()
    {
        var widget = new DetectedWidget(
            WidgetType.Slider,
            Label: "Price",
            CurrentValue: "50",
            Options: [],
            Metadata: new Dictionary<string, string>
            {
                ["min"] = "0",
                ["max"] = "500",
                ["step"] = "10"
            },
            NearbyActions: []);

        var content = WidgetDetector.FormatWidgetContent(widget);

        content.ShouldContain("[Widget: slider]");
        content.ShouldContain("Price");
        content.ShouldContain("Current value: 50");
        content.ShouldContain("Range: 0 - 500");
    }

    [Fact]
    public void FormatWidgetContent_WithNoOptions_ShowsNoOptionsMessage()
    {
        var widget = new DetectedWidget(
            WidgetType.Dropdown,
            Label: "Empty",
            CurrentValue: null,
            Options: [],
            Metadata: null,
            NearbyActions: []);

        var content = WidgetDetector.FormatWidgetContent(widget);

        content.ShouldContain("[Widget: dropdown]");
        content.ShouldContain("No options visible");
    }
}
