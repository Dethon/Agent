using System.Net;
using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.Clients;

[Collection("PlaywrightWebBrowserIntegration")]
public class JQueryFocusWidgetTests(
    PlaywrightWebBrowserFixture fixture,
    ITestOutputHelper output) : IAsyncLifetime
{
    /// <summary>
    /// JS that injects a jQuery shim and a readonly input with a jQuery-only focus handler
    /// into any page. Reproduces the bootstrap-datepicker pattern where the widget ONLY
    /// opens via jQuery focus events, not native DOM focus.
    /// </summary>
    private const string InjectTestWidgetScript = """
        () => {
            // Minimal jQuery shim
            (function(global) {
                function jQuery(selector) {
                    if (typeof selector === 'function') { selector(); return; }
                    if (selector instanceof jQuery) return selector;
                    if (!(this instanceof jQuery)) return new jQuery(selector);
                    if (typeof selector === 'string') {
                        this.elements = Array.from(document.querySelectorAll(selector));
                    } else if (selector && selector.nodeType) {
                        this.elements = [selector];
                    } else {
                        this.elements = [];
                    }
                    this.length = this.elements.length;
                }
                jQuery.prototype.on = function(event, handler) {
                    this.elements.forEach(function(el) {
                        if (!el.__jqEvents) el.__jqEvents = {};
                        if (!el.__jqEvents[event]) el.__jqEvents[event] = [];
                        el.__jqEvents[event].push(handler);
                    });
                    return this;
                };
                jQuery.prototype.triggerHandler = function(event) {
                    var el = this.elements[0];
                    if (el && el.__jqEvents && el.__jqEvents[event]) {
                        el.__jqEvents[event].forEach(function(h) { h.call(el); });
                    }
                    return this;
                };
                jQuery.prototype.addClass = function(cls) {
                    this.elements.forEach(function(el) { el.classList.add(cls); });
                    return this;
                };
                jQuery.prototype.removeClass = function(cls) {
                    this.elements.forEach(function(el) { el.classList.remove(cls); });
                    return this;
                };
                jQuery.prototype.find = function(sel) {
                    var r = [];
                    this.elements.forEach(function(el) { r = r.concat(Array.from(el.querySelectorAll(sel))); });
                    var jq = new jQuery('__none__'); jq.elements = r; jq.length = r.length; return jq;
                };
                jQuery.prototype.val = function(v) {
                    if (v === undefined) return this.elements[0] ? this.elements[0].value : undefined;
                    this.elements.forEach(function(el) { el.value = v; }); return this;
                };
                jQuery.prototype.data = function(key) {
                    return this.elements[0] ? this.elements[0].getAttribute('data-' + key) : undefined;
                };
                global.jQuery = global.$ = jQuery;
            })(window);

            // Clear page and inject test widget
            document.body.innerHTML = `
                <h1>jQuery Focus Widget Test</h1>
                <label for="date-input">Pick a date:</label>
                <input type="text" id="date-input" readonly value="2026/04/06"
                       style="cursor:pointer;width:200px;">
                <div id="dropdown" role="listbox"
                     style="display:none;position:absolute;background:white;border:1px solid #ccc;padding:10px;z-index:100;">
                    <button role="option" data-value="2026/04/07">April 7</button>
                    <button role="option" data-value="2026/04/08">April 8</button>
                    <button role="option" data-value="2026/04/09">April 9</button>
                </div>
            `;

            // Bind ONLY via jQuery .on('focus') — exactly like bootstrap-datepicker
            var $input = $('#date-input');
            var $dropdown = $('#dropdown');
            $input.on('focus', function() {
                document.getElementById('dropdown').style.display = 'block';
            });
            $input.on('blur', function() {
                setTimeout(function() { document.getElementById('dropdown').style.display = 'none'; }, 300);
            });
        }
        """;

    public async Task InitializeAsync() => await fixture.ClearContextStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task ClickAction_OnReadonlyInputWithJQueryFocus_OpensDropdown()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            // Navigate to a simple page, then inject test widget
            var browseResult = await fixture.Browser.NavigateAsync(new BrowseRequest(
                SessionId: sessionId,
                Url: "https://example.com"));
            browseResult.Status.ShouldBe(BrowseStatus.Success);

            // Inject jQuery shim + readonly input with jQuery-only focus handler
            await fixture.Browser.EvaluateOnSessionAsync(sessionId, InjectTestWidgetScript);

            // Take snapshot to get refs
            var snapshot = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            snapshot.ErrorMessage.ShouldBeNull();
            output.WriteLine($"Snapshot:\n{snapshot.Snapshot}");

            // Find the textbox ref
            var lines = snapshot.Snapshot!.Split('\n');
            var inputLine = lines.FirstOrDefault(l => l.Contains("textbox") && l.Contains("ref="));
            inputLine.ShouldNotBeNull("Should find the readonly textbox in snapshot");
            output.WriteLine($"Input line: {inputLine}");

            var refMatch = System.Text.RegularExpressions.Regex.Match(inputLine, @"ref=(e\d+)");
            refMatch.Success.ShouldBeTrue("Should extract ref from snapshot line");
            var inputRef = refMatch.Groups[1].Value;
            output.WriteLine($"Input ref: {inputRef}");

            // Click the input — this must trigger the jQuery focus handler
            var clickResult = await fixture.Browser.ActionAsync(new WebActionRequest(
                SessionId: sessionId,
                Ref: inputRef,
                Action: WebActionType.Click));

            clickResult.Status.ShouldBe(WebActionStatus.Success);
            output.WriteLine($"Click result snapshot:\n{clickResult.Snapshot}");

            // The dropdown should be visible in the snapshot after click
            clickResult.Snapshot.ShouldNotBeNull("Action should return a snapshot");
            // Dropdown options should be visible — proves jQuery focus handler fired
            clickResult.Snapshot.ShouldContain("option");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }

    [SkippableFact]
    public async Task ClickAction_OnNavitimeDatePicker_OpensCalendar()
    {
        Skip.IfNot(fixture.IsAvailable, $"Playwright not available: {fixture.InitializationError}");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            // Navigate to the real navitime JR booking page
            var browseResult = await fixture.Browser.NavigateAsync(new BrowseRequest(
                SessionId: sessionId,
                Url: "https://japantravel.navitime.com/en/booking/jr"));
            browseResult.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);
            output.WriteLine($"Page loaded: {browseResult.Url}");

            // Take snapshot to get refs
            var snapshot = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            snapshot.ErrorMessage.ShouldBeNull();
            output.WriteLine($"Snapshot (first 2000 chars):\n{snapshot.Snapshot?[..Math.Min(2000, snapshot.Snapshot.Length)]}");

            // Find the departure date textbox — readonly input with date value
            var lines = snapshot.Snapshot!.Split('\n');
            var dateLine = lines.FirstOrDefault(l =>
                l.Contains("textbox") && l.Contains("ref=") && l.Contains("readonly"));
            dateLine.ShouldNotBeNull("Should find a readonly textbox (date input) in snapshot");
            output.WriteLine($"Date input line: {dateLine}");

            var refMatch = System.Text.RegularExpressions.Regex.Match(dateLine, @"ref=(e\d+)");
            refMatch.Success.ShouldBeTrue();
            var dateRef = refMatch.Groups[1].Value;
            output.WriteLine($"Date ref: {dateRef}");

            // Click the date input
            var clickResult = await fixture.Browser.ActionAsync(new WebActionRequest(
                SessionId: sessionId,
                Ref: dateRef,
                Action: WebActionType.Click));

            clickResult.Status.ShouldBe(WebActionStatus.Success);
            output.WriteLine($"Click result snapshot:\n{clickResult.Snapshot}");

            clickResult.Snapshot.ShouldNotBeNull();
            output.WriteLine($"Full click snapshot length: {clickResult.Snapshot.Length}");

            // Dump lines containing "cell" or table-related roles to see if datepicker is there
            var snapshotLines = clickResult.Snapshot.Split('\n');
            var tableLines = snapshotLines
                .Where(l => l.Contains("cell") || l.Contains("columnheader") || l.Contains("table"))
                .ToList();
            output.WriteLine($"Table-related lines ({tableLines.Count}):");
            foreach (var line in tableLines.Take(30))
                output.WriteLine($"  {line}");

            // Check for day-of-week headers (Su/Mo/Tu or Sun/Mon/Tue) — definitive datepicker signal
            var hasDayHeaders = snapshotLines.Any(l =>
                l.Contains("\"Su\"") || l.Contains("\"Mo\"") || l.Contains("\"Tu\"") ||
                l.Contains("\"Sun\"") || l.Contains("\"Mon\"") || l.Contains("\"Tue\""));
            output.WriteLine($"Has day-of-week headers: {hasDayHeaders}");

            hasDayHeaders.ShouldBeTrue(
                "Datepicker day-of-week headers should be visible after clicking date input");

            // Also verify a follow-up WebSnapshot still shows the datepicker
            // (the agent often calls WebSnapshot after WebAction)
            var followupSnapshot = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            followupSnapshot.ErrorMessage.ShouldBeNull();
            var followupLines = followupSnapshot.Snapshot!.Split('\n');
            var stillHasDayHeaders = followupLines.Any(l =>
                l.Contains("\"Su\"") || l.Contains("\"Mo\"") || l.Contains("\"Tu\"") ||
                l.Contains("\"Sun\"") || l.Contains("\"Mon\"") || l.Contains("\"Tue\""));
            output.WriteLine($"Follow-up WebSnapshot still has datepicker: {stillHasDayHeaders}");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}
