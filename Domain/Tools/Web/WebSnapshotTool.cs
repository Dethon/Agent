using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebSnapshotTool(IWebBrowser browser)
{
    protected const string Name = "WebSnapshot";

    protected const string Description =
        """
        Returns the accessibility tree of the current page showing all elements:
        headings, text, buttons, links, form fields, dropdowns, and their current
        state (expanded, checked, disabled, etc.).

        Each interactive element has a ref you use with WebAction to interact with it.

        Use this to understand page state and find elements before interacting.
        Call after WebBrowse to see interactive elements, or after WebAction to see
        the full page when the scoped response isn't enough.
        """;

    protected async Task<JsonNode> RunAsync(
        string sessionId,
        string? selector,
        CancellationToken ct)
    {
        var request = new SnapshotRequest(sessionId, selector);
        var result = await browser.SnapshotAsync(request, ct);

        if (result.ErrorMessage is not null)
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["sessionId"] = result.SessionId,
                ["message"] = result.ErrorMessage
            };
        }

        return new JsonObject
        {
            ["status"] = "success",
            ["sessionId"] = result.SessionId,
            ["url"] = result.Url,
            ["snapshot"] = result.Snapshot,
            ["refCount"] = result.RefCount
        };
    }
}
