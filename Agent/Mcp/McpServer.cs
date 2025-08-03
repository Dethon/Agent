using Domain.Agents;
using Domain.Tools;
using Infrastructure.MCP;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Agent.Mcp;

public class McpServers
{
    public McpServerOptions Downloader { get; } = new()
    {
        ServerInfo = new Implementation
        {
            Name = "DataAppSqlGeneration",
            Version = "1.0.0"
        },
        Capabilities = new ServerCapabilities
        {
            Prompts = new PromptsCapability
            {
                PromptCollection =
                [
                    new McpPrompt<DownloaderPrompt>()
                ]
            },

            Tools = new ToolsCapability
            {
                ToolCollection =
                [
                    new McpTool<CleanupTool>(),
                    new McpTool<FileDownloadTool>(),
                    new McpTool<FileSearchTool>(),
                    new McpTool<ListDirectoriesTool>(),
                    new McpTool<ListFilesTool>(),
                    new McpTool<MoveTool>(),
                    new McpTool<WaitForDownloadTool>()
                ]
            }
        }
    };
}