using ModelContextProtocol.Server;

namespace Infrastructure.Extensions;

public static class McpServerExtensions
{
    extension(McpServer mcpServer)
    {
        public string StateKey => mcpServer.SessionId ??
                                  mcpServer.ClientInfo?.Name ??
                                  throw new InvalidOperationException("Session ID or Client Name is not available.");

        // Use this when the value MUST match the Mcp-Session-Id header (e.g., paired with
        // session-end cleanup middleware). Unlike StateKey, it never falls back to
        // ClientInfo.Name, so it cannot silently desynchronize from the transport-level id.
        public string RequireSessionId() => mcpServer.SessionId ??
            throw new InvalidOperationException(
                "MCP SessionId is not available. The Streamable HTTP transport must be running in stateful mode.");
    }
}