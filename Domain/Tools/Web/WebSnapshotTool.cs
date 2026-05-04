using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebSnapshotTool(IWebBrowser browser)
{
    protected const string Name = "web_snapshot";

    protected const string Description =
        """
        Returns the accessibility tree showing all elements: headings, text, buttons,
        links, form fields, dropdowns, and their current state.

        Each interactive element has a ref you use with web_action to interact with it.

        Use this to understand page state and find elements before interacting.
        Call after web_browse to see interactive elements, or after web_action when the
        diff response isn't enough context.
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
            var error = ToolError.Create(
                ToolError.Codes.InternalError,
                result.ErrorMessage,
                retryable: false);
            error["sessionId"] = result.SessionId;
            return error;
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
