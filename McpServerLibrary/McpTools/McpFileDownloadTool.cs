using System.ComponentModel;
using System.Text.Json.Nodes;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Domain.Tools.Downloads;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerLibrary.McpTools;

[McpServerToolType]
public class McpFileDownloadTool(
    IDownloadClient client,
    ISearchResultsManager searchResultsManager,
    ITrackedDownloadsManager trackedDownloadsManager,
    DownloadPathConfig pathConfig)
    : FileDownloadTool(client, searchResultsManager, trackedDownloadsManager, pathConfig)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        [Description("Id from a prior file_search result. Mutually exclusive with link.")]
        int? searchResultId,
        [Description("Magnet URI or http(s) .torrent URL obtained from any other tool. Requires title. Mutually exclusive with searchResultId.")]
        string? link,
        [Description("Descriptive title for the download (required when link is provided; ignored otherwise).")]
        string? title,
        CancellationToken cancellationToken)
    {
        var sessionId = context.Server.StateKey;

        var validation = ValidateInputs(searchResultId, link, title);
        if (validation is not null)
        {
            return ToolResponse.Create(validation);
        }

        var result = searchResultId.HasValue
            ? await Run(sessionId, searchResultId.Value, cancellationToken)
            : await Run(sessionId, link!, title!, cancellationToken);

        await context.Server.SendNotificationAsync(
            "notifications/resources/list_changed",
            cancellationToken: cancellationToken);
        return ToolResponse.Create(result);
    }

    public static JsonNode? ValidateInputs(int? searchResultId, string? link, string? title)
    {
        var hasId = searchResultId.HasValue;
        var hasLink = !string.IsNullOrWhiteSpace(link);

        if (hasId && hasLink)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Provide either searchResultId or link, not both.",
                retryable: false);
        }

        if (!hasId && !hasLink)
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "Provide either searchResultId or link.",
                retryable: false);
        }

        if (hasLink && string.IsNullOrWhiteSpace(title))
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "title is required when link is provided.",
                retryable: false);
        }

        if (hasLink && !IsAcceptedLink(link!))
        {
            return ToolError.Create(
                ToolError.Codes.InvalidArgument,
                "link must start with magnet:, http://, or https://.",
                retryable: false);
        }

        return null;
    }

    private static bool IsAcceptedLink(string link)
    {
        return link.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
               || link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }
}
