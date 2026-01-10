using System.Text.Json.Nodes;
using Domain.Contracts;

namespace Domain.Tools.Web;

public class WebInspectTool(IWebBrowser browser)
{
    protected const string Name = "WebInspect";

    protected const string Description =
        """
        Inspects the current page structure without returning full content.
        Use to explore large pages before extracting specific content with WebBrowse.

        Modes:
        - 'structure' (default): Page outline with headings, sections, form/button/link counts
        - 'search': Find text in page, returns matches with context and CSS selectors
        - 'forms': Detailed form inspection with all fields and buttons
        - 'interactive': All clickable elements (buttons, links) with CSS selectors

        Returns CSS selectors for use with WebBrowse (selector parameter) or WebClick.

        Examples:
        - Get page overview: mode="structure"
        - Find text on page: mode="search", query="price"
        - Find all forms: mode="forms"
        - Find clickable elements: mode="interactive"
        """;

    protected async Task<JsonNode> RunAsync(
        string sessionId,
        string mode,
        string? query,
        bool regex,
        int maxResults,
        string? selector,
        CancellationToken ct)
    {
        var parsedMode = ParseMode(mode);

        var request = new InspectRequest(
            SessionId: sessionId,
            Mode: parsedMode,
            Query: query,
            Regex: regex,
            MaxResults: Math.Clamp(maxResults, 1, 100),
            Selector: selector
        );

        var result = await browser.InspectAsync(request, ct);

        if (!string.IsNullOrEmpty(result.ErrorMessage))
        {
            return new JsonObject
            {
                ["status"] = "error",
                ["sessionId"] = result.SessionId,
                ["mode"] = mode,
                ["message"] = result.ErrorMessage
            };
        }

        var response = new JsonObject
        {
            ["status"] = "success",
            ["sessionId"] = result.SessionId,
            ["url"] = result.Url,
            ["title"] = result.Title,
            ["mode"] = result.Mode.ToString().ToLowerInvariant()
        };

        switch (result.Mode)
        {
            case InspectMode.Structure when result.Structure != null:
                AddStructureToResponse(response, result.Structure);
                break;
            case InspectMode.Search when result.SearchResult != null:
                AddSearchResultToResponse(response, result.SearchResult);
                break;
            case InspectMode.Forms when result.Forms != null:
                AddFormsToResponse(response, result.Forms);
                break;
            case InspectMode.Interactive when result.Interactive != null:
                AddInteractiveToResponse(response, result.Interactive);
                break;
        }

        return response;
    }

    private static InspectMode ParseMode(string? mode)
    {
        if (string.IsNullOrEmpty(mode))
        {
            return InspectMode.Structure;
        }

        return mode.ToLowerInvariant() switch
        {
            "search" => InspectMode.Search,
            "forms" => InspectMode.Forms,
            "interactive" => InspectMode.Interactive,
            _ => InspectMode.Structure
        };
    }

    private static void AddStructureToResponse(JsonObject response, InspectStructure structure)
    {
        response["totalTextLength"] = structure.TotalTextLength;
        response["preview"] = structure.Preview;
        response["formCount"] = structure.FormCount;
        response["buttonCount"] = structure.ButtonCount;
        response["linkCount"] = structure.LinkCount;

        var headingsArray = new JsonArray();
        foreach (var heading in structure.Headings)
        {
            headingsArray.Add(new JsonObject
            {
                ["level"] = heading.Level,
                ["text"] = heading.Text,
                ["id"] = heading.Id,
                ["selector"] = heading.Selector
            });
        }

        response["headings"] = headingsArray;

        var sectionsArray = new JsonArray();
        foreach (var section in structure.Sections)
        {
            sectionsArray.Add(new JsonObject
            {
                ["tag"] = section.Tag,
                ["id"] = section.Id,
                ["className"] = section.ClassName,
                ["selector"] = section.Selector,
                ["textLength"] = section.TextLength
            });
        }

        response["sections"] = sectionsArray;
    }

    private static void AddSearchResultToResponse(JsonObject response, InspectSearchResult searchResult)
    {
        response["query"] = searchResult.Query;
        response["totalMatches"] = searchResult.TotalMatches;

        var matchesArray = new JsonArray();
        foreach (var match in searchResult.Matches)
        {
            matchesArray.Add(new JsonObject
            {
                ["text"] = match.Text,
                ["context"] = match.Context,
                ["selector"] = match.NearestSelector,
                ["nearestHeading"] = match.NearestHeading
            });
        }

        response["matches"] = matchesArray;
    }

    private static void AddFormsToResponse(JsonObject response, IReadOnlyList<InspectForm> forms)
    {
        var formsArray = new JsonArray();
        foreach (var form in forms)
        {
            var fieldsArray = new JsonArray();
            foreach (var field in form.Fields)
            {
                fieldsArray.Add(new JsonObject
                {
                    ["type"] = field.Type,
                    ["name"] = field.Name,
                    ["label"] = field.Label,
                    ["placeholder"] = field.Placeholder,
                    ["selector"] = field.Selector,
                    ["required"] = field.Required
                });
            }

            var buttonsArray = new JsonArray();
            foreach (var button in form.Buttons)
            {
                buttonsArray.Add(new JsonObject
                {
                    ["tag"] = button.Tag,
                    ["text"] = button.Text,
                    ["selector"] = button.Selector
                });
            }

            formsArray.Add(new JsonObject
            {
                ["name"] = form.Name,
                ["action"] = form.Action,
                ["method"] = form.Method,
                ["selector"] = form.Selector,
                ["fields"] = fieldsArray,
                ["buttons"] = buttonsArray
            });
        }

        response["forms"] = formsArray;
    }

    private static void AddInteractiveToResponse(JsonObject response, InspectInteractive interactive)
    {
        var buttonsArray = new JsonArray();
        foreach (var button in interactive.Buttons)
        {
            buttonsArray.Add(new JsonObject
            {
                ["tag"] = button.Tag,
                ["text"] = button.Text,
                ["selector"] = button.Selector,
                ["count"] = button.Count
            });
        }

        response["buttons"] = buttonsArray;

        var linksArray = new JsonArray();
        foreach (var link in interactive.Links)
        {
            linksArray.Add(new JsonObject
            {
                ["text"] = link.Text,
                ["selector"] = link.Selector,
                ["count"] = link.Count
            });
        }

        response["links"] = linksArray;
    }
}