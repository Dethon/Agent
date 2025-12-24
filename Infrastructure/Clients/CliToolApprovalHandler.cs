using System.Text.Json;
using Domain.Agents;
using Domain.Contracts;
using Domain.DTOs;
using Infrastructure.CliGui.Abstractions;

namespace Infrastructure.Clients;

public sealed class CliToolApprovalHandler(IToolApprovalUi terminalAdapter) : IToolApprovalHandler
{
    public async Task<ToolApprovalResult> RequestApprovalAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        var request = requests[0];
        var details = FormatArguments(request.Arguments);

        var result = await terminalAdapter.ShowApprovalDialogAsync(
            request.ToolName,
            details,
            cancellationToken);

        terminalAdapter.ShowToolResult(request.ToolName, request.Arguments, result);

        return result;
    }

    public Task NotifyAutoApprovedAsync(
        IReadOnlyList<ToolApprovalRequest> requests,
        CancellationToken cancellationToken)
    {
        foreach (var request in requests)
        {
            terminalAdapter.ShowToolResult(request.ToolName, request.Arguments, ToolApprovalResult.AutoApproved);
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
}

public sealed class CliToolApprovalHandlerFactory(CliToolApprovalHandler handler) : IToolApprovalHandlerFactory
{
    public IToolApprovalHandler Create(AgentKey agentKey)
    {
        return handler;
    }
}