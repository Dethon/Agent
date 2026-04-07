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

            // Take snapshot to find the departure textbox ref
            var snapshot1 = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            snapshot1.ErrorMessage.ShouldBeNull();

            var refMatch = System.Text.RegularExpressions.Regex.Match(
                snapshot1.Snapshot!, @"textbox ""ex\) Tokyo"" \[ref=(e\d+)\]");
            refMatch.Success.ShouldBeTrue("Should find departure textbox in snapshot");
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

            // Take a snapshot and verify dropdown items appear
            var snapshot2 = await fixture.Browser.SnapshotAsync(new SnapshotRequest(sessionId));
            snapshot2.ErrorMessage.ShouldBeNull();
            output.WriteLine($"Snapshot after type:\n{snapshot2.Snapshot}");

            var hasOdawaraItem = snapshot2.Snapshot!.Contains("\"Odawara\"")
                              && snapshot2.Snapshot.Contains("listitem");
            hasOdawaraItem.ShouldBeTrue("Dropdown should contain 'Odawara' listitem");

            // Find the Odawara listitem ref and click it
            var itemMatch = System.Text.RegularExpressions.Regex.Match(
                snapshot2.Snapshot!, @"listitem ""Odawara"" \[ref=(e\d+)\]");
            itemMatch.Success.ShouldBeTrue("Should find Odawara listitem with ref");
            var itemRef = itemMatch.Groups[1].Value;
            output.WriteLine($"Odawara item ref: {itemRef}");

            var clickResult = await fixture.Browser.ActionAsync(new WebActionRequest(
                SessionId: sessionId,
                Ref: itemRef,
                Action: WebActionType.Click));
            clickResult.Status.ShouldBe(WebActionStatus.Success);
            output.WriteLine($"Click diff:\n{clickResult.Snapshot}");

            // Verify the hidden departure input was set via JS eval
            await fixture.Browser.EvaluateOnSessionAsync(sessionId, """
                (() => {
                    const hidden = document.querySelector('input[name="departure"]');
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
