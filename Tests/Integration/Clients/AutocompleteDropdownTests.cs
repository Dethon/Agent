using Domain.Contracts;
using Shouldly;
using Tests.Integration.Fixtures;
using Xunit.Abstractions;

namespace Tests.Integration.Clients;

[Collection("PlaywrightWebBrowserIntegration")]
public class AutocompleteDropdownTests(
    PlaywrightWebBrowserFixture fixture,
    ITestOutputHelper output) : IAsyncLifetime
{
    public async Task InitializeAsync() => await fixture.ClearContextStateAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [SkippableFact]
    public async Task TypeAction_OnAutocompleteField_ShowsDropdownInSnapshot()
    {
        Skip.IfNot(fixture.IsAvailable, $"Not available: {fixture.InitializationError}");

        var sessionId = $"test-{Guid.NewGuid():N}";
        try
        {
            // Navigate to the page
            var browseResult = await fixture.Browser.NavigateAsync(new BrowseRequest(
                SessionId: sessionId,
                Url: "https://japantravel.navitime.com/en/"));
            browseResult.Status.ShouldBeOneOf(BrowseStatus.Success, BrowseStatus.Partial);

            // Wait for the departure textbox to render — navitime builds the form client-side
            // after navigation, so a single immediate snapshot is a race.
            var snapshot1 = await fixture.WaitForSnapshotAsync(
                sessionId,
                s => System.Text.RegularExpressions.Regex.IsMatch(s, @"textbox ""ex\) Tokyo"" \[ref=(e\d+)\]"),
                "departure textbox to appear in snapshot");

            var refMatch = System.Text.RegularExpressions.Regex.Match(
                snapshot1, @"textbox ""ex\) Tokyo"" \[ref=(e\d+)\]");
            var depRef = refMatch.Groups[1].Value;
            output.WriteLine($"Departure ref: {depRef}");

            // Type "Odawara" — the jQuery trigger fix should open the dropdown
            var typeResult = await fixture.Browser.ActionAsync(new WebActionRequest(
                SessionId: sessionId,
                Ref: depRef,
                Action: WebActionType.Type,
                Value: "Odawara"));
            typeResult.Status.ShouldBe(WebActionStatus.Success);
            output.WriteLine($"Type diff:\n{typeResult.Snapshot}");

            // Wait for the dropdown to populate — suggestions arrive asynchronously after typing.
            var snapshot2 = await fixture.WaitForSnapshotAsync(
                sessionId,
                s => System.Text.RegularExpressions.Regex.IsMatch(s, @"listitem ""Odawara"" \[ref=(e\d+)\]"),
                "Odawara dropdown listitem to appear");
            output.WriteLine($"Snapshot after type:\n{snapshot2}");

            // Find the Odawara listitem ref and click it
            var itemMatch = System.Text.RegularExpressions.Regex.Match(
                snapshot2, @"listitem ""Odawara"" \[ref=(e\d+)\]");
            var itemRef = itemMatch.Groups[1].Value;
            output.WriteLine($"Odawara item ref: {itemRef}");

            var clickResult = await fixture.Browser.ActionAsync(new WebActionRequest(
                SessionId: sessionId,
                Ref: itemRef,
                Action: WebActionType.Click));
            clickResult.Status.ShouldBe(WebActionStatus.Success);
            output.WriteLine($"Click diff:\n{clickResult.Snapshot}");

            // Verify the hidden departure input for the JR train form was set via JS eval.
            // The page has multiple inputs with name="departure" (flight booking + JR route),
            // so the selector must be scoped to the JR train suggest container.
            await fixture.Browser.EvaluateOnSessionAsync(sessionId, """
                (() => {
                    const hidden = document.querySelector(
                        '.p-book__jr__form__station__suggest__departure input[name="departure"]');
                    if (!hidden || !hidden.value) throw new Error('Hidden departure input not set');
                    window.__testDepartureCode = hidden.value;
                })()
            """);

            output.WriteLine("Dropdown opened, item clicked, hidden input set — autocomplete works in Camoufox");
        }
        finally
        {
            await fixture.Browser.CloseSessionAsync(sessionId);
        }
    }
}