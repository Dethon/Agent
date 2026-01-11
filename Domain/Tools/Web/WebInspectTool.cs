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
        - 'structure' (default): Smart page analysis with actionable suggestions
          - Detects main content area
          - Finds repeating elements (product cards, search results) with field detection
          - Identifies pagination/navigation
          - Returns hierarchical outline with selectors
          - Provides suggestions like "Found 24 items: use selector='.product-card'"
        - 'search': Find visible TEXT in page, returns matches with context and selectors
        - 'forms': Detailed form inspection with all fields and buttons
        - 'interactive': All clickable elements (buttons, links) with selectors

        IMPORTANT: To extract elements by CSS selector (e.g., '.product', '#main'), use WebBrowse
        with the selector parameter directly. The search mode only finds visible text content.

        Examples:
        - Analyze page structure: mode="structure" → get suggestions for extraction
        - Find text on page: mode="search", query="price"
        - Extract items: Use suggestions from structure, e.g., WebBrowse(selector=".product-card")
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

        // Main content region
        if (structure.MainContent != null)
        {
            response["mainContent"] = new JsonObject
            {
                ["selector"] = structure.MainContent.Selector,
                ["preview"] = structure.MainContent.Preview,
                ["textLength"] = structure.MainContent.TextLength
            };
        }

        // Repeating elements (product cards, search results, etc.)
        if (structure.RepeatingElements.Count > 0)
        {
            var repeatingArray = new JsonArray();
            foreach (var repeating in structure.RepeatingElements)
            {
                var item = new JsonObject
                {
                    ["selector"] = repeating.Selector,
                    ["count"] = repeating.Count,
                    ["preview"] = repeating.Preview
                };

                if (repeating.DetectedFields is { Count: > 0 })
                {
                    var fieldsArray = new JsonArray();
                    foreach (var field in repeating.DetectedFields)
                    {
                        fieldsArray.Add(field);
                    }

                    item["detectedFields"] = fieldsArray;
                }

                repeatingArray.Add(item);
            }

            response["repeatingElements"] = repeatingArray;
        }

        // Navigation info
        if (structure.Navigation != null)
        {
            var nav = new JsonObject();
            if (structure.Navigation.PaginationSelector != null)
            {
                nav["paginationSelector"] = structure.Navigation.PaginationSelector;
            }

            if (structure.Navigation.NextPageSelector != null)
            {
                nav["nextPageSelector"] = structure.Navigation.NextPageSelector;
            }

            if (structure.Navigation.PrevPageSelector != null)
            {
                nav["prevPageSelector"] = structure.Navigation.PrevPageSelector;
            }

            if (structure.Navigation.MenuSelector != null)
            {
                nav["menuSelector"] = structure.Navigation.MenuSelector;
            }

            response["navigation"] = nav;
        }

        // Hierarchical outline
        if (structure.Outline.Count > 0)
        {
            response["outline"] = BuildOutlineJson(structure.Outline);
        }

        // Actionable suggestions
        if (structure.Suggestions.Count > 0)
        {
            var suggestionsArray = new JsonArray();
            foreach (var suggestion in structure.Suggestions)
            {
                suggestionsArray.Add(suggestion);
            }

            response["suggestions"] = suggestionsArray;
        }
    }

    private static JsonArray BuildOutlineJson(IReadOnlyList<OutlineNode> nodes)
    {
        var array = new JsonArray();
        foreach (var node in nodes)
        {
            var item = new JsonObject
            {
                ["tag"] = node.Tag,
                ["selector"] = node.Selector,
                ["preview"] = node.Preview,
                ["textLength"] = node.TextLength
            };

            if (node.Children is { Count: > 0 })
            {
                item["children"] = BuildOutlineJson(node.Children);
            }

            array.Add(item);
        }

        return array;
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