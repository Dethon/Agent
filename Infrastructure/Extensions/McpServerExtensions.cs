using ModelContextProtocol.Server;

namespace Infrastructure.Extensions;

public static class McpServerExtensions
{
    public const string SessionIdHeader = "Mcp-Session-Id";

    extension(McpServer mcpServer)
    {
        public string StateKey => mcpServer.SessionId ??
                                  mcpServer.ClientInfo?.Name ??
                                  throw new InvalidOperationException("Session ID or Client Name is not available.");

        // Why: must match the Mcp-Session-Id header for transport-level cleanup hooks.
        // StateKey's ClientInfo.Name fallback would silently desynchronize from the header.
        public string RequireSessionId() => mcpServer.SessionId ??
            throw new InvalidOperationException(
                "MCP SessionId is not available. The Streamable HTTP transport must be running in stateful mode.");
    }
}