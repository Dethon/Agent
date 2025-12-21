using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;

namespace Infrastructure.Clients.Cli;

public sealed class CliToolApprovalHandler(ITerminalAdapter terminalAdapter) : IToolApprovalHandler
{
    public async Task<bool> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        var request = requests[0];
        var details = FormatArguments(request.Arguments);

        var approved = await terminalAdapter.ShowApprovalDialogAsync(
            request.ToolName,
            details,
            cancellationToken);

        var resultMessage = approved
            ? $"✅ Approved: {request.ToolName}"
            : $"❌ Rejected: {request.ToolName}";
        terminalAdapter.ShowSystemMessage(resultMessage);

        return approved;
    }

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        foreach (var request in requests)
        {
            var args = FormatArgumentsMultiline(request.Arguments);
            var message = string.IsNullOrEmpty(args)
                ? $"✅ Auto-approved: {request.ToolName}"
                : $"✅ Auto-approved:\n{request.ToolName}\n{args}";
            terminalAdapter.ShowSystemMessage(message);
        }

        return Task.CompletedTask;
    }

    private static string FormatArguments(IReadOnlyDictionary<string, object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return "(no arguments)";
        }

        return JsonSerializer.Serialize(arguments, new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static string FormatArgumentsMultiline(IReadOnlyDictionary<string, object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return "";
        }

        var lines = new List<string>();
        foreach (var kvp in arguments)
        {
            var formattedValue = FormatValue(kvp.Value, "   ");
            lines.Add($"   {kvp.Key}: {formattedValue}");
        }

        return string.Join("\n", lines);
    }

    private static string FormatValue(object? value, string indent)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is JsonElement element)
        {
            return FormatJsonElement(element, indent);
        }

        if (value is string s)
        {
            return s;
        }

        return value.ToString() ?? "null";
    }

    private static string FormatJsonElement(JsonElement element, string indent)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                var items = element.EnumerateArray().ToList();
                if (items.Count <= 1)
                {
                    return items.Count == 0 ? "[]" : FormatJsonElement(items[0], indent);
                }

                var arrayLines = items.Select(item => $"{indent}   - {FormatJsonElement(item, indent + "   ")}");
                return "\n" + string.Join("\n", arrayLines);

            case JsonValueKind.Object:
                var props = element.EnumerateObject().ToList();
                if (props.Count == 0)
                {
                    return "{}";
                }

                var objLines = props.Select(p => $"{indent}   {p.Name}: {FormatJsonElement(p.Value, indent + "   ")}");
                return "\n" + string.Join("\n", objLines);

            case JsonValueKind.String:
                return element.GetString() ?? "";

            case JsonValueKind.Number:
                return element.GetRawText();

            case JsonValueKind.True:
                return "true";

            case JsonValueKind.False:
                return "false";

            case JsonValueKind.Null:
                return "null";

            default:
                return element.GetRawText();
        }
    }
}

public sealed class CliToolApprovalHandlerFactory(CliToolApprovalHandler handler) : IToolApprovalHandlerFactory
{
    public IToolApprovalHandler Create(AgentKey agentKey)
    {
        return handler;
    }
}