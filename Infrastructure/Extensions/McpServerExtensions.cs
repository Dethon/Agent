using ModelContextProtocol.Server;

namespace Infrastructure.Extensions;

public static class McpServerExtensions
{
    extension(McpServer mcpServer)
    {
        public string StateKey => mcpServer.SessionId ??
                                  mcpServer.ClientInfo?.Name ??
                                  throw new InvalidOperationException("Session ID or Client Name is not available.");
    }
}
