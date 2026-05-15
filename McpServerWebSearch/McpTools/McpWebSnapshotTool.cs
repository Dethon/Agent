using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools.Web;
using Infrastructure.Extensions;
using Infrastructure.Utils;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServerWebSearch.McpTools;

[McpServerToolType]
public class McpWebSnapshotTool(IWebBrowser browser)
    : WebSnapshotTool(browser)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> Run(
        RequestContext<CallToolRequestParams> context,
        [Description("CSS selector to limit snapshot scope (e.g. 'main', '.search-form'). Omit for full page.")]
        string? selector = null,
        CancellationToken ct = default)
    {
        var sessionId = context.Server.RequireSessionId();
        var result = await RunAsync(sessionId, selector, ct);
        return result.Body is null
            ? ToolResponse.Create(result.Envelope)
            : ToolResponse.Create(result.Envelope, result.Body);
    }
}