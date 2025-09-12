﻿using System.ComponentModel;
using Domain.Contracts;
using Domain.Tools;
using Domain.Tools.Config;
using Infrastructure.Utils;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpServer.Download.McpTools;

[McpServerToolType]
public class McpFileDownloadTool(
    IDownloadClient client,
    IStateManager stateManager,
    DownloadPathConfig pathConfig,
    ILogger<McpFileDownloadTool> logger) : FileDownloadTool(client, stateManager, pathConfig)
{
    [McpServerTool(Name = Name)]
    [Description(Description)]
    public async Task<CallToolResult> McpRun(
        RequestContext<CallToolRequestParams> context,
        int searchResultId,
        CancellationToken cancellationToken)
    {
        try
        {
            var sessionId = context.Server.SessionId ?? "";
            return ToolResponse.Create(await Run(sessionId, searchResultId, cancellationToken));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in {ToolName} tool", Name);
            return ToolResponse.Create(ex);
        }
    }
}