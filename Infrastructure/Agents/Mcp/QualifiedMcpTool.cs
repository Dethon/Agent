using System.Text.Json;
using Microsoft.Extensions.AI;
using ModelContextProtocol;
using ModelContextProtocol.Client;

namespace Infrastructure.Agents.Mcp;

internal sealed class QualifiedMcpTool(string serverName, McpClientTool innerTool) : AIFunction
{
    private const string McpPrefix = "mcp";
    private const string Separator = "__";

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
        var result = await innerTool.InvokeAsync(arguments, cancellationToken);
        return Flatten(result);
    }

    // Multi-block tool results from MCP arrive here as AIContent[]. The downstream
    // OpenAI bridge (Microsoft.Extensions.AI.OpenAI) JSON-serializes any non-string
    // FunctionResultContent.Result into the tool message, which re-escapes every
    // body character. Flattening to a single string short-circuits that path.
    internal static object? Flatten(object? result)
    {
        if (result is not IList<AIContent> contents || contents.Count <= 1)
        {
            return result;
        }

        if (!contents.All(c => c is TextContent))
        {
            return result;
        }

        return string.Join("\n\n", contents.OfType<TextContent>().Select(c => c.Text));
    }
}