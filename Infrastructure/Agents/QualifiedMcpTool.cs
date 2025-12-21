using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents;

internal sealed class QualifiedMcpTool(string serverName, McpClientTool innerTool) : AIFunction
{
    private const string McpPrefix = "mcp";
    private const string Separator = ":";

    public override string Name { get; } = $"{McpPrefix}{Separator}{serverName}{Separator}{innerTool.Name}";

    public override string Description => innerTool.Description;
    public override JsonElement JsonSchema => innerTool.JsonSchema;

    public QualifiedMcpTool WithProgress(IProgress<ProgressNotificationValue> progress)
    {
        return new QualifiedMcpTool(serverName, innerTool.WithProgress(progress));
    }

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        return await innerTool.InvokeAsync(arguments, cancellationToken);
    }
}